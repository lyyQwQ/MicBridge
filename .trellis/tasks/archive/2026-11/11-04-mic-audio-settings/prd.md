# Android 麦克风设置：增益与系统音效

## Goal

让内置麦和 Ray-Ban Meta 用户在 Android 上调节送往电脑的语音增益与系统 NS（降噪）、AEC（回声消除）、AGC（自动增益）偏好。

最终规划已获用户批准，当前实施范围为 implement.md 第 1–8 步；允许运行 task.py start、产品检查与 APK 构建。禁止 git 写操作、删除文件、手机操作和 GUI；第 9 步真机验收留给用户。实施与验证记录见 implement.md。

## Background：已确认事实与证据

下列路径相对仓库根目录；Kotlin 根路径为 `android/app/src/main/kotlin/io/github/lyyqwq/micbridge/`（表中 K/ 表示此路径）。

|事实|代码证据|
|---|---|
|录音为 48 kHz mono PCM16、VOICE_COMMUNICATION；先保存 recorder 所有权再校验状态与路由|K/audio/AudioCaptureController.kt:21–46|
|readLoop 使用4096字节缓冲，检查读结果、路由与当前 recorder，发送 copyOf(count)|K/audio/AudioCaptureController.kt:77–95|
|启动异常、停止、读异常分别汇入 cleanupLocked；清空字段、stop/release、恢复路由|K/audio/AudioCaptureController.kt:54–60,64–74,97–119|
|MainActivity 分发本地 MethodChannel；状态订阅回主线程，snapshotMap 合并输入选择和信任元数据|K/MainActivity.kt:54–106,181–186|
|通道为 micbridge/platform、micbridge/state、micbridge/discovery；Dart 的 _AndroidMicPlatform 封装|K/MainActivity.kt:189–191；lib/platform_bridge.dart:462–499|
|输入偏好使用 SharedPreferences("microphone_input") + apply；跟随系统会清空整个该文件|K/audio/BluetoothRouteManager.kt:25–39|
|信任使用独立 SharedPreferences 和检查 commit 结果；不宜混放音频偏好|K/trust/DeviceTrustStore.kt:36,49,113–114|
|Android UI 是 main.dart 的 MicrophoneSummary 分支；设备选择 sheet 返回 int；暂无 Slider/Switch 组件先例（只读搜索 lib/）|lib/main.dart:541–549；lib/dashboard_widgets.dart:529–643|
|Flutter 拥有呈现状态，_run 统一防重入；不应持有音频资源|lib/main.dart:365–378；.trellis/spec/frontend/state-management.md:5,13,18,64|
|已有 Flutter 页面、布局、路由更新、竞态、Windows 桥接测试；Android 无 src/test/src/androidTest，无 JUnit 依赖|test/widget_test.dart:27,106,1042；test/dashboard_widgets_test.dart:35；test/input_sheet_live_test.dart:23；test/state_races_test.dart:14；test/windows_bridge_test.dart；android/app/build.gradle.kts:1–61（完整文件）|
|Android 纯 Kotlin 验证脚本直接编译并运行检查，不能直接覆盖 AudioEffect/SharedPreferences|android/protocol-check/verify.ps1:25–48|
|minSdk=26；不新增依赖、不持久化 PCM、不记录密钥或个人设备信息|android/app/build.gradle.kts:39；AGENTS.md:84–89|
|自动采集连续空闲 3 秒后 STOP；手动采集不受此规则控制|desktop/src/MicBridge.Core/Control/AutoCapturePolicy.cs:6–8,43–50,59–63；AGENTS.md:67–68|

官方 API 证据集中在 [audiofx 调研](research/audiofx.md)：系统效果可能默认启用；isAvailable 不保证 create 成功或当前路由有效，getEnabled 需要实际会话。

## In Scope / Requirements

