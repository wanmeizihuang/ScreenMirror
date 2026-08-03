package com.screenmirror.ui

import android.app.Activity
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import android.os.IBinder
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.screenmirror.core.DeviceDiscoveryService
import com.screenmirror.core.DiscoveredDevice
import com.screenmirror.core.ScreenCaptureService
import com.screenmirror.modules.ReverseControlService
import com.screenmirror.protocol.*
import kotlinx.coroutines.*

class MainActivity : ComponentActivity() {

    private var captureService: ScreenCaptureService? = null
    private var isBound = false
    private var isStreaming by mutableStateOf(false)
    private var connectionStatus by mutableStateOf("等待开始投屏")
    private var pendingDevice: DiscoveredDevice? = null
    private val scope = CoroutineScope(Dispatchers.Main + SupervisorJob())

    // UI 日志
    private val logLines = mutableListOf<String>()
    private var showLog by mutableStateOf(false)
    private fun appendLog(msg: String) {
        val ts = java.text.SimpleDateFormat("HH:mm:ss.SSS", java.util.Locale.getDefault())
            .format(java.util.Date())
        logLines.add("[$ts] $msg")
        if (logLines.size > 100) logLines.removeAt(0)
    }

    // 设备发现
    private val discoveryService by lazy {
        DeviceDiscoveryService(this, android.os.Build.MODEL ?: "Android")
    }

