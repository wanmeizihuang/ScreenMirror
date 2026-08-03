using ScreenMirror.Protocol;
using System.Diagnostics;

namespace ScreenMirror.Server;

/// <summary>
/// 功能验证工具 — 测试核心模块是否正常工作
/// </summary>
public class ValidationTest
{
    public static async Task<int> RunAll()
    {
        Console.WriteLine("=== ScreenMirror 功能验证 ===");
        Console.WriteLine();

        int passed = 0, failed = 0;

        // 测试 1: 协议编解码
        passed += Test("协议编解码 - TouchEvent", TestProtocolCodec);
        passed += Test("协议编解码 - ScreenInfo", TestScreenInfoCodec);
        passed += Test("协议编解码 - DeviceHello", TestDeviceHello);
        passed += Test("协议编解码 - Heartbeat", TestHeartbeat);

        // 测试 2: 连接管理器
        passed += Test("连接管理器 - 启动监听", TestConnectionManager);

        // 测试 3: 消息路由
        passed += Test("消息路由器 - 注册和分发", TestMessageRouter);

        // 测试 4: 设备发现
        passed += Test("设备发现 - UDP 扫描", TestDiscoveryService);

        // 测试 5: 安全加密
        passed += Test("安全加密 - ECDH+AES-GCM", TestSecurityManager);

        Console.WriteLine();
        Console.WriteLine($"=== 验证完成: {passed} 通过, {failed} 失败 ===");
        return failed > 0 ? 1 : 0;
    }

    static int Test(string name, Func<bool> test)
    {
        try
        {
            bool ok = test();
            Console.WriteLine(ok ? $"  [PASS] {name}" : $"  [FAIL] {name}");
            return ok ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
            return 0;
        }
    }

    static bool TestProtocolCodec()
    {
        var evt = new TouchEventData
        {
            Action = TouchAction.Down,
            PointerId = 0,
            X = 0.5f,
            Y = 0.75f,
            Pressure = 1.0f,
            EventTime = 1234567890L,
        };

        var encoded = ControlProtocolCodec.EncodeTouchEvent(evt);
        if (encoded.Length < ControlProtocolCodec.HeaderSize) return false;

        ControlProtocolCodec.TryDecodeHeader(encoded, out var type, out var _);
        if (type != ControlMessageType.TouchEvent) return false;

        var decoded = ControlProtocolCodec.DecodeTouchEvent(
            encoded.AsSpan(ControlProtocolCodec.HeaderSize));
        return decoded.X > 0.49f && decoded.X < 0.51f;
    }

    static bool TestScreenInfoCodec()
    {
        var info = new ScreenInfoData
        {
            Width = 1080, Height = 1920, Dpi = 420,
            FrameRate = 60, Codec = VideoCodecType.H264,
            MaxBitrateKbps = 8000, HasAudio = true,
        };
        var encoded = ControlProtocolCodec.EncodeScreenInfo(info);
        var decoded = ControlProtocolCodec.DecodeScreenInfo(
            encoded.AsSpan(ControlProtocolCodec.HeaderSize));
        return decoded.Width == 1080 && decoded.Height == 1920 && decoded.HasAudio;
    }

    static bool TestDeviceHello()
    {
        var hello = new DeviceHelloData
        {
            ProtocolVersion = 1,
            DeviceType = DeviceType.Windows,
            DeviceName = "Test-PC",
        };
        var encoded = ControlProtocolCodec.EncodeDeviceHello(hello);
        var decoded = ControlProtocolCodec.DecodeDeviceHello(
            encoded.AsSpan(ControlProtocolCodec.HeaderSize));
        return decoded.DeviceName == "Test-PC" && decoded.DeviceType == DeviceType.Windows;
    }

    static bool TestHeartbeat()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var encoded = ControlProtocolCodec.EncodeHeartbeat(ts);
        var decoded = ControlProtocolCodec.DecodeHeartbeat(
            encoded.AsSpan(ControlProtocolCodec.HeaderSize));
        return Math.Abs(decoded - ts) < 100;
    }

    static bool TestConnectionManager()
    {
        using var mgr = new ConnectionManager();
        mgr.StartListeningAsync(35358).Wait();
        // 验证端口绑定成功
        mgr.Stop();
        return true;
    }

    static bool TestMessageRouter()
    {
        var router = new MessageRouter();
        bool received = false;

        router.RegisterHandler(ControlMessageType.TouchEvent, (type, payload) =>
        {
            received = true;
        });

        router.RouteAsync(ControlMessageType.TouchEvent, ReadOnlyMemory<byte>.Empty).Wait();
        return received;
    }

    static bool TestDiscoveryService()
    {
        using var discovery = new ScreenMirror.Server.Core.DiscoveryService();
        discovery.Start();
        System.Threading.Thread.Sleep(500);
        // 验证 UdpClient 绑定成功（不抛异常即成功）
        return true;
    }

    static bool TestSecurityManager()
    {
        var alice = new ScreenMirror.Server.Core.SecurityManager();
        var bob = new ScreenMirror.Server.Core.SecurityManager();

        var alicePub = alice.GenerateKeyPair();
        var bobPub = bob.GenerateKeyPair();

        alice.DeriveSharedKey(bobPub);
        bob.DeriveSharedKey(alicePub);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello ScreenMirror!");
        var encrypted = alice.Encrypt(plaintext);
        var decrypted = bob.Decrypt(encrypted);

        return System.Text.Encoding.UTF8.GetString(decrypted) == "Hello ScreenMirror!";
    }
}
