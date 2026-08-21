package com.screenmirror.core

import android.app.*
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.DisplayMetrics
import android.view.Gravity
import android.view.Surface
import android.view.View
import android.view.WindowManager
import android.view.MotionEvent
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.widget.ImageView
import android.widget.LinearLayout
import com.screenmirror.protocol.ScreenInfoData
import com.screenmirror.protocol.VideoCodec
import kotlinx.coroutines.*
import java.io.File
import java.io.FileWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

class ScreenCaptureService : Service() {

    private val binder = LocalBinder()
    private var mediaProjection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var mediaCodec: MediaCodec? = null
    private var videoSocket: Socket? = null
    var videoConnected: Boolean = false; private set
    private var audioSocket: Socket? = null
    private val isRunning = AtomicBoolean(false)
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    private var keepAliveView: View? = null
    private var keepAliveParams: WindowManager.LayoutParams? = null
    private var windowManager: WindowManager? = null
    private var wakeLock: PowerManager.WakeLock? = null

    private var screenWidth = 0
    private var screenHeight = 0
    private var screenDpi = 0
    private var targetHost: String = ""
    private var targetBitrate = 8_000_000
    private var targetFps = 30
    private var resultCallback: ((ScreenInfoData) -> Unit)? = null
    private var captureErrorCallback: ((String) -> Unit)? = null

    inner class LocalBinder : Binder() {
        fun getService(): ScreenCaptureService = this@ScreenCaptureService
    }

    override fun onBind(intent: Intent?): IBinder = binder

