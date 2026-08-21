package com.screenmirror.protocol

import android.util.Log
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.thread

// ============================================================
// 数据模型
// ============================================================
enum class DeviceType(val code: Byte) {
    UNKNOWN(0), ANDROID(1), IOS(2), HARMONY_OS(3),
    WINDOWS(4), MAC(5), TV(6);
    companion object {
        fun fromCode(code: Byte) = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

enum class VideoCodec(val code: Byte) { H264(0), H265(1) }
enum class AudioCodec(val code: Byte) { AAC(0), OPUS(1) }
enum class TouchAction(val code: Byte) { DOWN(0), MOVE(1), UP(2), CANCEL(3) }
enum class KeyAction(val code: Byte) { DOWN(0), UP(1) }
enum class MouseAction(val code: Byte) { MOVE(0), DOWN(1), UP(2) }

data class TouchEventData(
    val action: TouchAction,
    val pointerId: Int = 0,
    val x: Float,        // 0.0 ~ 1.0
    val y: Float,
    val pressure: Float = 1.0f,
    val eventTime: Long
)

data class KeyEventData(
    val action: KeyAction,
    val keyCode: Int,
    val metaState: Int = 0,
    val eventTime: Long
)

data class ScreenInfoData(
    val width: Int,
    val height: Int,
    val dpi: Int,
    val frameRate: Int = 60,
    val codec: VideoCodec = VideoCodec.H264,
    val maxBitrateKbps: Int = 8000,
    val hasAudio: Boolean = false,
    val audioCodec: AudioCodec = AudioCodec.AAC,
    val rotation: Int = 0
)

data class DeviceHelloData(
    val protocolVersion: Int = 1,
    val deviceType: DeviceType,
    val deviceName: String
)

data class MouseEventData(
    val action: MouseAction,
    val x: Float,
    val y: Float,
    val button: Int = 0
)

data class ScrollEventData(
    val x: Float,
    val y: Float,
    val hScroll: Float,
    val vScroll: Float
)

data class ErrorData(
    val errorCode: Int,
    val message: String
)

// ============================================================
// 控制协议 编解码器
// ============================================================
object ControlProtocolCodec {
    const val HEADER_SIZE = 5
    const val MAX_PAYLOAD = 4 * 1024 * 1024  // 4MB — 使用 4 字节长度字段，避免大帧溢出

    fun encode(type: ControlMessageType, payload: ByteArray = ByteArray(0)): ByteArray {
        val buffer = ByteBuffer.allocate(HEADER_SIZE + payload.size)
        buffer.order(ByteOrder.BIG_ENDIAN)
        buffer.put(type.code)
        buffer.putInt(payload.size)  // 4 字节无符号长度，替代 toShort 溢出 bug
        if (payload.isNotEmpty()) buffer.put(payload)
        return buffer.array()
    }

    fun decodeHeader(data: ByteArray): Pair<ControlMessageType, Int>? {
        if (data.size < HEADER_SIZE) return null
        val type = ControlMessageType.fromCode(data[0]) ?: return null
        val len = ((data[1].toInt() and 0xFF) shl 24) or
                  ((data[2].toInt() and 0xFF) shl 16) or
                  ((data[3].toInt() and 0xFF) shl 8) or
                  (data[4].toInt() and 0xFF)
        return type to len
    }

    // Touch Event
    fun encodeTouchEvent(e: TouchEventData): ByteArray {
        val buf = ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN)
        buf.put(e.action.code)
        buf.put(e.pointerId.toByte())
        buf.putShort((e.x * 10000).toInt().toShort())
        buf.putShort((e.y * 10000).toInt().toShort())
        buf.putShort((e.pressure * 1000).toInt().toShort())
        buf.putLong(e.eventTime)
        return encode(ControlMessageType.TOUCH_EVENT, buf.array())
    }

    fun decodeTouchEvent(payload: ByteArray): TouchEventData {
        val buf = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        val actionCode = buf.get()
        return TouchEventData(
            action = TouchAction.entries.first { it.code == actionCode },
            pointerId = buf.get().toInt() and 0xFF,
            x = (buf.short.toInt() and 0xFFFF) / 10000f,
            y = (buf.short.toInt() and 0xFFFF) / 10000f,
            pressure = (buf.short.toInt() and 0xFFFF) / 1000f,
            eventTime = buf.long
        )
    }

    // Key Event
    fun encodeKeyEvent(e: KeyEventData): ByteArray {
        val buf = ByteBuffer.allocate(17).order(ByteOrder.BIG_ENDIAN)
        buf.put(e.action.code)
        buf.putInt(e.keyCode)
        buf.putInt(e.metaState)
        buf.putLong(e.eventTime)
        return encode(ControlMessageType.KEY_EVENT, buf.array())
    }

    fun decodeKeyEvent(payload: ByteArray): KeyEventData {
        val buf = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        val actionCode = buf.get()
        return KeyEventData(
            action = KeyAction.entries.first { it.code == actionCode },
            keyCode = buf.int,
            metaState = buf.int,
            eventTime = buf.long
        )
    }

    // Screen Info
    fun encodeScreenInfo(info: ScreenInfoData): ByteArray {
        val buf = ByteBuffer.allocate(19).order(ByteOrder.BIG_ENDIAN)
        buf.putInt(info.width)
        buf.putInt(info.height)
        buf.putShort(info.dpi.toShort())
        buf.put(info.frameRate.toByte())
        buf.put(info.codec.code)
        buf.putInt(info.maxBitrateKbps)
        buf.put(if (info.hasAudio) 1.toByte() else 0)
        buf.put(info.audioCodec.code)
        buf.put((info.rotation / 90).toByte())
        return encode(ControlMessageType.SCREEN_INFO, buf.array())
    }

    fun decodeScreenInfo(payload: ByteArray): ScreenInfoData {
        val buf = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        val width = buf.int
        val height = buf.int
        val dpi = buf.short.toInt() and 0xFFFF
        val frameRate = buf.get().toInt() and 0xFF
        val codecCode = buf.get()
        val maxBitrateKbps = buf.int
        val hasAudio = buf.get() == 1.toByte()
        val audioCodecCode = buf.get()
        val rotation = (buf.get().toInt() and 0xFF) * 90
        return ScreenInfoData(
            width = width,
            height = height,
            dpi = dpi,
            frameRate = frameRate,
            codec = VideoCodec.entries.first { it.code == codecCode },
            maxBitrateKbps = maxBitrateKbps,
            hasAudio = hasAudio,
            audioCodec = AudioCodec.entries.first { it.code == audioCodecCode },
            rotation = rotation
        )
    }

    // Device Hello
    fun encodeDeviceHello(h: DeviceHelloData): ByteArray {
        val nameBytes = h.deviceName.toByteArray(Charsets.UTF_8)
        val buf = ByteBuffer.allocate(2 + nameBytes.size)
        buf.put(h.protocolVersion.toByte())
        buf.put(h.deviceType.code)
        buf.put(nameBytes)
        return encode(ControlMessageType.DEVICE_HELLO, buf.array())
    }

    fun decodeDeviceHello(payload: ByteArray): DeviceHelloData {
        val name = String(payload, 2, payload.size - 2, Charsets.UTF_8)
        return DeviceHelloData(
            protocolVersion = payload[0].toInt() and 0xFF,
            deviceType = DeviceType.fromCode(payload[1]),
            deviceName = name
        )
    }

    // Heartbeat
    fun encodeHeartbeat(ts: Long = System.currentTimeMillis()): ByteArray {
        val buf = ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN)
        buf.putLong(ts)
        return encode(ControlMessageType.HEARTBEAT, buf.array())
    }

