// DeviceDiscoveryService.ets — 设备发现：mDNS 注册/发现 + UDP 广播(35357)
// 对应 Android 端 DeviceDiscoveryService.kt

import { mdns } from '@kit.NetworkKit'
import { socket } from '@kit.NetworkKit'
import { BusinessError } from '@kit.BasicServicesKit'
import type { DiscoveredDevice } from '../protocol/ControlDataTypes'
import { systemDateTime } from '@kit.BasicServicesKit'

export type DeviceFoundCallback = (device: DiscoveredDevice) => void
export type DeviceLostCallback = (deviceName: string) => void
export type LogCallback = (msg: string) => void

const SERVICE_TYPE: string = '_screenmirror._tcp'
const DISCOVERY_PORT: number = 35357
const SCAN_INTERVAL_MS: number = 3000

export class DeviceDiscoveryService {
  private mdnsCallbackIndex: number = -1
  private udpSocket: socket.UdpSocket | null = null
  private isAdvertising: boolean = false
  private isDiscovering: boolean = false
  private deviceName: string = ''
  private listenPort: number = 35354
  private devices: Map<string, DiscoveredDevice> = new Map()

  onDeviceFound: DeviceFoundCallback | null = null
  onDeviceLost: DeviceLostCallback | null = null
  onLog: LogCallback | null = null

  constructor(deviceName: string = 'HarmonyOS') {
    this.deviceName = deviceName
  }

  // ===== mDNS 广播自己 + UDP 被动监听 =====
  async startAdvertising(listenPort: number = 35354): Promise<void> {
    if (this.isAdvertising) return
    this.listenPort = listenPort

    try {
      // 注册 mDNS 服务
      const localServiceInfo: mdns.LocalServiceInfo = {
        serviceName: `ScreenMirror-${this.deviceName}`,
        serviceType: SERVICE_TYPE,
        port: listenPort,
        host: await this.getLocalIp(),
        txtRecord: {
          'device_name': this.deviceName,
          'device_type': 'harmonyos',
          'version': '1',
          'has_audio': '0'
        }
      }

      const mdnsInstance = mdns.addLocalService(localServiceInfo)
      this.mdnsCallbackIndex = mdnsInstance
      console.info(`[Discovery] mDNS service registered: ${localServiceInfo.serviceName}`)

      this.startUdpListener()
      this.isAdvertising = true
      this.log('服务已广播（mDNS + UDP）')

    } catch (err) {
      const e = err as BusinessError
      console.error(`[Discovery] mDNS register failed: ${e.message}`)
      this.log('mDNS 注册失败，尝试仅 UDP')
      this.startUdpListener()
    }
  }

  /** UDP 监听 35357（响应 discover，接收 present） */
  private async startUdpListener(): Promise<void> {
    try {
      this.udpSocket = socket.constructUDPSocketInstance()

      // 绑定 35357
      const bindAddr: socket.NetAddress = {
        address: '0.0.0.0',
        port: DISCOVERY_PORT,
        family: 1
      }
      await this.udpSocket.bind(bindAddr)

      // 监听消息
      this.udpSocket.on('message', (value: socket.SocketMessage) => {
        this.handleUdpPacket(new Uint8Array(value.message), value.remoteInfo)
      })

      this.udpSocket.on('error', (err: BusinessError) => {
        console.error(`[Discovery] UDP error: ${err.message}`)
      })

      console.info('[Discovery] UDP listener started on 35357')
    } catch (err) {
      console.error(`[Discovery] UDP bind failed: ${JSON.stringify(err)}`)
    }
  }

  /** 处理 UDP 数据包 */
  private handleUdpPacket(data: Uint8Array, remote: socket.SocketRemoteInfo): void {
    try {
      const decoder = new TextDecoder()
      const json = decoder.decode(data)
      const msg = JSON.parse(json)

      if (msg.type === 'screenmirror.discover') {
        this.log(`发现请求: ${msg.device_name || '未知'}`)
        // 回应 present
        this.sendUdpPresent(remote.address, msg.device_name || 'unknown')
        // 将请求方也加入设备列表
        this.addDevice({
          host: remote.address,
          port: msg.port || 35354,
          deviceName: msg.device_name || '未知设备',
          deviceType: msg.device_type || 'unknown',
          version: msg.version || 1,
          macSuffix: '',
          hasAudio: false
        })
      } else if (msg.type === 'screenmirror.present') {
        this.log(`发现设备: ${msg.device_name} @ ${msg.host}`)
        this.addDevice({
          host: msg.host || remote.address,
          port: msg.port || 35354,
          deviceName: msg.device_name || '未知设备',
          deviceType: msg.device_type || 'unknown',
          version: msg.version || 1,
          macSuffix: msg.mac_suffix || '',
          hasAudio: msg.has_audio === true
        })
      }
    } catch (err) {
      console.error(`[Discovery] parse packet error: ${JSON.stringify(err)}`)
    }
  }