    // ---- 诊断日志（写到外部目录，进程被杀后仍可查看） ----
    private val logFile by lazy { File(getExternalFilesDir(null), "service_events.log") }
    private fun logd(msg: String) {
        val ts = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.getDefault()).format(Date())
        val line = "[$ts] pid=${android.os.Process.myPid()} $msg"
        android.util.Log.i("ScreenCapture", line)
        try { FileWriter(logFile, true).use { it.write(line + "\n") } }
        catch (_: Exception) {}
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        windowManager = getSystemService(WINDOW_SERVICE) as WindowManager
        logd("onCreate")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        logd("onStartCommand flags=$flags startId=$startId")
        return START_STICKY
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        logd("onTaskRemoved!!! user swiped away or activity removed")
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        logd("onDestroy isRunning=${isRunning.get()}")
        stopCapture()
        hideKeepAliveOverlay()
        scope.cancel()
        super.onDestroy()
    }

    // ---- capture ----

    private var sendNal: ((ByteArray) -> Unit)? = null

    fun startCapture(
        resultCode: Int, data: Intent, host: String,
        vPort: Int = 35355, aPort: Int = 35356,
        bitrate: Int = 8_000_000, fps: Int = 30,
        sendNalCallback: ((ByteArray) -> Unit)? = null,
        callback: (ScreenInfoData) -> Unit,
        errorCallback: (String) -> Unit = {}
    ) {
        check(isRunning.compareAndSet(false, true)) { "投屏已经在运行" }
        targetHost = host; targetBitrate = bitrate; targetFps = fps
        resultCallback = callback; sendNal = sendNalCallback
        captureErrorCallback = errorCallback
        logd("startCapture host=$host")

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                startForeground(
                    NOTIFICATION_ID,
                    createNotification(),
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                )
            } else {
                startForeground(NOTIFICATION_ID, createNotification())
            }
            acquireWakeLock()

            val metrics = getRealDisplayMetrics()
            val maxDim = 1280
            val rawW = metrics.widthPixels; val rawH = metrics.heightPixels
            if (rawW >= rawH) { screenWidth = maxDim; screenHeight = maxDim * rawH / rawW }
            else { screenHeight = maxDim; screenWidth = maxDim * rawW / rawH }
            screenWidth = (screenWidth / 2) * 2
            screenHeight = (screenHeight / 2) * 2
            if (screenWidth < 176) screenWidth = 176
            if (screenHeight < 144) screenHeight = 144
            screenDpi = metrics.densityDpi
            logd("resolution raw=${rawW}x${rawH} scaled=${screenWidth}x${screenHeight}")

            val pm = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            mediaProjection = pm.getMediaProjection(resultCode, data)
                ?: error("无法创建 MediaProjection")
            mediaProjection?.registerCallback(ProjectionCallback(), null)

            if (sendNal != null) {
                videoConnected = true
                startEncoding()
            } else {
                scope.launch {
                    for (attempt in 1..5) {
                        try {
                            videoSocket = Socket(targetHost, vPort).apply {
                                tcpNoDelay = true; sendBufferSize = 256 * 1024
                            }
                            videoConnected = true
                            startEncoding()
                            break
                        } catch (e: Exception) {
                            delay(500L * attempt)
                        }
                    }
                    if (!videoConnected) {
                        captureErrorCallback?.invoke("无法连接视频通道")
                        stopCapture()
                    }
                }
            }
        } catch (e: Exception) {
            logd("startCapture FAILED: ${e.javaClass.simpleName}: ${e.message}")
            stopCapture()
            throw e
        }
    }

    @Suppress("DEPRECATION")
    private fun getRealDisplayMetrics(): DisplayMetrics {
        return DisplayMetrics().also { metrics ->
            (getSystemService(WINDOW_SERVICE) as WindowManager)
                .defaultDisplay
                .getRealMetrics(metrics)
        }
    }

    private fun startEncoding() {
        val format = MediaFormat.createVideoFormat(
            MediaFormat.MIMETYPE_VIDEO_AVC, screenWidth, screenHeight
        ).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, targetBitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, targetFps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) setInteger(MediaFormat.KEY_LATENCY, 1)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) setInteger(MediaFormat.KEY_PRIORITY, 0)
        }
        mediaCodec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        mediaCodec?.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        val inputSurface = mediaCodec?.createInputSurface()
        mediaCodec?.start()
        virtualDisplay = mediaProjection?.createVirtualDisplay(
            "ScreenMirror", screenWidth, screenHeight, screenDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, inputSurface, null, null)
        val info = ScreenInfoData(
            width = screenWidth, height = screenHeight, dpi = screenDpi,
            frameRate = targetFps, codec = VideoCodec.H264, maxBitrateKbps = targetBitrate / 1000,
            rotation = (getSystemService(WINDOW_SERVICE) as android.view.WindowManager).defaultDisplay.rotation * 90)
        scope.launch(Dispatchers.IO) {
            // Send stream metadata before the first encoded frame. The callback writes to
            // the control socket, so it must never run on Android's main thread.
            resultCallback?.invoke(info)
            encodeAndSend()
        }
    }

    private suspend fun encodeAndSend() {
        val codec = mediaCodec ?: return
        val bi = MediaCodec.BufferInfo()
        val cb = sendNal
        try {
            while (isRunning.get()) {
                val idx = codec.dequeueOutputBuffer(bi, 10_000L)
                if (idx >= 0) {
                    val buf = codec.getOutputBuffer(idx) ?: continue
                    if (bi.size > 0 && cb != null) {
                        val raw = ByteArray(bi.size)
                        buf.position(bi.offset); buf.get(raw, 0, bi.size)
                        try { cb(normalizeToAnnexB(raw)) } catch (_: Exception) {}
                    }
                    codec.releaseOutputBuffer(idx, false)
                    if (bi.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                } else if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    val nf = codec.outputFormat
                    nf.getByteBuffer("csd-0")?.let { csd ->
                        val sps = ByteArray(csd.remaining()); csd.get(sps)
                        try { cb?.invoke(normalizeToAnnexB(sps)) } catch (_: Exception) {}
                    }
                    nf.getByteBuffer("csd-1")?.let { csd ->
                        val pps = ByteArray(csd.remaining()); csd.get(pps)
                        try { cb?.invoke(normalizeToAnnexB(pps)) } catch (_: Exception) {}
                    }
                }
            }
        } catch (e: Exception) {
            logd("encodeAndSend EXCEPTION: ${e.message}")
            captureErrorCallback?.invoke("编码发送失败: ${e.message ?: e.javaClass.simpleName}")
        } finally {
            logd("encodeAndSend loop exited, calling stopCapture")
            stopCapture()
        }
    }

    @Synchronized
    fun stopCapture() {
        val wasRunning = isRunning.getAndSet(false)
        if (!wasRunning && mediaCodec == null && mediaProjection == null && virtualDisplay == null) return
        logd("stopCapture called")
        try { virtualDisplay?.release(); virtualDisplay = null } catch (_: Exception) {}
        try { mediaCodec?.stop(); mediaCodec?.release(); mediaCodec = null } catch (_: Exception) {}
        try { videoSocket?.close() } catch (_: Exception) {}
        try { audioSocket?.close() } catch (_: Exception) {}
        val projection = mediaProjection
        mediaProjection = null
        try { projection?.stop() } catch (_: Exception) {}
        videoConnected = false
        releaseWakeLock()
        hideKeepAliveOverlay()
        stopForeground(STOP_FOREGROUND_REMOVE)
    }

    private fun acquireWakeLock() {
        try {
            val pm = getSystemService(POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "ScreenMirror::CaptureLock")
            wakeLock?.setReferenceCounted(false)
            wakeLock?.acquire(30 * 60 * 1000L)
            logd("WakeLock acquired")
        } catch (e: Exception) { logd("WakeLock FAILED: ${e.message}") }
    }
    private fun releaseWakeLock() {
        try { if (wakeLock?.isHeld == true) { wakeLock?.release(); logd("WakeLock released") } }
        catch (_: Exception) {}
        wakeLock = null
    }

    private inner class ProjectionCallback : MediaProjection.Callback() {
        override fun onStop() {
            logd("ProjectionCallback.onStop — system revoked screen capture!")
            stopCapture()
        }
    }

    // ---- floating return button ----

    // 当前 App 是否在前台（由 MainActivity 控制）
    @Volatile var isAppInForeground = true

    fun showOverlay() {
        if (isAppInForeground) { logd("overlay skipped: app is in foreground"); return }
        showKeepAliveOverlay()
    }
    fun hideOverlay() { hideKeepAliveOverlay() }

    private fun showKeepAliveOverlay() {
        if (!android.provider.Settings.canDrawOverlays(this)) {
            logd("no overlay permission — showing toast")
            scope.launch(Dispatchers.Main) {
                try {
                    android.widget.Toast.makeText(
                        this@ScreenCaptureService,
                        "请授予「显示在其他应用上层」权限以显示返回按钮",
                        android.widget.Toast.LENGTH_LONG
                    ).show()
                } catch (_: Exception) {}
            }
            return
        }
        try {
            if (keepAliveView != null) { logd("overlay already shown, skip"); return }
            val container = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER
                background = GradientDrawable().apply {
                    setColor(Color.argb(140, 50, 50, 55)); cornerRadius = 72f
                }
            }
            val icon = ImageView(this).apply {
                setImageResource(if (Build.VERSION.SDK_INT >= 21)
                    android.R.drawable.ic_menu_revert else android.R.drawable.ic_media_previous)
                setColorFilter(Color.argb(220, 255, 255, 255))
                setPadding(32, 32, 32, 32)
            }
            container.addView(icon, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT))

            val disp = resources.displayMetrics
            val screenW = disp.widthPixels; val screenH = disp.heightPixels
            val size = 144

            val params = WindowManager.LayoutParams().apply {
                type = if (Build.VERSION.SDK_INT >= 26)
                    WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                else WindowManager.LayoutParams.TYPE_PHONE
                flags = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                        WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                        WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or
                        WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or
                        WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                format = android.graphics.PixelFormat.TRANSLUCENT
                width = size; height = size
                x = screenW - size - 8   // 默认靠右完整显示
                y = (screenH - size) / 2
                gravity = Gravity.START or Gravity.TOP
            }
            keepAliveParams = params

            var downX = 0; var downY = 0
            var downRawX = 0f; var downRawY = 0f
            var hasMoved = false
            container.setOnTouchListener { _, ev ->
                when (ev.action and MotionEvent.ACTION_MASK) {
                    MotionEvent.ACTION_DOWN -> {
                        downX = params.x; downY = params.y
                        downRawX = ev.rawX; downRawY = ev.rawY
                        hasMoved = false
                        logd("overlay touch DOWN raw=(${ev.rawX},${ev.rawY})")
                        true
                    }
                    MotionEvent.ACTION_MOVE -> {
                        val dx = (ev.rawX - downRawX).toInt()
                        val dy = (ev.rawY - downRawY).toInt()
                        if (Math.abs(dx) > 8 || Math.abs(dy) > 8) hasMoved = true
                        params.x = downX + dx
                        params.y = downY + dy
                        try { windowManager?.updateViewLayout(container, params) } catch (_: Exception) {}
                        true
                    }
                    MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                        if (hasMoved) {
                            // 拖拽松手 → 靠边吸附
                            val midX = params.x + size / 2
                            params.x = if (midX < screenW / 2) 8 else screenW - size - 8
                            params.y = params.y.coerceIn(0, screenH - size)
                            try { windowManager?.updateViewLayout(container, params) } catch (_: Exception) {}
                            logd("overlay snapped x=${params.x}")
                        } else {
                            // 点击 → 直接返回 APP
                            logd("overlay CLICK — returning to app")
                            try {
                                val intent = packageManager.getLaunchIntentForPackage(packageName)
                                if (intent != null) {
                                    intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or
                                                    Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or
                                                    Intent.FLAG_ACTIVITY_SINGLE_TOP)
                                    startActivity(intent)
                                    logd("overlay: startActivity sent")
                                } else {
                                    logd("overlay: getLaunchIntentForPackage returned null!")
                                }
                            } catch (ex: Exception) {
                                logd("overlay startActivity FAILED: ${ex.message}")
                            }
                        }
                        true
                    }
                    else -> false
                }
            }
            windowManager?.addView(container, params)
            keepAliveView = container
            logd("overlay created size=$size x=${params.x} y=${params.y}")
        } catch (e: Exception) {
            logd("overlay FAILED: ${e.message}")
        }
    }

    private fun hideKeepAliveOverlay() {
        val v = keepAliveView
        keepAliveView = null  // ★ 先置 null，防止 removeView 失败导致无法重建
        keepAliveParams = null
        if (v != null) {
            try { windowManager?.removeView(v) } catch (_: Exception) {}
        }
        logd("overlay removed")
    }

    // ---- NAL normalization, notification ----

    private fun normalizeToAnnexB(data: ByteArray): ByteArray {
        if (data.size < 4) return data
        if ((data[0] == 0.toByte() && data[1] == 0.toByte() && data[2] == 0.toByte() && data[3] == 1.toByte()) ||
            (data[0] == 0.toByte() && data[1] == 0.toByte() && data[2] == 1.toByte())) return data
        val sc = byteArrayOf(0x00, 0x00, 0x00, 0x01)
        val out = ArrayList<Byte>(data.size + 32)
        var off = 0
        while (off + 4 <= data.size) {
            val len = ((data[off].toInt() and 0xFF) shl 24) or ((data[off+1].toInt() and 0xFF) shl 16) or
                      ((data[off+2].toInt() and 0xFF) shl 8) or (data[off+3].toInt() and 0xFF)
            off += 4
            if (len <= 0 || off + len > data.size) break
            out.addAll(sc.toList())
            for (i in 0 until len) out.add(data[off + i])
            off += len
        }
        return out.toByteArray()
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            val ch = NotificationChannel(CHANNEL_ID, "投屏服务", NotificationManager.IMPORTANCE_LOW).apply {
                description = "ScreenMirror running"; setShowBadge(false)
                lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            }
            getSystemService(NotificationManager::class.java).createNotificationChannel(ch)
        }
    }

    private fun createNotification(): Notification {
        val intent = packageManager.getLaunchIntentForPackage(packageName)
        val pi = if (intent != null) PendingIntent.getActivity(
            this, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE) else null
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("屏幕投屏中").setContentText("正在投屏到 $targetHost")
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setOngoing(true).setPriority(Notification.PRIORITY_LOW)
            .setCategory(Notification.CATEGORY_SERVICE)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .setContentIntent(pi).setShowWhen(true).build()
    }

    companion object { private const val CHANNEL_ID = "screen_mirror_capture"; private const val NOTIFICATION_ID = 1001 }
}
