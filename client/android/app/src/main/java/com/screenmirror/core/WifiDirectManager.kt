package com.screenmirror.core

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.NetworkInfo
import android.net.wifi.WpsInfo
import android.net.wifi.p2p.WifiP2pConfig
import android.net.wifi.p2p.WifiP2pDevice
import android.net.wifi.p2p.WifiP2pDeviceList
import android.net.wifi.p2p.WifiP2pGroup
import android.net.wifi.p2p.WifiP2pInfo
import android.net.wifi.p2p.WifiP2pManager
import android.util.Log
import kotlinx.coroutines.*

/**
 * 发现的 P2P 设备
 */
data class P2pDiscoveredDevice(
    val device: WifiP2pDevice,
    val deviceName: String = device.deviceName,
    val deviceAddress: String = device.deviceAddress,
    val isGroupOwner: Boolean = device.isGroupOwner
)

/**
 * WiFi Direct (P2P) 管理器
 * 
 * 两种模式：
 * 1. GO 模式：手机作为 Group Owner，PC 直接连接（无需路由器）
 * 2. 扫描连接：发现并使用已有 P2P Group
 */
class WifiDirectManager(private val context: Context) {

    companion object {
        const val TAG = "WifiDirectManager"
        const val GO_IP = "192.168.49.1"  // WiFi Direct GO 默认 IP
        const val SERVER_PORT = 35354
    }

    private val wifiP2pManager: WifiP2pManager? by lazy {
        context.getSystemService(Context.WIFI_P2P_SERVICE) as? WifiP2pManager
    }
    private var channel: WifiP2pManager.Channel? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    private val discoveredPeers = mutableListOf<P2pDiscoveredDevice>()

    var onPeersChanged: ((List<P2pDiscoveredDevice>) -> Unit)? = null
    var onGroupFormed: ((WifiP2pGroup) -> Unit)? = null
    var onConnected: ((WifiP2pInfo) -> Unit)? = null
    var onGroupRemoved: (() -> Unit)? = null
    var onError: ((String) -> Unit)? = null

    private val p2pReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            when (intent.action) {
                WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION -> {
                    val state = intent.getIntExtra(WifiP2pManager.EXTRA_WIFI_STATE, -1)
                    Log.d(TAG, "P2P state changed: $state")
                }
                WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION -> {
                    requestPeers()
                }
                WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION -> {
                    val networkInfo = intent.getParcelableExtra<NetworkInfo>(
                        WifiP2pManager.EXTRA_NETWORK_INFO
                    )
                    if (networkInfo?.isConnected == true) {
                        wifiP2pManager?.requestConnectionInfo(channel) { info ->
                            info?.let { onConnected?.invoke(it) }
                        }
                    } else {
                        onGroupRemoved?.invoke()
                    }
                }
                WifiP2pManager.WIFI_P2P_THIS_DEVICE_CHANGED_ACTION -> {
                    val device = intent.getParcelableExtra<WifiP2pDevice>(
                        WifiP2pManager.EXTRA_WIFI_P2P_DEVICE
                    )
                    Log.d(TAG, "This device: ${device?.deviceName}")
                }
            }
        }
    }

    fun initialize() {
        channel = wifiP2pManager?.initialize(context, context.mainLooper, null)
        channel ?: run {
            onError?.invoke("WiFi Direct 不可用")
            return
        }

        // 注册广播
        val filter = IntentFilter().apply {
            addAction(WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_THIS_DEVICE_CHANGED_ACTION)
        }
        context.registerReceiver(p2pReceiver, filter)
    }

    /**
     * 模式 1：创建 Group Owner（手机作为热点）
     * PC 可直接连接到此设备
     */
    fun createGroup() {
        wifiP2pManager?.createGroup(channel, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {
                Log.i(TAG, "P2P Group 创建中...")
                // 等待 WIFI_P2P_CONNECTION_CHANGED_ACTION 广播获取 group 信息
                scope.launch {
                    delay(3000)
                    wifiP2pManager?.requestGroupInfo(channel) { group ->
                        group?.let {
                            Log.i(TAG, "Group 已创建: ${it.networkName}")
                            onGroupFormed?.invoke(it)
                        }
                    }
                }
            }

            override fun onFailure(reason: Int) {
                val msg = when (reason) {
                    WifiP2pManager.BUSY -> "WiFi Direct 忙"
                    WifiP2pManager.ERROR -> "创建失败"
                    WifiP2pManager.P2P_UNSUPPORTED -> "设备不支持 WiFi Direct"
                    else -> "未知错误: $reason"
                }
                Log.e(TAG, msg)
                onError?.invoke(msg)
            }
        })
    }

    /**
     * 模式 2：移除已有 Group（停止 GO 模式）
     */
    fun removeGroup() {
        wifiP2pManager?.removeGroup(channel, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {
                Log.i(TAG, "P2P Group 已移除")
                onGroupRemoved?.invoke()
            }
            override fun onFailure(reason: Int) {
                Log.e(TAG, "移除 Group 失败: $reason")
            }
        })
    }

    /**
     * 模式 3：扫描附近的 P2P 设备
     */
    fun discoverPeers() {
        wifiP2pManager?.discoverPeers(channel, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {
                Log.i(TAG, "P2P 设备扫描中...")
            }
            override fun onFailure(reason: Int) {
                val msg = when (reason) {
                    WifiP2pManager.BUSY -> "WiFi Direct 忙，稍后重试"
                    WifiP2pManager.ERROR -> "扫描失败"
                    WifiP2pManager.P2P_UNSUPPORTED -> "设备不支持 WiFi Direct"
                    else -> "扫描失败: $reason"
                }
                onError?.invoke(msg)
            }
        })
    }

    /**
     * 连接到指定 P2P 设备
     */
    fun connect(device: WifiP2pDevice) {
        val config = WifiP2pConfig().apply {
            deviceAddress = device.deviceAddress
            wps.setup = WpsInfo.PBC  // 按钮配对
        }

        wifiP2pManager?.connect(channel, config, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {
                Log.i(TAG, "正在连接到 ${device.deviceName}...")
            }
            override fun onFailure(reason: Int) {
                onError?.invoke("连接失败: $reason")
            }
        })
    }

    private fun requestPeers() {
        wifiP2pManager?.requestPeers(channel) { peers: WifiP2pDeviceList? ->
            discoveredPeers.clear()
            peers?.deviceList?.forEach { device ->
                if (device.status == WifiP2pDevice.AVAILABLE ||
                    device.status == WifiP2pDevice.CONNECTED) {
                    discoveredPeers.add(P2pDiscoveredDevice(device))
                }
            }
            onPeersChanged?.invoke(discoveredPeers.toList())
        }
    }

    /**
     * 获取 GO 模式下的连接 IP
     * PC 端连接此 IP + 端口即可建立投屏连接
     */
    fun getServerEndpoint(): Pair<String, Int> {
        return GO_IP to SERVER_PORT
    }

    /**
     * 获取当前 P2P 连接信息（非 GO 模式）
     */
    fun getConnectionInfo(): Pair<String, Int>? {
        return channel?.let {
            // GO 地址会在 onConnected 回调中获取
            null
        }
    }

    fun destroy() {
        scope.cancel()
        try { context.unregisterReceiver(p2pReceiver) } catch (_: Exception) {}
    }
}
