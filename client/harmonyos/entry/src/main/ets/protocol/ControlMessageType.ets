// ControlMessageType.ets — 消息类型枚举，与 protocol/kotlin/ControlMessageType.kt 对齐

export enum ControlMessageType {
  TOUCH_EVENT         = 0x01,
  KEY_EVENT           = 0x02,
  SCREEN_INFO         = 0x03,
  DEVICE_HELLO        = 0x04,
  HEARTBEAT           = 0x05,
  HEARTBEAT_ACK       = 0x06,
  STREAM_START        = 0x07,
  STREAM_STOP         = 0x08,
  STREAM_PAUSE        = 0x09,
  STREAM_RESUME       = 0x0A,
  MOUSE_EVENT         = 0x0B,
  SCROLL_EVENT        = 0x0C,
  ROTATION_CHANGE     = 0x0D,
  ERROR               = 0x0E,
  CLIPBOARD_SYNC      = 0x20,
  FILE_TRANSFER       = 0x21,
  VIDEO_FRAME         = 0x22,
}

export enum DeviceType {
  UNKNOWN   = 0,
  ANDROID   = 1,
  IOS       = 2,
  HARMONYOS = 3,
  WINDOWS   = 4,
  MAC       = 5,
  TV        = 6,
}

export enum VideoCodec {
  H264 = 0,
  H265 = 1,
}

export enum AudioCodec {
  AAC  = 0,
  OPUS = 1,
}

export enum TouchAction {
  DOWN   = 0,
  UP     = 1,
  MOVE   = 2,
  CANCEL = 3,
}

export enum KeyAction {
  DOWN = 0,
  UP   = 1,
}

export enum MouseAction {
  MOVE        = 0,
  LEFT_DOWN   = 1,
  LEFT_UP     = 2,
  RIGHT_DOWN  = 3,
  RIGHT_UP    = 4,
  MIDDLE_DOWN = 5,
  MIDDLE_UP   = 6,
}

export enum ErrorCode {
  UNKNOWN            = 0,
  PROTOCOL_ERROR     = 1,
  ENCODE_ERROR       = 2,
  DECODE_ERROR       = 3,
  CONNECTION_TIMEOUT = 4,
  DEVICE_NOT_FOUND   = 5,
  AUTH_FAILED        = 6,
}
