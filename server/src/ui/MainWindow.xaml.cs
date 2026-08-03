using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using ScreenMirror.Server.Core;
using ScreenMirror.Server.Modules.ScreenMirror;
using ScreenMirror.Server.Modules.ReverseControl;
using ScreenMirror.Server.Plugins;
using ScreenMirror.Protocol;

namespace ScreenMirror.Server.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DiscoveryService _discovery;
    private readonly ConnectionManager _connectionManager;
    private readonly MessageRouter _router;
    private readonly PluginManager _pluginManager;
    private readonly ScreenMirrorPlugin _screenMirror;
    private readonly ReverseControlPlugin _reverseControl;

    private ControlConnection? _activeConnection;
    private MirrorWindow? _mirrorWindow;
    private bool _isLoading;

    public ObservableCollection<DiscoveredDevice> Devices { get; } = new();

    private string _statusText = "等待设备连接...";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _statusBackground = "#F5F5F5";
    public string StatusBackground
    {
        get => _statusBackground;
        set { _statusBackground = value; OnPropertyChanged(); }
    }

    private string _statusForeground = "#666666";
    public string StatusForeground
    {
        get => _statusForeground;
        set { _statusForeground = value; OnPropertyChanged(); }
    }

    public string DeviceCountText => $"已发现 {Devices.Count} 台设备";

    public Visibility IsLoading => _isLoading ? Visibility.Visible : Visibility.Collapsed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        // 全局未捕获异常处理：写到文件 + 弹窗
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            try
            {
                var logFile = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "screenmirror-crash.log");
                System.IO.File.AppendAllText(logFile,
                    $"[{DateTime.Now}] FATAL: {ex}\n\n");
            }
            catch { }
            System.Diagnostics.Debug.WriteLine($"FATAL: {ex}");
        };

        // 初始化核心服务
        _discovery = new DiscoveryService();
        _connectionManager = new ConnectionManager();
        _router = new MessageRouter();
        _pluginManager = new PluginManager(_router);

        // 初始化功能模块
        _screenMirror = new ScreenMirrorPlugin();
        _reverseControl = new ReverseControlPlugin();

        // 绑定事件
        _discovery.OnDeviceFound += OnDeviceFound;
        _discovery.OnDeviceLost += OnDeviceLost;
        _discovery.OnLog += msg =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText = msg;
            });
        };
        _connectionManager.OnConnectionAccepted += OnIncomingConnection;

        // 启动 TCP 监听（手机连接用）
        _ = _connectionManager.StartListeningAsync();

        // 启动设备发现
        _discovery.Start();
        _isLoading = true;
        OnPropertyChanged(nameof(IsLoading));

        // 1 秒后关闭加载
        _ = DelayedHideLoading();
    }

    private async Task DelayedHideLoading()
    {
        await Task.Delay(1500);
        _isLoading = false;
        OnPropertyChanged(nameof(IsLoading));
    }

    private void OnDeviceFound(DiscoveredDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            Devices.Add(device);
            OnPropertyChanged(nameof(DeviceCountText));
        });
    }

    private void OnDeviceLost(DiscoveredDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            Devices.Remove(device);
            OnPropertyChanged(nameof(DeviceCountText));
        });
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        OnPropertyChanged(nameof(IsLoading));
        Devices.Clear();

        await _discovery.ScanAsync();
        await DelayedHideLoading();
        OnPropertyChanged(nameof(DeviceCountText));
    }

    private void OnDeviceClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is DiscoveredDevice device)
        {
            _ = ConnectToDeviceAsync(device);
        }
    }

    private void OnDeviceConnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is DiscoveredDevice device)
        {
            _ = ConnectToDeviceAsync(device);
        }
    }

    private async Task ConnectToDeviceAsync(DiscoveredDevice device)
    {
        StatusText = $"正在连接 {device.DeviceName}...";
        StatusBackground = "#FFF3E0";
        StatusForeground = "#E65100";

        try
        {
            // VLC 方案：通过 WiFi TCP 连接手机，PC 端用 VLC 解码
            var connection = await _connectionManager.ConnectAsync(device.Host, device.Port);
            if (connection != null)
            {
                _activeConnection = connection;

                // 注册插件（发现/握手等仍走原有协议）
                await _pluginManager.RegisterPlugin(_screenMirror);
                await _pluginManager.RegisterPlugin(_reverseControl);
                await _pluginManager.InitializeAll(connection);

                // 发送握手
                await connection.SendDeviceHelloAsync(new DeviceHelloData
                {
                    ProtocolVersion = 1,
                    DeviceType = DeviceType.Windows,
                    DeviceName = Environment.MachineName,
                });

                _router.BindToConnection(connection);

                // 打开投屏窗口（VLC 渲染）
                Dispatcher.Invoke(() =>
                {
                    _mirrorWindow = new MirrorWindow(connection);
                    _mirrorWindow.Closed += (_, _) =>
                    {
                        DisconnectDevice();
                        Show();
                    };
                    // 反向控制：绑定投屏窗口句柄并启动输入钩子
                    _reverseControl.MirrorWindowHandle =
                        new System.Windows.Interop.WindowInteropHelper(_mirrorWindow).Handle;
                    _ = _pluginManager.StartAll();
                    _mirrorWindow.Show();
                    Hide();
                });

                StatusText = $"已连接: {device.DeviceName}";
                StatusBackground = "#E8F5E9";
                StatusForeground = "#2E7D32";
            }
            else
            {
                StatusText = $"连接失败: {device.DeviceName}";
                StatusBackground = "#FFEBEE";
                StatusForeground = "#C62828";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败: {ex.Message}";
            StatusBackground = "#FFEBEE";
            StatusForeground = "#C62828";
        }
    }

    /// <summary>
    /// 手机端主动连入时的处理
    /// </summary>
    private async void OnIncomingConnection(ScreenMirror.Protocol.ControlConnection connection)
    {
        if (_activeConnection != null) return; // 已连，忽略

        StatusText = "手机端已连接...";
        StatusBackground = "#FFF3E0";
        StatusForeground = "#E65100";

        try
        {
            _activeConnection = connection;
            connection.OnDisconnected += _ => DisconnectDevice();

            // 注册插件 + 初始化
            await _pluginManager.RegisterPlugin(_screenMirror);
            await _pluginManager.RegisterPlugin(_reverseControl);
            await _pluginManager.InitializeAll(connection);

            await connection.SendDeviceHelloAsync(new DeviceHelloData
            {
                ProtocolVersion = 1,
                DeviceType = DeviceType.Windows,
                DeviceName = Environment.MachineName,
            });

            _router.BindToConnection(connection);

            // 打开 VLC 投屏窗口
            Dispatcher.Invoke(() =>
            {
                _mirrorWindow = new MirrorWindow(connection);
                _mirrorWindow.Closed += (_, _) =>
                {
                    DisconnectDevice();
                    Show();
                };
                // 反向控制：绑定投屏窗口句柄并启动输入钩子
                _reverseControl.MirrorWindowHandle =
                    new System.Windows.Interop.WindowInteropHelper(_mirrorWindow).Handle;
                _ = _pluginManager.StartAll();
                _mirrorWindow.Show();
                Hide();
            });

            StatusText = "已连接: 手机端";
            StatusBackground = "#E8F5E9";
            StatusForeground = "#2E7D32";
        }
        catch (Exception ex)
        {
            StatusText = $"手机连接处理失败: {ex.Message}";
            StatusBackground = "#FFEBEE";
            StatusForeground = "#C62828";
        }
    }

    private bool _isDisconnecting;  // 防止重复断开导致无限递归

    private void DisconnectDevice()
    {
        if (_isDisconnecting) return;  // 已在断开中，跳过
        _isDisconnecting = true;

        try
        {
            // 先置 null，再 Disconnect，避免 OnDisconnected 回调再次进入此方法
            var conn = _activeConnection;
            _activeConnection = null;

            // 关闭投屏窗口 — 触发 VLC 解码器清理，防止 VLC 阻塞导致卡死
            // 使用 BeginInvoke（异步派发）避免 UI 线程死锁
            var win = _mirrorWindow;
            _mirrorWindow = null;
            if (win != null)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try { win.Close(); } catch { }
                });
            }

            conn?.Disconnect();
        }
        finally
        {
            _isDisconnecting = false;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try { _ = _pluginManager.StopAll(); } catch { }   // 卸载输入钩子
            this.Show();
            StatusText = "等待设备连接...";
            StatusBackground = "#F5F5F5";
            StatusForeground = "#666666";
        });
    }

    // 窗口操作
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _discovery.Dispose();
        _connectionManager.Dispose();
        _mirrorWindow?.Close();
        Application.Current.Shutdown();
    }

    // INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
