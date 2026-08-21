# 控制协议规格说明（当前实现）

> 本文档描述**当前代码实际实现**。与早期版本的重要差异：
> - 长度字段为 **4 字节（uint32 大端）**，非 2 字节；
> - **单 TCP 通道（端口 35354）复用视频与控制**，不再有独立的视频/音频端口；
> - 音频通道当前未实现；加密握手为占位空壳（明文传输）；
> - 仅局域网发现，手机**主动**连 PC。

## 通道设计（单 TCP 复用）

```
┌──────────────────────────────────────────────┐
│  单条 TCP 连接 (端口 35354)                      │
│  ┌──────────────────────────────────────────┐ │
│  │ 控制消息: 握手/心跳/反向控制事件/屏幕参数   │ │
│  │ 视频帧:   VIDEO_FRAME(0x22) 承载 H.264 NAL │ │
│  └──────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘

UDP 发现端口: 35357   (UDP 广播 / mDNS 辅助，不参与视频)
```

- 控制端口：**35354**（TCP，同时承载视频帧与控制消息）
- 视频：与控制通道**复用同一条 TCP**，消息类型为 `VIDEO_FRAME=0x22`，Payload 为单个 H.264 NAL（已含 `00 00 00 01` Annex-B 起始码）
- 音频：**未实现**（协议保留了 `AudioCodec` 字段，但两端均无音频编解码/发送）
- UDP 发现端口：**35357**

## 视频帧格式（VIDEO_FRAME）

```
每个 VIDEO_FRAME 消息的 Payload:
  [00 00 00 01]  [NAL Unit]
   起始码(4字节)    H.264 NAL 单元 (由 MediaCodec 输出归一化得到)

- IDR 关键帧前会先发送 SPS / PPS（同样以 VIDEO_FRAME 承载）
- PC 端 VLC 以 demux=h264 解封装，起始码分隔 NAL
```

推荐编码参数（实际由 Android 端 MediaCodec 决定，当前为手机原生分辨率）：
- Profile: Baseline / Main
- 码率: ~4 Mbps（硬编码，无动态降采样）

## 控制通道 — 二进制协议

### 数据包格式

```
字节偏移    长度    字段            说明
─────────────────────────────────────────
 0         1      Type           消息类型 (1 字节)
 1         4      PayloadLength  负载长度 (4 字节, 大端 uint32)
 5         N      Payload        消息体 (变长)

最大包大小: 4 MB (PayloadLength 上限, 容纳超大 I 帧)
```

### 消息类型定义

```
Type   名称              方向              说明
──────────────────────────────────────────────────
0x01   TOUCH_EVENT       PC → Phone        触摸事件（预留）
0x02   KEY_EVENT         PC → Phone        按键事件
0x03   SCREEN_INFO       Phone → PC        屏幕参数（分辨率/旋转/编码）
0x04   DEVICE_HELLO      Both             设备握手
0x05   HEARTBEAT         Phone → PC       心跳探测（每 3 秒）
0x06   HEARTBEAT_ACK     PC → Phone       心跳响应（回显探测时间戳）
0x07   STREAM_START      Phone → PC       开始推流
0x08   STREAM_STOP       Both             停止推流
0x09   STREAM_PAUSE      PC → Phone       暂停推流
0x0A   STREAM_RESUME     PC → Phone       恢复推流
0x0B   MOUSE_EVENT       PC → Phone        鼠标事件
0x0C   SCROLL_EVENT      PC → Phone        滚轮事件
0x0D   ROTATION_CHANGE   Phone → PC       旋转变化
0x0E   ERROR             Both             错误信息

0x20   CLIPBOARD_SYNC    Both             剪贴板 (Phase 2, 未实现)
0x21   FILE_TRANSFER      Both             文件传输 (Phase 2, 未实现)
0x22   VIDEO_FRAME       Phone → PC       H.264 NAL 单元原始数据
```

### Payload 格式

#### TOUCH_EVENT (0x01)
```
[1]   Action:    0=DOWN, 1=MOVE, 2=UP, 3=CANCEL
[1]   PointerId: 触摸点 ID
[2]   X:         归一化 X 坐标 × 10000 (uint16 大端)
[2]   Y:         归一化 Y 坐标 × 10000 (uint16 大端)
[2]   Pressure:  压力值 × 1000 (uint16 大端)
[8]   EventTime: Unix 毫秒时间戳 (int64 大端)
────────────────────────────────
16 字节
```

#### KEY_EVENT (0x02)
```
[1]   Action:    0=DOWN, 1=UP
[4]   KeyCode:   Android KeyEvent 键码 (int32 大端)
[4]   MetaState: 修饰键掩码 (int32 大端)
[8]   EventTime: Unix 毫秒时间戳 (int64 大端)
────────────────────────────────
17 字节
```

#### SCREEN_INFO (0x03)
```
[4]   Width:      屏幕宽度 (int32 大端)
[4]   Height:     屏幕高度 (int32 大端)
[2]   DPI:        屏幕 DPI (uint16 大端)
[1]   FrameRate:  帧率 (uint8)
[1]   Codec:      0=H264, 1=H265
[4]   MaxBitrate: 最大码率 kbps (int32 大端)
[1]   HasAudio:   当前恒为 0（音频未实现）
[1]   AudioCodec: 0=AAC, 1=Opus
[1]   Rotation:   0/90/180/270 → 0/1/2/3
────────────────────────────────
19 字节
```

