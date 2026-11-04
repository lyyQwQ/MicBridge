# Android 麦克风设置技术设计

## 边界与数据流

最终规划已获用户批准并进入实施；需求与验收以 prd.md 的 R1–R7 / A1–A9 为准。仅 Android 本地设置与采集处理，不新增依赖，保留 VOICE_COMMUNICATION、48 kHz mono PCM16、路由校验、认证、网络协议和 Windows 行为。

Flutter 设置 sheet → `_AndroidMicPlatform` → 现有 `micbridge/platform` → 原生全局 store → 每次 START 的不可变快照 → 当前 AudioRecord 的系统效果 → readLoop 软件增益 → 原 sender。设置只由 Flutter 修改，读取和保存回应足够同步 UI；不新增 EventChannel、settings stream、revision/baseRevision、pending/session 状态模型或状态上报。AudioStateBus/state 保持原用途。

`android/app/src/main/kotlin/io/github/lyyqwq/micbridge/audio/MicrophoneService.kt:42` 创建 capture，注入 applicationContext 作用域的 MicAudioSettingsStore；Activity 与 Service 使用同一进程单例 store，Flutter 只持有已确认值和临时编辑值。读设置不启动 Service、录音或权限申请，Activity 不持有音效资源。

## 桥接契约（R2–R5、R7）

在 MainActivity.kt:54,96,189–191 / lib/platform_bridge.dart:462–470 的现有通道增加两个方法；MainActivity.kt:181 的 snapshotMap 不扩展。Dart 可选能力接口 `MicAudioSettingsPlatform` 仅有 get/set 两个 Future，由同一个 `_AndroidMicPlatform` 实现，不创建第二个 adapter。保持 MicPlatform:119–137 和 Windows 实现不变。

- `getAudioSettings`：参数为 null，无副作用，返回下述完整设置对象。
- `setAudioSettings`：参数为完整偏好对象 `{gainDb: int, ns: bool|null, aec: bool|null, agc: bool|null}`；四个键必须存在，null 是恢复跟随系统，不能将缺键视为 null。持久化成功返回与 get 相同的对象。

返回形状：

```text
{
  preferences: {gainDb: int, ns: bool|null, aec: bool|null, agc: bool|null},
  available: {ns: bool, aec: bool, agc: bool}
}
```

available 分别来自三个效果的 isAvailable()，仅表达系统 API 支持，不代表当前路由可控或实际开启。查询异常返回 `settings_read_failed`，UI 提供重试，不伪造 false。set 先查询能力并校验，再串行保存；保存成功返回已确认偏好与本次能力结果，避免写成功后因能力查询异常误报保存失败。不可用效果的已有偏好仍保留，不清除、不强制设为 false。

边界拒绝缺字段、额外字段、非整数/越界 gainDb、非 bool/null 效果值，返回 `settings_invalid`；存储失败返回 `settings_persist_failed`。Dart 在唯一封装处类型化解码，畸形响应作为错误。UI 的 `_run` 串行防重入；加载结束前不可写，滑动只预览，onChangeEnd 提交一次完整对象。保存时禁用编辑；成功采用回应、失败恢复已确认值并提示。页面关闭/重开通过 mounted 与页面请求代次丢弃晚到响应，不需要原生事件订阅或跨端版本号。

## 持久化与一致性（R1、R3、R4）

独立 SharedPreferences 文件名 `micbridge_audio_settings`，全局一套，不与 `microphone_input` 或信任存储混用。键名：`gainDb` 为 Int（缺省 0，0–20）；`ns`、`aec`、`agc` 为 Boolean（键缺失表示 null/跟随系统）。恢复系统默认只 remove 对应效果键；不保存 null 字符串、系统初始值、availability、sessionId 或实际 enabled。不增加 schemaVersion 键，当前结构无需迁移。

单一 store 用串行后台执行器执行完整校验与 commit；检查成功后才发布不可变已确认快照和方法成功回应。特别注意 SharedPreferences 在 commit 返回失败时内存仍可能已更新：采集与 get 必须使用 store 的已确认快照，不能直接重读该内存 map；失败保持旧快照并尽力将底层偏好恢复至旧值，恢复失败记日志，不谎报成功。进程重启从磁盘加载并校验；损坏或错误类型逐键回退默认并记一次诊断，不清空其他设置。不可保证磁盘故障时崩溃前后的写入耐久性，错误须如实报告。

每次采集启动只取一次已确认快照；与保存同时发生时按快照发布先后使用完整旧值或新值，不允许混合字段。系数随会话预计算并作为只读参数传给 readLoop，不在音频线程做存储 I/O 或跨线程读可变配置。设置保存不主动 STOP/START。提示固定为“下次开始采集时生效”。自动模式连续空闲 3 秒 STOP 后下一次听写使用新值；短暂停顿或手动连续采集没有新 START 就不会更新（desktop/src/MicBridge.Core/Control/AutoCapturePolicy.cs:6–8,43–50,59–63）。

## 增益算法（R1）

