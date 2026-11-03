# 双端自主发现与连接

## Goal

让 Windows 与 Android 都能在同一局域网内广播、扫描并主动建立音频连接，交互方式参考 AudioRelay 的对称 Server/Player 模型，解决当前 Android 扫不到 Windows 的问题。

## Confirmed Facts

- 当前 Windows 仅广播 `_rayban-voice-mic._tcp`，Android 仅通过 `NsdManager` 扫描。
- 当前 Windows 以太网地址为 `<windows-lan-ip>`，Android WLAN 地址为 `<android-lan-ip>`；Android 到 Windows TCP `17891` 已验证可达。
- mDNS 是链路本地组播：IPv4 使用 `224.0.0.251:5353`，每张可用网卡都需要独立加入组播组。
- Makaretu 支持网卡过滤以及 IPv4/IPv6 开关；当前实现没有显式筛选物理网卡。
- 现有 `HELLO`、TCP/UDP 音频帧和 Android Ray-Ban 采集链路已经可用，不能因发现改造而破坏。

## Requirements

### R1. 对称发现

- Windows 和 Android 都必须同时提供 mDNS 广播和扫描能力。
- 发现记录至少包含设备名、设备 ID、协议版本、TCP 端口、可用 source/sink 能力和音频传输能力。
- Android 必须能发现 Windows，Windows 也必须能发现 Android。

### R2. 多网卡广播

- Windows 对所有已启用且有局域网 IPv4 地址的物理接口分别广播。
- 必须排除回环、Tailscale、Clash、VMware 和隧道接口。
- 至少覆盖当前以太网 `<windows-lan-ip>`。
- mDNS 失败不能阻止 TCP 服务启动；手动 IP 仍保留。

### R3. 任意一端发起连接

- 任一端选择发现到的对端后都能主动建立 TCP 控制连接。
- TCP `HELLO` 必须协商发起方、对端身份、source/sink 能力、采样参数和 TCP/UDP 能力。
- 两端同时发起时必须确定性收敛到一条连接，不能形成重复会话。
- 现有 Android Ray-Ban 麦克风 → Windows 音频输出路径保持兼容。

### R4. 安全与边界

- mDNS 只用于发现，不作为认证。
- 不在 TXT 记录中放置配对码、PCM 或敏感主机信息。
- 非法 HELLO、能力不匹配、重复连接和断线必须进入现有 cleanup/error 状态机。

## Acceptance Criteria

- [ ] Windows 和 Android 在同一 WLAN 上互相出现在发现列表。
- [ ] Windows 多网卡环境下，物理以太网接口可发现；虚拟网卡不会成为唯一广播接口。
- [ ] 从 Android 发起连接后，Windows Core 收到合法 HELLO 并显示连接状态。
- [ ] 从 Windows 发起连接后，Android 控制客户端收到合法 HELLO 并显示连接状态。
- [ ] 同时点击双方连接时最终只有一个活动会话。
- [ ] Android Ray-Ban 麦克风 → Windows 音频输出的现有 TCP/UDP 链路回归通过。
- [ ] mDNS 不可用时，手动 IP 连接仍可用。
- [ ] Android/Windows 构建、协议测试、发现测试和 UI 烟测通过。

### R5. LocalSend 风格发现回退

- mDNS 不再是唯一发现机制；Windows 和 Android 增加独立 UDP discovery listener。
- 默认 discovery UDP 端口为 `17893`，与控制 TCP `17891`、音频 UDP `17892` 分离。
- 双端通过 `224.0.0.167` 和当前局域网 IPv4 directed broadcast 发送非敏感 JSON announcement；收到 announcement 后通过单播 response 返回自身端点。
- discovery payload 只包含设备名、设备 ID、角色、协议版本、TCP 端口和能力；真正连接仍必须通过现有 TCP `HELLO`。
- UDP discovery 失败不能阻止 TCP listener、mDNS 或手动 IP；不得把 discovery 当认证。

### R6. 双端中文界面

- Windows Avalonia 可见标题、状态、连接、发现、音频设备、启动/停止和错误提示使用简体中文。
- Android Flutter 可见标题、发现列表、连接、权限、音频路由、启动/停止和错误提示使用简体中文。
- 协议字段、设备名、端口、技术日志和错误码保持英文原文。

## Acceptance Additions

- [ ] 不依赖 mDNS 时，Windows 和 Android 可通过 UDP discovery 互相发现。
- [ ] UDP discovery 使用独立端口，不与控制/音频帧混用；重复 announcement 去重且响应可重复。
- [ ] Windows 和 Android 的主要用户可见文案均为简体中文。

## Out of Scope

- 本任务不新增 PC 音频采集到 Android 播放的完整反向 PCM 链路。
- 不复制 AudioRelay 私有网络协议。
- 不删除旧 Go/Flutter Windows 回滚路径，待双端真机验证后另行处理。

## Risks / Deferred

- Android `NsdManager` 的服务注册和发现行为受系统权限、Wi-Fi 隔离和厂商实现影响。
- Windows 防火墙可能阻止 UDP `5353`，需要保留诊断和手动 IP 回退。
- 多个同时存在的 mDNS responder 可能共享 `5353`，需要用实际网卡和抓包/枚举结果验证。
