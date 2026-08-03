using System.Net;
using System.Net.Sockets;
using ScreenMirror.Protocol;
using ScreenMirror.Server.Plugins;

namespace ScreenMirror.Server.Modules.ScreenMirror;

/// <summary>
/// 投屏插件 — 精简版，仅处理协议层消息
/// 视频解码渲染由 MirrorWindow 中的 VlcDecoder 负责
/// </summary>
public class ScreenMirrorPlugin : IPlugin
{
    private ControlConnection? _connection;
    private ScreenInfoData? _screenInfo;

    public string Name => "ScreenMirror";
    public string Version => "1.0.0";
    public PluginState State { get; private set; } = PluginState.Unloaded;
    public ControlMessageType[] SubscribedMessageTypes => new[]
    {
        ControlMessageType.ScreenInfo,
        ControlMessageType.StreamStart,
        ControlMessageType.StreamStop,
    };

    public int ScreenWidth => _screenInfo?.Width ?? 0;
    public int ScreenHeight => _screenInfo?.Height ?? 0;

    public Task<bool> Initialize(ControlConnection connection)
    {
        _connection = connection;
        State = PluginState.Loaded;
        return Task.FromResult(true);
    }

    public Task Start()
    {
        State = PluginState.Running;
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        State = PluginState.Unloaded;
        return Task.CompletedTask;
    }

    public PluginCapability[] GetCapabilities() => Array.Empty<PluginCapability>();

    public Task HandleMessage(PluginMessage message) => Task.CompletedTask;

    public void Dispose()
    {
        State = PluginState.Unloaded;
    }
}