`Width`、`Height` 和视频帧均采用设备当前显示方向。反向控制坐标也以当前视频帧方向归一化，`Rotation` 仅描述设备相对自然方向的状态，接收端不得据此再次旋转输入坐标。

#### DEVICE_HELLO (0x04)
```
[1]   Version:    协议版本号
[1]   DeviceType: 0=Android,1=iOS,2=Harmony,3=Windows,4=Mac,5=TV
[N]   DeviceName: UTF-8 字符串 (变长)
────────────────────────────────
2+N 字节
```

#### HEARTBEAT (0x05) / HEARTBEAT_ACK (0x06)
```
[8]   Timestamp:  Unix 毫秒 (int64 大端)
────────────────────────────────
8 字节
Android 每 3 秒发送一次 HEARTBEAT；Windows 收到后原样回显 Timestamp。
```

#### MOUSE_EVENT (0x0B)
```
[1]   Action:   0=MOVE, 1=DOWN, 2=UP
[2]   X:        归一化 X × 10000 (uint16 大端)
[2]   Y:        归一化 Y × 10000 (uint16 大端)
[1]   Button:   0=左键, 1=中键, 2=右键
────────────────────────────────
6 字节
```

#### SCROLL_EVENT (0x0C)
```
[2]   X:        归一化 X × 10000 (uint16 大端)
[2]   Y:        归一化 Y × 10000 (uint16 大端)
[2]   HScroll:  水平滚动量 × 100 (int16 大端)
[2]   VScroll:  垂直滚动量 × 100 (int16 大端)
────────────────────────────────
8 字节
```

#### ERROR (0x0E)
```
[4]   ErrorCode: 错误码 (int32 大端)
[N]   Message:   UTF-8 描述 (变长)
────────────────────────────────
4+N 字节
```

## 设备发现协议

### mDNS 服务注册（辅助）
```
服务类型:  _screenmirror._tcp
端口:     35354 (控制端口)
TXT 记录:
  device_name=<设备名称>
  device_type=<android|ios|harmonyos|windows|mac|tv>
  device_id=<匿名安装标识>  (同一设备多网卡地址去重)
  version=<协议版本号>
  has_audio=<0>        (当前恒为 0)
```

### UDP 广播（主发现方式）
```
目标端口:    35357
广播地址:    255.255.255.255  (IPv4) 或 子网广播地址

发现消息 (JSON):
{
  "type": "screenmirror.discover",
  "device_name": "My Phone",
  "device_type": "android",
  "device_id": "3f6146a1-7ee4-4cb7-9e70-c2c362a47e66",
  "version": 1,
  "port": 35354,
  "has_audio": false
}

响应 (单播回请求方):
{
  "type": "screenmirror.present",
  "device_name": "My PC",
  "device_type": "windows",
  "device_id": "可选的匿名安装标识",
  "version": 1,
  "port": 35354,
  "host": "192.168.1.100"
}
```

接收端优先以 `device_type + device_id` 识别物理设备；旧版本未携带
`device_id` 时回退到 `IP:端口`。同一设备通过多个网卡响应时，优先选择
与本机有效物理网卡同网段且该网卡具有默认网关的地址。

## 连接建立流程（手机主动）

```
1. 手机端绑定并启动 UDP 广播 + mDNS 注册（端口 35354 信息上报）
2. PC 端扫描局域网:
   a) UDP 广播 discover 消息
   b) mDNS 查询 _screenmirror._tcp (辅助)
3. PC 端获得设备列表, 用户点「投屏」→ 手机端弹出 MediaProjection 授权
4. 手机端授权后, TCP 主动连接 PC:35354 (PC 此前已 StartListening 监听)
5. 手机 → PC: DEVICE_HELLO
6. PC → 手机: DEVICE_HELLO
7. 手机 → PC: SCREEN_INFO + STREAM_START
8. 手机 → PC: 持续推送 VIDEO_FRAME(0x22) 视频流
9. 反向控制: PC → 手机 MOUSE_EVENT / KEY_EVENT（鼠标位于投屏窗口上时）
10. 保活: 手机每 3 秒发送 HEARTBEAT，PC 回 HEARTBEAT_ACK；任一端超时后关闭连接

注: PC 端「主动连接设备」路径当前不可用 —— 手机端不监听 TCP，仅 PC 监听。
```

## 心跳与断线检测

```
心跳间隔:     Android 每 3 秒发送 HEARTBEAT
心跳响应:     Windows 回显 HEARTBEAT 的 8 字节 Timestamp
连接超时:     Android 10 秒无入站数据；Windows 10 秒无入站数据
卡流检测:     PC 端 NAL 连续 3 秒无新增 → 标记「信号丢失」, 6 秒 → 自动重置 VLC
断线处理:     关闭 TCP 并触发现有停止投屏/等待重新连接流程
```
