// ControlConnection.ets — TCP 连接读写与分包/粘包处理
// HarmonyOS socket API 是事件驱动的，通过 on('message') 回调累积+解析帧

import { ControlMessageType } from './ControlMessageType'
import { ControlProtocolCodec } from './ProtocolCodec'
import { socket } from '@kit.NetworkKit'
import { BusinessError } from '@kit.BasicServicesKit'
import { util } from '@kit.ArkTS'

export type MessageHandler = (type: ControlMessageType, payload: Uint8Array) => void
export type DisconnectHandler = () => void

export class ControlConnection {
  private tcp: socket.TcpSocket
  private isConnected: boolean = false
  private buffer: Uint8Array = new Uint8Array(0)
  private sendQueue: ArrayBuffer[] = []
  private sending: boolean = false

  onMessage: MessageHandler | null = null
  onDisconnected: DisconnectHandler | null = null

  constructor(tcp: socket.TcpSocket) {
    this.tcp = tcp
    this.isConnected = true
    this.setupListeners()
  }

  /** 设置事件监听 */
  private setupListeners(): void {
    // 数据到达
    this.tcp.on('message', (value: socket.SocketMessage) => {
      this.handleData(new Uint8Array(value.message))
    })

    // 连接关闭
    this.tcp.on('close', () => {
      console.info('[ControlConnection] Socket closed')
      this.isConnected = false
      if (this.onDisconnected) this.onDisconnected()
    })

    // 错误
    this.tcp.on('error', (err: BusinessError) => {
      console.error(`[ControlConnection] Socket error: ${err.message}`)
      if (this.isConnected) {
        this.isConnected = false
        if (this.onDisconnected) this.onDisconnected()
      }
    })
  }

  /** 处理到达的二进制数据：追加到缓冲区，尝试解析完整帧 */
  private handleData(chunk: Uint8Array): void {
    // 合并到缓冲区
    const newBuf = new Uint8Array(this.buffer.length + chunk.length)
    newBuf.set(this.buffer, 0)
    newBuf.set(chunk, this.buffer.length)
    this.buffer = newBuf

    // 循环尝试解析完整帧
    while (this.buffer.length >= ControlProtocolCodec.HEADER_SIZE) {
      const header = ControlProtocolCodec.decodeHeader(this.buffer)
      if (!header) {
        console.error('[ControlConnection] Decode header failed, clearing buffer')
        this.buffer = new Uint8Array(0)
        break
      }

      if (header.payloadLength > ControlProtocolCodec.MAX_PAYLOAD_SIZE) {
        console.error(`[ControlConnection] Payload too large: ${header.payloadLength}, clearing`)
        this.buffer = new Uint8Array(0)
        break
      }

      const totalLen = ControlProtocolCodec.HEADER_SIZE + header.payloadLength
      if (this.buffer.length < totalLen) {
        // 数据不够，等待更多数据
        break
      }

      // 提取完整帧
      const payload = this.buffer.slice(ControlProtocolCodec.HEADER_SIZE, totalLen)
      const remaining = this.buffer.slice(totalLen)

      // 回调
      if (this.onMessage) {
        this.onMessage(header.type, payload)
      }

      // 剩余数据继续处理
      this.buffer = remaining
    }

    // 防止缓冲区无限增长
    if (this.buffer.length > ControlProtocolCodec.MAX_PAYLOAD_SIZE * 2) {
      console.error(`[ControlConnection] Buffer overflow, resetting`)
      this.buffer = new Uint8Array(0)
    }
  }

  /** 发送数据（线程安全，排队发送） */
  async send(data: ArrayBuffer): Promise<boolean> {
    if (!this.isConnected) return false

    this.sendQueue.push(data)
    if (!this.sending) {
      this.sending = true
      await this.flushQueue()
    }
    return true
  }

  /** 逐个发送队列 */
  private async flushQueue(): Promise<void> {
    while (this.sendQueue.length > 0 && this.isConnected) {
      const data = this.sendQueue.shift()
      if (!data) continue
      try {
        const opts: socket.TCPExtraOptions = {
          keepAlive: true,
          OOBInline: false,
          TCPNoDelay: true,
          socketLinger: { on: false }
        }
        await this.tcp.send(opts, new Uint8Array(data))
      } catch (err) {
        console.error(`[ControlConnection] send error: ${JSON.stringify(err)}`)
        this.isConnected = false
        if (this.onDisconnected) this.onDisconnected()
        break
      }
    }
    this.sending = false
  }

  /** 发送包装好的消息 */
  async sendMessage(type: ControlMessageType, payload: Uint8Array): Promise<boolean> {
    const frame = ControlProtocolCodec.buildFrame(type, payload)
    return this.send(frame)
  }

  /** 发送设备问候 */
  async sendDeviceHello(deviceName: string, deviceType: number): Promise<boolean> {
    const payload = ControlProtocolCodec.encodeDeviceHello({
      protocolVersion: ControlProtocolCodec.PROTOCOL_VERSION,
      deviceType: deviceType,
      deviceName: deviceName
    })
    return this.sendMessage(ControlMessageType.DEVICE_HELLO, payload)
  }

  /** 发送屏幕信息 */
  async sendScreenInfo(info: {
    width: number; height: number; dpi: number; frameRate: number;
    codec: number; maxBitrate: number; hasAudio: number; audioCodec: number; rotation: number
  }): Promise<boolean> {
    const payload = ControlProtocolCodec.encodeScreenInfo(info)
    return this.sendMessage(ControlMessageType.SCREEN_INFO, payload)
  }

  /** 发送视频帧（裸 NAL，已含 Annex-B 起始码） */
  async sendVideoFrame(nalData: Uint8Array): Promise<boolean> {
    return this.sendMessage(ControlMessageType.VIDEO_FRAME, nalData)
  }

  /** 主动断开 */
  async disconnect(): Promise<void> {
    this.isConnected = false
    try {
      // 移除监听避免触发 disconnect 回调
      this.tcp.off('message')
      this.tcp.off('close')
      this.tcp.off('error')
      await this.tcp.close()
    } catch (_err) {
      // 忽略
    }
  }

  get connected(): boolean {
    return this.isConnected
  }
}
