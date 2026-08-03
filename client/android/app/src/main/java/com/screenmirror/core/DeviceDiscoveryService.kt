package com.screenmirror.core

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.util.Log
import kotlinx.coroutines.*
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap

data class DiscoveredDevice(
    val deviceName: String,
    val deviceType: String,
    val host: String,
    val port: Int,
    val hasAudio: Boolean = false,
    val version: Int = 1,
    var lastSeen: Long = System.currentTimeMillis()
) {
    val displayName: String get() = "$deviceName ($host:$port)"
}

class DeviceDiscoveryService(
    private val context: Context,
    private val deviceName: String = Build.MODEL ?: "Android Device"
) {
    companion object {
        const val DISCOVERY_PORT = 35357
        const val SERVICE_TYPE = "_screenmirror._tcp"
        const val TAG = "DiscoveryService"
    }

    private var nsdManager: NsdManager? = null
    private var registrationListener: NsdManager.RegistrationListener? = null
    private var udpSocket: DatagramSocket? = null
    private var isRegistered = false
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    val discoveredDevices = ConcurrentHashMap<String, DiscoveredDevice>()
    var onDeviceFound: ((DiscoveredDevice) -> Unit)? = null
    var onDeviceLost: ((DiscoveredDevice) -> Unit)? = null

    fun startAdvertising(port: Int, hasAudio: Boolean = false) {
        val listenPort = port
        nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager

        val serviceInfo = NsdServiceInfo().apply {
            serviceName = "ScreenMirror-$deviceName"
            serviceType = SERVICE_TYPE
            this.port = listenPort
            setAttribute("device_name", deviceName)
            setAttribute("device_type", "android")
            setAttribute("version", "1")
            setAttribute("has_audio", if (hasAudio) "1" else "0")
        }

        registrationListener = object : NsdManager.RegistrationListener {
            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.e(TAG, "mDNS registration failed: $errorCode")
            }
            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {}
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) {
                Log.i(TAG, "mDNS registered: ${serviceInfo.serviceName}")
            }
            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) {}
        }

        try {
            nsdManager?.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, registrationListener)
            isRegistered = true
        } catch (e: Exception) {
            Log.e(TAG, "mDNS register error: ${e.message}")
        }
        startUdpListener(listenPort)
    }

    /**
     * 主动 UDP 广播扫描 — 发送 discover 后用同一 socket 等待 PC 响应 3 秒
     */
    fun scanByUdp() {
        scope.launch {
            var socket: DatagramSocket? = null
            try {
                socket = DatagramSocket()
                socket.broadcast = true
                socket.soTimeout = 3000  // 3 秒收不到响应则停止等待

                val json = JSONObject().apply {
                    put("type", "screenmirror.discover")
                    put("device_name", deviceName)
                    put("device_type", "android")
                    put("version", 1)
                    put("port", 35354)
                }
                val data = json.toString().toByteArray()

                val interfaces = java.net.NetworkInterface.getNetworkInterfaces()
                var sentCount = 0
                val virtualPatterns = listOf("docker","veth","tun","tap","br-","virbr",
                    "vbox","vmnet","vpn","ppp","wsl","bluetooth","pan")
                while (interfaces.hasMoreElements()) {
                    val iface = interfaces.nextElement()
                    if (!iface.isUp || iface.isLoopback) continue
                    val ifName = iface.name.lowercase()
                    if (virtualPatterns.any { ifName.contains(it) }) continue
                    val addrs = iface.interfaceAddresses
                    for (addr in addrs) {
                        val ip = addr.address
                        if (ip !is java.net.Inet4Address) continue
                        val prefixLen = addr.networkPrefixLength.toInt()
                        if (prefixLen < 8 || prefixLen > 32) continue

                        val broadcast = computeBroadcast(ip.address, prefixLen) ?: continue

                        try {
                            val pkt = DatagramPacket(data, data.size, broadcast, DISCOVERY_PORT)
                            socket.send(pkt)
                            sentCount++
                            Log.i(TAG, "Sent UDP scan to ${broadcast.hostAddress}:$DISCOVERY_PORT")
                        } catch (e: Exception) {
                            Log.e(TAG, "UDP send to ${broadcast.hostAddress}: ${e.message}")
                        }
                    }
                }

                if (sentCount == 0) return@launch

                // 等待 PC 端的 screenmirror.present 响应
                val buffer = ByteArray(4096)
                while (true) {
                    try {
                        val packet = DatagramPacket(buffer, buffer.size)
                        socket.receive(packet)
                        Log.i(TAG, "Received UDP scan response from ${packet.address}:${packet.port}")
                        handleUdpPacket(packet, 35354)
                    } catch (e: java.net.SocketTimeoutException) {
                        Log.i(TAG, "UDP scan complete: socket timeout")
                        break
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "UDP scan error: ${e.message}")
            } finally {
                try { socket?.close() } catch (_: Exception) {}
            }
        }
    }

    private fun computeBroadcast(ipBytes: ByteArray, prefixLen: Int): java.net.InetAddress? {
        if (ipBytes.size != 4) return null
        val maskInt = (0xFFFFFFFFL shl (32 - prefixLen)) and 0xFFFFFFFFL
        val bc = ByteArray(4)
        for (i in 0 until 4) {
            val mByte = ((maskInt shr (24 - i * 8)) and 0xFF).toInt()
            bc[i] = (ipBytes[i].toInt() or (mByte.inv() and 0xFF)).toByte()
        }
        return java.net.InetAddress.getByAddress(bc)
    }

    fun startDiscovery() {
        nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
        val localIp = getLocalIpAddress()

        val resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {}
            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                val host = serviceInfo.host.hostAddress ?: return
                // 过滤掉本机
                if (host == localIp || host.startsWith("127.") || host.startsWith("0.")) return
                val device = DiscoveredDevice(
                    deviceName = serviceInfo.serviceName.removePrefix("ScreenMirror-"),
                    deviceType = serviceInfo.attributes["device_type"]?.toString() ?: "unknown",
                    host = host,
                    port = serviceInfo.port,
                    hasAudio = serviceInfo.attributes["has_audio"]?.toString() == "1",
                )
                val key = "${device.host}:${device.port}"
                val isNew = !discoveredDevices.containsKey(key)
                discoveredDevices[key] = device
                if (isNew) onDeviceFound?.invoke(device)
            }
        }

        val discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (serviceInfo.serviceType == SERVICE_TYPE)
                    nsdManager?.resolveService(serviceInfo, resolveListener)
            }
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                val key = "${serviceInfo.host}:${serviceInfo.port}"
                discoveredDevices.remove(key)?.let { onDeviceLost?.invoke(it) }
            }
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
        }
        nsdManager?.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
    }

    private fun startUdpListener(port: Int) {
        scope.launch {
            try {
                udpSocket = DatagramSocket(DISCOVERY_PORT)
                udpSocket?.broadcast = true
                val buffer = ByteArray(4096)
                while (isActive) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    udpSocket?.receive(packet)
                    Log.i(TAG, "Received UDP from ${packet.address}: ${packet.length} bytes")
                    handleUdpPacket(packet, port)
                }
            } catch (e: Exception) {
                Log.e(TAG, "UDP listener error: ${e.message}")
            }
        }
    }

    private fun handleUdpPacket(packet: DatagramPacket, myPort: Int) {
        try {
            val json = String(packet.data, 0, packet.length)
            val obj = JSONObject(json)
            val type = obj.optString("type")

            when (type) {
                "screenmirror.discover" -> {
                    val response = JSONObject().apply {
                        put("type", "screenmirror.present")
                        put("device_name", deviceName)
                        put("device_type", "android")
                        put("version", 1)
                        put("port", myPort)
                        put("host", getLocalIpAddress())
                    }
                    val respData = response.toString().toByteArray()
                    // 回复到已知端口 35357，而非来源的 ephemeral 端口
                    udpSocket?.send(DatagramPacket(respData, respData.size,
                        packet.address, DISCOVERY_PORT))
                }
                "screenmirror.present" -> {
                    // 过滤自己的广播回声
                    val sourceHost = packet.address.hostAddress ?: ""
                    val localHost = getLocalIpAddress()
                    if (sourceHost == localHost || sourceHost.startsWith("127.")) return

                    val device = DiscoveredDevice(
                        deviceName = obj.optString("device_name", "Unknown"),
                        deviceType = obj.optString("device_type", "unknown"),
                        host = obj.optString("host", sourceHost),  // PC 自报 IP
                        port = obj.optInt("port", 35354),
                    )
                    val key = "${device.host}:${device.port}"
                    val isNew = !discoveredDevices.containsKey(key)
                    discoveredDevices[key] = device
                    if (isNew) onDeviceFound?.invoke(device)
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "handleUdpPacket error: ${e.message}")
        }
    }

    private fun getLocalIpAddress(): String {
        try {
            val virtualPatterns = listOf(
                "docker", "veth", "tun", "tap", "br-", "virbr", "vbox",
                "vmnet", "vpn", "ppp", "wsl", "bluetooth", "pan"
            )
            val interfaces = java.net.NetworkInterface.getNetworkInterfaces()
            var fallback: String? = null

            while (interfaces.hasMoreElements()) {
                val iface = interfaces.nextElement()
                if (iface.isLoopback || !iface.isUp) continue
                val ifName = iface.name.lowercase()

                // 跳过虚拟适配器
                if (virtualPatterns.any { ifName.contains(it) }) continue
                if (iface.isVirtual) continue  // Android API 21+

                val addrs = iface.inetAddresses
                while (addrs.hasMoreElements()) {
                    val addr = addrs.nextElement()
                    if (addr.isLoopbackAddress || addr !is java.net.Inet4Address) continue
                    val host = addr.hostAddress ?: continue

                    // 优先 WiFi 局域网网段
                    if (host.startsWith("192.168.") || host.startsWith("10."))
                        return host

                    // 172.16-31 作为备选
                    if (host.startsWith("172.") && fallback == null) {
                        val seg2 = host.split(".")[1].toIntOrNull() ?: 0
                        if (seg2 in 16..31) fallback = host
                    }
                    if (fallback == null) fallback = host
                }
            }
            if (fallback != null) return fallback
        } catch (_: Exception) {}
        return "127.0.0.1"
    }

    fun stop() {
        try {
            registrationListener?.let { nsdManager?.unregisterService(it) }
        } catch (_: Exception) {}
        isRegistered = false
        try { udpSocket?.close() } catch (_: Exception) {}
        scope.cancel()
    }
}