AudioCaptureController.kt:77–95 使用 4096 字节缓冲；在路由检查通过、原 copyOf(count)/send 之前原地处理有效字节，保留 recorder 身份校验与原发送拷贝，不分配中间 PCM 数组。每次 START 预计算 g = 10^(gainDb/20)，20 dB 为 10 倍，0 dB 跳过逐样本循环。little-endian PCM16 解码为有符号样本，round(sample*g) 后夹到 [-32768,32767]，再写回两字节，不能先转 Short。有效 count 外不处理；意外奇数 count 走 `audio_read_failed` 和统一清理，不发送半样本。

系统效果在 read 之前，软件增益在之后；不控制 OEM 效果顺序。限幅防整数回绕，不是无失真 limiter。UI 提示“增益过高可能破音，请降低增益”。

## 效果所有权、失败与日志（R2、R5、R6）

AudioCaptureController.kt:38 先接管 recorder；初始化和 preferred device 校验后、AudioCaptureController.kt:46 startRecording 前，对支持项分别 create(audioSessionId)，成功句柄立即归入本会话。读取 initialEnabled/hasControl；偏好为 null 时绝不调用 setEnabled，显式 bool 在有控制权时调用并检查返回码/getEnabled；启动后再回读一次用于诊断。读取诊断失败不能阻止显式偏好的独立尝试或基础采集。每项 create/hasControl/setEnabled/getEnabled 均独立捕获异常；单项失败不妨碍其他效果或覆盖既有采集错误。

遵循 `.trellis/spec/backend/logging-guidelines.md`；该文件目前为模板，尚无具体日志库或级别约定，本任务不补写外部规范。采用项目现有原生日志方式，建议正常会话诊断为 debug、失败为 warn，只记录效果类型、操作、返回码、布尔结果和匿名会话代次；落实 AGENTS.md:84–89，不记录 PCM、凭据、设备身份或内网 IP。不逐块打印日志。实际 create/setEnabled 失败、无控制权、getEnabled 结果仅日志可见；UI 只展示偏好及全局能力，不新增控制权/状态监听器、重试循环或实际上报。

cleanupLocked():110–120 汇合 stop、启动/读异常。先失效当前 recorder/sender/failure，尝试 stop；逐个清空并 release 效果，最后 release recorder 与恢复路由，各步骤异常隔离，句柄清理不依赖 recorder 非空。保持旧 readLoop 的身份校验，不能清理新会话；不持锁 join/read。重复 START 不创建第二套句柄，重复 STOP 安全。下次采集重新 create 并应用新快照。

## UI 选择与理由（R2、R7）

采用每个效果始终显示同一个三段选择「系统默认 / 开 / 关」，分别对应 null / true / false，使用 Material Slider 与自带 `SegmentedButton`（单选、不允许空选），不使用第三方控件。（用户真机反馈后修改：原“系统默认标注 + 设为开/设为关、显式后改为 Switch + 恢复系统默认”的方案会在首次点击后中途变样，已整体替换，不再保留 Switch、设为开/设为关与恢复系统默认按钮及其文案。）不可用项整段禁用并显示“此效果在该设备上不可用”，仍可看到已保存偏好；保存中整段禁用防重入，保存失败回到已确认的选中项。

理由：未采集时没有系统实际初始值；“系统默认”作为独立一段，不画一个关闭的开关冒充实际状态，且无需临时 AudioRecord。控件形态不随取值变化。全页说明“选项表示保存的偏好，实际效果取决于系统和设备”，保存提示仅表示持久化完成，不承诺实际生效。三段与标题上下排列、横向撑满，412 宽 1.3 倍字号下单行不溢出。

入口位于 lib/main.dart:541–549 的 Android MicrophoneSummary 之后，独立设置 sheet；dashboard_widgets.dart:565 的设备选择 sheet 仍返回 int。标题“麦克风设置”避免 test/widget_test.dart:106 的精确查找冲突。滑块 min=0、max=20、divisions=20，整数 dB 语义标签；沿用主题、大字号和无状态呈现。Windows 无入口、不调用新方法。

## 兼容、风险与回滚

旧安装缺键即使用默认，无协议迁移。isAvailable/create/getEnabled 均不能保证 SCO 听感；AGC 叠加可能削波，AEC 无 Windows 回放参考。真机验证见 implement.md；这些是已知边界，无未决产品决策。

后续回滚顺序：隐藏入口/停用新调用 → 增益设 0、效果恢复系统默认并重新采集 → 若资源生命周期回归则撤回新增处理链。可忽略遗留非敏感存储键，无须删除文件。本轮不执行任何回滚或 git 写操作。

## 证据索引

K/ 表示 android/app/src/main/kotlin/io/github/lyyqwq/micbridge/。保留原设计挂点供实施复核：K/audio/AudioCaptureController.kt:38,77–95,110–120；K/audio/MicrophoneService.kt:42；K/MainActivity.kt:54,96,181,189–191；lib/platform_bridge.dart:119–137,462–470；lib/main.dart:541–549；lib/dashboard_widgets.dart:565；test/widget_test.dart:106；K/audio/BluetoothRouteManager.kt:25–39；android/app/build.gradle.kts:39；android/protocol-check/verify.ps1:25–48。规范依据（均位于 .trellis/spec/）：frontend/state-management.md:5,13,18,64,79；backend/error-handling.md:23–36；guides/cross-layer-thinking-guide.md:19–50,86–101；隐私约束 AGENTS.md:84–89。官方 API 与设备边界见 research/audiofx.md。
