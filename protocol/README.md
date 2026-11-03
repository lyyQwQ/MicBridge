# MicBridge 协议

## 连接与发现

- Windows 和 Android 都监听 TCP `17891`，都广播并扫描 `_micbridge._tcp.local`。
- mDNS 之外，双方使用独立 UDP discovery 端口 `17893`，向 IPv4 `224.0.0.167:17893`、局域网 directed broadcast 和 `255.255.255.255` 发送小型 UTF-8 JSON announcement。
- UDP discovery JSON 字段为 `protocol`、`id`、`name`、`role`、`tcp_port`、`sources`、`sinks`、`transports`、`announcement`；单播响应必须声明响应者自己的身份和能力，并将 `announcement` 设为 `false`，不能回显请求者载荷。不携带 PCM、长期密钥或认证 proof。
- TXT 和 UDP discovery 记录包含 `protocol=1`、稳定 `id`、显示 `name`、`sources`、`sinks` 和 `transports`；不包含 PCM 或认证材料。
- mDNS/UDP 只提供候选地址和自报身份，不建立信任。选择发现设备时向本地连接 API 传 `expectedDeviceId`；原生连接仍须验证持久化的设备身份、配对 ID 和双向 proof。发现失败时保留手动主机/端口回退。
- UDP 每 5 秒发送；Android 按 `role:id` 增量合并各来源和地址，每次真实观测续命并转发页面，即使列表内容未改变。Android 候选 TTL 为 20 秒（5 秒扫描），mDNS lost 仅移除对应服务来源；同一实例只显示一行，优先手机 Wi-Fi 同网段地址，同等级地址在 6 秒内保持当前选择，之后选择最近观测地址。Windows 的发现 TTL 保持 15 秒。
- 两端都可发起 TCP 连接，但授权角色固定：Windows 允许 Android。`HELLO` 必须声明 `auth_mode=device_trust_v1`，携带有效的 `device_id` 和每次连接独立的 `connection_id`。旧认证模式被拒绝，不静默降级。首次无信任记录时发送 `pairing_requested=true`；有记录时发送保存的 `pairing_id`，不请求覆盖已有信任。

设备显示名称：Windows 取 `Environment.MachineName`，空或空白时回退为 `MicBridge`；Android 优先取 `Settings.Global.DEVICE_NAME`，空或空白时依次回退为 `Build.MODEL`、`Android`。名称用于发现与界面显示，不参与设备身份匹配或 HMAC 认证。mDNS/NSD 实例标签最多 63 个 UTF-8 字节，截断时保留完整字符；`HELLO.device_name` 最多 256 个 UTF-16 代码单元，不允许控制字符。

已配对设备重新认证成功后，仅当对端 `HELLO.device_name` 非空、合法且与保存名称不同时刷新信任记录中的名称；设备 ID、配对 ID、密钥、host 和 port 保持不变。未认证或认证失败的连接不能刷新名称。Windows 通过现有 `TrustedDevice` 状态发布更新后的显示名称。
 
## 帧格式

每帧为 20 字节大端序头部加 payload：

| 偏移 | 长度 | 内容 |
| --- | ---: | --- |
| 0 | 4 | magic `MCBR` |
| 4 | 1 | version `1` |
| 5 | 1 | message type |
| 6 | 2 | 保留 |
| 8 | 4 | sequence |
| 12 | 4 | payload length |
| 16 | 4 | CRC-32/IEEE(payload) |
| 20 | N | payload |

控制 payload 是 UTF-8 JSON，最大 64 KiB；音频 payload 是 48 kHz、16-bit、mono PCM，单帧最大 1 MiB。双方都拒绝非法 magic/version、超长 payload 和校验失败。

## 传输选择

TCP 同时承载控制和音频，是默认且推荐路径。Windows core 设置 `MICBRIDGE_AUDIO_TRANSPORT=udp` 后，只有 Android `HELLO` 声明支持 UDP 时才切换音频到 UDP；控制连接仍保持 TCP。UDP 音频使用相同帧格式，端口为 `17892`，单个 datagram 不超过 1200 字节，不做重传或排序恢复；网络不稳定时应保持默认 TCP。

`HELLO` 至少包含：

```json
{"peer":"android","device_name":"<phone-model>","device_id":"0123456789abcdef0123456789abcdef","connection_id":"fedcba9876543210fedcba9876543210","auth_mode":"device_trust_v1","pairing_requested":true,"pairing_id":null,"sources":["micbridge_mic"],"sinks":[],"audio_rate":48000,"audio_bits":16,"audio_channels":1,"audio_transports":["tcp","udp"]}
```

`device_id`、`connection_id` 和非空 `pairing_id` 必须是 32 位小写十六进制字符串（`[0-9a-f]{32}`，无连字符）。设备 ID 来自持久化的安装身份，连接 ID 每次新建，配对 ID 每次重新允许配对时新建。发现记录中的设备 ID 必须与认证身份一致；不接受大小写或连字符等自动归一化作为身份匹配。

