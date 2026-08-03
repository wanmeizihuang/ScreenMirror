// ProtocolCodec.ets — 手写二进制编解码器
// 帧格式: [1B Type][4B 大端 PayloadLength][N B Payload]
// 与 protocol/kotlin/Protocol.kt 和 protocol/csharp/ControlProtocolCodec.cs 完全对齐

import { ControlMessageType, TouchAction, KeyAction, MouseAction, DeviceType, VideoCodec, AudioCodec, ErrorCode } from './ControlMessageType'
import type { TouchEventData, KeyEventData, ScreenInfoData, DeviceHelloData, HeartbeatData, MouseEventData, ErrorData } from './ControlDataTypes'
import { buffer } from '@kit.ArkTS'

export class ControlProtocolCodec {
  // ===== 常量 =====
  static readonly HEADER_SIZE: number = 5 // Type(1) + Length(4)
  static readonly MAX_PAYLOAD_SIZE: number = 4 * 1024 * 1024 // 4MB (容纳大I帧)
  static readonly PROTOCOL_VERSION: number = 1

  // ===== 工具方法 =====

  /** 写入 4 字节大端 uint32 */
  static writeUInt32BE(buf: ArrayBuffer, offset: number, value: number): void {
    new DataView(buf).setUint32(offset, value, false)
  }

  /** 读取 4 字节大端 uint32 */
  static readUInt32BE(buf: ArrayBuffer, offset: number): number {
    return new DataView(buf).getUint32(offset, false)
  }

  /** 写入 2 字节大端 uint16 */
  static writeUInt16BE(buf: ArrayBuffer, offset: number, value: number): void {
    new DataView(buf).setUint16(offset, value, false)
  }

  /** 读取 2 字节大端 uint16 */
  static readUInt16BE(buf: ArrayBuffer, offset: number): number {
    return new DataView(buf).getUint16(offset, false)
  }

  /** 写入 8 字节大端 int64 */
  static writeInt64BE(buf: ArrayBuffer, offset: number, value: bigint): void {
    new DataView(buf).setBigInt64(offset, value, false)
  }

  /** 读取 8 字节大端 int64 */
  static readInt64BE(buf: ArrayBuffer, offset: number): bigint {
    return new DataView(buf).getBigInt64(offset, false)
  }

  /** UTF-8 编码字符串到 Uint8Array */
  static encodeString(str: string): Uint8Array {
    const encoder = new util.TextEncoder()
    return new Uint8Array(encoder.encodeInto(str))
  }

  /** UTF-8 解码 Uint8Array 到字符串 */
  static decodeString(data: Uint8Array): string {
    const decoder = new util.TextDecoder('utf-8')
    return decoder.decodeWithStream(data, { stream: false })
  }

  // ===== 帧封装/解封 =====

  /** 编码完整帧: [Type][Len4][Payload] */
  static encodeFrame(type: ControlMessageType, payload: Uint8Array): ArrayBuffer {
    const totalLen = ControlProtocolCodec.HEADER_SIZE + payload.length
    const buf = new ArrayBuffer(totalLen)
    const view = new Uint8Array(buf)
    view[0] = type
    ControlProtocolCodec.writeUInt32BE(buf, 1, payload.length)
    view.set(payload, ControlProtocolCodec.HEADER_SIZE)
    return buf
  }

  /** 解码帧头，返回 {type, payloadLength}，null 表示不足 5 字节 */
  static decodeHeader(headerData: Uint8Array): { type: ControlMessageType; payloadLength: number } | null {
    if (headerData.length < ControlProtocolCodec.HEADER_SIZE) return null
    return {
      type: headerData[0] as ControlMessageType,
      payloadLength: ControlProtocolCodec.readUInt32BE(headerData.buffer.slice(headerData.byteOffset, headerData.byteOffset + 4), 0)
    }
  }

  // ===== TOUCH_EVENT (0x01) 16B =====
  static encodeTouchEvent(data: TouchEventData): Uint8Array {
    const buf = new ArrayBuffer(16)
    const v = new DataView(buf)
    v.setUint8(0, data.action)
    v.setUint8(1, data.pointerId)
    v.setUint16(2, data.x, false)
    v.setUint16(4, data.y, false)
    v.setUint16(6, data.pressure, false)
    v.setBigInt64(8, data.eventTime, false)
    return new Uint8Array(buf)
  }

