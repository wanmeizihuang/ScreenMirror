using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenMirror.Protocol;
using ScreenMirror.Server.Plugins;

namespace ScreenMirror.Server.Modules.ReverseControl;

/// <summary>
/// 反向控制插件 — 捕获 Windows 鼠标/键盘，发送到 Android 设备
/// </summary>
public class ReverseControlPlugin : IPlugin
{
    private static readonly object DiagnosticLogLock = new();
    private static readonly string DiagnosticLogPath = Path.Combine(
        Path.GetTempPath(), "screenmirror-reverse-control.log");

    private ControlConnection? _connection;
    private IntPtr _mirrorWindowHandle;
    private ScreenInfoData _screenInfo;

    // 低级别鼠标/键盘钩子
    private LowLevelMouseHook? _mouseHook;
    private LowLevelKeyboardHook? _keyboardHook;
    private bool _isActive;
    private bool _forwardingLeftDrag;
    private readonly object _coalescingLock = new();
    private Channel<OutboundInput>? _outboundInputs;
    private CancellationTokenSource? _senderCts;
    private Task? _senderTask;
    private byte[]? _pendingMouseMove;
    private byte[]? _pendingScroll;
    private bool _mouseMoveQueued;
    private bool _scrollQueued;
    private long _mouseHookEventCount;
    private long _mouseButtonEventCount;
    private long _sentEventCount;

    private enum OutboundInputKind
    {
        Data,
        MouseMove,
        Scroll,
    }

    private readonly record struct OutboundInput(OutboundInputKind Kind, byte[]? Data = null);

    public string Name => "ReverseControl";
    public string Version => "1.1.0";
    public PluginState State { get; private set; } = PluginState.Unloaded;
    public ControlMessageType[] SubscribedMessageTypes => new[]
    {
        ControlMessageType.ScreenInfo,
    };

    /// <summary>
    /// 设置投屏窗口句柄，输入事件仅在该窗口激活时转发
    /// </summary>
    public IntPtr MirrorWindowHandle
    {
        get => _mirrorWindowHandle;
        set
        {
            _mirrorWindowHandle = value;
            if (_isActive)
            {
                StopHooks();
                StartHooks();
            }
        }
    }

    public Func<(float Left, float Top, float Width, float Height)?>? InputBoundsProvider { get; set; }

    public Task<bool> Initialize(ControlConnection connection)
    {
        _connection = connection;
        State = PluginState.Initialized;
        WriteDiagnostic($"Initialize: connection={connection.ConnectionId}, remote={connection.RemoteHost}, connected={connection.IsConnected}");
        return Task.FromResult(true);
    }

    public Task Start()
    {
        WriteDiagnostic($"Start: state={State}, hwnd=0x{_mirrorWindowHandle.ToInt64():X}");
        StartSender();

        // 低级别钩子必须在具有消息循环的线程（UI 线程）上安装，
        // 否则系统不会回调钩子过程
        if (System.Windows.Application.Current?.Dispatcher != null)
            System.Windows.Application.Current.Dispatcher.Invoke(StartHooks);
        else
            StartHooks();

        _isActive = true;
        State = PluginState.Running;
        WriteDiagnostic("Start completed: state=Running");
        return Task.CompletedTask;
    }

    public async Task Stop()
    {
        WriteDiagnostic($"Stop: state={State}");
        StopHooks();
        _isActive = false;
        await StopSenderAsync();
        State = PluginState.Initialized;
    }

    public PluginCapability[] GetCapabilities() => new[]
    {
        new PluginCapability
        {
            Name = "reverse_control",
            Description = "鼠标键盘反向控制 Android 设备",
            Version = "1.1.0",
        }
    };

    public async Task HandleMessage(PluginMessage message)
    {
        if (message.MessageType == ControlMessageType.ScreenInfo)
        {
            _screenInfo = ControlProtocolCodec.DecodeScreenInfo(message.Payload.Span);
        }
        await Task.CompletedTask;
    }

