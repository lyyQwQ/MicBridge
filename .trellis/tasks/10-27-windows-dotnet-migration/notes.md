# 实施与验证记录

## 已完成

- Windows 工程改为 .NET 10 `net10.0-windows`，单进程 Avalonia + ShadUI App 承载 Core。
- .NET SDK 已安装并确认：`10.0.403`。
- .NET Core 已覆盖固定帧协议、CRC、JSON payload、TCP 控制、mDNS、可选 UDP 音频、NAudio VB-Cable sink、全局热键和断线 cleanup。
- Android Kotlin 协议实现保持不变；HELLO 的 TCP/UDP 能力协商与 `STATUS.audio_transport/audio_port` 继续兼容。
- README、协议文档、backend/frontend Trellis spec 已更新到 .NET 单进程入口和状态/错误契约。

## 验证证据

- `dotnet build desktop/RaybanVoiceMic.slnx -c Release --no-restore`：通过，0 warning / 0 error。
- `dotnet test desktop/RaybanVoiceMic.slnx -c Release --no-build`：5/5 通过，含协议 checksum/partial-read/oversize 和 fake sink TCP START/STOP 测试。
- `dotnet format desktop/RaybanVoiceMic.slnx --verify-no-changes --no-restore`：通过。
- `cmd /c gradlew.bat assembleDebug --no-daemon --console=plain`：通过。
- `flutter analyze`：通过。
- `flutter test`：通过，1/1 通过。
- Release UI smoke 启动后保持进程存活至超时，未出现 XAML/启动异常；运行环境报告 global hotkey 不可用，属于当前非交互烟测环境限制。

## 尚未闭环的风险

- 未在本次验证中用真实 Ray-Ban Meta 输入完成 PCM 闭环；Android 蓝牙 HFP/BLE Audio 路由仍需真机确认。
- 未确认本机已安装 VB-Cable；NAudio sink 的真实设备打开、长时间播放和释放仍需 Windows 音频设备验证。
- mDNS/UDP 需要同一局域网和防火墙条件；当前自动化测试覆盖 TCP 控制和协议，未替代真机发现测试。
- 旧 Go/Flutter Windows 路径暂未删除，保留作为回滚路径；待 .NET 真机闭环通过后再单独审批清理。