  static decodeTouchEvent(payload: Uint8Array): TouchEventData {
    const v = new DataView(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.length))
    return {
      action: v.getUint8(0) as TouchAction,
      pointerId: v.getUint8(1),
      x: v.getUint16(2, false),
      y: v.getUint16(4, false),
      pressure: v.getUint16(6, false),
      eventTime: v.getBigInt64(8, false)
    }
  }

  // ===== KEY_EVENT (0x02) 17B =====
  static encodeKeyEvent(data: KeyEventData): Uint8Array {
    const buf = new ArrayBuffer(17)
    const v = new DataView(buf)
    v.setUint8(0, data.action)
    v.setInt32(1, data.keyCode, false)
    v.setInt32(5, data.metaState, false)
    v.setBigInt64(9, data.eventTime, false)
    return new Uint8Array(buf)
  }

  static decodeKeyEvent(payload: Uint8Array): KeyEventData {
    const v = new DataView(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.length))
    return {
      action: v.getUint8(0) as KeyAction,
      keyCode: v.getInt32(1, false),
      metaState: v.getInt32(5, false),
      eventTime: v.getBigInt64(9, false)
    }
  }

  // ===== SCREEN_INFO (0x03) 19B =====
  static encodeScreenInfo(data: ScreenInfoData): Uint8Array {
    const buf = new ArrayBuffer(19)
    const v = new DataView(buf)
    v.setUint32(0, data.width, false)
    v.setUint32(4, data.height, false)
    v.setUint16(8, data.dpi, false)
    v.setUint8(10, data.frameRate)
    v.setUint8(11, data.codec)
    v.setUint32(12, data.maxBitrate, false)
    v.setUint8(16, data.hasAudio)
    v.setUint8(17, data.audioCodec)
    v.setUint8(18, data.rotation)
    return new Uint8Array(buf)
  }

  static decodeScreenInfo(payload: Uint8Array): ScreenInfoData {
    const v = new DataView(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.length))
    return {
      width: v.getUint32(0, false),
      height: v.getUint32(4, false),
      dpi: v.getUint16(8, false),
      frameRate: v.getUint8(10),
      codec: v.getUint8(11) as VideoCodec,
      maxBitrate: v.getUint32(12, false),
      hasAudio: v.getUint8(16),
      audioCodec: v.getUint8(17) as AudioCodec,
      rotation: v.getUint8(18)
    }
  }

  // ===== DEVICE_HELLO (0x04) 2+N =====
  static encodeDeviceHello(data: DeviceHelloData): Uint8Array {
    const nameBytes = ControlProtocolCodec.encodeString(data.deviceName)
    const buf = new ArrayBuffer(2 + nameBytes.length)
    const v = new Uint8Array(buf)
    v[0] = data.protocolVersion
    v[1] = data.deviceType
    v.set(nameBytes, 2)
    return v
  }

  static decodeDeviceHello(payload: Uint8Array): DeviceHelloData {
    const nameBytes = payload.slice(2)
    return {
      protocolVersion: payload[0],
      deviceType: payload[1] as DeviceType,
      deviceName: ControlProtocolCodec.decodeString(nameBytes)
    }
  }

  // ===== HEARTBEAT/HEARTBEAT_ACK (0x05/0x06) 8B =====
  static encodeHeartbeat(timestamp: bigint): Uint8Array {
    const buf = new ArrayBuffer(8)
    new DataView(buf).setBigInt64(0, timestamp, false)
    return new Uint8Array(buf)
  }

  static decodeHeartbeat(payload: Uint8Array): bigint {
    return new DataView(payload.buffer.slice(payload.byteOffset, payload.byteOffset + 8)).getBigInt64(0, false)
  }

  // ===== MOUSE_EVENT (0x0B) 6B =====
  static encodeMouseEvent(data: MouseEventData): Uint8Array {
    const buf = new ArrayBuffer(6)
    const v = new DataView(buf)
    v.setUint8(0, data.action)
    v.setUint16(1, data.x, false)
    v.setUint16(3, data.y, false)
    v.setUint8(5, data.button)
    return new Uint8Array(buf)
  }

  static decodeMouseEvent(payload: Uint8Array): MouseEventData {
    const v = new DataView(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.length))
    return {
      action: v.getUint8(0) as MouseAction,
      x: v.getUint16(1, false),
      y: v.getUint16(3, false),
      button: v.getUint8(5)
    }
  }

  // ===== ERROR (0x0E) 4+N =====
  static encodeError(data: ErrorData): Uint8Array {
    const msgBytes = ControlProtocolCodec.encodeString(data.message)
    const buf = new ArrayBuffer(4 + msgBytes.length)
    const v = new DataView(buf)
    v.setInt32(0, data.errorCode, false)
    new Uint8Array(buf).set(msgBytes, 4)
    return new Uint8Array(buf)
  }

  static decodeError(payload: Uint8Array): ErrorData {
    const v = new DataView(payload.buffer.slice(payload.byteOffset, payload.byteOffset + 4))
    const msgBytes = payload.slice(4)
    return {
      errorCode: v.getInt32(0, false) as ErrorCode,
      message: ControlProtocolCodec.decodeString(msgBytes)
    }
  }

  // ===== 便捷发送封装 =====

  static buildFrame(type: ControlMessageType, payload: Uint8Array): ArrayBuffer {
    return ControlProtocolCodec.encodeFrame(type, payload)
  }
}
