package com.screenmirror.protocol

/**
 * 控制协议消息类型
 */
enum class ControlMessageType(val code: Byte) {
    TOUCH_EVENT(0x01),
    KEY_EVENT(0x02),
    SCREEN_INFO(0x03),
    DEVICE_HELLO(0x04),
    HEARTBEAT(0x05),
    HEARTBEAT_ACK(0x06),
    STREAM_START(0x07),
    STREAM_STOP(0x08),
    STREAM_PAUSE(0x09),
    STREAM_RESUME(0x0A),
    MOUSE_EVENT(0x0B),
    SCROLL_EVENT(0x0C),
    ROTATION_CHANGE(0x0D),
    ERROR(0x0E),
    CLIPBOARD_SYNC(0x20),
    FILE_TRANSFER(0x21),
    VIDEO_FRAME(0x22);  // H.264 NAL 单元数据

    companion object {
        fun fromCode(code: Byte): ControlMessageType? =
            entries.firstOrNull { it.code == code }
    }
}
