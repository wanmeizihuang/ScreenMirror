// ReverseControlService.ets — 反向控制：接收 PC 指令并注入为触摸/按键事件
// 对应 Android 端 ReverseControlService.kt
// 鸿蒙 API: @ohos.accessibility 提供 AccessibilityExtensionAbility 和 GestureInject

import { accessibility } from '@kit.AccessibilityKit'
import { ControlProtocolCodec } from '../protocol/ProtocolCodec'
import { ControlMessageType, TouchAction, KeyAction, MouseAction } from '../protocol/ControlMessageType'
import type { TouchEventData, KeyEventData, MouseEventData } from '../protocol/ControlDataTypes'

export class ReverseControlService {
  private screenWidth: number = 1080
  private screenHeight: number = 1920
  private rotation: number = 0

  /** 设置屏幕参数（用于坐标换算） */
  setScreenInfo(width: number, height: number, rotation: number = 0): void {
    this.screenWidth = width
    this.screenHeight = height
    this.rotation = rotation
  }

  /** 分发 PC 端发来的控制消息 */
  handleMessage(type: ControlMessageType, payload: Uint8Array): boolean {
    switch (type) {
      case ControlMessageType.TOUCH_EVENT:
        return this.handleTouch(ControlProtocolCodec.decodeTouchEvent(payload))
      case ControlMessageType.KEY_EVENT:
        return this.handleKey(ControlProtocolCodec.decodeKeyEvent(payload))
      case ControlMessageType.MOUSE_EVENT:
        return this.handleMouse(ControlProtocolCodec.decodeMouseEvent(payload))
      default:
        return false
    }
  }

  /** 处理触摸事件（归一化坐标 → 像素坐标 → 手势注入） */
  private handleTouch(data: TouchEventData): boolean {
    // 坐标归一化 (0~10000) → 像素
    const x = Math.round((data.x / 10000) * this.screenWidth)
    const y = Math.round((data.y / 10000) * this.screenHeight)

    try {
      switch (data.action) {
        case TouchAction.DOWN: {
          // 手指按下
          const gesture: accessibility.GesturePath = {
            points: [{ x: x, y: y }],
            duration: 0
          }
          accessibility.injectTouchEvent({
            action: 0, // down
            pointers: [{ id: data.pointerId, x: x, y: y, pressure: data.pressure / 1000 }]
          })
          break
        }
        case TouchAction.MOVE: {
          // 手指移动
          accessibility.injectTouchEvent({
            action: 2, // move
            pointers: [{ id: data.pointerId, x: x, y: y, pressure: data.pressure / 1000 }]
          })
          break
        }
        case TouchAction.UP:
        case TouchAction.CANCEL: {
          // 手指抬起
          accessibility.injectTouchEvent({
            action: 1, // up
            pointers: [{ id: data.pointerId, x: x, y: y, pressure: 0 }]
          })
          break
        }
      }
      return true
    } catch (err) {
      console.error(`[ReverseControl] touch error: ${JSON.stringify(err)}`)
      return false
    }
  }

  /** 处理按键事件 */
  private handleKey(data: KeyEventData): boolean {
    try {
      // HarmonyOS 按键注入需要通过 AccessibilityExtensionAbility
      // 或者在系统应用中通过 inputEventClient.injectEvent 注入
      accessibility.injectKeyEvent({
        keyCode: data.keyCode,
        keyAction: data.action === KeyAction.DOWN ? 0 : 1,
        triggerTime: Number(data.eventTime)
      })
      return true
    } catch (err) {
      console.error(`[ReverseControl] key inject error: ${JSON.stringify(err)}`)
      return false
    }
  }

  /** 处理鼠标事件（映射为触摸） */
  private handleMouse(data: MouseEventData): boolean {
    const x = Math.round((data.x / 10000) * this.screenWidth)
    const y = Math.round((data.y / 10000) * this.screenHeight)

    try {
      switch (data.action) {
        case MouseAction.MOVE:
          // 鼠标移动不注入触摸，仅跟踪位置
          return true
        case MouseAction.LEFT_DOWN:
          accessibility.injectTouchEvent({
            action: 0,
            pointers: [{ id: 0, x: x, y: y, pressure: 1 }]
          })
          return true
        case MouseAction.LEFT_UP:
          accessibility.injectTouchEvent({
            action: 1,
            pointers: [{ id: 0, x: x, y: y, pressure: 0 }]
          })
          return true
        case MouseAction.RIGHT_DOWN:
          // 右键映射为返回键
          accessibility.injectKeyEvent({
            keyCode: 2066, // Back
            keyAction: 0,
            triggerTime: Date.now()
          })
          return true
        default:
          return false
      }
    } catch (err) {
      console.error(`[ReverseControl] mouse error: ${JSON.stringify(err)}`)
      return false
    }
  }
}
