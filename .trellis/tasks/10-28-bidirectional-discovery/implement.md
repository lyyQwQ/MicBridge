# 实施计划：双端自主发现与连接

## Phase 0：规格与协议

1. 更新 `.trellis/spec/backend/error-handling.md` 的发现/HELLO/重复连接契约。
2. 更新 `.trellis/spec/frontend/state-management.md` 的双端发现列表和连接命令契约。
3. 扩展 `protocol/README.md`：双端 TXT 字段、HELLO 可选能力字段、重复连接仲裁。
4. 为 `HelloPayload` 增加兼容的 device/connection/capability 字段；旧客户端缺失字段时保持 Android→Windows 默认路径。

验证：协议 JSON round-trip、旧字段兼容、未知能力拒绝或降级到 TCP。

## Phase 1：Windows mDNS 多网卡与 Browser

1. 修改 `MdnsAdvertisement` 使用 IPv4、显式接口过滤和诊断状态。
2. 排除 Loopback、隧道、Tailscale、Clash、VMware 等虚拟接口；覆盖物理以太网/WLAN。
3. 新增 Windows `MdnsBrowser`，解析 TXT/SRV/A 记录并去重 `DiscoveredPeer`。
4. 将 Core 的 discovered peers 事件接到 Avalonia ViewModel；手动 IP 继续保留。

验证：在本机多网卡环境列出 `<windows-lan-ip>` 广播接口；mDNS 不可用时 TCP listener 仍启动。

## Phase 2：Android 注册与双向 Listener

1. 扩展 `LanDiscovery` 注册 Android 服务并保留扫描；处理注销、服务丢失和重复 resolve。
2. 新增 Android 控制 listener/service，复用现有 `Protocol` 和 `ControlClient` 的握手/状态处理，不复制帧编解码。
3. 将 Flutter platform bridge 改为订阅 discovered peers 和连接状态，连接命令可选择任意合规对端。
4. 更新 Android 权限、生命周期和错误展示；不得在 Activity 销毁后泄漏 NSD listener 或 ServerSocket。

验证：Android build、连接/断开状态、Android→Windows 现有路径回归。

## Phase 3：连接仲裁与 UI

1. 为 Windows outgoing connector 和 Android incoming/outgoing session 增加 per-attempt connection ID。
2. 实现同时拨号的 deterministic winner；淘汰连接走统一 STOP/close cleanup。
3. Windows/Android UI 显示对端名称、地址、能力和连接方向；Start/Stop 规则保持现有状态机。
4. 保持无 mDNS 时手动 IP 连接。

验证：单向拨号、反向拨号、同时拨号、重复 START/STOP、断线 cleanup。

## Phase 4：LocalSend 风格 UDP discovery fallback

1. 新增跨 Windows/Android 共用的 discovery JSON 约定和独立 UDP `17893` listener。
2. Windows 对物理/局域网接口发送 `224.0.0.167` 与 directed broadcast；Android 发送 Wi-Fi broadcast，并对 announcement 单播回应。
3. discovery 结果复用现有 `DiscoveredPeer`/`ComputerEndpoint`，收到候选后仍通过 TCP `17891` HELLO。
4. mDNS 保留为并行发现路径；UDP、mDNS 任一路径失败都不能阻止 TCP/manual。

验证：关闭/绕过 mDNS 后，Android 和 Windows 仍能收到 announcement、去重设备并完成 TCP 握手。

## Phase 5：中文 UI

1. 将 Windows Avalonia 可见文案、状态标签、连接/发现/音频/错误提示改为简体中文。
2. 将 Android Flutter 可见文案、发现列表、连接、权限、音频路由、启动/停止提示改为简体中文。
3. 保留协议字段、端口、设备 capability、日志和错误码的英文原文。

验证：Windows UI smoke 和 Android APK 启动后主要用户可见文案为中文，状态转换不改变。

## Phase 6：质量门

```powershell
dotnet format desktop/RaybanVoiceMic.slnx --verify-no-changes
dotnet test desktop/RaybanVoiceMic.slnx -c Release
cmd /c gradlew.bat assembleDebug --no-daemon --console=plain
flutter analyze
flutter test
```

额外验证：

- Windows 真实网卡与虚拟网卡并存时 mDNS 发现。
- Android 与 Windows 互相出现在发现列表。
- 两端分别发起连接和同时发起连接。
- `RAYBAN_AUDIO_TRANSPORT=tcp/udp` 协商不回归。
- 无 VB-Cable、无 mDNS、端口占用时错误可见且资源释放。

## 风险与回滚

- Android NSD 注册在不同厂商系统上可能失败；保留手动 IP。
- Windows 5353 端口可能被其他 responder 共享；显式网卡过滤不能破坏 TCP。
- 双向 listener 扩大攻击面；只接受协议版本、能力和长度合法的 HELLO，不把 mDNS 当认证。
- 旧 Go/Flutter Windows 路径保持不动，直到双端真机验证通过。
