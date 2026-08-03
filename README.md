# ScreenMirror — WiFi 投屏

<p align="center">
  <img src="ScreenMirror.ico" alt="ScreenMirror Icon" width="96" height="96">
</p>

<p align="center">
  <strong>手机屏幕实时镜像到电脑 + 反向控制（后续迭代）</strong>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License"></a>
  <a href="#"><img src="https://img.shields.io/badge/Android-8.0%2B-green.svg" alt="Android"></a>
  <a href="#"><img src="https://img.shields.io/badge/Windows-10%2F11-blue.svg" alt="Windows"></a>
  <a href="#"><img src="https://img.shields.io/badge/.NET-8.0-purple.svg" alt=".NET"></a>
</p>

---

## 简介

ScreenMirror 是一款轻量级局域网投屏工具。Android 手机通过 WiFi 将屏幕实时镜像到 Windows 电脑，后续支持**反向控制**——直接用电脑的鼠标键盘操作手机。

- **无需 USB 线**：纯 WiFi 局域网传输，手机主动扫码/发现电脑后连接
- **硬件加速**：Android 端 MediaCodec 硬编 H.264，PC 端 LibVLC 硬解（DXVA2/D3D11VA）
- **低延迟**：VLC `network-caching` 低至 100ms
- **已实测设备**：Android 8.0+ 设备、华为 Mate 60 Pro（鸿蒙 6.0）

---

## 功能

### ✅ 已实现（Phase 1）
| 功能 | 说明 |
|------|------|
| 屏幕镜像 | Android → Windows 实时投屏，H.264 硬件编码 |
| 局域网发现 | UDP 广播 + mDNS，一键扫描连接 |
| 信号检测 | NAL 帧计数监控，3 秒无信号显示提示，6 秒自动重连 |

### 🚧 规划中（Phase 2）
- [ ] 音频传输（AAC）
- [ ] 传输加密（ECDH + AES-128-GCM，算法已实现，待接线）
- [ ] 分辨率动态降采样（720p/1080p 可选）
- [ ] 心跳保活
- [ ] 反向控制
- [ ] 多设备同屏

### 🔮 远期（Phase 3）
- [ ] iOS / macOS 发送端
- [ ] 鸿蒙原生发送端（ArkTS，工程骨架已在 `client/harmonyos/`）

---

## 架构

```
┌─────────────────────────────┐      TCP 35354 (单通道)      ┌─────────────────────────────┐
│         Android 发送端        │ ◄══════════════════════════ │       Windows 接收端          │
│                              │                             │                              │
│  MediaProjection             │      VIDEO_FRAME(0x22)      │  ControlConnection            │
│       ↓                      │ ◄────────────────────────── │       ↓                      │
│  VirtualDisplay(Surface)     │    (H.264 Annex-B NAL)      │  MirrorWindow                 │
│       ↓                      │                             │       ↓                      │
│  MediaCodec (H.264 硬编)     │      MOUSE/KEY_EVENT        │  VlcDecoder / NalStream       │
│       ↓                      │ ──────────────────────────► │       ↓                      │
│  ControlConnection            │   (反向控制指令)             │  LibVLC (DXVA2 硬解)          │
│                              │                             │       ↓                      │
│  ReverseControlService       │      UDP 35357 发现          │  VideoView (WPF 渲染)         │
│  (AccessibilityService)      │ ◄═════════════════════════► │                              │
│                              │         mDNS 辅助             │  MirrorWindow               │
│                              │                             │       ↓                      │
│                              │                             │  ReverseControlPlugin         │
│                              │                             │  (低级别键鼠钩子)               │
└─────────────────────────────┘                             └─────────────────────────────┘
```

- **单 TCP 通道复用**：视频帧与控制消息共用端口 35354
- **帧格式**：`[1B Type][4B PayloadLen(BigEndian)][N B Payload]`
- **最大帧**：4MB（容纳大 I 帧，1080p/8Mbps 下可达 200-500KB）
- **连接方向**：手机主动连 PC（PC 监听 35354）

---

## 技术栈

| 端 | 技术 | 说明 |
|---|---|---|
| **Android 发送端** | Kotlin + Jetpack Compose | 单 `:app` 模块，Min API 26 (Android 8.0) |
| 屏幕采集 | MediaProjection + VirtualDisplay | Surface 通路，零 CPU 拷贝 |
| 视频编码 | MediaCodec H.264 | 8Mbps VBR, 30fps, I 帧间隔 2s |
| **Windows 接收端** | C# .NET 8 + WPF | WinExe，单工程 |
| 视频解码 | LibVLCSharp 3.9.3 | VLC 内部 DXVA2/D3D11VA 硬解 |
| **通信协议** | 手写二进制帧 | 非 Protobuf（`messages.proto` 仅为设计参考稿） |
| 设备发现 | UDP 广播 (35357) + mDNS | `_screenmirror._tcp` |
| 安全 | ECDH P-256 + AES-128-GCM | 算法已实现，握手待接线（当前明文） |
| 反向控制 | AccessibilityService + GestureDescription | PC 低级别钩子 → 坐标归一化 → 手机注入 |

---

## 快速开始

### Windows 接收端