    private void StartHooks()
    {
        StopHooks();
        if (_mirrorWindowHandle == IntPtr.Zero)
        {
            WriteDiagnostic("StartHooks skipped: mirror window handle is zero");
            return;
        }

        try
        {
            _mouseHook = new LowLevelMouseHook(OnMouseEvent, OnScrollEvent);
            _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent);
            WriteDiagnostic($"Hooks installed: mouse=0x{_mouseHook.HookHandle.ToInt64():X}, keyboard=0x{_keyboardHook.HookHandle.ToInt64():X}");
        }
        catch (Exception ex)
        {
            StopHooks();
            State = PluginState.Error;
            WriteDiagnostic($"Hook installation failed: {ex}");
            throw;
        }
    }

    private void StopHooks()
    {
        _mouseHook?.Dispose();
        _keyboardHook?.Dispose();
        _mouseHook = null;
        _keyboardHook = null;
        _forwardingLeftDrag = false;
    }

    private void OnMouseEvent(MouseEventData mouseEvent)
    {
        var hookEventNumber = Interlocked.Increment(ref _mouseHookEventCount);
        var buttonEventNumber = mouseEvent.Action == MouseAction.Move
            ? 0
            : Interlocked.Increment(ref _mouseButtonEventCount);
        var shouldLog = ShouldLogInputEvent(buttonEventNumber);
        if (_connection == null || !_connection.IsConnected)
        {
            if (shouldLog)
                WriteDiagnostic($"Mouse #{hookEventNumber} ignored: no active connection, action={mouseEvent.Action}, button={mouseEvent.Button}");
            return;
        }
        if (_mirrorWindowHandle == IntPtr.Zero)
        {
            if (shouldLog)
                WriteDiagnostic($"Mouse #{hookEventNumber} ignored: window handle is zero");
            return;
        }

        bool isOverVideo = IsCursorOverInputBounds(out var cursor, out var bounds);
        if (shouldLog)
        {
            WriteDiagnostic(
                $"Mouse #{hookEventNumber}: action={mouseEvent.Action}, button={mouseEvent.Button}, " +
                $"cursor={FormatPoint(cursor)}, bounds={FormatBounds(bounds)}, inside={isOverVideo}");
        }
        if (mouseEvent.Button == 0 && mouseEvent.Action == MouseAction.Down)
        {
            if (!isOverVideo) return;
            _forwardingLeftDrag = true;
        }
        else if (mouseEvent.Action == MouseAction.Move)
        {
            if (!_forwardingLeftDrag) return;
        }
        else if (mouseEvent.Button == 0 && mouseEvent.Action == MouseAction.Up)
        {
            if (!_forwardingLeftDrag) return;
            _forwardingLeftDrag = false;
        }
        else if (!isOverVideo)
        {
            return;
        }

        // 钩子回调运行在 UI 消息循环中，只做坐标计算和入队，绝不等待网络。
        var (nx, ny) = GetNormalizedCoords();
        var evt = mouseEvent with { X = nx, Y = ny };
        var data = ControlProtocolCodec.EncodeMouseEvent(evt);
        if (evt.Action == MouseAction.Move)
            QueueCoalesced(OutboundInputKind.MouseMove, data);
        else
            QueueData(data);
    }

    private void OnScrollEvent(ScrollEventData scrollEvent)
    {
        if (_connection == null || !_connection.IsConnected) return;
        if (!IsCursorOverInputBounds(out var cursor, out var bounds))
        {
            WriteDiagnostic($"Scroll ignored: cursor={FormatPoint(cursor)}, bounds={FormatBounds(bounds)}");
            return;
        }

        var (nx, ny) = GetNormalizedCoords();
        var evt = scrollEvent with { X = nx, Y = ny };
        QueueCoalesced(OutboundInputKind.Scroll, ControlProtocolCodec.EncodeScrollEvent(evt));
    }

    private void OnKeyEvent(KeyEventData keyEvent)
    {
        if (_connection == null || !_connection.IsConnected) return;
        if (_mirrorWindowHandle == IntPtr.Zero) return;
        if (GetForegroundWindow() != _mirrorWindowHandle) return;

        QueueData(ControlProtocolCodec.EncodeKeyEvent(keyEvent));
    }

    private void StartSender()
    {
        if (_senderTask is { IsCompleted: false }) return;

        _senderCts?.Dispose();
        _senderCts = new CancellationTokenSource();
        _outboundInputs = Channel.CreateUnbounded<OutboundInput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        var reader = _outboundInputs.Reader;
        var cancellationToken = _senderCts.Token;
        _senderTask = Task.Run(() => SendLoopAsync(reader, cancellationToken));
        WriteDiagnostic("Outbound sender started");
    }

    private async Task StopSenderAsync()
    {
        var channel = _outboundInputs;
        var cts = _senderCts;
        var senderTask = _senderTask;

        _outboundInputs = null;
        _senderCts = null;
        _senderTask = null;

        lock (_coalescingLock)
        {
            _pendingMouseMove = null;
            _pendingScroll = null;
            _mouseMoveQueued = false;
            _scrollQueued = false;
        }

        channel?.Writer.TryComplete();
        cts?.Cancel();
        if (senderTask != null)
        {
            try { await senderTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
    }

    private void QueueData(byte[] data)
    {
        var queued = _outboundInputs?.Writer.TryWrite(new OutboundInput(OutboundInputKind.Data, data)) == true;
        if (!queued)
            WriteDiagnostic($"Input packet queue failed: bytes={data.Length}");
    }

    private void QueueCoalesced(OutboundInputKind kind, byte[] data)
    {
        var channel = _outboundInputs;
        if (channel == null) return;

        lock (_coalescingLock)
        {
            ref byte[]? pending = ref (kind == OutboundInputKind.MouseMove
                ? ref _pendingMouseMove
                : ref _pendingScroll);
            ref bool queued = ref (kind == OutboundInputKind.MouseMove
                ? ref _mouseMoveQueued
                : ref _scrollQueued);

            pending = data;
            if (queued) return;
            queued = channel.Writer.TryWrite(new OutboundInput(kind));
        }
    }

    private async Task SendLoopAsync(
        ChannelReader<OutboundInput> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            byte[]? data = item.Data;
            if (item.Kind != OutboundInputKind.Data)
            {
                lock (_coalescingLock)
                {
                    if (item.Kind == OutboundInputKind.MouseMove)
                    {
                        data = _pendingMouseMove;
                        _pendingMouseMove = null;
                        _mouseMoveQueued = false;
                    }
                    else
                    {
                        data = _pendingScroll;
                        _pendingScroll = null;
                        _scrollQueued = false;
                    }
                }
            }

            var connection = _connection;
            if (data == null || connection == null || !connection.IsConnected) continue;

            try
            {
                await connection.SendAsync(data, cancellationToken).ConfigureAwait(false);
                var sentEventNumber = Interlocked.Increment(ref _sentEventCount);
                if (sentEventNumber <= 20 || sentEventNumber % 200 == 0)
                    WriteDiagnostic($"Input packet sent #{sentEventNumber}: kind={item.Kind}, bytes={data.Length}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ReverseControl] input send failed: {ex}");
                WriteDiagnostic($"Input send failed: {ex}");
            }
        }
    }

    /// <summary>
    /// 将 Windows 屏幕坐标转换为 Android 归一化坐标
    /// </summary>
    public (float x, float y) WindowsToAndroid(float winX, float winY, float winWidth, float winHeight)
    {
        // 视频尺寸和 Android 显示尺寸均已采用当前方向，不能再依据 Rotation 二次旋转。
        float normX = Math.Clamp(winX / winWidth, 0f, 1f);
        float normY = Math.Clamp(winY / winHeight, 0f, 1f);
        return (normX, normY);
    }

    // ===== 窗口命中检测与坐标换算 (Win32) =====

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// 判断当前鼠标是否位于投屏窗口之上（或该窗口为前台窗口）。
    /// 反向控制仅在用户与投屏窗口交互时才生效，避免误控 PC 自身。
    /// </summary>
    private bool IsCursorOverInputBounds(
        out (int X, int Y)? cursor,
        out (float Left, float Top, float Width, float Height)? bounds)
    {
        cursor = null;
        bounds = null;
        if (!GetCursorPos(out var pt)) return false;
        cursor = (pt.X, pt.Y);
        bounds = GetInputBounds();
        return bounds.HasValue &&
               pt.X >= bounds.Value.Left && pt.X <= bounds.Value.Left + bounds.Value.Width &&
               pt.Y >= bounds.Value.Top && pt.Y <= bounds.Value.Top + bounds.Value.Height;
    }

    /// <summary>
    /// 将当前鼠标屏幕坐标换算为相对实际视频画面的归一化坐标 (0~1)
    /// </summary>
    private (float x, float y) GetNormalizedCoords()
    {
        if (!GetCursorPos(out var pt)) return (0f, 0f);
        var bounds = GetInputBounds();
        if (!bounds.HasValue) return (0f, 0f);

        return WindowsToAndroid(
            pt.X - bounds.Value.Left,
            pt.Y - bounds.Value.Top,
            bounds.Value.Width,
            bounds.Value.Height);
    }

    private (float Left, float Top, float Width, float Height)? GetInputBounds()
    {
        (float Left, float Top, float Width, float Height)? provided = null;
        try { provided = InputBoundsProvider?.Invoke(); } catch { }
        if (provided.HasValue && provided.Value.Width > 0 && provided.Value.Height > 0)
            return FitVideoBounds(provided.Value);

        if (!GetWindowRect(_mirrorWindowHandle, out var rect)) return null;
        float width = rect.Right - rect.Left;
        float height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;
        return FitVideoBounds((rect.Left, rect.Top, width, height));
    }

    private static string FormatPoint((int X, int Y)? point) =>
        point.HasValue ? $"({point.Value.X},{point.Value.Y})" : "unavailable";

    private static string FormatBounds((float Left, float Top, float Width, float Height)? bounds) =>
        bounds.HasValue
            ? $"({bounds.Value.Left:F0},{bounds.Value.Top:F0},{bounds.Value.Width:F0},{bounds.Value.Height:F0})"
            : "unavailable";

    private static bool ShouldLogInputEvent(long eventNumber) =>
        eventNumber > 0 && (eventNumber <= 20 || eventNumber % 200 == 0);

    private static void WriteDiagnostic(string message)
    {
        try
        {
            lock (DiagnosticLogLock)
            {
                File.AppendAllText(
                    DiagnosticLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private (float Left, float Top, float Width, float Height) FitVideoBounds(
        (float Left, float Top, float Width, float Height) bounds)
    {
        if (_screenInfo.Width <= 0 || _screenInfo.Height <= 0) return bounds;

        float videoAspect = (float)_screenInfo.Width / _screenInfo.Height;
        float hostAspect = bounds.Width / bounds.Height;
        if (hostAspect > videoAspect)
        {
            float width = bounds.Height * videoAspect;
            return (bounds.Left + (bounds.Width - width) / 2f, bounds.Top, width, bounds.Height);
        }

        float height = bounds.Width / videoAspect;
        return (bounds.Left, bounds.Top + (bounds.Height - height) / 2f, bounds.Width, height);
    }
}

/// <summary>
/// 低级别鼠标钩子
/// </summary>
public class LowLevelMouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly Win32HookProc _proc;
    private readonly Action<MouseEventData> _callback;
    private readonly Action<ScrollEventData> _scrollCallback;
    private bool _leftButtonDown;

    public IntPtr HookHandle => _hookId;

    public LowLevelMouseHook(
        Action<MouseEventData> callback,
        Action<ScrollEventData> scrollCallback)
    {
        _callback = callback;
        _scrollCallback = scrollCallback;
        _proc = HookCallback;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null!), 0);
        if (_hookId == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install low-level mouse hook");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            MouseEventData? evt = null;

            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN:
                    evt = new MouseEventData { Action = MouseAction.Down, Button = 0 };
                    _leftButtonDown = true;
                    break;
                case WM_LBUTTONUP:
                    evt = new MouseEventData { Action = MouseAction.Up, Button = 0 };
                    _leftButtonDown = false;
                    break;
                case WM_RBUTTONDOWN:
                    evt = new MouseEventData { Action = MouseAction.Down, Button = 2 };
                    break;
                case WM_RBUTTONUP:
                    evt = new MouseEventData { Action = MouseAction.Up, Button = 2 };
                    break;
                case WM_MOUSEMOVE:
                    if (_leftButtonDown)
                        evt = new MouseEventData { Action = MouseAction.Move, Button = 0 };
                    break;
                case WM_MOUSEWHEEL:
                    var hookData = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var delta = unchecked((short)(hookData.MouseData >> 16));
                    _scrollCallback(new ScrollEventData { VScroll = delta / 120f });
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (evt.HasValue)
            {
                _callback(evt.Value);
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // P/Invoke
    private delegate IntPtr Win32HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, Win32HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

/// <summary>
/// 低级别键盘钩子
/// </summary>
public class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly Win32HookProc _proc;
    private readonly Action<KeyEventData> _callback;

    public IntPtr HookHandle => _hookId;

    public LowLevelKeyboardHook(Action<KeyEventData> callback)
    {
        _callback = callback;
        _proc = HookCallback;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null!), 0);
        if (_hookId == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install low-level keyboard hook");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var keyCode = Marshal.ReadInt32(lParam);

            var action = (int)wParam switch
            {
                WM_KEYDOWN or WM_SYSKEYDOWN => KeyAction.Down,
                WM_KEYUP or WM_SYSKEYUP => KeyAction.Up,
                _ => (KeyAction?)null,
            };

            if (action.HasValue)
            {
                var evt = new KeyEventData
                {
                    Action = action.Value,
                    KeyCode = WindowsKeyCodeToAndroid(keyCode),
                    EventTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                _callback(evt);
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// 将 Windows 虚拟键码转换为 Android KeyEvent 键码
    /// </summary>
    private static int WindowsKeyCodeToAndroid(int winKeyCode)
    {
        // 常用映射
        return winKeyCode switch
        {
            // 字母键 — 与 Android 基本一致
            >= 0x41 and <= 0x5A => 29 + (winKeyCode - 0x41), // A-Z
            >= 0x30 and <= 0x39 => 7 + (winKeyCode - 0x30),  // 0-9

            0x08 => 67,  // Backspace
            0x0D => 66,  // Enter
            0x20 => 62,  // Space
            0x1B => 4,   // Escape -> Android Back
            0x09 => 61,  // Tab
            0x25 => 21,  // Left
            0x26 => 19,  // Up
            0x27 => 22,  // Right
            0x28 => 20,  // Down
            0x2E => 112, // Delete
            0x24 => 3,   // Home -> Android Home
            0x23 => 93,  // End
            0x21 => 122, // Page Up
            0x22 => 123, // Page Down
            0x70 => 131, // F1
            0x71 => 132, // F2
            0x72 => 133, // F3
            0x73 => 134, // F4
            0x74 => 135, // F5
            0x75 => 136, // F6
            0x76 => 137, // F7
            0x77 => 138, // F8
            0x78 => 139, // F9
            0x79 => 140, // F10
            0x7A => 141, // F11
            0x7B => 142, // F12
            0xA0 => 59,  // Left Shift
            0xA2 => 113, // Left Ctrl
            0xA4 => 57,  // Left Alt
            _ => winKeyCode, // 未知键直接传原始值
        };
    }

    private delegate IntPtr Win32HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, Win32HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
