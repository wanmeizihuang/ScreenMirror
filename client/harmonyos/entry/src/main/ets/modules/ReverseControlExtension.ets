// ReverseControlExtension.ets — 无障碍扩展能力
// 通过 AccessibilityExtensionAbility 实现反向控制注入
// 在 module.json5 中注册为 type: "accessibility"

import { AccessibilityExtensionAbility } from '@kit.AccessibilityKit'

let instance: ReverseControlExtension | null = null

export default class ReverseControlExtension extends AccessibilityExtensionAbility {
  onCreate(): void {
    console.info('[ReverseControlExt] onCreate')
    instance = this
  }

  onDestroy(): void {
    console.info('[ReverseControlExt] onDestroy')
    instance = null
  }

  onConnect(): void {
    console.info('[ReverseControlExt] onConnect — 无障碍服务已连接')
  }

  onDisconnect(): void {
    console.info('[ReverseControlExt] onDisconnect — 无障碍服务已断开')
  }

  onAccessibilityEvent(event: unknown): void {
    // 可在此处理系统无障碍事件
  }

  static getInstance(): ReverseControlExtension | null {
    return instance
  }
}
