using System.Collections.Concurrent;

namespace ScreenMirror.Protocol;

/// <summary>
/// 消息路由器 — 将控制协议消息分发到注册的处理器
/// </summary>
public class MessageRouter
{
    private readonly ConcurrentDictionary<ControlMessageType, List<Func<ControlMessageType, ReadOnlyMemory<byte>, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<ControlMessageType, List<Action<ControlMessageType, ReadOnlyMemory<byte>>>> _syncHandlers = new();

    /// <summary>
    /// 注册异步消息处理器
    /// </summary>
    public void RegisterHandler(ControlMessageType type, Func<ControlMessageType, ReadOnlyMemory<byte>, Task> handler)
    {
        _handlers.AddOrUpdate(type,
            _ => new List<Func<ControlMessageType, ReadOnlyMemory<byte>, Task>> { handler },
            (_, list) => { list.Add(handler); return list; });
    }

    /// <summary>
    /// 注册同步消息处理器
    /// </summary>
    public void RegisterHandler(ControlMessageType type, Action<ControlMessageType, ReadOnlyMemory<byte>> handler)
    {
        _syncHandlers.AddOrUpdate(type,
            _ => new List<Action<ControlMessageType, ReadOnlyMemory<byte>>> { handler },
            (_, list) => { list.Add(handler); return list; });
    }

    /// <summary>
    /// 注册通配处理器 — 处理所有消息类型
    /// </summary>
    public void RegisterWildcard(Func<ControlMessageType, ReadOnlyMemory<byte>, Task> handler)
    {
        foreach (ControlMessageType type in Enum.GetValues<ControlMessageType>())
        {
            RegisterHandler(type, handler);
        }
    }

    /// <summary>
    /// 路由消息到已注册的处理器
    /// </summary>
    public async Task RouteAsync(ControlMessageType type, ReadOnlyMemory<byte> payload)
    {
        // 异步处理器
        if (_handlers.TryGetValue(type, out var asyncHandlers))
        {
            foreach (var handler in asyncHandlers)
            {
                await handler(type, payload);
            }
        }

        // 同步处理器
        if (_syncHandlers.TryGetValue(type, out var syncHandlers))
        {
            foreach (var handler in syncHandlers)
            {
                handler(type, payload);
            }
        }
    }

    /// <summary>
    /// 绑定路由器到连接 — 连接收到消息时自动路由
    /// </summary>
    public void BindToConnection(ControlConnection connection)
    {
        connection.OnMessageReceived += async (type, payload) =>
        {
            await RouteAsync(type, payload);
        };

        connection.OnDisconnected += (id) =>
        {
            // 连接断开时路由一次空通知（插件可自行清理）
        };
    }

    /// <summary>
    /// 绑定路由器到连接管理器 — 所有新连接自动绑定
    /// </summary>
    public void BindToConnectionManager(ConnectionManager manager)
    {
        // 已有连接绑定
        foreach (var conn in manager.GetConnections())
        {
            BindToConnection(conn);
        }

        // 新连接自动绑定
        manager.OnConnectionAccepted += conn => BindToConnection(conn);
        manager.OnConnectionEstablished += conn => BindToConnection(conn);
    }
}
