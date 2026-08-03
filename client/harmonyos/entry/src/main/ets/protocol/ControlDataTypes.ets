// ControlDataTypes.ets — 数据模型，与 Kotlin data class 对齐

import { TouchAction, KeyAction, MouseAction, DeviceType, VideoCodec, AudioCodec, ErrorCode } from './ControlMessageType'

// 触摸事件 16B
export interface TouchEventData {
  action: TouchAction      // 1B
  pointerId: number        // 1B
  x: number                // uint16, ×10000 归一化
  y: number                // uint16, ×10000
  pressure: number         // uint16, ×1000
  eventTime: bigint        // int64, 大端
}

// 按键事件 17B
export interface KeyEventData {
  action: KeyAction        // 1B
  keyCode: number          // int32
  metaState: number        // int32
  eventTime: bigint        // int64, 大端
}

// 屏幕信息 19B
export interface ScreenInfoData {
  width: number            // uint32
  height: number           // uint32
  dpi: number              // uint16
  frameRate: number        // 1B
  codec: VideoCodec        // 1B
  maxBitrate: number       // uint32
  hasAudio: number         // 1B (0/1)
  audioCodec: AudioCodec   // 1B
  rotation: number         // 1B (0/90/180/270)
}

// 设备问候 2+N 字节
export interface DeviceHelloData {
  protocolVersion: number  // 1B
  deviceType: DeviceType   // 1B
  deviceName: string       // UTF-8
}

// 心跳 8B
export interface HeartbeatData {
  timestamp: bigint        // int64, 大端
}

// 鼠标事件 6B
export interface MouseEventData {
  action: MouseAction      // 1B
  x: number                // uint16, ×10000
  y: number                // uint16, ×10000
  button: number           // 1B
}

// 错误消息 4+N 字节
export interface ErrorData {
  errorCode: ErrorCode     // int32
  message: string          // UTF-8
}

// 发现的设备
export interface DiscoveredDevice {
  host: string
  port: number
  deviceName: string
  deviceType: string
  version: number
  macSuffix: string
  hasAudio: boolean
}
