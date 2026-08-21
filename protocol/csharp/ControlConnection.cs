using System.Net.Sockets;
using System.Collections.Concurrent;

namespace ScreenMirror.Protocol;

/// <summary>
/// 控制连接处理器 — 管理 TCP 控制通道的读写和消息分发
/// </summary>
public class ControlConnection : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly string _remoteHost;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _headerBuffer = new byte[ControlProtocolCodec.HeaderSize];
    private readonly byte[] _payloadBuffer = new byte[ControlProtocolCodec.MaxPayloadSize];
    private long _lastReceivedAt = Environment.TickCount64;
    private int _listeningStarted;
    private int _disconnected;

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsConnected => Volatile.Read(ref _disconnected) == 0 && _tcpClient.Connected;
    public string RemoteHost => _remoteHost;
    public string LastDisconnectReason { get; private set; } = "连接中";

    public event Action<ControlMessageType, ReadOnlyMemory<byte>>? OnMessageReceived;
    public event Action<string>? OnDisconnected;

    public ControlConnection(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _remoteHost = (tcpClient.Client?.RemoteEndPoint as System.Net.IPEndPoint)
            ?.Address.ToString() ?? "unknown";
        _tcpClient.NoDelay = true;         // 禁用 Nagle，视频帧小包立即发送
        _tcpClient.SendBufferSize = 256 * 1024;
        _tcpClient.ReceiveBufferSize = 64 * 1024;
        _stream = tcpClient.GetStream();
    }

    /// <summary>
    /// 开始监听消息
    /// </summary>
    public void StartListening()
    {
        if (Interlocked.Exchange(ref _listeningStarted, 1) != 0) return;

        _ = ReadLoopAsync(_cts.Token);
        _ = MonitorConnectionAsync(_cts.Token);
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (!IsConnected) return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (!IsConnected) return;
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Disconnect($"发送失败: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 发送编码好的消息
    /// </summary>
    public async Task SendMessageAsync(ControlMessageType type, byte[] payload)
    {
        await SendAsync(ControlProtocolCodec.Encode(type, payload));
    }

    public async Task SendMessageAsync(ControlMessageType type)
    {
        await SendAsync(ControlProtocolCodec.Encode(type));
    }

    /// <summary>
    /// 发送触摸事件
    /// </summary>
    public async Task SendTouchEventAsync(TouchEventData evt)
    {
        await SendAsync(ControlProtocolCodec.EncodeTouchEvent(evt));
    }

    /// <summary>
    /// 发送按键事件
    /// </summary>
    public async Task SendKeyEventAsync(KeyEventData evt)
    {
        await SendAsync(ControlProtocolCodec.EncodeKeyEvent(evt));
    }

    /// <summary>
    /// 发送屏幕参数
    /// </summary>
    public async Task SendScreenInfoAsync(ScreenInfoData info)
    {
        await SendAsync(ControlProtocolCodec.EncodeScreenInfo(info));
    }

    /// <summary>
    /// 发送设备握手
    /// </summary>
    public async Task SendDeviceHelloAsync(DeviceHelloData hello)
    {
        await SendAsync(ControlProtocolCodec.EncodeDeviceHello(hello));
    }

    /// <summary>
    /// 发送心跳
    /// </summary>
    public async Task SendHeartbeatAsync()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await SendAsync(ControlProtocolCodec.EncodeHeartbeat(ts));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        string disconnectReason = "读取循环结束";
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                // 读取消息头
                int headerRead = await ReadExactAsync(_headerBuffer, 0, ControlProtocolCodec.HeaderSize, ct);
                if (headerRead == 0)
                {
                    disconnectReason = "远端关闭连接";
                    break;
                }

                if (!ControlProtocolCodec.TryDecodeHeader(_headerBuffer, out var type, out int payloadLength) ||
                    payloadLength < 0 || payloadLength > ControlProtocolCodec.MaxPayloadSize)
                {
                    disconnectReason = $"协议头无效: type=0x{_headerBuffer[0]:X2}, length={payloadLength}";
                    break;
                }

                // 读取负载
                if (payloadLength > 0)
                {
                    int payloadRead = await ReadExactAsync(_payloadBuffer, 0, payloadLength, ct);
                    if (payloadRead == 0)
                    {
                        disconnectReason = $"读取 {type} 负载时远端关闭连接";
                        break;
                    }

                    var payload = new ReadOnlyMemory<byte>(_payloadBuffer, 0, payloadLength);
                    Interlocked.Exchange(ref _lastReceivedAt, Environment.TickCount64);

                    if (type == ControlMessageType.Heartbeat && payloadLength == sizeof(long))
                    {
                        var timestamp = ControlProtocolCodec.DecodeHeartbeat(payload.Span);
                        await SendAsync(ControlProtocolCodec.EncodeHeartbeatAck(timestamp), ct);
                    }

                    DispatchMessage(type, payload);
                }
                else
                {
                    Interlocked.Exchange(ref _lastReceivedAt, Environment.TickCount64);
                    DispatchMessage(type, ReadOnlyMemory<byte>.Empty);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            disconnectReason = $"读取失败: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Disconnect(disconnectReason);
        }
    }

    private void DispatchMessage(ControlMessageType type, ReadOnlyMemory<byte> payload)
    {
        var handlers = OnMessageReceived;
        if (handlers == null) return;

        foreach (Action<ControlMessageType, ReadOnlyMemory<byte>> handler in handlers.GetInvocationList())
        {
            try { handler(type, payload); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ControlConnection] {type} handler failed: {ex}");
            }
        }
    }

    private async Task MonitorConnectionAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var idleFor = Environment.TickCount64 - Interlocked.Read(ref _lastReceivedAt);
                if (idleFor > ConnectionTimeout.TotalMilliseconds)
                {
                    Disconnect($"连接超时: {idleFor / 1000:F1} 秒无入站数据");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<int> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0) return 0; // 连接关闭
            totalRead += read;
        }
        return totalRead;
    }

    public void Disconnect(string reason = "本地主动断开")
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;

        LastDisconnectReason = reason;
        _cts.Cancel();
        try
        {
            _stream.Close();
            _tcpClient.Close();
        }
        catch { }
        OnDisconnected?.Invoke(ConnectionId);
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
        _sendLock.Dispose();
        _stream.Dispose();
        _tcpClient.Dispose();
    }
}
