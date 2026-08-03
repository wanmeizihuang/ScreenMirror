namespace ScreenMirror.Protocol;

/// <summary>
/// 控制协议消息类型
/// </summary>
public enum ControlMessageType : byte
{
    TouchEvent = 0x01,
    KeyEvent = 0x02,
    ScreenInfo = 0x03,
    DeviceHello = 0x04,
    Heartbeat = 0x05,
    HeartbeatAck = 0x06,
    StreamStart = 0x07,
    StreamStop = 0x08,
    StreamPause = 0x09,
    StreamResume = 0x0A,
    MouseEvent = 0x0B,
    ScrollEvent = 0x0C,
    RotationChange = 0x0D,
    Error = 0x0E,

    // Phase 2
    ClipboardSync = 0x20,
    FileTransfer = 0x21,
    VideoFrame = 0x22,  // H.264 NAL unit raw data
}

/// <summary>
/// 设备类型
/// </summary>
public enum DeviceType : byte
{
    Unknown = 0,
    Android = 1,
    iOS = 2,
    HarmonyOS = 3,
    Windows = 4,
    Mac = 5,
    TV = 6,
}

/// <summary>
/// 视频编码类型
/// </summary>
public enum VideoCodecType : byte
{
    H264 = 0,
    H265 = 1,
}

/// <summary>
/// 音频编码类型
/// </summary>
public enum AudioCodecType : byte
{
    AAC = 0,
    Opus = 1,
}

/// <summary>
/// 触摸动作
/// </summary>
public enum TouchAction : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

/// <summary>
/// 按键动作
/// </summary>
public enum KeyAction : byte
{
    Down = 0,
    Up = 1,
}

/// <summary>
/// 鼠标动作
/// </summary>
public enum MouseAction : byte
{
    Move = 0,
    Down = 1,
    Up = 2,
}