    fun encodeHeartbeatAck(ts: Long = System.currentTimeMillis()): ByteArray {
        val buf = ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN)
        buf.putLong(ts)
        return encode(ControlMessageType.HEARTBEAT_ACK, buf.array())
    }

    fun decodeHeartbeat(payload: ByteArray): Long {
        return ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN).long
    }

    // Mouse Event
    fun encodeMouseEvent(e: MouseEventData): ByteArray {
        val buf = ByteBuffer.allocate(6).order(ByteOrder.BIG_ENDIAN)
        buf.put(e.action.code)
        buf.putShort((e.x * 10000).toInt().toShort())
        buf.putShort((e.y * 10000).toInt().toShort())
        buf.put(e.button.toByte())
        return encode(ControlMessageType.MOUSE_EVENT, buf.array())
    }

    fun decodeMouseEvent(payload: ByteArray): MouseEventData {
        val buf = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        val actionCode = buf.get()
        return MouseEventData(
            action = MouseAction.entries.first { it.code == actionCode },
            x = (buf.short.toInt() and 0xFFFF) / 10000f,
            y = (buf.short.toInt() and 0xFFFF) / 10000f,
            button = buf.get().toInt() and 0xFF
        )
    }

    fun decodeScrollEvent(payload: ByteArray): ScrollEventData {
        val buf = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        return ScrollEventData(
            x = (buf.short.toInt() and 0xFFFF) / 10000f,
            y = (buf.short.toInt() and 0xFFFF) / 10000f,
            hScroll = buf.short / 100f,
            vScroll = buf.short / 100f
        )
    }

    // Error
    fun encodeError(code: Int, msg: String): ByteArray {
        val msgBytes = msg.toByteArray(Charsets.UTF_8)
        val buf = ByteBuffer.allocate(4 + msgBytes.size).order(ByteOrder.BIG_ENDIAN)
        buf.putInt(code)
        buf.put(msgBytes)
        return encode(ControlMessageType.ERROR, buf.array())
    }
}

