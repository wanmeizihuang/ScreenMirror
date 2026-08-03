// MainPage.ets — 主投屏界面
// 设备发现列表 + 投屏控制 + 反向控制提示
// 对应 Android 端 MainActivity.kt 的 Compose UI

import { ConnectionManager } from '../protocol/ConnectionManager'
import { ControlConnection } from '../protocol/ControlConnection'
import { DeviceDiscoveryService } from '../core/DeviceDiscoveryService'
import { ScreenCaptureService, CaptrueState } from '../core/ScreenCaptureService'
import { ReverseControlService } from '../modules/ReverseControlService'
import { ControlMessageType, DeviceType, VideoCodec, AudioCodec } from '../protocol/ControlMessageType'
import { ControlProtocolCodec } from '../protocol/ProtocolCodec'
import type { DiscoveredDevice } from '../protocol/ControlDataTypes'
import { promptAction } from '@kit.ArkUI'
import { router } from '@kit.ArkUI'
import { accessibility } from '@kit.AccessibilityKit'

// 状态枚举
enum AppState {
  IDLE,
  SCANNING,
  CONNECTING,
  AWAIT_CAPTURE,
  STREAMING,
  ERROR
}

@Entry
@Component
struct MainPage {
  // ===== 状态 =====
  @State appState: AppState = AppState.IDLE
  @State devices: DiscoveredDevice[] = []
  @State statusText: string = '等待连接'
  @State manualIp: string = ''
  @State showManualInput: boolean = false
  @State isAccessibilityEnabled: boolean = false
  @State logLines: string[] = []
  @State nalCount: number = 0
  @State errorMessage: string = ''

  // ===== 服务实例 =====
  private discovery: DeviceDiscoveryService = new DeviceDiscoveryService('HarmonyOS')
  private connectionManager: ConnectionManager = new ConnectionManager()
  private captureService: ScreenCaptureService = new ScreenCaptureService()
  private reverseControl: ReverseControlService = new ReverseControlService()
  private activeConnection: ControlConnection | null = null
  private scanTimer: number = -1

  aboutToAppear(): void {
    // 启动发现服务
    this.discovery.onDeviceFound = (device: DiscoveredDevice) => {
      this.devices = [...this.discovery.getDevices()]
      this.updateStatus(`发现 ${device.deviceName}`)
    }
    this.discovery.onDeviceLost = (name: string) => {
      this.devices = [...this.discovery.getDevices()]
    }
    this.discovery.onLog = (msg: string) => {
      this.addLog(msg)
    }
    this.discovery.startAdvertising(35354)

    // 自动扫描
    this.startScanning()
  }

  aboutToDisappear(): void {
    this.stopScanning()
    this.discovery.stop()
    this.connectionManager.disconnect()
    this.captureService.stopCapture()
  }

  // ===== UI =====
  build() {
    Column() {
      // 标题栏
      this.buildHeader()

      // 错误提示
      if (this.errorMessage) {
        this.buildErrorBar()
      }

      // 状态卡片
      this.buildStatusCard()

      // 操作按钮
      this.buildActionButtons()

      // 手动 IP 输入
      if (this.showManualInput) {
        this.buildManualInput()
      }

      // 日志
      this.buildLogPanel()
    }
    .width('100%')
    .height('100%')
    .backgroundColor('#F2F4F8')
  }

  @Builder
  buildHeader() {
    Column() {
      Text('ScreenMirror')
        .fontSize(22)
        .fontWeight(FontWeight.Bold)
        .fontColor(Color.White)
      Text('WiFi 投屏 · 鸿蒙')
        .fontSize(12)
        .fontColor('#DDDDDD')
        .margin({ top: 4 })
    }
    .width('100%')
    .padding({ top: 40, bottom: 16 })
    .linearGradient({
      direction: GradientDirection.Bottom,
      colors: [['#4A90D9', 0], ['#2E5C8A', 1]]
    })
  }

