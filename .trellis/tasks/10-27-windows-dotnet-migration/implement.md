# 实施计划：Windows .NET 10 Avalonia ShadUI 迁移

## Phase 0：构建前置

1. 安装并确认 `.NET 10 SDK`；确认 `dotnet --list-sdks` 同时列出 `10.0.x`，不要把仅有 runtime 当作 SDK。
2. 使用精确版本还原 `ShadUI 0.2.4`、Avalonia `12.1.0`、mDNS 和音频依赖；记录 NuGet 许可证和 lock/restore 结果。
3. 保留旧 Flutter/Go Windows 入口，先建立新工程，不在前置阶段删除文件。

验证：`dotnet --info`、`dotnet restore desktop/RaybanVoiceMic.sln`。

## Phase 1：协议和 Core 骨架

1. 创建 `desktop/RaybanVoiceMic.sln`、App、Core、Core.Tests 项目，目标 `net10.0`。
2. 将 Go protocol 的 frame encode/decode、CRC、JSON payload 和长度限制移植到 C#，先写 round-trip、checksum、oversize 测试。
3. 移植状态机和有界 PCM channel；覆盖重复 START/STOP、断线 cleanup、sink open failure。
4. 建立 `CoreController` 生命周期，启动/停止 TCP listener、取消 token 和状态事件。
5. 移植 Android HELLO/STATUS 协商；默认 TCP，UDP 环境变量和能力协商逻辑保持一致。

验证：`dotnet test desktop/RaybanVoiceMic.sln`；使用本地 fake sink 和 loopback TCP 客户端验证控制帧。

## Phase 2：网络发现和 Windows 能力

1. 移植 mDNS advertisement：服务类型、端口和 TXT 字段与 Android `LanDiscovery` 保持兼容。
2. 移植 UDP receiver：1200 字节 datagram 上限、Android peer IP 校验、bad frame/drop counter。
3. 移植 NAudio VB-Cable sink：设备枚举、48 kHz/16-bit/mono、bounded buffer、open/write/close error。
4. 移植 `RegisterHotKey` 快捷键，确保只调用 Core dispatcher。
5. 记录诊断日志，但禁止记录 PCM、ADB 凭据和敏感主机信息。

验证：.NET unit tests；Go/Android 协议互通；无 VB-Cable 时启动后显示明确错误而不进入 streaming。

## Phase 3：Avalonia + ShadUI UI

1. 创建 `App.axaml`、`ShadTheme`、`ShadUI.Controls.Window` 和主 ViewModel。
2. 实现 disconnected/connected_idle/starting/streaming/stopping/error 状态展示。
3. 实现 Android 连接地址、当前 Android/眼镜输入、VB-Cable 输出、START/STOP 和错误详情。
4. 绑定 Core typed events；UI 不解析协议或直接操作 socket/audio sink。
5. 增加 UI tests：启动状态、连接错误、START/STOP enable rules、错误详情显示。

验证：`dotnet run --project desktop/src/RaybanVoiceMic.App`；Windows 10 本机截图/手动操作确认界面可启动。

## Phase 4：跨端互通和切换

1. 启动 .NET 单进程 App，使用现有 Android APK 手动连接 TCP `17891`。
2. 验证 Android HELLO、STATUS、START、STOP、断线释放和重复命令。
3. 验证 mDNS 广播被 Android `NsdManager` 发现；发现失败时验证手动 IP 回退。
4. 设置 `RAYBAN_AUDIO_TRANSPORT=udp`，分别验证 Android 声明 UDP 和不声明 UDP 时的最终 transport。
5. 确认 VB-Cable 可用时 PCM 输出；不可用时 UI 显示错误并执行 cleanup。

验证：<phone-model> API 36 真机（如果 Ray-Ban 输入仍缺失，只验证连接/状态，不声称音频闭环）。

## Phase 5：旧 Windows 路径清理

1. 搜索所有 Flutter Windows、Go core、`127.0.0.1:17890` 和旧启动命令引用。
2. 确认 .NET App 已成为 README、构建脚本和用户操作说明唯一 Windows 入口。
3. 在用户已批准的迁移范围内移除旧 Windows Flutter runner、Go Windows core 和重复的 Windows-only bridge；保留 Android 工程和协议文档。
4. 更新 README、协议说明、Trellis spec 和任务 notes，明确 .NET 单进程启动方式。

验证：旧路径搜索无生产引用；只执行 .NET Windows 构建和 Android 构建。

## Phase 6：质量门

```powershell
dotnet format desktop/RaybanVoiceMic.sln --verify-no-changes
dotnet test desktop/RaybanVoiceMic.sln
dotnet build desktop/src/RaybanVoiceMic.App -c Release
flutter analyze
flutter test
cmd /c gradlew.bat assembleDebug --no-daemon --console=plain
```

另外执行：

- Windows 10 上 `dotnet run` 或 Release publish 启动检查。
- Android 真机连接/断线/重复 START/STOP 检查。
- 无 VB-Cable、禁用 mDNS、UDP 端口占用和快捷键冲突的失败路径。
- 不在没有真实设备证据时声称 Ray-Ban 音频闭环兼容。

## 风险和回滚点

- `.NET 10 SDK` 未安装：先停止实施，安装 SDK，不降级目标框架以掩盖环境问题。
- ShadUI/Avalonia restore 或 API 不兼容：锁定已验证包版本，不复制库源码。
- NAudio/VB-Cable 输出不稳定：只替换 `IAudioSink` 实现，保留协议和 Core 状态机；必要时回退旧 Go core。
- Android 互通失败：先对照固定协议测试和旧 Go server，不能在 Android UI 内添加临时字段解析。
- 旧文件清理前保留可运行 Go 路径；清理属于迁移最后一步，失败可恢复到旧入口。
