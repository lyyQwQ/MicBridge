# 技术设计：双端自主发现与连接

## 1. 边界

本次改造解决“双方都能发现对方、双方都能发起 TCP 连接、连接最终只有一条有效会话”。保留当前 Ray-Ban Android 采集 → Windows 音频输出作为唯一音频闭环；不新增 PC 音频采集 → Android 播放的完整 PCM 路径。

## 2. 对称端点模型

Windows 和 Android 各自拥有一个 Endpoint：

- TCP listener：接受对端控制连接。
- mDNS advertiser：发布设备名、实例 ID、端口、协议版本、source/sink 能力和音频传输能力。
- mDNS browser：发现同服务类型的对端并解析 TXT/SRV/A 记录。
- Connector：用户从发现列表选择对端后主动拨号。
- HELLO：完成身份、角色和音频能力协商。

服务类型保持 `_rayban-voice-mic._tcp`，但 TXT 从固定 `role=windows` 扩展为：

```text
protocol=1
id=<stable local instance id>
name=<display name>
sources=rayban_mic
sinks=windows_audio
transports=tcp,udp
```

Android 的 source/sink 值按实际能力发布。TXT 只放非敏感能力，不放配对码、PCM 或完整主机信息。

## 3. Windows mDNS

`MdnsAdvertisement` 改为显式 IPv4 模式，并用 `NetworkInterface` 过滤器排除回环、隧道、Tailscale、Clash、VMware 等虚拟接口，只保留 Up 且有 IPv4 地址的物理/局域网接口。每个有效接口都必须能对 `224.0.0.251:5353` 发送。

新增 Windows `MdnsBrowser`，订阅 `_rayban-voice-mic._tcp` 的服务发现和解析结果，向 UI/Core 发布去重后的 `DiscoveredPeer`。

mDNS 失败只记录诊断，不阻止 TCP listener 或手动 IP 连接。

## 4. Android mDNS

扩展 `LanDiscovery`：

- 保留现有 `discoverServices`。
- 增加 `registerService`，服务端口由 Android listener 实际绑定结果决定。
- 在服务丢失、Activity/service 停止时注销并清理。
- 解析时接受 `role=windows` 和 `role=android`，改为校验 `protocol=1`、有效 ID、端口和能力字段。

Android listener 由独立的控制服务持有，不能依赖 Flutter Activity 生命周期；Flutter 只订阅发现列表和连接状态。

## 5. LocalSend 风格 UDP discovery fallback

为避免把发现能力绑定到 Windows `5353` mDNS 防火墙规则，增加独立 UDP discovery 层：

- discovery UDP 端口固定为 `17893`；控制 TCP 使用 `17891`，音频 UDP 使用 `17892`，三者不复用。
- IPv4 默认组播地址使用 `224.0.0.167`，同时对每个可用局域网接口的 directed broadcast 发送；不依赖 IPv6。
- announcement/response 使用小型 UTF-8 JSON，字段为 `protocol`、`id`、`name`、`role`、`tcp_port`、`sources`、`sinks`、`transports`、`announcement`。
- 双端启动 listener 后周期性发送 announcement；收到 `announcement=true` 时回单播 response；用设备 ID 和地址去重。
- discovery payload 不携带配对码、PCM、完整主机信息或认证材料；发现结果仍必须经过 TCP `HELLO`。
- UDP listener、发送失败和组播失败只产生诊断，不阻止 TCP、mDNS 或手动 IP。
- 不新增 HTTP 文件传输协议；LocalSend 只作为发现容错思路参考。

这条回退仍可能受防火墙或 AP isolation 影响，因此 manual IP 是最终回退。

## 6. 中文 UI

- Avalonia Windows 使用中文标题、状态、连接、发现、音频、启动/停止、错误文案。
- Flutter Android 使用中文标题、发现列表、连接、权限、音频路由、启动/停止、错误文案。
- 协议字段、端口、设备 capability 名称、日志和错误码保持英文，避免破坏互操作性。

## 7. 连接与重复拨号

两端均可从发现列表拨号。HELLO 增加可选字段：

```json
{
  "peer": "android|windows",
  "device_id": "stable-local-id",
  "connection_id": "per-attempt-id",
  "sources": ["rayban_mic"],
  "sinks": ["windows_audio"],
  "audio_rate": 48000,
  "audio_bits": 16,
  "audio_channels": 1,
  "audio_transports": ["tcp", "udp"]
}
```

兼容规则：旧字段仍然存在；缺失新字段的旧 Android 客户端按当前 Android→Windows 模式处理。

同时拨号时两端都允许连接先完成 HELLO，然后按确定性 key `min(device_id, peer_id) + ":" + min(connection_id, peer_connection_id)` 选择唯一会话；被淘汰的 socket 走现有断线 cleanup。单独拨号的连接不应因方向被拒绝。

## 8. UI 数据流

- Core/Android adapter 负责发现、连接、协议和 socket。
- Avalonia/Flutter 只显示 `DiscoveredPeer` 和连接状态，并调用 typed connect/disconnect command。
- 不在 View 或 Flutter widget 中解析 TXT、帧或能力字符串。
- Windows 的发现列表增加 Android 对端；Android 的发现列表同时显示 Windows 和 Android 对端，但默认只展示能力匹配的连接操作。

## 9. 兼容与回滚

- TCP `17891`、可选 UDP `17892`、固定帧格式和现有 PCM 参数保持不变。
- Windows 旧 Go/Flutter 入口保留，不在本任务删除。
- mDNS 改造失败时，手动 IP 仍连接现有 listener；发现层可单独回滚，不影响 Core 音频链路。
