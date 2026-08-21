using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;
using ScreenMirror.Protocol;

namespace ScreenMirror.Server.Core;

/// <summary>
/// 发现的设备信息
/// </summary>
public class DiscoveredDevice
{
    public string DeviceName { get; init; } = "";
    public string DeviceType { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public int ProtocolVersion { get; init; }
    public string MacSuffix { get; init; } = "";
    public bool HasAudio { get; init; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string DisplayName => $"{DeviceName} ({Host})";
    public override string ToString() => DisplayName;
}

/// <summary>
/// 设备发现服务 — mDNS 查询 + UDP 广播 + UDP 监听
/// </summary>
public class DiscoveryService : IDisposable
{
    private const int DiscoveryPort = 35357;
    private readonly UdpClient? _udpListener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();

    public event Action<DiscoveredDevice>? OnDeviceFound;
    public event Action<DiscoveredDevice, DiscoveredDevice>? OnDeviceUpdated;
    public event Action<DiscoveredDevice>? OnDeviceLost;
    public event Action<string>? OnLog;

    public IReadOnlyCollection<DiscoveredDevice> Devices => _devices.Values.ToList().AsReadOnly();

    private void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[Discovery] {msg}");
        OnLog?.Invoke(msg);
    }

    public DiscoveryService()
    {
        try
        {
            _udpListener = new UdpClient(DiscoveryPort);
            _udpListener.EnableBroadcast = true;
            Log($"UDP 监听已启动 :{DiscoveryPort}");
        }
        catch (Exception ex)
        {
            Log($"UDP 端口 {DiscoveryPort} 被占用: {ex.Message} — 只能主动扫描");
            _udpListener = null;
        }
    }

    /// <summary>
    /// 开始设备发现（扫描 + 监听）
    /// </summary>
    public void Start()
    {
        // 尝试添加 Windows 防火墙规则（非管理员会静默失败）
        TryAddFirewallRule();

        if (_udpListener != null)
        {
            _ = UdpListenLoopAsync(_cts.Token);
        }

        // 定时清理过期设备
        _ = CleanupLoopAsync(_cts.Token);

        // 周期性扫描（每 3 秒一次）
        _ = ScanLoopAsync(_cts.Token);
    }

    /// <summary>
    /// 尝试添加 Windows 防火墙规则（非管理员会静默失败）
    /// 使用 UseShellExecute=false 避免 cmd.exe 窗口闪烁
    /// </summary>
    private void TryAddFirewallRule()
    {
        try
        {
            var ruleName = "ScreenMirror Discovery";

            // 先检查规则是否已存在，避免不必要的 netsh 调用
            bool alreadyExists = RunNetshSilent($"advfirewall firewall show rule name=\"{ruleName}\"")
                .Contains(ruleName);

            if (alreadyExists)
            {
                Log("防火墙规则已存在 (UDP 35357)，跳过添加");
                return;
            }

            // 添加规则 — UseShellExecute=false 不经过 cmd.exe，无窗口闪烁
            var output = RunNetshSilent(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=UDP localport=35357");

            if (output.Contains("确定") || output.Contains("Ok"))
                Log("防火墙规则已添加 (UDP 35357)");
            else
                Log($"防火墙规则添加跳过: {output.Trim()}");
        }
        catch
        {
            Log("防火墙规则跳过 (无法执行 netsh)");
        }
    }

    /// <summary>
    /// 静默执行 netsh 命令（不弹出任何窗口）
    /// </summary>
    private static string RunNetshSilent(string arguments)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        p.Start();
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ScanAsync(); } catch { }
            await Task.Delay(3000, ct);
        }
    }

    /// <summary>
    /// 主动扫描局域网
    /// </summary>
    public async Task ScanAsync()
    {
        Log("开始扫描...");
        // 发送 UDP 广播
        var discoverMsg = new
        {
            type = "screenmirror.discover",
            device_name = Environment.MachineName,
            device_type = "windows",
            version = 1,
            port = ConnectionManager.DefaultControlPort,
        };

        var json = JsonSerializer.Serialize(discoverMsg);
        var data = System.Text.Encoding.UTF8.GetBytes(json);

        int sentCount = 0;
        // 向所有网络接口的广播地址发送
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            // 跳过虚拟适配器（VPN/Docker/Hyper-V/WSL 等）
            var ifName = iface.Name.ToLowerInvariant();
            var ifDesc = (iface.Description ?? "").ToLowerInvariant();
            if (ifName.Contains("vethernet") || ifName.Contains("vpn") ||
                ifName.Contains("docker") || ifName.Contains("hyper-v") ||
                ifName.Contains("wsl") || ifName.Contains("virtualbox") ||
                ifName.Contains("vmware") || ifName.Contains("tunnel") ||
                ifName.Contains("bluetooth") || ifDesc.Contains("virtual"))
                continue;

            var ipProps = iface.GetIPProperties();
            foreach (var unicast in ipProps.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                // 计算广播地址
                var ip = unicast.Address;
                var mask = unicast.IPv4Mask;
                if (mask == null) continue;

                var broadcastBytes = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcastBytes[i] = (byte)(ip.GetAddressBytes()[i] | (~mask.GetAddressBytes()[i]));

                var broadcastAddr = new IPAddress(broadcastBytes);

                try
                {
                    using var client = new UdpClient();
                    client.EnableBroadcast = true;
                    await client.SendAsync(data, data.Length, new IPEndPoint(broadcastAddr, DiscoveryPort));
                    sentCount++;
                    Log($"  广播 → {broadcastAddr}:{DiscoveryPort} (接口 {iface.Name})");
                }
                catch (Exception ex) {
                    Log($"  广播失败 {broadcastAddr}: {ex.Message}");
                }
            }
        }
        Log($"扫描完成，已发送 {sentCount} 个广播包，当前已知设备 {_devices.Count} 台");
    }

    private async Task UdpListenLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _udpListener != null)
            {
                var result = await _udpListener.ReceiveAsync(ct);
                var msg = System.Text.Encoding.UTF8.GetString(result.Buffer);
                // 过滤自己的广播回声
                if (msg.Contains("\"device_type\":\"windows\"")) continue;
                Log($"收到 UDP 响应: {result.RemoteEndPoint}");
                ProcessDiscoveryMessage(result.Buffer, result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"UDP 监听出错: {ex.Message}"); }
    }

    private void ProcessDiscoveryMessage(byte[] data, IPEndPoint remote)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(data);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.GetProperty("type").GetString();
            if (type != "screenmirror.discover" && type != "screenmirror.present")
                return;

            var device = new DiscoveredDevice
            {
                DeviceName = root.TryGetProperty("device_name", out var name) ? name.GetString()! : "Unknown",
                DeviceType = root.TryGetProperty("device_type", out var dt) ? dt.GetString()! : "unknown",
                DeviceId = root.TryGetProperty("device_id", out var id) ? id.GetString() ?? "" : "",
                Host = remote.Address.ToString(),
                Port = root.TryGetProperty("port", out var p) ? p.GetInt32() : ConnectionManager.DefaultControlPort,
                ProtocolVersion = root.TryGetProperty("version", out var v) ? v.GetInt32() : 1,
                MacSuffix = root.TryGetProperty("mac_suffix", out var mac) ? mac.GetString()! : "",
                HasAudio = root.TryGetProperty("has_audio", out var ha) ? ha.GetBoolean() : false,
                LastSeen = DateTime.UtcNow,
            };

            // 过滤本机自己
            var localIP = GetLocalIPAddress();
            if (device.Host == localIP || device.Host == "127.0.0.1") return;

            var key = GetDeviceKey(device);
            if (!_devices.TryGetValue(key, out var existing))
            {
                _devices[key] = device;
                Log($"发现设备: {device.DeviceName} @ {device.Host}:{device.Port}");
                OnDeviceFound?.Invoke(device);
            }
            else if (existing.Host == device.Host && existing.Port == device.Port)
            {
                existing.LastSeen = device.LastSeen;
            }
            else if (GetEndpointPreference(device.Host) > GetEndpointPreference(existing.Host))
            {
                _devices[key] = device;
                Log($"更新设备地址: {device.DeviceName} {existing.Host} -> {device.Host}");
                OnDeviceUpdated?.Invoke(existing, device);
            }
            else
            {
                Log($"忽略同一设备的次选地址: {device.DeviceName} @ {device.Host}");
            }

            // 如果是 discover 请求，回复 present
            if (type == "screenmirror.discover" && _udpListener != null)
            {
                var presentMsg = new
                {
                    type = "screenmirror.present",
                    device_name = Environment.MachineName,
                    device_type = "windows",
                    version = 1,
                    port = ConnectionManager.DefaultControlPort,
                    host = GetLocalIPAddress(),
                };
                var responseJson = JsonSerializer.Serialize(presentMsg);
                var responseData = System.Text.Encoding.UTF8.GetBytes(responseJson);
                // 回复到来源 IP 的已知端口 35357，而非 ephemeral 源端口
                _udpListener.SendAsync(responseData, responseData.Length,
                    new IPEndPoint(remote.Address, DiscoveryPort));
            }
        }
        catch { }
    }

    private static string GetDeviceKey(DiscoveredDevice device)
    {
        return string.IsNullOrWhiteSpace(device.DeviceId)
            ? $"endpoint:{device.Host}:{device.Port}"
            : $"device:{device.DeviceType}:{device.DeviceId}";
    }

    private static int GetEndpointPreference(string host)
    {
        if (!IPAddress.TryParse(host, out var remote) ||
            remote.AddressFamily != AddressFamily.InterNetwork)
            return 0;

        int bestScore = 1;
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up ||
                iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var name = iface.Name.ToLowerInvariant();
            var desc = (iface.Description ?? "").ToLowerInvariant();
            if (name.Contains("vethernet") || name.Contains("vpn") ||
                name.Contains("docker") || name.Contains("hyper-v") ||
                name.Contains("wsl") || name.Contains("virtualbox") ||
                name.Contains("vmware") || name.Contains("tunnel") ||
                name.Contains("bluetooth") || desc.Contains("virtual"))
                continue;

            var properties = iface.GetIPProperties();
            bool hasDefaultGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask == null ||
                    !IsSameSubnet(remote, unicast.Address, unicast.IPv4Mask))
                    continue;

                int score = 100;
                if (hasDefaultGateway) score += 100;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or
                    NetworkInterfaceType.Ethernet)
                    score += 20;
                bestScore = Math.Max(bestScore, score);
            }
        }

        return bestScore;
    }

    private static bool IsSameSubnet(IPAddress first, IPAddress second, IPAddress mask)
    {
        var firstBytes = first.GetAddressBytes();
        var secondBytes = second.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (firstBytes.Length != 4 || secondBytes.Length != 4 || maskBytes.Length != 4)
            return false;

        for (int i = 0; i < 4; i++)
        {
            if ((firstBytes[i] & maskBytes[i]) != (secondBytes[i] & maskBytes[i]))
                return false;
        }

        return true;
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            var expired = _devices.Where(kv => kv.Value.LastSeen < cutoff).ToList();
            foreach (var kv in expired)
            {
                _devices.TryRemove(kv.Key, out _);
                OnDeviceLost?.Invoke(kv.Value);
            }
        }
    }

    private static string GetLocalIPAddress()
    {
        string? fallback = null;

        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            // 排除虚拟适配器（VPN、Docker、Hyper-V、WSL、VirtualBox 等）
            var name = iface.Name.ToLowerInvariant();
            var desc = (iface.Description ?? "").ToLowerInvariant();
            if (name.Contains("vethernet") || name.Contains("vpn") ||
                name.Contains("docker") || name.Contains("hyper-v") ||
                name.Contains("wsl") || name.Contains("virtualbox") ||
                name.Contains("vmware") || name.Contains("tunnel") ||
                name.Contains("bluetooth") || desc.Contains("virtual"))
                continue;

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();

                // 优先 192.168.x.x 或 10.x.x.x 网段（典型局域网）
                if (ip.StartsWith("192.168.") || ip.StartsWith("10."))
                    return ip;

                // 记录 172.16-31 网段作为备选
                if (ip.StartsWith("172.") && fallback == null)
                {
                    var seg2 = int.Parse(ip.Split('.')[1]);
                    if (seg2 >= 16 && seg2 <= 31)
                        fallback = ip;
                }

                // 其他 IPv4 作为最后备选
                if (fallback == null) fallback = ip;
            }
        }

        return fallback ?? "127.0.0.1";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udpListener?.Dispose();
        _cts.Dispose();
    }
}
