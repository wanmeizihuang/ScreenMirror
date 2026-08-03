using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace ScreenMirror.Protocol;

/// <summary>
/// 连接管理器 — 管理所有控制连接，支持 Server 和 Client 两种模式
/// </summary>
public class ConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ControlConnection> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;

    // 控制端口号
    public const int DefaultControlPort = 35354;
    public const int DefaultVideoPort = 35355;
    public const int DefaultAudioPort = 35356;

    public int ControlPort { get; private set; } = DefaultControlPort;
    public bool IsListening => _listener != null;

    // 事件
    public event Action<ControlConnection>? OnConnectionAccepted;     // 作为 Server 接受连接
    public event Action<ControlConnection>? OnConnectionEstablished;  // 作为 Client 连接成功
    public event Action<string>? OnConnectionLost;

    /// <summary>
    /// 作为 Server 开始监听
    /// </summary>
    public async Task StartListeningAsync(int port = DefaultControlPort)
    {
        if (_listener != null) return;

        ControlPort = port;
        _listenerCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();

        _ = AcceptLoopAsync(_listenerCts.Token);

        await Task.CompletedTask;
    }

    /// <summary>
    /// 作为 Client 连接到远程设备
    /// </summary>
    public async Task<ControlConnection?> ConnectAsync(string host, int port = DefaultControlPort)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port);

        if (client.Connected)
        {
            var connection = new ControlConnection(client);
            connection.OnDisconnected += OnConnectionDisconnected;
            _connections[connection.ConnectionId] = connection;
            connection.StartListening();
            OnConnectionEstablished?.Invoke(connection);
            return connection;
        }

        return null;
    }

    /// <summary>
    /// 发送消息到指定连接
    /// </summary>
    public async Task SendAsync(string connectionId, byte[] data)
    {
        if (_connections.TryGetValue(connectionId, out var conn))
        {
            await conn.SendAsync(data);
        }
    }

    /// <summary>
    /// 获取所有活跃连接
    /// </summary>
    public IReadOnlyCollection<ControlConnection> GetConnections()
    {
        return _connections.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// 断开指定连接
    /// </summary>
    public void Disconnect(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var conn))
        {
            conn.Disconnect();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                var connection = new ControlConnection(tcpClient);
                connection.OnDisconnected += OnConnectionDisconnected;

                _connections[connection.ConnectionId] = connection;
                connection.StartListening();
                OnConnectionAccepted?.Invoke(connection);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private void OnConnectionDisconnected(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
        OnConnectionLost?.Invoke(connectionId);
    }

    public void Stop()
    {
        _listenerCts?.Cancel();
        _listener?.Stop();
        _listener = null;

        foreach (var conn in _connections.Values)
        {
            conn.Disconnect();
        }
        _connections.Clear();
    }

    public void Dispose()
    {
        Stop();
        _listenerCts?.Dispose();
    }
}