  @Builder
  buildErrorBar() {
    Row() {
      Text(this.errorMessage)
        .fontSize(14)
        .fontColor(Color.White)
        .layoutWeight(1)
      Button('×')
        .width(28).height(28)
        .fontSize(16)
        .fontColor(Color.White)
        .backgroundColor('#A00000')
        .onClick(() => { this.errorMessage = '' })
    }
    .width('100%')
    .padding(10)
    .backgroundColor('#D32F2F')
  }

  @Builder
  buildStatusCard() {
    Column() {
      Row() {
        // 状态指示点
        Circle({ width: 10, height: 10 })
          .fill(this.getStatusColor())
        Text(this.statusText)
          .fontSize(16)
          .margin({ left: 8 })
        Blank()
        Text(`设备: ${this.devices.length}`)
          .fontSize(12)
          .fontColor('#888')
      }
      .width('100%')

      if (this.appState === AppState.STREAMING) {
        Text(`NAL: ${this.nalCount}`)
          .fontSize(11)
          .fontColor('#666')
          .margin({ top: 6 })
      }
    }
    .width('90%')
    .padding(16)
    .margin({ top: 16 })
    .backgroundColor(Color.White)
    .borderRadius(12)
    .shadow({ radius: 4, color: '#10000000' })
  }

  @Builder
  buildActionButtons() {
    Row() {
      // 扫描按钮
      Button(this.appState === AppState.SCANNING ? '扫描中...' : '扫描设备')
        .fontSize(14)
        .width('45%')
        .height(40)
        .enabled(this.appState !== AppState.STREAMING)
        .onClick(() => { this.startScanning() })

      // 手动 IP
      Button('手动输入IP')
        .fontSize(14)
        .width('45%')
        .height(40)
        .enabled(this.appState !== AppState.STREAMING)
        .onClick(() => { this.showManualInput = !this.showManualInput })
    }
    .width('90%')
    .justifyContent(FlexAlign.SpaceBetween)
    .margin({ top: 12, bottom: 12 })
  }

  @Builder
  buildManualInput() {
    Row() {
      TextInput({ placeholder: '接收端 IP 地址', text: this.manualIp })
        .width('60%')
        .height(40)
        .fontSize(14)
        .onChange((val: string) => { this.manualIp = val })
      Button('连接')
        .fontSize(14)
        .height(40)
        .margin({ left: 8 })
        .onClick(() => {
          const ip = this.manualIp.trim()
          if (ip) {
            this.connectToDevice(ip, 35354)
            this.showManualInput = false
          }
        })
    }
    .width('90%')
    .padding(8)
    .backgroundColor(Color.White)
    .borderRadius(8)
    .margin({ bottom: 12 })
  }

  @Builder
  buildLogPanel() {
    Column() {
      Text('日志')
        .fontSize(12)
        .fontColor('#888')
        .width('100%')
        .padding({ left: 8, top: 4 })

      Scroll() {
        Column() {
          ForEach(this.logLines.slice(-15), (line: string) => {
            Text(line)
              .fontSize(10)
              .fontColor('#666')
              .fontFamily('monospace')
              .width('100%')
              .padding(1)
          })
        }
      }
      .width('100%')
      .height(160)
      .constraintSize({ minHeight: 60 })
    }
    .width('90%')
    .padding(8)
    .backgroundColor('#F0F0F0')
    .borderRadius(8)
    .margin({ top: 12 })
  }

  // ===== 逻辑方法 =====

  /** 开始扫描 */
  startScanning(): void {
    this.appState = AppState.SCANNING
    this.updateStatus('扫描中...')

    this.discovery.startDiscovery()
    this.discovery.scanByUdp()

    // 3 秒后刷新
    clearInterval(this.scanTimer)
    this.scanTimer = setInterval(() => {
      this.devices = [...this.discovery.getDevices()]
    }, 3000)
  }

  /** 停止扫描 */
  stopScanning(): void {
    clearInterval(this.scanTimer)
    this.scanTimer = -1
  }

