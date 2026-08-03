// ConnectionManager.ets — TCP 连接管理器
// 主要负责主动连接 PC 接收端（端口 35354）

import { ControlConnection } from './ControlConnection'
import { socket } from '@kit.NetworkKit'
import { BusinessError } from '@kit.BasicServicesKit'

export type ConnectionCallback = (connection: ControlConnection) => void

export class ConnectionManager {
  private activeConnection: ControlConnection | null = null
  private connectCallback: ConnectionCallback | null = null

  static readonly DEFAULT_CONTROL_PORT: number = 35354
  static readonly DEFAULT_VIDEO_PORT: number = 35355    // 死常量，当前单通道复用
  static readonly DEFAULT_AUDIO_PORT: number = 35356    // 死常量

  /** 主动连接 PC 接收端 */
  async connect(host: string, port: number): Promise<ControlConnection | null> {
    console.info(`[ConnectionManager] Connecting to ${host}:${port}...`)

    try {
      const tcp = socket.constructTCPSocketInstance()

      // 绑定本地
      const bindAddr: socket.NetAddress = {
        address: '0.0.0.0',
        port: 0,
        family: 1 // IPv4
      }
      await tcp.bind(bindAddr)

      // 连接远程
      const remoteAddr: socket.NetAddress = {
        address: host,
        port: port,
        family: 1
      }
      await tcp.connect(remoteAddr, {
        timeout: 5000
      })

      const connection = new ControlConnection(tcp)
      this.activeConnection = connection

      // 连接断开时清理
      connection.onDisconnected = () => {
        console.info('[ConnectionManager] Connection disconnected')
        this.activeConnection = null
      }

      console.info(`[ConnectionManager] Connected to ${host}:${port}`)
      return connection

    } catch (err) {
      const e = err as BusinessError
      console.error(`[ConnectionManager] Connect failed: ${e.message} (code: ${e.code})`)
      return null
    }
  }

  /** 断开当前连接 */
  async disconnect(): Promise<void> {
    if (this.activeConnection) {
      await this.activeConnection.disconnect()
      this.activeConnection = null
    }
  }

  /** 获取当前活动连接 */
  getActiveConnection(): ControlConnection | null {
    return this.activeConnection
  }
}
