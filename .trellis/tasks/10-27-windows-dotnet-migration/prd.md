# Windows .NET 10 Avalonia ShadUI 迁移

## Goal

将 Windows 端从 Flutter UI + Go core 双进程迁移为一个 `.NET 10` Avalonia 桌面进程；使用 `ShadUI` 改善界面，同时保持 Android Kotlin 音频服务、控制协议、mDNS 自动发现和 TCP/可选 UDP 行为不变。

## Confirmed Facts

- 当前 Flutter Windows 构建被本机 CMake/MSVC 环境阻塞，错误为 `LNK1104: cannot open file 'ucrtd.lib'`；Flutter analyze/test 和 Android 构建已通过。
- 本机当前安装 `.NET SDK 8.0.403`，已有 `.NET 10.0.9` runtime，但尚未安装 `.NET 10 SDK`；迁移前必须补齐 .NET 10 SDK。
- `accntech/shad-ui` 是 Avalonia UI 库；仓库项目目标为 `net9.0;net10.0`，使用 Avalonia `12.1.0`，MIT License。NuGet `ShadUI 0.2.4` 的 `net10.0` 依赖与该目标匹配。
- 现有 Go core 已实现 TCP `17891`、mDNS `_rayban-voice-mic._tcp.local`、可选 UDP 音频 `17892`、固定 20 字节帧头、状态机、全局快捷键和 VB-Cable WinMM sink。
- Android 端仍需要 Kotlin `AudioRecord`、Bluetooth communication device 和前台服务；本次不迁移 Android 音频实现。

## Requirements

### R1. Windows 桌面应用

- Windows UI 必须使用 `.NET 10`、Avalonia 和 `ShadUI`。
- Windows UI 必须提供连接状态、Android/眼镜输入状态、VB-Cable 输出状态、mDNS 广播状态、监听端口、START/STOP 和错误详情；电脑发现列表和手动主机入口继续由 Android Flutter UI 展示。
- Start 只有在 Android 已连接且状态允许时可用；UI 不自行推导 `streaming`。

### R2. 单进程 Core

- Avalonia 进程内直接托管 Core，不再通过 `127.0.0.1:17890` 连接独立 Go 进程。
- Core 必须包含状态机、TCP server、可选 UDP audio receiver、mDNS advertisement、全局快捷键和音频 sink。
- 窗口关闭必须停止音频 sink、关闭 Android 连接并释放网络资源。

### R3. 协议兼容

- Android 无需改动即可连接迁移后的 Windows Core；保留现有 `HELLO`、`START`、`STOP`、`STATUS`、`PING`、`RESPONSE`、`ERROR` 和 PCM frame。
- 默认 TCP 承载控制和音频。
- `RAYBAN_AUDIO_TRANSPORT=udp` 仍表示显式启用 UDP 音频；只有 Android `HELLO.audio_transports` 声明 UDP 时才切换。
- 保留帧长度上限、CRC 校验、断线清理、重复 START/STOP 幂等行为。

### R4. 局域网发现

- Windows 继续广播 `_rayban-voice-mic._tcp.local`，TCP 端口 `17891`。
- TXT 只包含角色、协议版本和显示名称，不包含 PCM、ADB 凭据或主机敏感信息。
- mDNS 失败时，手动输入主机仍可连接。

### R5. Windows 音频和快捷键

- 使用 NAudio 或等价的 Windows wave output 实现向 VB-Cable `CABLE Input` 输出 48 kHz、16-bit、mono PCM。
- 输出必须使用有界缓冲，不能因网络抖动无限增长。
- 保留 `Ctrl+Shift+Space`，调用与 UI 相同的 dispatcher。

### R6. 构建和迁移

- Windows 构建使用 `.NET 10 SDK`，不依赖 Flutter Windows CMake。
- Android Flutter/Kotlin 工程保留 Android 构建能力。
- .NET Windows 应用通过验收后，移除 Flutter Windows runner、Windows 本地 Go IPC 客户端和 Go Windows core；不保留两套生产实现。

## Acceptance Criteria

- [ ] `.NET 10 SDK` 可还原、构建并启动 Avalonia + ShadUI Windows 应用。
- [ ] 应用单进程启动 Core，启动日志和 UI 能显示 disconnected/connected_idle/streaming/error。
- [ ] Android 使用原协议连接 `.NET Core` 的 TCP `17891`，无需修改 Kotlin 控制客户端。
- [ ] 默认 TCP 音频路径可通过协议测试和本机测试 sink 验证；UDP 显式配置和协商路径保留。
- [ ] mDNS 广播和 Android 现有 `NsdManager` 发现保持兼容；手动 IP 回退可用。
- [ ] 无 VB-Cable 或无法打开音频设备时，UI 显示错误且不报告成功 streaming。
- [ ] 重复 START/STOP、Android 断线、窗口关闭均执行幂等清理。
- [ ] `Ctrl+Shift+Space` 与 UI 按钮调用同一 dispatcher。
- [ ] Windows 10 开发机可以构建并运行；程序不要求 Windows 11 才能启动。
- [ ] Flutter Windows/Go Windows 生产入口已移除或明确不再被构建、启动脚本和文档引用。
- [ ] Android 现有 `flutter analyze`、Flutter tests、Gradle debug build 和协议回归测试继续通过。

## Out of Scope

- 不迁移 Android Kotlin 音频路由到 .NET。
- 不使用 .NET MAUI 或 WPF；不引入 ASP.NET、HTTP API、云端服务或账号系统。
- 不开发 Windows 虚拟声卡驱动，不保存 PCM，不增加多 Android 客户端。
- 不重新设计协议、不增加新的音频格式、不在迁移期间添加托盘后台运行。
- 不声称真实 Ray-Ban 音频闭环已通过，除非物理设备和 VB-Cable 实测成功。

## Key Decisions

- Windows UI 和 Core 合并为一个 Avalonia 进程，优先减少 IPC 和部署复杂度。
- ShadUI 使用 NuGet `0.2.4`，并锁定 Avalonia `12.1.0` 的兼容依赖。
- .NET Core 直接复用现有协议字段和端口，Android 端先不改。
- Windows 音频优先使用 NAudio 的 bounded buffer；若 VB-Cable 行为不满足要求，再改为等价 WinMM P/Invoke，不提前引入 C++。
- 迁移验收后删除旧 Windows Flutter/Go 生产路径，避免长期维护双实现。

## Risks / Deferred

- 当前只有 .NET 8 SDK，必须先安装 .NET 10 SDK；.NET 10 runtime 不能替代 SDK。
- NAudio、Avalonia、ShadUI 的具体版本组合和 Win10 上的 VB-Cable 输出需要实际构建与运行验证。
- mDNS 仍可能受 VPN、热点隔离或企业网络组播策略影响，手动 IP 是必要回退。
- 真机目前尚未证明 Ray-Ban 输入设备可被 <phone-model> API 36 枚举；本任务不扩大蓝牙兼容范围。