Windows `STATUS` 会返回 `audio_transport`；选择 UDP 时还返回 `audio_port`。Android 以该状态为准发送音频，不能自行猜测 UDP 端口。

## 控制消息与认证

消息类型为 `HELLO=1`、`START=2`、`STOP=3`、`STATUS=4`、`PING=5`、`RESPONSE=6`、`AUDIO=7`、`ERROR=8`、`AUTH_CHALLENGE=9`、`AUTH_PROOF=10`、`AUTH_OK=11`、`PAIR_GRANT=12`、`PAIR_ACK=13`。认证完成前不得接受录音控制或音频。

### 方案 A 的安全边界

此版本仅适用于**可信家庭 LAN**，TCP/UDP 都是明文传输，未使用 TLS 或音频加密。首次允许后，Windows 通过明文 TCP 发送长期密钥；能监听这次交换的人可以获得该密钥。后续 HMAC-SHA256 双向证明用于验证密钥持有者，**不代表传输加密，也不为后续控制消息或 PCM 提供密码学完整性保护**。CRC-32 只检测传输损坏。本方案不抵御局域网中的主动中间人，不应用于公共或不可信网络，也不应将端口转发到互联网。

### 首次允许与记住设备

1. 双方交换 `HELLO`。Windows 验证请求并向本地界面发布待审批请求，默认有效期 60 秒；设备名是对端自报的显示信息。未经允许、过期、用户拒绝或关闭对话框时，不能建立新信任。桌面 IPC 的 stdin EOF 会拒绝当前请求、禁用后续审批并关闭核心；没有审批界面时不能自动允许。
2. 允许后 Windows 生成新的 `pairing_id` 和 32 字节随机密钥，将密钥编码成无 padding 的 base64url 字符串。`PAIR_GRANT` 载荷为 `{"pairing_id":"...","secret":"..."}`。
3. Android 保存该设备身份、配对 ID 和密钥后发送 `PAIR_ACK {"pairing_id":"..."}`。Windows 核对 ACK；只有正确 ACK 的新配对才可持久化为可信记录。Windows 用当前用户 DPAPI 保护本地密钥，Android 用 Keystore 密钥保护本地记录；保护的是静态存储，不是网络流量。
4. 随后执行下述双向证明。保存信任不等于当前会话已认证；两端通过证明后才进入可用控制状态。

已经记住的设备重连时，跳过 `PAIR_GRANT` / `PAIR_ACK`，直接验证保存的设备 ID、`pairing_id` 并执行双向证明。身份或配对 ID 不匹配、旧设备仍持有已被另一端忘记的记录、存储损坏或解密失败时，不能回退到无需确认的新配对，也不能自动覆盖旧记录。系统只保存一个可信对端。

本版本没有 provisional 配对恢复协议。Android 落盘后若 ACK 丢失，或任一端在 ACK / 落盘阶段中断，可能只有一端保存了新记录。遇到这种半完成状态，需要两端都忘记旧记录，再重新连接并允许；不能把已落盘的单边记录视为双方配对成功。

MicBridge 的 Windows 信任文件位于 `%LOCALAPPDATA%\MicBridge\device-trust.dat`（测试可用 `MICBRIDGE_TRUST_PATH` 指定隔离路径）。Android 使用 SharedPreferences `micbridge_device_trust` 和 Keystore alias `micbridge_device_trust_aes256_v1`；云备份与设备转移均排除 `micbridge_device_trust.xml`。新版不会迁移或删除旧版数据，首次使用需两端重新配对。

### HMAC 双向证明

Windows 发送 `AUTH_CHALLENGE`，包含 `challenge`、Windows 的 `device_id` / `connection_id` 和 `expires_unix_ms`。challenge 来自 24 字节随机数，有效期 10 秒。

两端使用 **UTF-8(secret 字符串)** 作为 HMAC-SHA256 密钥，不把 base64url 字符串先解码成随机字节。摘要编码为无 padding 的 base64url。Android 的 proof 消息如下，其中两个设备 ID 按 ordinal 排序、两个连接 ID 独立按 ordinal 排序：

```text
MICBRIDGE-AUTH-V1|1|deviceIdLow|deviceIdHigh|connectionIdLow|connectionIdHigh|challenge
```

Android 发送 `AUTH_PROOF {"proof":"...","nonce":"..."}`；nonce 来自本次认证独立的 24 字节随机数（base64url 无 padding）。Windows 验证手机 proof 和 challenge 时效后，发送 `AUTH_OK {"proof":"..."}`。此服务器 proof 的消息严格按角色排列：

```text
MICBRIDGE-SERVER-AUTH-V1|1|windowsDeviceId|androidDeviceId|windowsConnectionId|androidConnectionId|pairingId|challenge|nonce|phoneProof
```