    // MediaProjection 权限请求
    private val projectionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK && result.data != null) {
            startScreenCapture(result.resultCode, result.data!!)
        }
    }

    private val serviceConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
            val binder = service as ScreenCaptureService.LocalBinder
            captureService = binder.getService()
            isBound = true
            // 触发待处理的启动请求
            pendingStart?.let { (code, data) ->
                pendingStart = null
                doStartCapture(code, data)
            }
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            captureService = null
            isBound = false
        }
    }

    // 待处理的启动请求（MediaProjection 授权完成时 captureService 可能还没绑定）
    private var pendingStart: Pair<Int, Intent>? = null

    private fun doStartCapture(resultCode: Int, data: Intent) {
        val device = pendingDevice ?: return
        val host = device.host
        val controlPort = 35354
        connectionStatus = "启动屏幕采集..."

        val connection = activeConnection
        if (connection == null) {
            connectionStatus = "未连接到 PC"
            return
        }

        val sendNal: (ByteArray) -> Unit = { nalData ->
            connection.sendMessage(ControlMessageType.VIDEO_FRAME, nalData)
            videoFramesSent++
            if (videoFramesSent == 1L || videoFramesSent % 30 == 0L) {
                appendLog("发送NAL #$videoFramesSent ${nalData.size}B")
                connectionStatus = "已发送 $videoFramesSent 帧"
            }
        }

        captureService?.startCapture(
            resultCode, data,
            host = host,
            sendNalCallback = sendNal,
            callback = { screenInfo ->
                connection.sendScreenInfo(screenInfo)
                connection.sendMessage(ControlMessageType.STREAM_START)
            }
        )
        isStreaming = true
        connectionStatus = "推流中"

        // 投屏期间阻止屏幕休眠
        window.addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    private var activeConnection: ControlConnection? = null
    private var videoFramesSent: Long = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 启动时清理超过 24 小时的诊断日志
        try {
            val logFile = java.io.File(getExternalFilesDir(null), "service_events.log")
            if (logFile.exists() && System.currentTimeMillis() - logFile.lastModified() > 86400000L) {
                logFile.delete()
                appendLog("已清理旧日志")
            }
        } catch (_: Exception) {}

        // 绑定投屏服务
        bindService(
            Intent(this, ScreenCaptureService::class.java),
            serviceConnection,
            Context.BIND_AUTO_CREATE
        )

        // 注册 mDNS + 监听 UDP 广播，让 PC 端能发现本机
        discoveryService.startAdvertising(port = 35354)

        setContent {
            ScreenMirrorApp()
        }
    }

    @OptIn(ExperimentalMaterial3Api::class)
    @Composable
    fun ScreenMirrorApp() {
        var devices by remember { mutableStateOf(listOf<DiscoveredDevice>()) }
        var selectedDevice by remember { mutableStateOf<DiscoveredDevice?>(null) }
        var isScanning by remember { mutableStateOf(false) }
        var manualIp by remember { mutableStateOf("") }
        var accessibilityEnabled by remember {
            mutableStateOf(isAccessibilityServiceEnabled())
        }

        val headerGradient = Brush.horizontalGradient(
            colors = listOf(Color(0xFF60A5FA), Color(0xFF2563EB))
        )

        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(Color(0xFFF5F8FB))
        ) {
            // ===== 顶部蓝色渐变标题栏 =====
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(headerGradient)
                    .padding(start = 24.dp, end = 24.dp, top = 48.dp, bottom = 36.dp)
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    // App 图标：白色圆角方形 + 手机轮廓
                    Box(
                        modifier = Modifier
                            .size(40.dp)
                            .clip(RoundedCornerShape(10.dp))
                            .background(Color.White.copy(alpha = 0.2f)),
                        contentAlignment = Alignment.Center
                    ) {
                        Text("📱", fontSize = 22.sp)
                    }
                    Spacer(modifier = Modifier.width(12.dp))
                    Text(
                        text = "ScreenMirror",
                        color = Color.White,
                        fontSize = 22.sp,
                        fontWeight = FontWeight.Bold
                    )
                }
            }

            // ===== 内容区（与头部有重叠效果） =====
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(horizontal = 20.dp)
                    .offset(y = (-20).dp)
            ) {
                // 自动扫描：未投屏时每 3 秒刷新一次设备列表
                LaunchedEffect(isStreaming) {
                    if (isStreaming) return@LaunchedEffect
                    while (true) {
                        discoveryService.startDiscovery()
                        discoveryService.scanByUdp()
                        delay(3000)
                        devices = discoveryService.discoveredDevices.values.toList()
                    }
                }

                // 状态卡片
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(20.dp),
                    colors = CardDefaults.cardColors(containerColor = Color.White),
                    elevation = CardDefaults.cardElevation(defaultElevation = 4.dp)
                ) {
                    Row(
                        modifier = Modifier.padding(20.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        // 左侧图标：琥珀色圆形背景 + 时钟
                        Box(
                            modifier = Modifier
                                .size(48.dp)
                                .clip(CircleShape)
                                .background(Color(0xFFFEF3C7)),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(if (isStreaming) "✅" else "🕐", fontSize = 22.sp)
                        }
                        Spacer(modifier = Modifier.width(14.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = if (isStreaming) "投屏进行中" else "等待开始投屏",
                                fontSize = 15.sp,
                                fontWeight = FontWeight.Medium,
                                color = Color(0xFF1a1a1a)
                            )
                            Spacer(modifier = Modifier.height(2.dp))
                            Text(
                                text = if (isStreaming) "正在将屏幕推送到 ${selectedDevice?.deviceName ?: "PC"}"
                                        else "扫描设备或手动输入 IP 连接",
                                fontSize = 12.sp,
                                color = Color(0xFF9CA3AF)
                            )
                        }
                        // 右侧指示点
                        Box(
                            modifier = Modifier
                                .size(10.dp)
                                .clip(CircleShape)
                                .background(if (isStreaming) Color(0xFF10B981) else Color(0xFFFBBF24))
                        )
                    }
                }

                // 无障碍提示
                if (!accessibilityEnabled && selectedDevice != null) {
                    Spacer(modifier = Modifier.height(8.dp))
                    TextButton(
                        onClick = { openAccessibilitySettings() },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("⚙️ 开启无障碍服务（反向控制）", fontSize = 12.sp, color = Color(0xFF6B7280))
                    }
                }

                Spacer(modifier = Modifier.height(20.dp))

                // ===== 操作按钮 =====
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    // 扫描设备 - 蓝色渐变主按钮
                    Button(
                        onClick = {
                            isScanning = true
                            discoveryService.startDiscovery()
                            discoveryService.scanByUdp()
                            scope.launch {
                                delay(3000)
                                devices = discoveryService.discoveredDevices.values.toList()
                                isScanning = false
                            }
                        },
                        enabled = !isStreaming,
                        modifier = Modifier.weight(1f).height(48.dp),
                        shape = RoundedCornerShape(14.dp),
                        contentPadding = PaddingValues(0.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = Color.Transparent,
                            disabledContainerColor = Color.Transparent
                        ),
                    ) {
                        Box(
                            modifier = Modifier
                                .fillMaxSize()
                                .then(
                                    if (isStreaming)
                                        Modifier.background(Color(0xFFE5E7EB), RoundedCornerShape(14.dp))
                                    else
                                        Modifier.background(headerGradient, RoundedCornerShape(14.dp))
                                ),
                            contentAlignment = Alignment.Center
                        ) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text("🔍", fontSize = 16.sp)
                                Spacer(modifier = Modifier.width(6.dp))
                                Text(
                                    if (isScanning) "扫描中..." else "扫描设备",
                                    color = if (isStreaming) Color(0xFF9CA3AF) else Color.White,
                                    fontSize = 14.sp,
                                    fontWeight = FontWeight.Medium
                                )
                            }
                        }
                    }

                    // 停止投屏 - 白色次按钮
                    OutlinedButton(
                        onClick = { stopStreaming() },
                        enabled = isStreaming,
                        modifier = Modifier.weight(1f).height(48.dp),
                        shape = RoundedCornerShape(14.dp),
                        border = androidx.compose.foundation.BorderStroke(1.dp, Color(0xFFE5E7EB)),
                        colors = ButtonDefaults.outlinedButtonColors(
                            containerColor = Color.White,
                            disabledContainerColor = Color.White
                        )
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("⬜", fontSize = 14.sp, color = if (isStreaming) Color(0xFF6B7280) else Color(0xFF9CA3AF))
                            Spacer(modifier = Modifier.width(6.dp))
                            Text(
                                "停止投屏",
                                color = if (isStreaming) Color(0xFF374151) else Color(0xFF9CA3AF),
                                fontSize = 14.sp,
                                fontWeight = FontWeight.Medium
                            )
                        }
                    }
                }

                Spacer(modifier = Modifier.height(24.dp))

                // ===== "可用设备" 标题栏 =====
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Box(
                        modifier = Modifier
                            .width(3.dp)
                            .height(14.dp)
                            .background(Color(0xFF3B82F6), RoundedCornerShape(2.dp))
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(
                        text = "可用设备",
                        fontSize = 14.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = Color(0xFF1F2937)
                    )
                    Spacer(modifier = Modifier.weight(1f))
                    // 设备数徽章
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(10.dp))
                            .background(Color(0xFFDBEAFE))
                            .padding(horizontal = 10.dp, vertical = 3.dp)
                    ) {
                        Text(
                            text = "已发现 ${devices.size} 台",
                            fontSize = 11.sp,
                            color = Color(0xFF1E40AF)
                        )
                    }
                }

                Spacer(modifier = Modifier.height(12.dp))

                // ===== 手动输入 IP =====
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    OutlinedTextField(
                        value = manualIp,
                        onValueChange = { manualIp = it },
                        placeholder = { Text("手动输入 PC IP 地址", fontSize = 13.sp, color = Color(0xFF9CA3AF)) },
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                        shape = RoundedCornerShape(12.dp),
                        textStyle = LocalTextStyle.current.copy(fontSize = 13.sp),
                        colors = OutlinedTextFieldDefaults.colors(
                            unfocusedBorderColor = Color(0xFFE5E7EB),
                            focusedBorderColor = Color(0xFF3B82F6)
                        )
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    OutlinedButton(
                        onClick = {
                            val ip = manualIp.trim()
                            if (ip.isNotEmpty()) {
                                val manual = DiscoveredDevice(
                                    deviceName = "手动输入",
                                    deviceType = "windows",
                                    host = ip,
                                    port = 35354,
                                )
                                devices = devices + manual
                                pendingDevice = manual
                                connectionStatus = "已添加 $ip"
                            }
                        },
                        shape = RoundedCornerShape(10.dp),
                        border = androidx.compose.foundation.BorderStroke(1.dp, Color(0xFF3B82F6)),
                        contentPadding = PaddingValues(horizontal = 18.dp, vertical = 8.dp)
                    ) {
                        Text("添加", color = Color(0xFF3B82F6), fontSize = 13.sp)
                    }
                }

                Spacer(modifier = Modifier.height(16.dp))

                // ===== 设备列表 =====
                LazyColumn(
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                    modifier = Modifier.weight(1f)
                ) {
                    items(devices) { device ->
                        DeviceCard(
                            device = device,
                            isSelected = device == selectedDevice,
                            onClick = {
                                selectedDevice = device
                                requestMediaProjection(device)
                            }
                        )
                    }
                }

                // ===== 底部信息 =====
                Spacer(modifier = Modifier.height(16.dp))
                Text(
                    text = "Design by zzh · ScreenMirror", // footer
                    fontSize = 11.sp,
                    color = Color(0xFF9CA3AF),
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(bottom = 16.dp),
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center
                )
            }
        }
    }

    @Composable
    fun DeviceCard(
        device: DiscoveredDevice,
        isSelected: Boolean,
        onClick: () -> Unit
    ) {
        // 设备类型决定图标背景色
        val isPc = device.deviceType == "windows"
        val iconBg = if (isPc) Color(0xFFE0E7FF) else Color(0xFFDBEAFE)
        val iconFg = if (isPc) Color(0xFF6366F1) else Color(0xFF2563EB)
        val iconEmoji = if (isPc) "💻" else "📱"

        Card(
            modifier = Modifier
                .fillMaxWidth()
                .clickable(enabled = !isStreaming) { onClick() },
            shape = RoundedCornerShape(16.dp),
            colors = CardDefaults.cardColors(
                containerColor = if (isSelected) Color(0xFFF0F9FF) else Color.White
            ),
            elevation = CardDefaults.cardElevation(defaultElevation = 2.dp)
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(16.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                // 设备图标
                Box(
                    modifier = Modifier
                        .size(48.dp)
                        .clip(RoundedCornerShape(14.dp))
                        .background(iconBg),
                    contentAlignment = Alignment.Center
                ) {
                    Text(iconEmoji, fontSize = 22.sp)
                }

                Spacer(modifier = Modifier.width(14.dp))

                // 设备信息
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = device.deviceName,
                        fontWeight = FontWeight.SemiBold,
                        fontSize = 15.sp,
                        color = Color(0xFF1F2937)
                    )
                    Spacer(modifier = Modifier.height(2.dp))
                    Text(
                        text = "${device.host} : ${device.port}",
                        fontSize = 12.sp,
                        color = Color(0xFF9CA3AF)
                    )
                }

                // 投屏按钮
                if (!isStreaming) {
                    Button(
                        onClick = {
                            pendingDevice = device
                            requestMediaProjection(device)
                        },
                        shape = RoundedCornerShape(10.dp),
                        contentPadding = PaddingValues(horizontal = 18.dp, vertical = 6.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = Color(0xFF10B981)
                        )
                    ) {
                        Text("投屏", color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.Medium)
                    }
                } else if (isSelected) {
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(10.dp))
                            .background(Color(0xFFD1FAE5))
                            .padding(horizontal = 18.dp, vertical = 8.dp)
                    ) {
                        Text("进行中", color = Color(0xFF047857), fontSize = 13.sp, fontWeight = FontWeight.Medium)
                    }
                }
            }
        }
    }

    // ============================================================
    // 投屏流程
    // ============================================================
    private fun requestMediaProjection(device: DiscoveredDevice) {
        pendingDevice = device
        try {
            val projectionManager = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val intent = projectionManager.createScreenCaptureIntent()
            if (intent != null) {
                projectionLauncher.launch(intent)
            } else {
                connectionStatus = "此设备不支持录屏"
            }
        } catch (e: Exception) {
            connectionStatus = "启动失败: ${e.message}"
        }
    }

    private fun startScreenCapture(resultCode: Int, data: Intent) {
        val device = pendingDevice
        if (device == null) {
            connectionStatus = "未选择设备"
            return
        }
        if (resultCode != Activity.RESULT_OK) {
            connectionStatus = "录屏授权被取消"
            return
        }
        val controlPort = 35354
        val host = device.host
        connectionStatus = "连接 $host:$controlPort..."

        // 先建立控制连接
        scope.launch {
            try {
                val connectionManager = ConnectionManager()
                val connection = connectionManager.connect(host, controlPort)

            if (connection != null) {
                activeConnection = connection
                connectionStatus = "已连接，准备推流"
                appendLog("TCP已连接 ${host}:${controlPort}")
                appendLog("协议头=${com.screenmirror.protocol.ControlProtocolCodec.HEADER_SIZE}B")

                // 路由 PC 端反向控制指令到无障碍服务
                connection.onMessage = msgHandler@{ type, payload ->
                    val svc = ReverseControlService.getInstance() ?: return@msgHandler
                    when (type) {
                        ControlMessageType.MOUSE_EVENT ->
                            svc.handleMouseEvent(ControlProtocolCodec.decodeMouseEvent(payload))
                        ControlMessageType.KEY_EVENT ->
                            svc.handleKeyEvent(ControlProtocolCodec.decodeKeyEvent(payload))
                        ControlMessageType.TOUCH_EVENT ->
                            svc.handleTouchEvent(ControlProtocolCodec.decodeTouchEvent(payload))
                        else -> {}
                    }
                }

                // 监听 TCP 断开：PC 端主动断开或网络异常时自动停止投屏
                connection.onDisconnected = { _ ->
                    appendLog("TCP 连接断开，停止投屏")
                    runOnUiThread { stopStreaming() }
                }

                // 发送握手
                connection.sendDeviceHello(
                    DeviceHelloData(
                        deviceType = DeviceType.ANDROID,
                        deviceName = android.os.Build.MODEL ?: "Android"
                    )
                )

                // 启动屏幕采集服务
                val intent = Intent(this@MainActivity, ScreenCaptureService::class.java)
                bindService(intent, serviceConnection, Context.BIND_AUTO_CREATE)

                // 保存待处理启动请求，等 ServiceConnection 回调时再触发
                pendingStart = Pair(resultCode, data)

                // 如果已经绑定，立即触发
                if (captureService != null) {
                    pendingStart = null
                    doStartCapture(resultCode, data)
                }
            } else {
                connectionStatus = "连接失败"
            }
            } catch (e: Exception) {
                connectionStatus = "连接异常: ${e.message}"
            }
        }
    }

    private var isStopping = false

    private fun stopStreaming() {
        if (isStopping) return
        if (!isStreaming) return
        isStopping = true
        isStreaming = false
        connectionStatus = "已停止"
        videoFramesSent = 0L

        window.clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // 先取引用再置 null，防止 disconnect()→onDisconnected→stopStreaming 重入
        val conn = activeConnection
        val svc = captureService
        activeConnection = null

        // 后台清理：断开 TCP + 停止编码（不 stopService，否则 HarmonyOS 杀进程）
        scope.launch(Dispatchers.IO) {
            try { conn?.disconnect() } catch (_: Exception) {}
            try { svc?.stopCapture() } catch (_: Exception) {}
            isStopping = false
        }
    }

    // ============================================================
    // 无障碍服务
    // ============================================================
    private fun isAccessibilityServiceEnabled(): Boolean {
        val enabledServices = Settings.Secure.getString(
            contentResolver,
            Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
        )
        return enabledServices?.contains(packageName) == true
    }

    private fun openAccessibilitySettings() {
        startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        connectionStatus = "请在无障碍设置中开启 ScreenMirror"
    }

    override fun onPause() {
        super.onPause()
        captureService?.isAppInForeground = false
        if (isStreaming) captureService?.showOverlay()
    }

    override fun onResume() {
        super.onResume()
        captureService?.isAppInForeground = true
        if (isStreaming) captureService?.hideOverlay()
    }

    override fun onDestroy() {
        stopStreaming()  // 确保资源释放
        if (isBound) unbindService(serviceConnection)
        try {
            val intent = Intent(this, ScreenCaptureService::class.java)
            stopService(intent)
        } catch (_: Exception) {}
        discoveryService.stop()
        scope.cancel()
        super.onDestroy()
    }
}