// ============================================================
// 控制连接 — 管理单个 TCP 控制通道
// ============================================================
class ControlConnection(
    private val socket: Socket,
    val connectionId: String = generateId()
) {
    companion object {
        private var counter = 0
        private fun generateId() = "${System.currentTimeMillis() % 100000}_${counter++}"
        private fun monotonicMillis() = System.nanoTime() / 1_000_000
    }

    private val input = DataInputStream(socket.getInputStream())
    private val output = DataOutputStream(socket.getOutputStream())
    private val running = AtomicBoolean(true)
    private val lastReceivedAt = AtomicLong(monotonicMillis())
    private val headerBuffer = ByteArray(ControlProtocolCodec.HEADER_SIZE)
    private val sendLock = Any()  // 防止多线程并发写入损坏帧

    var onMessage: ((ControlMessageType, ByteArray) -> Unit)? = null
    var onDisconnected: ((String) -> Unit)? = null
    @Volatile var lastDisconnectReason: String = "连接中"
        private set

    val isConnected: Boolean get() = socket.isConnected && !socket.isClosed && running.get()

    init {
        thread(name = "ctrl-conn-$connectionId", isDaemon = true) {
            readLoop()
        }
        thread(name = "ctrl-heartbeat-$connectionId", isDaemon = true) {
            heartbeatLoop()
        }
    }

    fun send(data: ByteArray) {
        if (!isConnected) return
        synchronized(sendLock) {
            try {
                output.write(data)
                output.flush()  // 大帧立即推送，避免 TCP 缓冲堆积
            } catch (e: Exception) {
                disconnect("发送失败: ${e.javaClass.simpleName}: ${e.message}")
            }
        }
    }

    fun sendMessage(type: ControlMessageType, payload: ByteArray = ByteArray(0)) {
        send(ControlProtocolCodec.encode(type, payload))
    }

    fun sendTouchEvent(e: TouchEventData) = send(ControlProtocolCodec.encodeTouchEvent(e))
    fun sendKeyEvent(e: KeyEventData) = send(ControlProtocolCodec.encodeKeyEvent(e))
    fun sendScreenInfo(info: ScreenInfoData) = send(ControlProtocolCodec.encodeScreenInfo(info))
    fun sendDeviceHello(h: DeviceHelloData) = send(ControlProtocolCodec.encodeDeviceHello(h))
    fun sendHeartbeat() = send(ControlProtocolCodec.encodeHeartbeat())
    fun sendHeartbeatAck() = send(ControlProtocolCodec.encodeHeartbeatAck())
    fun sendMouseEvent(e: MouseEventData) = send(ControlProtocolCodec.encodeMouseEvent(e))

    private fun readLoop() {
        var reason = "远端关闭连接"
        try {
            while (running.get() && isConnected) {
                input.readFully(headerBuffer)
                val decodedHeader = ControlProtocolCodec.decodeHeader(headerBuffer)
                if (decodedHeader == null) {
                    reason = "协议头无效"
                    break
                }
                val (type, payloadLen) = decodedHeader
                if (payloadLen !in 0..ControlProtocolCodec.MAX_PAYLOAD) {
                    reason = "负载长度无效: $payloadLen"
                    break
                }
                val payload = if (payloadLen > 0) {
                    ByteArray(payloadLen).also { input.readFully(it) }
                } else ByteArray(0)
                lastReceivedAt.set(monotonicMillis())

                if (type == ControlMessageType.HEARTBEAT && payload.size == Long.SIZE_BYTES) {
                    send(ControlProtocolCodec.encodeHeartbeatAck(ControlProtocolCodec.decodeHeartbeat(payload)))
                }
                try {
                    onMessage?.invoke(type, payload)
                } catch (e: Exception) {
                    Log.e("ControlConnection", "Message handler failed for $type", e)
                }
            }
        } catch (e: Exception) {
            reason = "读取失败: ${e.javaClass.simpleName}: ${e.message}"
        } finally {
            disconnect(reason)
        }
    }

    private fun heartbeatLoop() {
        try {
            while (running.get()) {
                Thread.sleep(3_000)
                if (!running.get()) break

                if (monotonicMillis() - lastReceivedAt.get() > 10_000) {
                    disconnect("连接超时: 10秒无入站数据")
                    break
                }

                sendHeartbeat()
            }
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
    }

    fun disconnect(reason: String = "本地主动断开") {
        if (!running.getAndSet(false)) return
        lastDisconnectReason = reason
        Log.w("ControlConnection", "Disconnected $connectionId: $reason")
        try { socket.close() } catch (_: Exception) {}
        onDisconnected?.invoke(connectionId)
    }
}

// ============================================================
// 连接管理器 — TCP Server / Client
// ============================================================
class ConnectionManager {
    companion object {
        const val DEFAULT_CONTROL_PORT = 35354
    }

    private val connections = ConcurrentHashMap<String, ControlConnection>()
    private var serverSocket: ServerSocket? = null
    private var listenThread: Thread? = null

    var onConnectionAccepted: ((ControlConnection) -> Unit)? = null
    var onConnectionEstablished: ((ControlConnection) -> Unit)? = null
    var onConnectionLost: ((String) -> Unit)? = null
    @Volatile var lastConnectionError: String? = null
        private set

    fun startListening(port: Int = DEFAULT_CONTROL_PORT) {
        if (serverSocket != null) return
        serverSocket = ServerSocket(port)
        listenThread = thread(name = "ctrl-server", isDaemon = true) {
            try {
                while (serverSocket?.isClosed == false) {
                    val client = serverSocket!!.accept()
                    val conn = ControlConnection(client)
                    conn.onDisconnected = { id ->
                        connections.remove(id)
                        onConnectionLost?.invoke(id)
                    }
                    connections[conn.connectionId] = conn
                    onConnectionAccepted?.invoke(conn)
                }
            } catch (_: Exception) {}
        }
    }

    fun connect(host: String, port: Int = DEFAULT_CONTROL_PORT): ControlConnection? {
        lastConnectionError = null
        val socket = Socket()
        return try {
            socket.tcpNoDelay = true           // 禁用 Nagle 算法，小包立即发送
            socket.sendBufferSize = 256 * 1024 // 256KB 发送缓冲
            socket.receiveBufferSize = 64 * 1024
            socket.connect(java.net.InetSocketAddress(host, port), 5_000)
            val conn = ControlConnection(socket)
            conn.onDisconnected = { id ->
                connections.remove(id)
                onConnectionLost?.invoke(id)
            }
            connections[conn.connectionId] = conn
            onConnectionEstablished?.invoke(conn)
            conn
        } catch (e: Exception) {
            try { socket.close() } catch (_: Exception) {}
            lastConnectionError = "${e.javaClass.simpleName}: ${e.message ?: "连接失败"}"
            null
        }
    }

    fun getConnections(): List<ControlConnection> = connections.values.toList()
    fun getConnection(id: String): ControlConnection? = connections[id]
    fun disconnect(id: String) = connections.remove(id)?.disconnect()

    fun stop() {
        connections.values.forEach { it.disconnect() }
        connections.clear()
        try { serverSocket?.close() } catch (_: Exception) {}
        serverSocket = null
        listenThread?.interrupt()
    }
}

// ============================================================
// 消息路由器
// ============================================================
class MessageRouter {
    private val handlers = mutableMapOf<ControlMessageType, MutableList<(ControlMessageType, ByteArray) -> Unit>>()

    fun register(type: ControlMessageType, handler: (ControlMessageType, ByteArray) -> Unit) {
        handlers.getOrPut(type) { mutableListOf() }.add(handler)
    }

    fun registerAll(handler: (ControlMessageType, ByteArray) -> Unit) {
        ControlMessageType.entries.forEach { register(it, handler) }
    }

    fun route(type: ControlMessageType, payload: ByteArray) {
        handlers[type]?.forEach { it(type, payload) }
    }

    fun bindToConnection(conn: ControlConnection) {
        conn.onMessage = { type, payload -> route(type, payload) }
    }

    fun bindToManager(manager: ConnectionManager) {
        manager.getConnections().forEach { bindToConnection(it) }
        manager.onConnectionAccepted = { bindToConnection(it) }
        manager.onConnectionEstablished = { bindToConnection(it) }
    }
}