Android 验证服务器 proof 后才接受会话。缺少或伪造的 `AUTH_OK`、过期 challenge、错误 proof、身份不匹配或非法载荷均拒绝。随后 Windows 的 `STATUS` 提供当前状态和协商后的音频传输方式。长期密钥、proof、nonce、challenge 不进入日志、发现记录或 UI 快照。

跨端固定向量：secret 为 UTF-8 字符串 `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8`，Windows / Android device ID 分别为 32 个 `0` / `1`，connection ID 分别为 32 个 `2` / `3`，pairing ID 为 32 个 `4`。challenge、nonce 为字面量 `challenge`、`nonce`。手机 proof 为 `qeQlIH3jgyRrUVuK5lYFItGvaSBAle_1SPSp3gGqPK8`。独立的服务器 proof 测试将 phoneProof 输入固定为字面量 `phone-proof`（不是前述手机 proof），结果为 `YDV738wX-8B_PNSxQN1zZFZajFfbMcOTvGlix6RpNrQ`；Kotlin 与 .NET 验证采用同一向量。

网络断开等价于 `STOP`；重复 `START`、`STOP` 保持幂等。Windows 只在 `streaming` 时接收音频；Android 只在收到 `START` 后创建 `AudioRecord`。路由、采集、读取或发送失败时发送 `ERROR` 并执行与 `STOP` 等价的清理。

## 本地界面 API

Windows Flutter 与 .NET 核心通过 stdin/stdout 交换逐行 JSON。命令带字符串 `id` 和 `command`；响应带相同 `id`、`ok` 以及结果或错误消息。诊断使用 stderr。

| 命令 | 参数 / 结果 |
| --- | --- |
| `connect` | `host`、`port`，以及选择发现设备时的可选 `expectedDeviceId`；不接受连接口令 |
| `status` | 返回 `snapshot`，与 `state` 事件的 `snapshot` 同结构 |
| `pairing_decide` | `requestId`、`decision: "allow"` 或 `"deny"`；核心校验该请求仍然有效 |
| `forget_device` | 无参数；停止当前会话并清除本机保存的可信对端 |

`pairing_decide` 成功返回 `{"id":"...","ok":true,"accepted":true}`。失效请求或无效决定返回 `{"id":"...","ok":false,"accepted":false,"code":"pairing_request_stale","message":"配对请求已失效或决定无效，请在手机上重新连接"}`。同一待审批请求重复提交相同决定具有幂等性；提交不同决定被拒绝。前端按 `ok` 判断结果并显示核心返回的错误消息，不因失效响应再次弹出同一请求。

`snapshot` 保留 `state`、`device`、`detail`、`connected`、音频及 `listenerActive` 等状态，并包含：

```json
{
  "trustState": "pairing",
  "trustedDevice": null,
  "pendingPairing": {
    "requestId": "00112233445566778899aabbccddeeff",
    "deviceId": "0123456789abcdef0123456789abcdef",
    "deviceName": "<phone-model>",
    "expiresAt": "2026-11-02T12:34:56.000Z"
  }
}
```

有可信记录时 `trustedDevice` 为 `{"deviceId":"...","deviceName":"...","pairingId":"..."}`。没有待审批请求时 `pendingPairing=null`。`trustState` 为 `unpaired`、`pairing`、`trusted` 或 `repair_required`；可信记录可在断线时保留，不能仅凭 `trusted` 判断已连接。

独立事件 `{"event":"pairing_request","request":{...}}` 的 `request` 与 `snapshot.pendingPairing` 完全同结构，`expiresAt` 为 UTC ISO 8601 字符串。Windows 界面从事件和状态快照恢复请求，按 `requestId` 只弹一次；允许/拒绝调用 `pairing_decide`，返回、Esc 或点击遮罩关闭按拒绝处理。界面每 2 秒补查 `status`，退出时取消轮询；命令进行中收到的新事件优先于较早的状态响应。请求撤销或过期后关闭对话框，核心仍是批准有效性的最终裁决者。

Android MethodChannel `micbridge/platform` 使用 `connect({host, port, expectedDeviceId?})` 和无参数 `forgetDevice()`。`status` 返回快照，EventChannel `micbridge/state` 发布状态变化；`trustedDevice` 与 `trustState` 使用上述结构。Android 不负责审批 Windows 弹窗。忘记操作由服务异步完成并发布最终状态或错误。

## 单边忘记与后台范围

忘记只删除执行操作的一端的持久化记录并停止它的当前会话，不假定另一端在线，也不声称远程删除。旧配对 ID 不再能在被忘记的一端通过认证。若另一端仍有旧记录，需要在那一端也执行忘记，再从 Android 连接并在 Windows 重新允许；没有静默重新配对。

自动重连仅限 Windows 核心和已启动的 Android 服务仍在运行且网络可达。Android 需从前台完成权限授权和服务启动，不提供开机自启、强行停止后的自动唤醒或绕过麦克风后台限制的保证。已启动服务在后台、锁屏和厂商省电策略下的录音行为必须真机验证。麦克风启动失败应报告错误并释放路由；没有录音活动时不持续占用麦克风。
