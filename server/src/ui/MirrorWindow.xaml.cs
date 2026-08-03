using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ScreenMirror.Protocol;
using ScreenMirror.Server.Modules.VlcDecoder;

namespace ScreenMirror.Server.UI;

public partial class MirrorWindow : Window
{
    private readonly ControlConnection _connection;
    private VlcDecoder? _vlc;
    private bool _isClosing;
    private bool _isPinned;
    private int _nalCount;

    public MirrorWindow(ControlConnection connection)
    {
        InitializeComponent();

        _connection = connection;
        DeviceInfo.Text = connection.RemoteHost;
        TitleText.Text = $"ScreenMirror — {connection.RemoteHost}";

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vlc = new VlcDecoder();
        if (!_vlc.Initialize(VlcVideoView))
        {
            StatusInfo.Text = "VLC 初始化失败";
            StatusInfo.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B));
            return;
        }

        _connection.OnMessageReceived += OnMessageReceived;

        if (_vlc.Start())
        {
            StatusInfo.Text = "已连接 | VLC 解码";
            StatusInfo.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        long lastNalCount = 0;
        int stallCount = 0;
        timer.Tick += (_, _) =>
        {
            if (_vlc != null)
            {
                long nals = _vlc.NalCount;
                FpsText.Text = $"帧数: {nals}";
                if (nals > 0)
                    LoadingText.Visibility = Visibility.Collapsed;

                if (nals == lastNalCount && _nalCount > 10)
                {
                    stallCount++;
                    if (stallCount >= 3)
                    {
                        long s = (Environment.TickCount64 - _vlc.LastNalTime) / 1000;
                        StatusInfo.Text = $"信号丢失 ({s}s)";
                        StatusInfo.Foreground = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B));

                        if (stallCount == 6)
                        {
                            try { _vlc.Start(); } catch { }
                        }
                    }
                }
                else
                {
                    stallCount = 0;
                    if (nals > 0)
                    {
                        StatusInfo.Text = "已连接 | VLC 解码";
                        StatusInfo.Foreground = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
                    }
                }
                lastNalCount = nals;
            }
        };
        timer.Start();
    }

    private void OnMessageReceived(ControlMessageType type, ReadOnlyMemory<byte> payload)
    {
        if (type == ControlMessageType.VideoFrame && _vlc != null)
        {
            _vlc.WriteNal(payload.ToArray());
            Interlocked.Increment(ref _nalCount);
        }
    }

    // ===== 窗口事件 =====
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // VLC VideoView 自动跟随 Grid 大小，无需手动调整
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        PinButton.Content = _isPinned ? "📍 已置顶" : "📌";
        PinButton.Foreground = _isPinned
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA));
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
        Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        await DisconnectAsync();
    }

    private Task DisconnectAsync()
    {
        _connection.OnMessageReceived -= OnMessageReceived;
        _connection.Disconnect();

        if (_vlc != null)
        {
            var vlc = _vlc;
            _vlc = null;
            // 在后台线程释放 VLC，避免 UI 线程死锁
            Task.Run(() => { try { vlc.Dispose(); } catch { } });
        }

        return Task.CompletedTask;
    }

    private void AppendLog(string text) { } // 已移除调试日志，保留空方法避免编译错误
}
