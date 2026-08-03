// ScreenCaptureService.ets — 屏幕采集 + H.264 编码
// 对应 Android 端 ScreenCaptureService.kt
// 鸿蒙 API: @ohos.multimedia.media.AVScreenCaptureRecorder / @ohos.multimedia.media.AVRecorder

import { media } from '@kit.MediaKit'
import { window } from '@kit.ArkUI'
import { BusinessError } from '@kit.BasicServicesKit'

export type NalCallback = (nalData: Uint8Array) => void
export type StateCallback = (state: CaptrueState) => void

export enum CaptrueState {
  IDLE,
  PREPARING,
  CAPTURING,
  ERROR,
  STOPPED
}

export class ScreenCaptureService {
  private recorder: media.AVScreenCaptureRecorder | null = null
  private isCapturing: boolean = false
  private state: CaptrueState = CaptrueState.IDLE

  private screenWidth: number = 1080
  private screenHeight: number = 1920
  private screenDpi: number = 420
  private maxDim: number = 1280 // 降采样最大边长

  private nalCallback: NalCallback | null = null
  private stateCallback: StateCallback | null = null

  private sps: Uint8Array = new Uint8Array(0)
  private pps: Uint8Array = new Uint8Array(0)
  private spsPpsSent: boolean = false

  /** 启动屏幕采集 */
  async startCapture(callback: NalCallback, stateCb?: StateCallback): Promise<boolean> {
    this.nalCallback = callback
    this.stateCallback = stateCb || null
    this.updateState(CaptrueState.PREPARING)

    try {
      // 创建屏幕录制器
      this.recorder = await media.createAVScreenCaptureRecorder()

      // 配置录制参数
      const config: media.AVScreenCaptureRecordConfig = {
        // 视频
        videoBitrate: 8000000,  // 8 Mbps
        videoFrameWidth: this.screenWidth,
        videoFrameHeight: this.screenHeight,
        videoFrameRate: 30,
        enableHdr: false,
        // 音频（暂不录制）
        enableMic: false,
        enableInnerAudio: false,
        // 输出回调
        dataConsumer: this
      }

      // 初始化
      await this.recorder.init(config)
      console.info('[ScreenCapture] Recorder initialized')

      // 开始录制（屏幕捕获由系统弹窗授权触发）
      await this.recorder.startRecording()
      console.info('[ScreenCapture] Recording started')

      this.isCapturing = true
      this.updateState(CaptrueState.CAPTURING)
      return true

    } catch (err) {
      const e = err as BusinessError
      console.error(`[ScreenCapture] Start failed: ${e.message} (code: ${e.code})`)
      this.updateState(CaptrueState.ERROR)
      return false
    }
  }

  /** 停止采集 */
  async stopCapture(): Promise<void> {
    this.isCapturing = false

    try {
      if (this.recorder) {
        await this.recorder.stopRecording()
        await this.recorder.release()
        this.recorder = null
        console.info('[ScreenCapture] Stopped')
      }
    } catch (err) {
      console.error(`[ScreenCapture] Stop error: ${JSON.stringify(err)}`)
    }

    this.updateState(CaptrueState.STOPPED)
  }

  // ===== AVScreenCaptureDataConsumer 回调 =====

  /** 视频帧回调 */
  onVideoFrameAvailable(frame: media.VideoFrameInfo): void {
    if (!this.isCapturing || !this.nalCallback) return

    try {
      // 从 frame 中提取编码后的数据
      // AVScreenCaptureRecorder 产出的是已编码的 H.264 帧
      // 需要归一化为 Annex-B 格式
      const nalData = this.extractNalFromFrame(frame)
      if (nalData && nalData.length > 0) {
        // 先发 SPS/PPS
        if (!this.spsPpsSent && this.sps.length > 0 && this.pps.length > 0) {
          this.nalCallback(this.sps)
          this.nalCallback(this.pps)
          this.spsPpsSent = true
        }
        this.nalCallback(nalData)
      }
    } catch (err) {
      console.error(`[ScreenCapture] onVideoFrameAvailable error: ${JSON.stringify(err)}`)
    }
  }

  /** 编码参数变化（提取 SPS/PPS） */
  onMediaFormatChanged(format: media.MediaDescription): void {
    // 从编码器配置中提取 SPS/PPS
    // HarmonyOS AVRecorder outputFormat 可能不直接暴露 CSD 数据
    // 通过解析 IDR 帧前的 NAL 或从配置中获取
    console.info(`[ScreenCapture] Format changed: ${JSON.stringify(format)}`)
  }

  /** 将帧数据转换为 Annex-B 格式的 NAL */
  private extractNalFromFrame(frame: media.VideoFrameInfo): Uint8Array | null {
    // AVScreenCaptureRecorder 的帧数据可能已经是 H.264 Annex-B 格式
    // 或者需要从 AVCC 格式转换
    // 此处从 frame 中提取原始编码数据
    return null // 占位，实际 API 需在设备上验证
  }

  /** AVCC → Annex-B 转换 */
  static normalizeToAnnexB(data: Uint8Array): Uint8Array {
    if (data.length < 4) return data

    // 已经是 Annex-B（起始码 00 00 00 01 或 00 00 01）
    const view = new DataView(data.buffer.slice(data.byteOffset, data.byteOffset + 4))
    if (data[0] === 0x00 && data[1] === 0x00 &&
        (data[2] === 0x01 || (data[2] === 0x00 && data[3] === 0x01))) {
      return data
    }

    // AVCC 格式：读取 4 字节大端长度
    const nalSize = view.getUint32(0, false)
    if (nalSize + 4 > data.length) return data

    const annexB = new Uint8Array(nalSize + 4)
    annexB[0] = 0x00; annexB[1] = 0x00; annexB[2] = 0x00; annexB[3] = 0x01
    annexB.set(data.slice(4, nalSize + 4), 4)
    return annexB
  }

  /** 更新状态并回调 */
  private updateState(state: CaptrueState): void {
    this.state = state
    if (this.stateCallback) {
      this.stateCallback(state)
    }
  }

  /** 获取屏幕信息（用于 SCREEN_INFO 消息） */
  getScreenInfo(): { width: number; height: number; dpi: number; frameRate: number } {
    return {
      width: this.screenWidth,
      height: this.screenHeight,
      dpi: this.screenDpi,
      frameRate: 30
    }
  }

  get capturing(): boolean {
    return this.isCapturing
  }
}
