using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenMirror.Protocol;
using ScreenMirror.Server.Plugins;

namespace ScreenMirror.Server.Modules.ReverseControl;

/// <summary>
/// 反向控制插件 — 捕获 Windows 鼠标/键盘，发送到 Android 设备
/// </summary>
public class ReverseControlPlugin : IPlugin
{
    private ControlConnection? _connection;
    private IntPtr _mirrorWindowHandle;
    private ScreenInfoData _screenInfo;

    // 低级别鼠标/键盘钩子
    private LowLevelMouseHook? _mouseHook;
    private LowLevelKeyboardHook? _keyboardHook;
    private bool _isActive;

    public string Name => "ReverseControl";
    public string Version => "1.0.0";
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

    public Task<bool> Initialize(ControlConnection connection)
    {
        _connection = connection;
        State = PluginState.Initialized;
        return Task.FromResult(true);
    }

    public Task Start()
    {
        // 低级别钩子必须在具有消息循环的线程（UI 线程）上安装，
        // 否则系统不会回调钩子过程
        if (System.Windows.Application.Current?.Dispatcher != null)
            System.Windows.Application.Current.Dispatcher.Invoke(StartHooks);
        else
            StartHooks();

        _isActive = true;
        State = PluginState.Running;
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        StopHooks();
        _isActive = false;
        State = PluginState.Initialized;
        return Task.CompletedTask;
    }

    public PluginCapability[] GetCapabilities() => new[]
    {
        new PluginCapability
        {
            Name = "reverse_control",
            Description = "鼠标键盘反向控制 Android 设备",
            Version = "1.0.0",
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
        _mouseHook = new LowLevelMouseHook(OnMouseEvent);
        _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent);
    }

    private void StopHooks()
    {
        _mouseHook?.Dispose();
        _keyboardHook?.Dispose();
        _mouseHook = null;
        _keyboardHook = null;
    }

    private async void OnMouseEvent(MouseEventData mouseEvent)
    {
        if (_connection == null || !_connection.IsConnected) return;
        if (_mirrorWindowHandle == IntPtr.Zero) return;
        if (!IsCursorOverMirrorWindow()) return;   // 仅当鼠标位于投屏窗口上时才转发

        try
        {
            // 计算相对投屏窗口的归一化坐标（修正原 Down/Up 坐标恒为 0 的 bug）
            var (nx, ny) = GetNormalizedCoords();
            var evt = mouseEvent with { X = nx, Y = ny };
            await _connection.SendAsync(ControlProtocolCodec.EncodeMouseEvent(evt));
        }
        catch { }
    }

    private async void OnKeyEvent(KeyEventData keyEvent)
    {
        if (_connection == null || !_connection.IsConnected) return;
        if (_mirrorWindowHandle == IntPtr.Zero) return;
        if (!IsCursorOverMirrorWindow()) return;

        try
        {
            await _connection.SendAsync(ControlProtocolCodec.EncodeKeyEvent(keyEvent));
        }
        catch { }
    }

    /// <summary>
    /// 将 Windows 屏幕坐标转换为 Android 归一化坐标
    /// </summary>
    public (float x, float y) WindowsToAndroid(float winX, float winY, float winWidth, float winHeight)
    {
        // 窗口坐标系 — 需要确保缩放比例正确
        float normX = Math.Clamp(winX / winWidth, 0f, 1f);
        float normY = Math.Clamp(winY / winHeight, 0f, 1f);

        // 考虑屏幕旋转
        return _screenInfo.Rotation switch
        {
            90 => (normY, 1f - normX),
            180 => (1f - normX, 1f - normY),
            270 => (1f - normY, normX),
            _ => (normX, normY),
        };
    }

    // ===== 窗口命中检测与坐标换算 (Win32) =====

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// 判断当前鼠标是否位于投屏窗口之上（或该窗口为前台窗口）。
    /// 反向控制仅在用户与投屏窗口交互时才生效，避免误控 PC 自身。
    /// </summary>
    private bool IsCursorOverMirrorWindow()
    {
        if (_mirrorWindowHandle == IntPtr.Zero) return false;
        if (GetForegroundWindow() == _mirrorWindowHandle) return true;
        if (GetCursorPos(out var pt))
            return WindowFromPoint(pt) == _mirrorWindowHandle;
        return false;
    }

    /// <summary>
    /// 将当前鼠标屏幕坐标换算为相对投屏窗口的归一化坐标 (0~1，含旋转修正)
    /// </summary>
    private (float x, float y) GetNormalizedCoords()
    {
        if (!GetCursorPos(out var pt)) return (0f, 0f);
        if (!GetWindowRect(_mirrorWindowHandle, out var rect)) return (0f, 0f);

        float winX = pt.X - rect.Left;
        float winY = pt.Y - rect.Top;
        float winW = rect.Right - rect.Left;
        float winH = rect.Bottom - rect.Top;
        if (winW <= 0 || winH <= 0) return (0f, 0f);

        return WindowsToAndroid(winX, winY, winW, winH);
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

    public LowLevelMouseHook(Action<MouseEventData> callback)
    {
        _callback = callback;
        _proc = HookCallback;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null!), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var evt = new MouseEventData();

            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN:
                    evt = new MouseEventData { Action = MouseAction.Down, Button = 0 };
                    break;
                case WM_LBUTTONUP:
                    evt = new MouseEventData { Action = MouseAction.Up, Button = 0 };
                    break;
                case WM_RBUTTONDOWN:
                    evt = new MouseEventData { Action = MouseAction.Down, Button = 2 };
                    break;
                case WM_RBUTTONUP:
                    evt = new MouseEventData { Action = MouseAction.Up, Button = 2 };
                    break;
                case WM_MOUSEMOVE:
                    evt = new MouseEventData { Action = MouseAction.Move };
                    break;
                case WM_MOUSEWHEEL:
                    // 滚轮通过 OnScrollEvent 单独处理
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (evt.Action == MouseAction.Move)
            {
                // 获取鼠标屏幕坐标
                GetCursorPos(out var pt);
                // 坐标在 WPF 窗口回调中转换
                evt = evt with { X = pt.X, Y = pt.Y };
            }

            _callback(evt);
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

    public LowLevelKeyboardHook(Action<KeyEventData> callback)
    {
        _callback = callback;
        _proc = HookCallback;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null!), 0);
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
            0x1B => 111, // Escape
            0x09 => 61,  // Tab
            0x25 => 21,  // Left
            0x26 => 19,  // Up
            0x27 => 22,  // Right
            0x28 => 20,  // Down
            0x2E => 112, // Delete
            0x24 => 92,  // Home
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
