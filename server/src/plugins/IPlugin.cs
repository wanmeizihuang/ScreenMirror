using ScreenMirror.Protocol;

namespace ScreenMirror.Server.Plugins;

/// <summary>
/// 插件状态
/// </summary>
public enum PluginState
{
    Unloaded,
    Loaded,
    Initialized,
    Running,
    Error,
}

/// <summary>
/// 插件元信息
/// </summary>
public class PluginCapability
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
}

/// <summary>
/// 插件消息
/// </summary>
public readonly struct PluginMessage
{
    public string SourcePlugin { get; init; }
    public string TargetPlugin { get; init; }
    public ControlMessageType MessageType { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
}

/// <summary>
/// 插件接口 — 所有功能模块必须实现
/// </summary>
public interface IPlugin
{
    /// <summary>插件唯一名称</summary>
    string Name { get; }

    /// <summary>插件版本</summary>
    string Version { get; }

    /// <summary>当前状态</summary>
    PluginState State { get; }

    /// <summary>
    /// 初始化插件
    /// </summary>
    /// <param name="connection">当前的控制连接</param>
    Task<bool> Initialize(ControlConnection connection);

    /// <summary>启动插件功能</summary>
    Task Start();

    /// <summary>停止插件功能</summary>
    Task Stop();

    /// <summary>获取插件提供的能力列表</summary>
    PluginCapability[] GetCapabilities();

    /// <summary>处理来自其他插件的消息</summary>
    Task HandleMessage(PluginMessage message);

    /// <summary>
    /// 处理来自控制连接的消息 — 插件可选择订阅的消息类型
    /// </summary>
    ControlMessageType[] SubscribedMessageTypes { get; }
}

/// <summary>
/// 插件管理器 — 管理所有插件的生命周期
/// </summary>
public class PluginManager
{
    private readonly Dictionary<string, IPlugin> _plugins = new();
    private readonly MessageRouter _router;
    private ControlConnection? _connection;

    public IReadOnlyDictionary<string, IPlugin> Plugins => _plugins;

    public PluginManager(MessageRouter router)
    {
        _router = router;
    }

    /// <summary>
    /// 注册并加载插件（幂等：已注册的插件不重复添加、不重复挂订阅，
    /// 但会重新 Initialize 以便在重连后绑定到新连接）
    /// </summary>
    public async Task RegisterPlugin(IPlugin plugin)
    {
        bool isNew = !_plugins.ContainsKey(plugin.Name);
        if (isNew)
        {
            _plugins[plugin.Name] = plugin;
        }
        else
        {
            // 重连前先停掉旧实例的钩子/资源，避免挂死/泄漏
            try { await plugin.Stop(); } catch { }
        }

        if (_connection != null)
        {
            await plugin.Initialize(_connection);
            // 注意：不要在这里 Start，由调用方通过 StartAll 显式启动
        }

        // 只在首次注册时挂订阅（避免重复触发）
        if (isNew)
        {
            foreach (var msgType in plugin.SubscribedMessageTypes)
            {
                _router.RegisterHandler(msgType, async (type, payload) =>
                {
                    await plugin.HandleMessage(new PluginMessage
                    {
                        SourcePlugin = "_connection",
                        TargetPlugin = plugin.Name,
                        MessageType = type,
                        Payload = payload,
                    });
                });
            }
        }
    }

    /// <summary>
    /// 设置当前连接并启动所有插件
    /// </summary>
    public async Task InitializeAll(ControlConnection connection)
    {
        _connection = connection;
        foreach (var plugin in _plugins.Values)
        {
            await plugin.Initialize(connection);
        }
    }

    /// <summary>
    /// 启动所有已加载的插件
    /// </summary>
    public async Task StartAll()
    {
        foreach (var plugin in _plugins.Values)
        {
            if (plugin.State == PluginState.Initialized)
                await plugin.Start();
        }
    }

    /// <summary>
    /// 停止所有插件
    /// </summary>
    public async Task StopAll()
    {
        foreach (var plugin in _plugins.Values)
        {
            if (plugin.State == PluginState.Running)
                await plugin.Stop();
        }
    }

    /// <summary>
    /// 获取指定插件
    /// </summary>
    public IPlugin? GetPlugin(string name) =>
        _plugins.TryGetValue(name, out var plugin) ? plugin : null;

    /// <summary>
    /// 卸载插件
    /// </summary>
    public async Task UnloadPlugin(string name)
    {
        if (_plugins.TryGetValue(name, out var plugin))
        {
            await plugin.Stop();
            _plugins.Remove(name);
        }
    }

    /// <summary>
    /// 向指定插件发送消息
    /// </summary>
    public async Task SendToPlugin(string targetPlugin, ControlMessageType type, ReadOnlyMemory<byte> payload)
    {
        if (_plugins.TryGetValue(targetPlugin, out var plugin))
        {
            await plugin.HandleMessage(new PluginMessage
            {
                SourcePlugin = "PluginManager",
                TargetPlugin = targetPlugin,
                MessageType = type,
                Payload = payload,
            });
        }
    }
}
