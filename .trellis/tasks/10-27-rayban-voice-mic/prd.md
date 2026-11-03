# Ray-Ban Meta 按需麦克风双端应用

## Goal

让 Ray-Ban Meta 继续通过 Android 手机保持蓝牙连接，但只在 Typeless 听写期间占用眼镜麦克风；电脑端通过局域网指令控制手机端启动和释放音频采集。

## Background and confirmed facts

- 当前链路是 Ray-Ban Meta → Android → AudioRelay → LAN → Windows → Typeless。
- 用户已验证：断开 Windows 端 AudioRelay 后，Android 仍占用眼镜麦克风，眼镜不会退出通话音频状态。
- 当前工作区没有产品代码；本任务从空仓库建立新双端应用。
- 本机已发现 Flutter 3.47.7、Android SDK 36、Visual Studio 2022；Android 设备已通过无线 ADB 配对并可读写查询。
- 已连接设备为 `<phone-model>`，Android API 36；手机已安装 AudioRelay `0.26.1`，包名为 `com.azefsw.audioconnect`。

## Requirements

### R1. 按需采集

- Android 端必须在收到 `START` 后才创建/启动 `AudioRecord`。
- Android 端收到 `STOP`、控制连接断开或发生音频异常时，必须停止并释放 `AudioRecord`，清理通信音频路由。
- 空闲状态不得持续占用眼镜麦克风。

### R2. 蓝牙音频路由

- Android 端使用原生音频 API 选择 Ray-Ban Meta 的可用输入设备。
- Android 12+ 优先使用 `AudioManager.setCommunicationDevice()`；不支持或设备不可用时返回可诊断错误，不静默回退到手机内置麦克风。
- 首次使用需要用户授予麦克风、蓝牙和通知权限；应用需要支持 Android 14+ 麦克风前台服务要求。

### R3. 双端通信

- Windows 端能够通过局域网与 Android 端建立控制连接。
- 控制协议至少支持 `START`、`STOP`、`STATUS`、`PING` 和错误响应。
- Android 端将录制的单声道 PCM 音频发送到 Windows 端。
- 默认使用 TCP 承载控制和音频；可选 UDP 只承载音频，控制连接仍使用 TCP。
- 断线和重复命令必须幂等：重复 `START` 不得创建多个录音器，重复 `STOP` 不得崩溃。

### R3.1. 局域网自动发现

- Windows Go 核心广播 `_rayban-voice-mic._tcp.local` 服务，携带协议版本和显示名称。
- Android 端使用系统 `NsdManager` 发现并解析 Windows 服务，不扫描整个网段。
- Android UI 显示发现的电脑；用户选择后建立现有 TCP 控制连接。
- 仍保留手动 IP 连接，兼容 VPN、热点和禁用组播的网络。
- 发现信息不包含配对码、音频数据或其他敏感信息；TCP `HELLO` 仍是最终协议校验。


### R4. Windows 音频输出

- Windows 端接收 Android PCM 后输出到用户安装的 VB-Cable 虚拟输出设备。
- Typeless 可以选择 VB-Cable 的虚拟输入设备作为麦克风。
- 第一版不开发 Windows 虚拟声卡驱动，不依赖 AudioRelay 的私有设备协议。

### R5. Flutter 界面

- Android 和 Windows 共用 Flutter UI 代码，并提供平台适配层。
- Windows 端展示连接、麦克风状态、目标音频设备和启动/停止操作。
- Android 端展示眼镜路由、电脑连接和麦克风占用状态。
- Windows 端支持一个全局听写快捷键；默认按键在设置中可调整。

### R6. 可诊断性

- Android 和 Windows 端显示明确的状态转移和失败原因。
- 日志不得包含麦克风 PCM 内容、无线 ADB 配对码或其他敏感信息。
- 用户可以在 UI 中看到当前状态：未连接、空闲、启动中、采集中、停止中、错误。

## Acceptance Criteria

- [ ] 无需启动 AudioRelay，Android 应用可以发现并选择 Ray-Ban Meta 的蓝牙输入设备。
- [ ] 电脑发送 `START` 后，Android 才开始采集；电脑发送 `STOP` 后，Android 停止采集并释放音频设备。
- [ ] STOP 后检查 Android 音频状态，眼镜退出通话音频占用；手机与眼镜的普通蓝牙连接保持。
- [ ] 控制连接断开时，Android 自动执行与 `STOP` 等价的清理。
- [ ] 重复 `START` / `STOP` 和启动失败不会产生重复服务、崩溃或无法恢复的状态。
- [ ] Windows 能接收 PCM 并输出到 VB-Cable，Typeless 能通过该设备获得眼镜语音。
- [ ] Android 能自动发现局域网中的 Go Windows 核心；发现失败时可以手动输入 IP，不影响现有 TCP 连接。
- [ ] 默认 TCP 链路保持可用；显式启用 UDP 且双方协商支持时，音频切换到 UDP，控制仍保持 TCP。


## Out of Scope

- 不继续自动化或包装 AudioRelay。
- 不开发 Windows 虚拟声卡驱动。
- 不支持 iOS、macOS、Linux、USB 音频和多手机。
- 不实现云端中继、账号系统、录音文件保存或语音识别。
- 不保证所有 Android 厂商的 Bluetooth HFP/SCO/BLE Audio 行为一致；不兼容设备必须显式报告。

## Decisions

- Flutter 只负责跨平台 UI；Android 麦克风和蓝牙由 Kotlin 原生服务负责。
- Windows 核心采用 Go；第一版通过 VB-Cable 输出，避免 C++/WASAPI 驱动级复杂度。
- 参考 AndroidMic 的音频服务与网络分层，但不依赖 AudioRelay 私有协议。
- TCP 是默认音频传输；UDP 作为显式配置的可选低延迟路径，仅在 `HELLO` 协商后启用，避免默认引入丢包和顺序恢复问题。
- 局域网发现采用 mDNS/DNS-SD；Windows 广播，Android 发现并连接，避免 Android 额外开放监听端口和网段扫描。

## Open Questions

无阻塞性问题。mDNS 可能被 VPN、热点隔离或企业网络禁用，因此手动 IP 作为稳定回退；Android 是否能稳定选择 Ray-Ban Meta 麦克风仍以真实设备结果为准。
