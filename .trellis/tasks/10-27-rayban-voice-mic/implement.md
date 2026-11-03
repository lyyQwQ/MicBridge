# 实施计划：Ray-Ban Meta 按需麦克风双端应用

## Phase 0：环境与设备基线

1. 固定并记录 Flutter 3.47.7、Dart 3.13.5、Android SDK 36、JDK 25、Go 1.26.9、Visual Studio 2022 版本输出；Go 使用官方 1.26.9，不升级到 1.27。
2. 确认无线 ADB 设备可用；不把配对码写入仓库或日志。
3. 读取 Android API、厂商、AudioRelay 版本；仅作现状记录。
4. 编写一次性设备探测命令/调试入口，列出 `GET_DEVICES_INPUTS` 的设备名称、类型、地址摘要和输入通道，不发送音频。

验证：`flutter doctor -v`、`adb devices -l`、设备探测日志。

## Phase 1：建立工程骨架

1. 创建 Flutter 工程，启用 Android 和 Windows。
2. 创建 `protocol` 目录和固定头部/控制状态定义。
3. 创建 Go Windows core，先实现 localhost IPC 和状态机，不接入真实音频。
4. 创建 Android Kotlin platform adapter 和前台服务骨架。
5. 建立最小 Flutter 状态页面，能够显示 disconnected/idle/starting/streaming/stopping/error。

验证：Flutter Android/Windows debug 构建；Go 单元测试覆盖状态转换和重复命令。

## Phase 2：Android 蓝牙与按需采集

1. 实现权限检查和输入设备枚举。
2. 实现 Android 12+ communication device 设置和清理。
3. 实现 `AudioRecord` 创建、启动、停止、释放。
4. 让 STOP、断线和异常共用幂等 cleanup。
5. 增加 Android 设备上的状态日志和设备探测页面。

验证：在 <phone-model> Android API 36 设备上先不联网，确认 START/STOP 前后 `AudioRecord` 和通信设备状态变化；确认没有 Ray-Ban 输入时不会回退到手机麦克风。

## Phase 3：局域网控制和 PCM 传输

1. 实现 Go LAN server 与 Android client 的 HELLO/STATUS/PING。
2. 实现 START/STOP 请求和响应。
3. 实现 48 kHz、16-bit、mono PCM 的固定头部 TCP frame。
4. 实现 Windows mDNS/DNS-SD 广播：`_rayban-voice-mic._tcp.local`、TCP `17891`、非敏感 TXT 元数据。
5. 实现 Android `NsdManager` 发现/解析、电脑选择和手动 IP 回退。
6. 保持 TCP 为默认；通过 `RAYBAN_AUDIO_TRANSPORT=udp` 显式启用 UDP 音频，使用 `HELLO/STATUS` 协商端口和能力，控制仍走 TCP。
7. 加入长度上限、超时、断线清理和重连策略。
8. 将 Go 接收音频送入有界缓冲，记录缓冲延迟和丢弃计数。

验证：使用本机虚拟音频源和 Android 测试音源进行端到端回环；覆盖发现、手动回退、重复 START/STOP、半包、超长帧、断线，以及 UDP 未协商/显式协商两条路径。


## Phase 4：Windows 音频输出和快捷键

1. 枚举 Windows 音频输出设备，选择 VB-Cable 输出端。
2. 验证 Go 音频库在 Windows 共享模式下持续播放 PCM。
3. 加入全局快捷键和托盘入口，统一调用 START/STOP dispatcher。
4. Flutter Windows 页面接入 Go 本地 IPC。
5. 增加启动时连接、关闭窗口后托盘运行和明确错误提示。

验证：Windows 声音设置中确认 VB-Cable 输入有音量；Typeless 选择该输入并完成一次听写；停止后确认 Android 麦克风释放。

## Phase 5：Flutter Android UI 与打包

1. 接入 Android platform channel 的 typed commands/state stream。
2. 实现权限、设备选择、连接和状态页面。
3. 将 Android host 输入框替换为发现服务列表，同时保留手动输入。
4. 实现错误详情和重新连接操作。
5. 配置 debug/release 构建、应用图标和本地安装说明。
6. 编写 Windows/Android 用户操作文档，明确 VB-Cable 前置条件。

验证：从干净安装开始完成权限授予、连接、START、听写、STOP、断线恢复；Android 和 Windows 两端分别执行 release 构建。

## Phase 6：收口检查

- 运行 Go formatter、Go tests、Flutter analyze、Flutter tests 和 Android/Windows builds。
- 复核所有状态边界、资源释放、日志脱敏和设备回退行为。
- 运行真实设备闭环至少一次；若眼镜输入不可用，保留精确失败证据并停止声称兼容。
- Verify discovery does not expose ADB credentials, PCM, or host secrets; verify TCP `HELLO` remains required after resolution.
- 根据实际实现更新 README、许可证说明和 Trellis spec；不提交 AudioRelay 私有文件或 ADB 凭据。

## 风险与回滚点

- Android 蓝牙路由失败：先保留设备探测和错误报告，不回退到手机内置麦克风。
- Go 音频库无法驱动 VB-Cable：暂停 UI 扩展，评估最小 C++ WASAPI sink；不先开发虚拟声卡驱动。
- Flutter/原生边界不稳定：保留 Go/Android 独立命令行或调试入口，先验证核心链路再接入 UI。
- 任何阶段发现协议需要变化：只修改 `protocol` 单一契约和双方实现，不在 UI 内添加字段解析。