  /** 连接到指定设备并启动投屏 */
  async connectToDevice(host: string, port: number): Promise<void> {
    this.appState = AppState.CONNECTING
    this.updateStatus('连接中...')

    const conn = await this.connectionManager.connect(host, port)
    if (!conn) {
      this.appState = AppState.ERROR
      this.errorMessage = '连接失败'
      this.updateStatus('连接失败')
      return
    }

    this.activeConnection = conn

    // 发送握手
    await conn.sendDeviceHello('HarmonyOS', DeviceType.HARMONYOS)

    // 监听反向控制消息
    conn.onMessage = (type: ControlMessageType, payload: Uint8Array) => {
      if (type === ControlMessageType.MOUSE_EVENT ||
          type === ControlMessageType.KEY_EVENT ||
          type === ControlMessageType.TOUCH_EVENT) {
        this.reverseControl.handleMessage(type, payload)
      }
    }

    // 监听断开
    conn.onDisconnected = () => {
      this.activeConnection = null
      this.appState = AppState.IDLE
      this.updateStatus('连接已断开')
      this.addLog('连接已断开')
    }

    this.addLog(`已连接 ${host}`)

    // 请求录屏权限后开始投屏
    this.appState = AppState.AWAIT_CAPTURE
    this.updateStatus('等待录屏授权...')

    // 开始屏幕采集
    const started = await this.captureService.startCapture(
      // NAL 回调：编码后的帧
      (nalData: Uint8Array) => {
        if (this.activeConnection) {
          this.activeConnection.sendVideoFrame(nalData)
          this.nalCount++
        }
      },
      // 状态回调
      (state: CaptrueState) => {
        if (state === CaptrueState.CAPTURING) {
          this.appState = AppState.STREAMING
          this.updateStatus('投屏中')
          // 发送屏幕信息
          const info = this.captureService.getScreenInfo()
          if (this.activeConnection) {
            this.activeConnection.sendScreenInfo({
              width: info.width, height: info.height, dpi: info.dpi,
              frameRate: info.frameRate, codec: VideoCodec.H264,
              maxBitrate: 8000000, hasAudio: 0,
              audioCodec: AudioCodec.AAC, rotation: 0
            })
          }
          // 设置反向控制屏幕信息
          this.reverseControl.setScreenInfo(info.width, info.height, 0)
        } else if (state === CaptrueState.ERROR) {
          this.appState = AppState.ERROR
          this.errorMessage = '屏幕采集失败'
          this.updateStatus('采集失败')
        }
      }
    )

    if (!started) {
      this.errorMessage = '录屏权限被拒绝'
      this.updateStatus('权限拒绝')
    }
  }

  /** 停止投屏 */
  stopStreaming(): void {
    this.captureService.stopCapture()
    this.connectionManager.disconnect()
    this.activeConnection = null
    this.appState = AppState.IDLE
    this.nalCount = 0
    this.updateStatus('已停止')
    this.addLog('投屏已停止')
    this.startScanning()
  }

  /** 打开无障碍设置 */
  openAccessibilitySettings(): void {
    try {
      // 跳转到系统辅助功能设置
      const want = {
        bundleName: 'com.huawei.hmos.settings',
        abilityName: 'com.huawei.hmos.settings.MainAbility',
        uri: 'accessibility_settings'
      }
      promptAction.showToast({ message: '请在设置中开启 "ScreenMirror" 无障碍服务' })
    } catch (_err) {
      promptAction.showToast({ message: '无法打开系统设置' })
    }
  }

  // ===== 辅助方法 =====
  private updateStatus(text: string): void { this.statusText = text }
  private addLog(msg: string): void { this.logLines = [...this.logLines, `[${new Date().toLocaleTimeString()}] ${msg}`] }
  private getStatusColor(): string {
    switch (this.appState) {
      case AppState.STREAMING: return '#4CAF50'
      case AppState.CONNECTING:
      case AppState.AWAIT_CAPTURE: return '#FF9800'
      case AppState.ERROR: return '#F44336'
      default: return '#888888'
    }
  }
}
