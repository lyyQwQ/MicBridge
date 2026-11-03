# Stabilize audio bridge and redesign UI

## Goal

让 Ray-Ban Meta 麦克风桥接在单用户、单手机、单 Windows 电脑场景下安全、可恢复、可诊断，并把 Android 与 Windows 界面改成清晰的中文操作流程：发现/连接、确认眼镜输入、开始采集、停止释放。

## Confirmed facts

- 当前产品边界：Android Flutter UI + Kotlin 音频/网络服务；Windows Avalonia + ShadUI + .NET 10 核心；VB-CABLE 是外部依赖。
- 当前协议默认 TCP 控制与音频，UDP discovery 使用 `17893`，可选 UDP 音频使用 `17892`，TCP 控制使用 `17891`。
- 当前核心代码没有局域网认证；discovery 不是认证机制，Android Manifest 允许明文网络。
- 当前 Android Release 使用 debug signing。
- Android 音频错误不会稳定传达到 Windows；Windows 可能保留 `Streaming` 和 VB-CABLE sink。
- UDP discovery 未加入 multicast group，Windows 只发送全局广播；Windows 自动连接失败后不会可靠重试；发现结果没有完整过期/移除状态。
- Windows UI 丢弃 START 失败结果；音频队列的丢帧统计与 `DropWrite` 行为不一致。
- Android 原生音频路由和服务生命周期没有自动化测试。
- Go backend 与 Flutter Windows adapter 未接入当前 .NET Windows 产品路径；README 含重复构建步骤。

## Requirements

### R1. 安全边界

- TCP 控制连接和音频连接必须在接受控制命令、转发 PCM 前完成配对认证。
- 认证不能依赖 mDNS、UDP discovery 或显示名称。
- 配对密钥不得写入仓库；正式构建密钥也不得写入仓库。
- 保留手动 IP 连接，但未经认证的连接必须失败并显示中文错误。

### R2. 音频生命周期

- Windows 不得在 Android 尚未确认采集成功时长期显示 `streaming`。
- Android 路由激活、`AudioRecord` 创建、读取失败、停止、断开和服务销毁都必须向对端传播可诊断状态。
- 任意失败或停止路径必须关闭 VB-CABLE sink，并清理 Android communication device、SCO 和 `AudioRecord`。
- 禁止回退到手机麦克风。
- START/STOP 必须保持幂等，重复操作不能创建第二个 recorder 或 sink。

### R3. 发现与连接恢复

- mDNS、multicast、directed broadcast/response 各自失败时不能阻断 TCP 监听和手动 IP。
- UDP discovery 必须真正接收 multicast，并覆盖当前可用 IPv4 网卡的 directed broadcast；发现包仍只作为候选地址。
- Windows 和 Android 必须清除过期 peer；地址变化或暂时连接失败后，必须有限重试且不能永久锁死同一 device ID。
- 连接状态、认证失败、设备离线和重试状态必须显示明确中文原因。

### R4. 构建与维护

- Android Release 使用独立正式签名配置；密钥由构建环境提供。
- 补齐 Android 路由/服务/错误传播的最小自动化测试和真实设备 smoke 验证。
- 修正音频队列丢帧计数，保持既有有界内存约束。
- 如果确认 .NET 是唯一 Windows backend，删除未接入产品路径的 Go backend、Flutter Windows legacy adapter 及其专用依赖；不保留两套协议实现。
- 删除 README 重复步骤，迁移已弃用的 Android Gradle 选项。

### R5. UI/交互

- Android 和 Windows 都使用简体中文，状态、错误、连接目标、输入设备和音频输出必须可读。
- 首屏必须明确显示当前阶段：未连接、正在连接、已连接但未采集、采集中、停止中、错误。
- 连接流程必须是可理解的引导：发现电脑 → 选择/手动输入 → 认证 → 显示已连接 → 开始采集。
- “麦克风路由”必须明确标注为自动路由状态，而不是让用户误以为可以点击选择；没有 Ray-Ban 输入时必须显示不可开始的原因。
- 开始/停止按钮必须反映当前状态并防止重复点击；命令失败必须显示错误，不得只留下静态按钮状态。
- 使用现有 Flutter Material 3、Avalonia 和 ShadUI，不新增 UI 框架；不做与音频任务无关的页面和设置。

## Acceptance Criteria

- [ ] 未配对的局域网客户端无法完成 HELLO/START 或收到 PCM；正常配对客户端仍可连接。
- [ ] Android 路由拒绝、眼镜断连、`AudioRecord.read()` 失败和网络断开时，Windows 在有限时间内关闭 sink 并显示错误/断开状态。
- [ ] 正常 START/STOP、重复 START/STOP、失败后再次 START 都保持单 recorder、单 sink 和正确释放。
- [ ] 同网段中 multicast 不可用但 directed broadcast 可用时，仍能发现；discovery 全部失败时，手动 IP 仍可连接。
- [ ] Windows 自动连接首次失败后，Android 服务稍后可用时能有限重试；设备离线后不会继续显示永久有效的旧 endpoint。
- [ ] 音频队列满时丢帧计数真实增加，队列内存仍保持有界。
- [ ] Android 原生测试覆盖路由选择/清理和错误传播；现有 .NET、Go（若未删除）和 Flutter 测试继续通过。
- [ ] Release 构建不再使用 debug signing；构建密钥不进入仓库。
- [ ] Android 与 Windows 首屏能在不看日志的情况下完成发现/连接/开始/停止，并能看懂失败原因。
- [ ] UI 视觉和交互改造不引入新的运行时依赖，且保留手动 IP 连接和“停止并释放”能力。

## Out of scope

- 不实现虚拟麦克风驱动；继续使用外部 VB-CABLE。
- 不实现云端中继、账号系统、多用户后台或远程互联网连接。
- 不增加录音保存、历史记录、音频处理、降噪或转码功能。
- 不支持多台 Android 同时采集；继续维持单个活动音频会话。
- 不在本任务中改造 Ray-Ban/Meta 官方蓝牙固件或 Meta View。

## Key decisions

- 用户已确认采用“深色、紧凑、状态优先”的工具型 UI；Android 与 Windows 首屏都按步骤式连接卡片和明确的主操作按钮重排，而不是只换颜色。
- 继续使用现有 Flutter Material 3、Avalonia 和 ShadUI，不引入新的 UI 框架或状态管理依赖。
- 本任务以单用户、单 Android、单 Windows 进程为目标；不扩展到多用户或互联网中继。

## Risks and deferred items

- 真实 Bluetooth route 行为依赖具体 Android 厂商和 Ray-Ban 固件；自动化测试覆盖逻辑，真实设备 smoke test 覆盖硬件边界。
- 若正式签名、配对认证或 Windows 防火墙配置需要外部凭据/系统权限，代码只提供安全配置入口，不把密钥或系统规则写入仓库。
