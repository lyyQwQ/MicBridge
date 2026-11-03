<!-- TRELLIS:START -->
# Trellis Instructions

These instructions are for AI assistants working in this project.

This project is managed by Trellis. The working knowledge you need lives under `.trellis/`:

- `.trellis/workflow.md` — development phases, when to create tasks, skill routing
- `.trellis/spec/` — package- and layer-scoped coding guidelines (read before writing code in a given layer)
- `.trellis/workspace/` — per-developer journals and session traces
- `.trellis/tasks/` — active and archived tasks (PRDs, research, jsonl context)

If a Trellis command is available on your platform (e.g. `/trellis:finish-work`, `/trellis:continue`), prefer it over manual steps. Not every platform exposes every command.

If you're using Codex or another agent-capable tool, additional project-scoped helpers may live in:
- `.agents/skills/` — reusable Trellis skills
- `.codex/agents/` — optional custom subagents

Managed by Trellis. Edits outside this block are preserved; edits inside may be overwritten by a future `trellis update`.

<!-- TRELLIS:END -->

# 项目级指引

把手机上的麦克风（内置、蓝牙耳机或智能眼镜如 Ray-Ban Meta）通过局域网用作 Windows 麦克风，供任何从 CABLE Output 录音的软件使用。
Flutter 是双端唯一界面；Windows .NET 宿主无窗口，Android Kotlin 服务负责采集。

## 架构与规范

- `lib/`、`test/`：Flutter 界面、平台桥接与界面测试。
  先读 `.trellis/spec/frontend/index.md`、`state-management.md`、`component-guidelines.md`。
- `android/app/src/main/kotlin/io/github/lyyqwq/micbridge/`：服务、蓝牙路由、录音、连接与信任。
  先读 `.trellis/spec/backend/index.md`、`error-handling.md`、`logging-guidelines.md`。
- `desktop/src/MicBridge.Core/`：CoreController、发现、认证、自动采集与音频输出。
  对应 `.trellis/spec/backend/` 的错误处理、日志与质量规范。
- `desktop/src/MicBridge.App/`：无窗口进程、stdin/stdout JSON 行 IPC、日志入口。
- `windows/CMakeLists.txt`：Flutter Windows 构建及 .NET 宿主发布与打包。
- `desktop/tests/MicBridge.Core.Tests/`：核心、网络和宿主测试。
- `protocol/README.md`、`android/protocol-check/`：共享协议与 Kotlin 验证。
- 跨端改动先读 `.trellis/spec/guides/cross-layer-thinking-guide.md`。
  上述简写的规范文件均位于对应的 `.trellis/spec/frontend/` 或 `backend/` 下。
  实际契约主要在 `frontend/state-management.md` 与 `backend/error-handling.md`；部分其他文件仍是模板，遇到旧描述以代码核实。

## 构建与验证

以下命令在仓库根目录用 PowerShell 执行：

```powershell
dotnet test desktop/tests/MicBridge.Core.Tests/MicBridge.Core.Tests.csproj -c Release -p:UseSharedCompilation=false
flutter analyze
flutter test
./android/protocol-check/verify.ps1 -JavaExecutable <jdk>
flutter build apk --debug
flutter build windows --release
```

- `<jdk>` 替换为 JDK 的 `bin/java.exe` 路径并按需加引号，不是 JDK 目录。
- 协议检查需要 JDK 17+ 和脚本指定版本的 Gradle 缓存 JAR；先完成 Android 构建准备缓存。
- .NET 全量测试按约 6 分钟预留，工具超时设为至少 1800 秒；有真实网络用例，不要仅因耗时判断死锁。
- Windows release 构建会覆盖输出的 exe，必须先退出桌面端。
- Windows 构建同时发布 .NET 宿主至 `core/`，不会清理其中旧文件。
- Dart SDK 约束见 `pubspec.yaml`；Android Java 目标与签名要求见 `android/app/build.gradle.kts`。
- 按改动范围选择验证；构建或单元测试通过不等于真机音频可用。

## 关键契约与常量

- `desktop/src/MicBridge.Core/Control/AutoCapturePolicy.cs`：`StopDelay` 为 3 秒，`ListenerDebounce` 为 1 秒。
  自动采集连续空闲 3 秒后 STOP；手动采集不被自动停止，去抖与停止计时独立。
- `desktop/src/MicBridge.Core/Control/CableSessionMonitor.cs`：检测 CABLE Output 的外部 Active 录音会话。
- `desktop/src/MicBridge.Core/Audio/NaudioSink.cs`：WaveOut 80 ms、两个缓冲；积压阈值 200 ms，追赶目标 80 ms。
- `android/app/src/main/kotlin/io/github/lyyqwq/micbridge/audio/AudioCaptureController.kt`：48 kHz mono PCM16、4096 字节读块与清理路径。
- `desktop/src/MicBridge.Core/Protocol/Frame.cs` 与 Android `protocol/Protocol.kt`：帧格式和认证计算。
  此处 Android 相对路径以 `android/app/src/main/kotlin/io/github/lyyqwq/micbridge/` 为根。
- `desktop/src/MicBridge.Core/Control/CoreController.cs`：默认 TCP 17891、UDP 音频 17892；Android `audio/ControlClient.kt` 连接并按协商结果选择音频传输。
- `desktop/src/MicBridge.Core/Discovery/UdpDiscovery.cs` 与 Android `discovery/UdpDiscovery.kt`：发现 UDP 17893。
- `desktop/src/MicBridge.App/LocalControlServer.cs`：Flutter 与宿主的 stdin/stdout JSON 行 IPC；stdout 不混入诊断。
- `desktop/src/MicBridge.Core/Control/DeviceTrustStore.cs` 与 Android `trust/DeviceTrustStore.kt`：单槽信任记录。
  Windows 使用 DPAPI，Android 使用 Keystore；忘记操作只清理本端，不能假定远端同时清除。
- `desktop/src/MicBridge.App/Program.cs`：日志 `%LOCALAPPDATA%\MicBridge\logs\core.log`。
- 协议改动必须同步两端实现及 `protocol/README.md`，并运行 protocol-check。

## 硬约束

- 不记录或持久化 PCM；密钥与认证材料不进日志，不进发现广播。
- 保留首次审批和后续 HMAC 双向认证；当前明文传输仅限可信家庭局域网。
- 不引入自定义虚拟声卡驱动、云服务、账号体系或多手机支持。
- 不新增依赖；新增或修改注释用中文。
- 不写入具体设备序列号、内网 IP、用户名、个人邮箱或凭据。
  示例使用 `<adb-serial>`、`<windows-lan-ip>`、`%USERPROFILE%` 等占位符。

## 真机、桌面与文件操作

- 不把 Windows 窗口置顶，不抢占前台。
- 用户使用其他 App 时不要操作手机；不要读取或保存其他 App 的屏幕内容。
- 未获授权不要打开 GUI 程序，包括 VB-Cable 控制面板。
- 删除文件一律先获授权，并移动到 `.bak/<YYYY-MM-DD>/`，不要直接永久删除。
- git 写操作先获授权；不覆盖其他代理或用户的未提交改动。
- Windows shell 使用 `pwsh`，或 Git Bash 的完整路径；不要裸写 `bash`。
- `toybox nc -z` 不能作为端口可达的证据；以实际连接、应用状态和日志验证。
- 真机验证应覆盖首字、连续听写、停止释放、断连、后台／锁屏以及重连；只报告实际执行的结果。
