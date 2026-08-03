namespace ScreenMirror.Protocol;

/// <summary>
/// 触摸事件
/// </summary>
public readonly struct TouchEventData
{
    public TouchAction Action { get; init; }
    public int PointerId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Pressure { get; init; }
    public long EventTime { get; init; }
}

/// <summary>
/// 按键事件
/// </summary>
public readonly struct KeyEventData
{
    public KeyAction Action { get; init; }
    public int KeyCode { get; init; }
    public int MetaState { get; init; }
    public long EventTime { get; init; }
}

/// <summary>
/// 屏幕参数
/// </summary>
public readonly struct ScreenInfoData
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Dpi { get; init; }
    public int FrameRate { get; init; }
    public VideoCodecType Codec { get; init; }
    public int MaxBitrateKbps { get; init; }
    public bool HasAudio { get; init; }
    public AudioCodecType AudioCodec { get; init; }
    public int Rotation { get; init; }
}

/// <summary>
/// 设备握手
/// </summary>
public readonly struct DeviceHelloData
{
    public int ProtocolVersion { get; init; }
    public DeviceType DeviceType { get; init; }
    public string DeviceName { get; init; }
}

/// <summary>
/// 鼠标事件
/// </summary>
public readonly struct MouseEventData
{
    public MouseAction Action { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public int Button { get; init; }
}

/// <summary>
/// 滚轮事件
/// </summary>
public readonly struct ScrollEventData
{
    public float X { get; init; }
    public float Y { get; init; }
    public float HScroll { get; init; }
    public float VScroll { get; init; }
}

/// <summary>
/// 错误信息
/// </summary>
public readonly struct ErrorData
{
    public int ErrorCode { get; init; }
    public string Message { get; init; }
}
