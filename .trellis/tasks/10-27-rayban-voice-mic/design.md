# 技术设计：Ray-Ban Meta 按需麦克风双端应用

## 1. 设计目标

建立一个最小双端系统：Flutter 提供 Android/Windows UI，Android 原生服务按命令控制 Ray-Ban Meta 麦克风，Go Windows 核心负责局域网通信、快捷键和 PCM 输出到 VB-Cable。

不包装 AudioRelay，不读取或依赖其私有协议；AudioRelay 只保留为对照和回退工具。

## 2. 组件边界

```text
apps/client (Flutter)
  ├─ Android UI
  │    └─ Kotlin platform adapter + ForegroundService
  └─ Windows UI
       └─ local IPC client

native/android
  ├─ Bluetooth route manager
  ├─ AudioRecord lifecycle
  ├─ LAN control/audio client
  └─ foreground service

native/desktop
  ├─ LAN control/audio server
  ├─ state machine
  ├─ global hotkey/tray integration
  ├─ Windows audio sink → VB-Cable
  └─ mDNS service advertisement

protocol
  ├─ control command/status contract
  └─ framed PCM packet contract

discovery
  ├─ Windows DNS-SD advertisement
  └─ Android NsdManager browser/resolver


Flutter 不直接访问 `AudioRecord`、Bluetooth 或 Windows 音频设备。平台层只向 UI 暴露已验证的 typed state 和 command API。

## 3. 数据流和状态

```text
Flutter Windows → Go local IPC → LAN control → Android service
Android Bluetooth input → AudioRecord → framed PCM → Go audio buffer
Go audio buffer → VB-Cable output → Typeless input
```

Android 状态：`idle → starting → streaming → stopping → idle`。

以下事件都必须经过同一个停止路径：用户 STOP、TCP 断开、录音异常、服务销毁。停止路径必须幂等，并按顺序停止读取、停止/释放 `AudioRecord`、清除 communication device、关闭网络资源。

Windows 状态：`disconnected → connected_idle → starting → streaming → stopping → connected_idle`。只有 `streaming` 状态允许 PCM 写入 VB-Cable；快捷键和 UI 按钮都调用同一个 command dispatcher。

## 4. 网络协议

TCP 是默认路径，控制和音频都走 Android 到 Windows 的 TCP 连接。Windows core 设置 `RAYBAN_AUDIO_TRANSPORT=udp` 时，只有 Android `HELLO` 声明支持 UDP 才切换音频；控制连接仍保持 TCP。UDP 音频使用同一 20 字节帧头、固定端口 `17892`、最大 1200 字节 datagram，不提供重传和排序恢复，未协商或 UDP 监听失败时回退 TCP。

Windows advertises `_rayban-voice-mic._tcp.local` on TCP port `17891`. The TXT record contains only `role=windows`, `protocol=1`, and a display name. Android resolves the service and then uses the existing TCP `HELLO`; discovery is not an authentication layer. Manual host entry remains the fallback when multicast DNS is unavailable.

每条消息使用固定头部：magic、version、message type、sequence、payload length；控制 payload 使用 UTF-8 JSON，音频 payload 使用指定格式的裸 PCM。协议实现集中在 `protocol` 包，Android 和 Go 不在业务层重复解析字段。

控制命令：`HELLO`、`START`、`STOP`、`STATUS`、`PING`。

`HELLO` 报告音频格式和 `audio_transports`；Windows `STATUS` 返回最终 `audio_transport`，选择 UDP 时还返回 `audio_port`。必须定义：

- 重复 `START` 返回当前 streaming 状态，不创建第二个 recorder；
- 重复 `STOP` 返回 idle，不报致命错误；
- 非法版本、长度超限、未知命令直接返回可诊断错误并关闭连接；
- 网络断开等价于 STOP；
- 音频格式在 `HELLO/STATUS` 中明确报告，首版固定 48 kHz、16-bit、mono PCM。

## 5. Android 原生实现

- Kotlin `ForegroundService` 保持控制连接，但 idle 时不得创建或启动 `AudioRecord`。
- 启动前检查 `RECORD_AUDIO`、Bluetooth 相关权限和通知权限。
- 枚举 `AudioManager.getDevices(GET_DEVICES_INPUTS)`，以设备地址/名称建立选择结果；不得把手机内置麦克风当作成功回退。
- Android 12+ 调用 `setCommunicationDevice()`，记录返回结果和最终 `communicationDevice`。
- 创建 `AudioRecord` 后验证初始化状态和实际输入设备，再进入 streaming。
- STOP 和所有异常使用统一 cleanup；`AudioRecord` 资源释放后才报告 stopped。
- 需要兼容 Android 14+ microphone foreground service 声明和后台启动约束；首次启动由用户在前台完成。

## 6. Go Windows 核心

- Go 单独进程运行，Flutter 通过 localhost IPC 调用它；这样音频核心不依赖 Flutter isolate 生命周期。
- LAN server 管理一个 Android client；首版不支持多设备。
- 使用固定大小缓冲和有界队列，避免网络抖动无限占用内存。
- 先验证 Go 的 Windows 音频输出库对 VB-Cable 的共享模式可用；不能工作时才评估 C++ WASAPI sink，不提前引入 C++。
- 全局快捷键、托盘、启动/停止按钮都调用同一个 state machine。
- 日志只记录状态、设备标识摘要、错误码和延迟，不记录 PCM。

## 7. Flutter UI

- 一个 Flutter 工程同时生成 Android 和 Windows 客户端。
- Android UI 展示眼镜设备、电脑连接、麦克风占用和权限状态。
- Windows UI 展示手机、音频设备、运行状态、快捷键和诊断信息。
- Android UI shows discovered Windows services and keeps manual host entry as fallback.
- UI 只消费平台层的 typed state stream，不自行解析网络消息或推导录音状态。
- 首版使用简单 Material 3 组件和明确状态颜色，不先做复杂动画、主题系统或账号功能。

## 8. 兼容与许可证

- AndroidMic 的实现作为行为和音频链路参考，不直接复制 GPL-3.0 代码，除非项目按 GPL-3.0-only 发布并保留相应声明。
- `BTMicFix` 的 Apache-2.0 路由实现可作为独立参考，复用时保留许可证和 NOTICE 要求。
- 新代码优先使用独立实现，依赖只在确认构建和许可证兼容后加入。
- VB-Cable 属于外部系统依赖，不打包其驱动；安装说明必须明确说明。

## 9. 失败与回退

- 若设备枚举中没有 Ray-Ban Meta 输入：停止，不发送手机内置麦克风音频，并显示设备列表和原因。
- 若 Go 无法打开 VB-Cable：显示设备打开错误，不假装 streaming 成功。
- 若控制连接断开：Android 自动释放录音；Windows 回到 connected_idle 或 disconnected。
- 在真实设备验证通过前，不承诺所有 Android 厂商和蓝牙协议模式兼容。
