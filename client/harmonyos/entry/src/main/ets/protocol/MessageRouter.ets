// MessageRouter.ets — 消息路由器，按消息类型分发到对应处理器

import { ControlMessageType } from './ControlMessageType'
import { ControlConnection } from './ControlConnection'

type MessageRouteHandler = (type: ControlMessageType, payload: Uint8Array) => void

export class MessageRouter {
  private handlers: Map<ControlMessageType, MessageRouteHandler[]> = new Map()
  private fallbackHandler: MessageRouteHandler | null = null

  /** 注册消息处理器 */
  register(type: ControlMessageType, handler: MessageRouteHandler): void {
    if (!this.handlers.has(type)) {
      this.handlers.set(type, [])
    }
    this.handlers.get(type)!.push(handler)
  }

  /** 设置未匹配消息的回退处理器 */
  setFallback(handler: MessageRouteHandler): void {
    this.fallbackHandler = handler
  }

  /** 将路由器绑定到连接 */
  bindToConnection(connection: ControlConnection): void {
    connection.onMessage = (type: ControlMessageType, payload: Uint8Array) => {
      this.route(type, payload)
    }
  }

  /** 分发消息 */
  private route(type: ControlMessageType, payload: Uint8Array): void {
    const handlers = this.handlers.get(type)
    if (handlers && handlers.length > 0) {
      for (const handler of handlers) {
        try {
          handler(type, payload)
        } catch (err) {
          console.error(`[MessageRouter] Handler error for type 0x${type.toString(16)}: ${JSON.stringify(err)}`)
        }
      }
      return
    }

    // 未匹配 → 回退
    if (this.fallbackHandler) {
      try {
        this.fallbackHandler(type, payload)
      } catch (err) {
        console.error(`[MessageRouter] Fallback error: ${JSON.stringify(err)}`)
      }
    }
  }

  /** 清理所有处理器 */
  clear(): void {
    this.handlers.clear()
    this.fallbackHandler = null
  }
}
