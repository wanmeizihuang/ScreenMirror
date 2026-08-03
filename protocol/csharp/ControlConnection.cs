using System.Net.Sockets;
using System.Collections.Concurrent;

namespace ScreenMirror.Protocol;

/// <summary>
/// 控制连接处理器 — 管理 TCP 控制通道的读写和消息分发
/// </summary>
public class ControlConnection : IDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _headerBuffer = new byte[ControlProtocolCodec.HeaderSize];
    private readonly byte[] _payloadBuffer = new byte[ControlProtocolCodec.MaxPayloadSize];

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsConnected => _tcpClient.Connected;
    public string RemoteHost => (_tcpClient.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "";

    public event Action<ControlMessageType, ReadOnlyMemory<byte>>? OnMessageReceived;
    public event Action<string>? OnDisconnected;

    public ControlConnection(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
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
        _ = ReadLoopAsync(_cts.Token);
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (!IsConnected) return;
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
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
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                // 读取消息头
                int headerRead = await ReadExactAsync(_headerBuffer, 0, ControlProtocolCodec.HeaderSize, ct);
                if (headerRead == 0) break;

                ControlProtocolCodec.TryDecodeHeader(_headerBuffer, out var type, out int payloadLength);

                // 读取负载
                if (payloadLength > 0)
                {
                    int payloadRead = await ReadExactAsync(_payloadBuffer, 0, payloadLength, ct);
                    if (payloadRead == 0) break;

                var payload = new ReadOnlyMemory<byte>(_payloadBuffer, 0, payloadLength);
                OnMessageReceived?.Invoke(type, payload);
                }
                else
                {
                    OnMessageReceived?.Invoke(type, ReadOnlyMemory<byte>.Empty);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            Disconnect();
        }
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

    public void Disconnect()
    {
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
        _stream.Dispose();
        _tcpClient.Dispose();
    }
}