**环境要求**：Windows 10/11，[.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
# 1. 进入服务器源码目录
cd server/src

# 2. 构建
dotnet build -c Release

# 3. 运行
dotnet run -c Release --no-build
```

或直接下载 `release/ScreenMirror/` 目录，双击 `启动投屏.bat`。

> **注意**：Windows 防火墙可能会阻止 UDP 35357 和 TCP 35354 端口，程序启动时会尝试自动添加规则（需管理员权限）。

### Android 发送端

**环境要求**：Android Studio + JDK 21 + Android SDK 34

```bash
# 1. 进入 Android 工程目录
cd client/android

# 2. 构建 Debug APK
export JAVA_HOME="/path/to/jdk-21"
export JAVA_TOOL_OPTIONS="-Dfile.encoding=UTF-8"
./gradlew assembleDebug

# 3. 安装
adb install app/build/outputs/apk/debug/app-debug.apk
```

或在 `release/ScreenMirror.apk` 直接下载预构建的 APK 安装。

### 使用流程

1. **PC 端**：启动 `ScreenMirror.exe`，开始监听并广播
2. **手机端**：打开 App，点击「扫描设备」或在列表中找到你的电脑
3. **连接**：点击「投屏」→ 授权录屏 → 自动连接
4. **反向控制**：手机设置 → 辅助功能 → 开启「ScreenMirror」无障碍服务

---

## 项目结构

```
Wifi投屏/
├── client/
│   ├── android/              # Android 发送端 (Kotlin + Compose)
│   │   └── app/src/main/
│   │       ├── java/.../ui/MainActivity.kt          # 主界面 + 连接编排
│   │       ├── java/.../core/ScreenCaptureService.kt  # 屏幕采集 + 编码
│   │       ├── java/.../core/DeviceDiscoveryService.kt # mDNS + UDP 发现
│   │       ├── java/.../core/SecurityManager.kt       # ECDH + AES (算法)
│   │       └── java/.../modules/ReverseControlService.kt # 无障碍反向控制
│   ├── harmonyos/            # 鸿蒙发送端 (ArkTS, 工程骨架)
│   └── ios/                  # iOS 预留
├── server/
│   └── src/                  # Windows 接收端 (C# WPF)
│       ├── ui/MainWindow.xaml.cs        # 设备列表 + 连接编排
│       ├── ui/MirrorWindow.xaml.cs      # 投屏渲染 + 信号检测
│       ├── core/DiscoveryService.cs     # UDP 发现
│       ├── core/SecurityManager.cs      # ECDH + AES (算法)
│       ├── plugins/IPlugin.cs           # 插件接口 + PluginManager
│       ├── modules/vlc-decoder/         # VLC 解码 + NAL 队列
│       └── modules/reverse-control/     # PC 键鼠钩子 → Android
├── protocol/
│   ├── protocol-spec.md                 # 协议规格说明
│   ├── proto/messages.proto             # Protobuf 设计参考 (未编译使用)
│   ├── csharp/                          # C# 协议库 (手写二进制)
│   └── kotlin/                          # Kotlin 协议库 (手写二进制)
├── docs/
│   ├── 实现方案.md                       # 详细实现方案 + 设计偏差
│   ├── 快速开始.md                       # 使用指南
│   └── 项目全景理解.md                    # 完整项目理解文档
├── release/                             # 构建产物 (APK + Windows 发布包)
├── ScreenMirror.ico                     # 应用图标
└── sign_apk.sh                          # APK 签名脚本
```

---

## 通信协议

协议采用**手写二进制帧格式**，非 Protobuf（`messages.proto` 仅为规划参考稿，未被编译使用）。

### 帧结构

```
[0]      1B  Type          消息类型 (0x01~0x22)
[1..4]   4B  PayloadLen    uint32 BE, 上限 4MB
[5..N]   NB  Payload       变长数据
```

### 消息类型

| Type | 名称 | 方向 | 说明 |
|------|------|------|------|
| 0x01 | TOUCH_EVENT | PC→Phone | 触摸事件 (16B) |
| 0x02 | KEY_EVENT | PC→Phone | 按键事件 (17B) |
| 0x03 | SCREEN_INFO | Phone→PC | 屏幕参数 (19B) |
| 0x04 | DEVICE_HELLO | 双向 | 设备握手 |
| 0x0B | MOUSE_EVENT | PC→Phone | 鼠标事件 (6B) |
| 0x0E | ERROR | 双向 | 错误消息 |
| **0x22** | **VIDEO_FRAME** | Phone→PC | **H.264 Annex-B NAL** |

完整协议规格见：[`protocol/protocol-spec.md`](protocol/protocol-spec.md)

---

## 开发

### PC 端构建

```bash
dotnet publish server/src/ScreenMirror.Server.csproj -c Release -o release/ScreenMirror/
```

### Android 端构建

```bash
cd client/android
./gradlew assembleDebug
```

### 鸿蒙端

`client/harmonyos/` 已有完整工程骨架，需在 DevEco Studio 中打开并配置签名后构建。

---

## 已知限制

- 音频传输未实现
- 传输未加密（当前明文 TCP）
- 反向控制按键注入在普通 Android ROM 上可能失效（`InputManager.injectInputEvent` 需系统权限），仅触摸可靠
- 仅支持手机主动连 PC，PC 无法主动发起连接
- 无心跳保活，断线依靠视频帧计数检测
- 无分辨率自适应（当前原生分辨率，约 8Mbps 码率）

---

## 许可证

[MIT License](LICENSE)
