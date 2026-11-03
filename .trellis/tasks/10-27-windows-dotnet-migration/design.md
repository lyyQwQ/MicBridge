# 技术设计：Windows .NET 10 Avalonia ShadUI 单进程迁移

## 1. 目标与边界

Windows 端改为一个 Avalonia 桌面进程：UI、协议控制、mDNS 广播、状态机、快捷键和 VB-Cable 音频输出都在同一进程内。Android 端继续使用现有 Flutter UI + Kotlin `MicrophoneService`，不迁移蓝牙路由和 `AudioRecord`。

迁移不是协议重设计。Android 连接目标从旧 Go core 改为新的 .NET Core，端口和帧格式保持兼容。

## 2. 工程结构

```text
desktop/
  RaybanVoiceMic.sln
  src/
    RaybanVoiceMic.App/
      App.axaml
      MainWindow.axaml
      ViewModels/MainWindowViewModel.cs
      RaybanVoiceMic.App.csproj
    RaybanVoiceMic.Core/
      Protocol/
      Control/
      Discovery/
      Audio/
      RaybanVoiceMic.Core.csproj
  tests/
    RaybanVoiceMic.Core.Tests/
```

- `RaybanVoiceMic.App`：`net10.0` Avalonia executable，引用 `ShadUI 0.2.4`、Avalonia `12.1.0` 和 Core。
- `RaybanVoiceMic.Core`：`net10.0` class library；包含协议、状态机、TCP/UDP server、mDNS advertisement、hotkey 和 Windows 音频 sink。
- `RaybanVoiceMic.Core.Tests`：协议、状态机、协商和有界缓冲测试；不依赖真实 VB-Cable。
- 根目录现有 Android Flutter 工程保留 Android target；Windows Flutter runner 只在 .NET 版本通过验收后清理。

## 3. 单进程生命周期

```text
App startup
  ├─ create CoreOptions
  ├─ start TcpListener :17891
  ├─ optionally start UdpClient :17892
  ├─ start mDNS advertisement
  ├─ register Ctrl+Shift+Space
  └─ bind CoreStateChanged to ViewModel

Window close / process exit
  ├─ unregister hotkey
  ├─ STOP state machine
  ├─ close audio sink
  ├─ close Android TCP/UDP connections
  └─ stop mDNS and listeners
```

UI 直接调用 `CoreController.DispatchAsync("START"/"STOP"/"STATUS")`，不走 `127.0.0.1:17890`。Core 对外只开放 Android LAN TCP `17891`，不新增 HTTP API。

## 4. 协议兼容

保留现有 20 字节大端序帧头：`RBVM`、version、type、sequence、payload length、CRC-32/IEEE。控制 JSON 字段与 `protocol/README.md` 保持一致：

- `HELLO`：`peer`、`audio_rate`、`audio_bits`、`audio_channels`、`audio_transports`。
- `STATUS`：`state`、`audio_device`、`detail`、`dropped_audio_frames`、`audio_transport`、可选 `audio_port`。
- `START` / `STOP` / `PING` / `RESPONSE` / `ERROR`：沿用现有字段。

C# 协议层只允许 `MaxPayload = 1 MiB`、控制 payload `64 KiB`；TCP 音频沿用 48 kHz、16-bit、mono PCM。UDP 仅在 `RAYBAN_AUDIO_TRANSPORT=udp` 且 Android HELLO 声明 `udp` 时启用，复用 `17892` 和 1200 字节 datagram 上限。

## 5. Core 组件

### 5.1 StateMachine

按现有状态和语义实现：`disconnected → connected_idle → starting → streaming → stopping → connected_idle`。`START`、`STOP`、Android 断线、窗口关闭、音频写入失败全部经过同一个幂等 cleanup。

### 5.2 TCP/UDP

- `TcpListener` 接受 Android 连接，首帧必须为有效 HELLO。
- 控制发送和接收使用 `NetworkStream`，每个连接独立 cancellation token。
- UDP 接收使用 `UdpClient`，只接受与 Android TCP peer IP 相同的来源；非法帧、超长帧、未协商来源直接丢弃。
- 有界 `Channel<ReadOnlyMemory<byte>>` 或固定容量队列接收 PCM；满时增加 dropped counter，不阻塞无限增长。
- 网络和关闭路径必须可取消，不使用 `Thread.Sleep` 或忙等待。

### 5.3 mDNS

使用 MIT 许可的 `Makaretu.Dns.Multicast` 兼容库在 TCP listener 启动后广播 `_rayban-voice-mic._tcp.local`。TXT 仅包含 `role=windows`、`protocol=1`、`name`。mDNS 失败只记录诊断并保留手动连接，不阻止 Core 启动。

### 5.4 Windows audio sink

优先使用 NAudio 的 wave output API：枚举设备名称，选择包含 `CABLE Input` 或 `VB-Audio` 的端点，使用 `BufferedWaveProvider` 设置有限缓冲和丢弃计数，再通过 `WaveOutEvent` 播放。接口保持：`Open`、`Write`、`Close`、`Name`。

如果 NAudio 在 Win10/VB-Cable 实测中不能稳定完成 48 kHz PCM 输出，只替换 sink 实现为 WinMM P/Invoke；协议和状态机不变，不引入 C++。

### 5.5 global hotkey

用 `RegisterHotKey`、`GetMessage`、`UnregisterHotKey` 的 C# P/Invoke 保留 `Ctrl+Shift+Space`，热键回调只调用同一个 `CoreController.DispatchAsync`。注册失败显示 warning，不阻止 UI 和 Android 控制连接启动。

## 6. Avalonia UI

- 使用 `ShadUI.Controls.Window` 和 `ShadTheme`。
- 页面保留当前产品信息：连接状态、Android/眼镜设备、VB-Cable 输出、Start/Stop、错误详情和连接地址。
- ViewModel 只订阅 Core 的 typed state；不得解析协议帧或 PCM。
- Start enabled 规则与现有 Flutter 状态规范一致：只有 `connected_idle` 且设备/输出前置条件满足时可用。
- UI 关闭默认退出整个单进程；Core cleanup 由 `IHostApplicationLifetime`/窗口关闭路径统一触发。

## 7. 兼容与清理

- Android Kotlin `ControlClient` 不要求改端口或字段；只验证其现有 HELLO/START/STOP 与 .NET Core 互通。
- Go core 与 Flutter Windows runner 在新应用通过协议、UI 和音频验收后移除，避免生产环境存在两套 Core。
- Android Flutter Windows 分支和 Windows 专用 CMake 文件只在确认没有构建/文档引用后清理。
- .NET 10 SDK 是构建前置；当前只有 SDK 8.0.403，runtime 10.0.9 不足以执行 `dotnet build` 的 net10 target。

## 8. 回滚

在 .NET Core 尚未通过 Android TCP、音频 sink 和 UI 验收前，不清理 Go/Flutter Windows 文件。失败时继续使用当前 Go core + Android APK；协议不变使回滚不需要升级 Android 端。