|编号|确定需求|
|---|---|
|R1|增益默认 0 dB，范围 0–20 dB、步长 1 dB，以整数保存。0 dB 保持样本不变；放大采用 PCM16 饱和限幅，显示高增益可能削波破音的提醒。|
|R2|NS、AEC、AGC 各自保存可空布尔偏好；空表示跟随系统，不调用 setEnabled；显式开/关按用户偏好尝试应用。UI 每项始终显示同一个「系统默认 / 开 / 关」三段选择（用户真机反馈后由“系统默认标注 + 恢复系统默认”改来，控件不再中途变样），isAvailable=false 时整段禁用并显示中文原因；会话创建/启用失败及 getEnabled 实际状态仅写日志，不承诺偏好已生效。|
|R3|全局一套设置，保存在独立 SharedPreferences 文件；跨进程恢复，停止、设备缺失或断连不擦除偏好。保存成功才显示已保存，失败恢复已确认值。切换输入“跟随系统”不影响音频设置。|
|R4|增益与效果全部下次采集生效。启动时读取一次不可变设置快照，readLoop 不跨线程读取可变设置；保存不触发 STOP/START。统一提示“下次开始采集时生效”，不临时创建 AudioRecord 探测状态。|
|R5|复用 micbridge/platform 的读取、写入两个方法；不新增设置 EventChannel 或实际状态上报。单项效果失败不阻断基础采集或掩盖既有错误；保持连接、认证、音频协议及 Windows 处理不变。|
|R6|停止、启动失败、读失败、路由丢失、断连、服务退出均释放本任务音效；单项释放异常不阻断其余清理。再次采集重建会话，旧 readLoop 不清理新会话。|
|R7|Android 简体中文设置入口使用 Flutter Material 自带控件，不新增依赖；支持明暗主题、大字号与屏幕阅读器，忙时防重入，异步失败反馈与页面销毁安全。Windows 无设置入口。|

## Acceptance Criteria

以下验收区分自动化检查与真机矩阵；本轮执行产品检查，不操作手机，不将模拟结果当作硬件效果证据。

|编号/需求映射|可观测验收|
|---|---|
|A1 / R1|合成 PCM16：0 dB 逐字节不变，20 dB 的 ±1000 得到 ±10000；正负满幅夹至 32767/-32768，零不变，无整数回绕；有效 count 外不变，0 dB 无逐样本循环或新增 PCM 分配。|
|A2 / R1,R7|滑块 21 档、显示整数 dB、辅助功能可调；错误类型/越界写入被拒绝，界面有破音提醒；不以“无失真”替代限幅验证。|
|A3 / R2,R4,R5|isAvailable=false 显示禁用原因；create=null、无控制权、setEnabled 失败或异常只记日志且其他效果/采集继续。UI 不显示虚构的实际开关状态，读取设置不启动录音。|
|A4 / R2,R3,R4|新安装为 0 dB 和三个系统默认；空偏好每次会话均无 setEnabled，系统读回值不持久化。显式偏好跨重启恢复，选回“系统默认”移除覆盖；保存失败不显示成功，切换输入不擦设置。|
|A5 / R4,R7|采集中保存新值后本次快照不变且无自动重启；下次 START 使用新值。自动模式空闲 3 秒 STOP 后下次听写验证新设置；不足停止间隔或手动持续采集仍沿用旧快照。界面显示规定提示。|
|A6 / R5,R6|内置麦、Ray-Ban 各验证默认、20 dB、可用效果显式开/关：相同环境对比基线首词无新增漏字，连续听写至少 5 分钟无新增中断；手动/自动停止释放麦克风，重复启停 10 次无资源耗尽，不保存 PCM。|
|A7 / R3,R5,R6|两种输入覆盖网络重连、后台/锁屏恢复；Ray-Ban 另测蓝牙断连重连。认证和原错误提示正常，新会话重建效果并应用偏好，缺失设备不擦偏好，无旧状态覆盖。|
|A8 / R5,R6|启动/读失败、路由丢失、单个 release 异常和重复 STOP 均无遗留句柄；旧 readLoop 不清理新会话；日志中的 SDK 状态不作为硬件处理效果保证。|
|A9 / R7|Flutter 覆盖 Android 显示/Windows 隐藏、加载/保存失败、不可用原因、三段选择写入 null/true/false、防重入、晚到响应与页面销毁；412×915 和 1.3 倍字号不溢出、明暗可读，现有输入选择/按钮/Windows 桥接测试不回归。|

## Out of Scope

实时或混合生效、按设备保存、独立设置事件通道、实际状态 UI；RNNoise 等软件降噪、AudioSource 切换、Windows 音频处理和设置 UI；协议、采样格式、端口及认证改动；新增依赖、录音存档、云处理；控制设备内置 DSP、保证关闭后完全原始信号、保证 AEC 消除 Windows 扬声器回声或所有 OEM/SCO 路由有可听辨效果。

## Key Decisions 与 Risks

R1–R7 是唯一需求决策表；桥接形状、存储键、系统默认控件呈现和资源所有权见 [design.md](design.md)，有序实施及验证见 [implement.md](implement.md)。自动模式在既有 STOP 后的下一次听写应用设置，不改变桌面策略。

风险：SDK 支持与实际听感可能不一致；系统初始值因 OEM/路由而异；AGC 与高增益叠加可能削波；全局设置切换输入时可能需要手调。真机矩阵是后续验收工作，不是未决产品问题。

PRD Convergence Pass 已完成：事实集中、R1–R7 与 A1–A9 映射保留、已解决问题移入确定需求；无未决产品问题或规划阻塞项。validate 仅验证任务上下文，不代表产品测试通过或实施获批。
