using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace ScreenMirror.Server.Modules.VlcDecoder;

/// <summary>
/// VLC H.264 解码器 — 使用 LibVLCSharp 从 NalStream 读取 H.264 裸流并解码渲染
///
/// 架构:
///   Android → WiFi TCP → ScreenMirrorPlugin → NalStream → VLC → 解码 → VideoView 渲染
///
/// 优势:
///   - VLC 内部硬解（DXVA2/D3D11VA），稳定运行 20+ 年
///   - 无需自己管理 FFmpeg/P/Invoke
///   - 解码+渲染一体化，无中间拷贝
///   - 低延迟配置（100ms 网络缓存）
/// </summary>
public class VlcDecoder : IDisposable
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private StreamMediaInput? _mediaInput;
    private NalStream? _nalStream;
    private bool _disposed;

    /// <summary>NAL 数据写入入口（ScreenMirrorPlugin 调用）</summary>
    public NalStream? Stream => _nalStream;

    /// <summary>当前是否已初始化</summary>
    public bool IsInitialized => _libVLC != null && _mediaPlayer != null;

    /// <summary>已写入的 NAL 数（来自 NalStream 统计）</summary>
    public long NalCount => _nalStream?.TotalNalsWritten ?? 0;

    /// <summary>最近一次写入 NAL 的时间（用于检测信号丢失）</summary>
    public long LastNalTime => _nalStream?.LastWriteTimeMs ?? 0;

    /// <summary>FPS 回调</summary>
    public event Action<int>? OnFpsUpdate;

    /// <summary>
    /// 初始化 VLC 引擎并开始播放
    /// </summary>
    /// <param name="videoView">WPF VideoView 控件（渲染目标）</param>
    public bool Initialize(VideoView videoView)
    {
        try
        {
            // 1. 初始化 LibVLC（低延迟配置）
            var coreOptions = new[]
            {
                "--network-caching=200",       // 200ms 缓存，吸收 TCP 抖动（默认 1000ms）
                "--clock-jitter=100",           // 时钟抖动容忍 100ms
                "--clock-synchro=1",            // 启用时钟同步
                "--no-interact",                // 禁用交互弹窗
                "--no-osd",                     // 禁用 OSD 显示
                "--no-stats",                   // 禁用统计输出
                "--no-video-title-show",        // 不在视频上显示标题
                "--avcodec-skiploopfilter=1",   // 跳过 H.264 deblock 滤镜（降延迟）
                "--demux=h264",                 // 强制 H.264 demuxer
                "--rtsp-tcp",                   // RTSP 走 TCP（备用）
                "--quiet",                      // 静默模式
                "--verbose=0",                  // 日志级别 0
            };

            _libVLC = new LibVLC(coreOptions);
            _mediaPlayer = new MediaPlayer(_libVLC);

            // 2. 绑定到 WPF VideoView
            videoView.MediaPlayer = _mediaPlayer;

            // 3. 创建 NAL 流
            _nalStream = new NalStream();

            // 4. 创建 StreamMediaInput（VLC 从这个流读取 H.264 数据）
            _mediaInput = new StreamMediaInput(_nalStream);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VlcDecoder] 初始化失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 开始播放（从 NalStream 读取 H.264 数据）
    /// </summary>
    public bool Start()
    {
        if (_mediaPlayer == null || _mediaInput == null) return false;

        try
        {
            // 使用 StreamMediaInput 播放
            using var media = new Media(_libVLC!, _mediaInput, ":demux=h264");
            _mediaPlayer.Play(media);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VlcDecoder] 启动播放失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>写入 NAL 数据（ScreenMirrorPlugin 调用）</summary>
    public void WriteNal(byte[] nalData)
    {
        _nalStream?.WriteNal(nalData);
    }

    /// <summary>停止播放</summary>
    public void Stop()
    {
        if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Stop();
        }
        _nalStream?.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        _mediaPlayer?.Dispose();
        _mediaInput?.Dispose();
        _nalStream?.Dispose();
        _libVLC?.Dispose();
    }
}