  /** 发送 UDP present 响应 */
  private async sendUdpPresent(targetAddress: string, targetName: string): Promise<void> {
    if (!this.udpSocket) return
    try {
      const response = JSON.stringify({
        type: 'screenmirror.present',
        device_name: this.deviceName,
        device_type: 'harmonyos',
        version: 1,
        port: this.listenPort,
        host: await this.getLocalIp(),
        mac_suffix: '',
        has_audio: false
      })
      const sendOpts: socket.UDPExtraOptions = {
        broadcast: false
      }
      const remoteAddr: socket.NetAddress = {
        address: targetAddress,
        port: DISCOVERY_PORT,
        family: 1
      }
      await this.udpSocket.send(sendOpts, new Uint8Array(new TextEncoder().encode(response)), remoteAddr)
    } catch (err) {
      console.error(`[Discovery] sendUdpPresent error: ${JSON.stringify(err)}`)
    }
  }

  // ===== mDNS 主动扫描 =====
  async startDiscovery(): Promise<void> {
    if (this.isDiscovering) return
    this.isDiscovering = true

    try {
      const mdnsInstance = await mdns.discoverServices({ serviceType: SERVICE_TYPE })
      // HarmonyOS mdns 发现通过 on('discoveryStart') 等事件驱动

      // 同时通过 UDP 广播主动扫描
      this.scanByUdp()

      this.log('开始扫描设备...')
    } catch (err) {
      console.error(`[Discovery] discover failed: ${JSON.stringify(err)}`)
    }
  }

  /** UDP 广播 discover */
  async scanByUdp(): Promise<void> {
    if (!this.udpSocket) return

    try {
      const localIp = await this.getLocalIp()
      const broadcastAddr = this.computeBroadcast(localIp, '255.255.255.255')

      const discoverMsg = JSON.stringify({
        type: 'screenmirror.discover',
        device_name: this.deviceName,
        device_type: 'harmonyos',
        version: 1,
        port: this.listenPort,
        mac_suffix: '',
        has_audio: false
      })

      const sendOpts: socket.UDPExtraOptions = { broadcast: true }
      const remoteAddr: socket.NetAddress = {
        address: broadcastAddr,
        port: DISCOVERY_PORT,
        family: 1
      }
      await this.udpSocket.send(sendOpts, new Uint8Array(new TextEncoder().encode(discoverMsg)), remoteAddr)

      // 也向本地广播地址发送
      const subnetBroadcast = this.computeBroadcast(localIp, broadcastAddr)
      if (subnetBroadcast !== broadcastAddr) {
        const addr2: socket.NetAddress = {
          address: subnetBroadcast,
          port: DISCOVERY_PORT,
          family: 1
        }
        await this.udpSocket.send(sendOpts, new Uint8Array(new TextEncoder().encode(discoverMsg)), addr2)
      }

      this.log('UDP 广播已发送')
    } catch (err) {
      console.error(`[Discovery] scanByUdp error: ${JSON.stringify(err)}`)
    }
  }

  /** 计算子网广播地址 */
  private computeBroadcast(ip: string, defaultBroadcast: string): string {
    // 简化实现，直接返回 255.255.255.255
    // 生产环境应解析 ip/掩码计算子网广播
    return defaultBroadcast
  }

  /** 获取本机 IPv4 */
  private async getLocalIp(): Promise<string> {
    try {
      // 通过 socket 获取本机地址
      const ifaces = socket.getSocketAddr()
      if (ifaces && ifaces.length > 0) {
        for (const iface of ifaces) {
          // 优先 192.168 / 10.x / 172.16-31
          const ip = iface.address
          if (ip.startsWith('192.168.') || ip.startsWith('10.') ||
              (/^172\.(1[6-9]|2\d|3[01])\./).test(ip)) {
            return ip
          }
        }
        return ifaces[0].address
      }
    } catch (err) {
      console.error(`[Discovery] getLocalIp error: ${JSON.stringify(err)}`)
    }
    return '127.0.0.1'
  }

  /** 添加设备到列表 */
  private addDevice(device: DiscoveredDevice): void {
    const key = `${device.host}:${device.port}`
    // 过滤自身
    if (device.deviceType === 'harmonyos') return
    if (device.host === '127.0.0.1' || device.host === '::1') return

    if (!this.devices.has(key)) {
      this.devices.set(key, device)
      if (this.onDeviceFound) this.onDeviceFound(device)
    }
  }

  /** 获取已发现设备列表 */
  getDevices(): DiscoveredDevice[] {
    return Array.from(this.devices.values())
  }

  /** 停止所有发现 */
  async stop(): Promise<void> {
    this.isAdvertising = false
    this.isDiscovering = false

    // 移除 mDNS 服务
    if (this.mdnsCallbackIndex >= 0) {
      try {
        mdns.removeLocalService(this.mdnsCallbackIndex)
      } catch (_err) { /* ignore */ }
    }

    // 关闭 UDP
    if (this.udpSocket) {
      try {
        this.udpSocket.off('message')
        await this.udpSocket.close()
      } catch (_err) { /* ignore */ }
      this.udpSocket = null
    }

    this.devices.clear()
  }

  private log(msg: string): void {
    console.info(`[Discovery] ${msg}`)
    if (this.onLog) this.onLog(msg)
  }
}
