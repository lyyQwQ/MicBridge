# Android 麦克风设置实施计划

## 当前状态

最终规划已获用户批准，第 1–8 步已完成并通过下述自动化检查。task.py start 在缺少 session identity 时按 degraded 模式成功将 planning 改为 in_progress，未持久化 active-task pointer；未手改 task.json 绕过。第 9 步留给用户，task 保持 in_progress。禁止 git 写操作、删除文件、手机操作及 GUI；本轮均未执行。R1–R7 / A1–A9 以 prd.md 为准，确定接口以 design.md 为准。

## 有序清单

1. [x] **复核实施上下文与范围**：已读取 AGENTS.md、相关 spec、prd/design、jsonl 与研究；保持音源、采样格式、认证、Windows 与协议实现，无新增依赖。
2. [x] **实现全局设置 store**（R1–R4）：独立 micbridge_audio_settings，完整参数校验；串行后台 commit，成功才发布不可变快照，失败尽力恢复，恢复失败仍保留已确认快照。
3. [x] **实现 PCM16 增益纯逻辑**（R1）：预计算系数、0 dB 快路、little-endian、有效 count、先饱和后转型及奇数 count 错误路径；在发送前原地处理，保留一次发送拷贝。
4. [x] **接入采集快照与效果生命周期**（R2、R4–R6）：START 只取一次快照；绑定 audioSessionId，null 不调用 setEnabled；各操作异常隔离；cleanupLocked 统一释放，旧 readLoop 身份检查保留。
5. [x] **扩展现有 MethodChannel 两个方法**（R3–R5）：getAudioSettings/setAudioSettings 复用 micbridge/platform；Dart 可选能力统一解码，MicPlatform/Windows 及 EventChannel 契约不变。
6. [x] **实现 Android 设置面板**（R1、R2、R4、R7）：21 档 Slider、每个效果固定的「系统默认 / 开 / 关」三段选择（用户真机反馈后替换原系统默认标注 + Switch/恢复系统默认）、不可用禁用、固定生效提示和破音提醒；串行提交、失败回退及页面销毁保护，Windows 隐藏。
7. [x] **补充算法、桥接与生命周期验证**（A1–A5、A8–A9）：纯 Kotlin 检查增益、参数、commit/恢复失败及效果操作/释放异常；16 项 Flutter 测试覆盖桥接、面板、失败、防重入、晚到回应、销毁、小屏明暗主题及滑块语义；旧 recorder 隔离另以代码审查确认，未声称真机通过。
8. [x] **统一运行检查并审查边界**：增益自检、flutter analyze、flutter test（91 项）及 APK debug 构建均通过；无 Windows、协议实现或依赖清单变更，无逐块日志或 PCM/凭据持久化。
9. [ ] **执行授权后的真机验收并记录限制**（A3–A8）：在后续具备设备操作授权时执行下表；本轮不操作手机。未执行或设备不具备的项如实记录，不能标为通过。后续 git 写操作仍遵守授权范围。

## 验证命令

以下命令均已在仓库根目录 PowerShell 实际执行并通过；真机操作不在本轮范围。

```powershell
flutter analyze
flutter test
flutter build apk --debug
./android/protocol-check/verify.ps1 -JavaExecutable "<jdk>/bin/java.exe"
python ./.trellis/scripts/task.py validate 11-04-mic-audio-settings
```

本轮使用本机 JDK 25 的 bin/java.exe。verify.ps1 直接编译实际 MicAudioSettings/PcmGain/PreprocessEffects 源码并运行 VerifyAudioSettingsKt，不新增库或依赖；原有四个协议/发现检查入口全部保留并通过。

算法自检必须覆盖：0 dB 全向量逐字节相同与快路；20 dB ±1000→±10000；正负饱和、零、小幅样本舍入、字节序、有效 count 外保持、奇数 count 拒绝、全部整数档位合法。Flutter 测试验证通道形状与 UI；APK 构建验证 Kotlin/Flutter 集成，均不能证明硬件效果。范围不涉及 desktop/windows，不额外运行 dotnet test 或 Windows release 构建。

## 验证矩阵与真机验收

|层级|检查项|验收映射|
|---|---|---|
|纯 Kotlin|增益向量、边界、0 dB 快路与 count|A1,A2 / R1|
|store/效果包装器|保存失败不发布、快照一致；null 无 setEnabled；create=null、失控、设置异常、部分初始化、release 异常隔离、旧 recorder 身份|A3–A5,A8 / R2–R6|
|Flutter|Android 显示/Windows 隐藏、三段选择写入 null/true/false、不可用原因、加载/保存失败、类型错误、串行命令、晚到响应与销毁；412×915、1.3 倍字号、明暗、语义标签|A2–A5,A9 / R1–R4,R7|
|内置麦|默认/20 dB/可用效果显式开、关与选回系统默认；与同环境基线比首词，连续听写至少5分钟，10次启停，手动/自动释放；网络重连、后台/锁屏|A3–A8 / R2–R6|
|Ray-Ban/SCO|内置麦同等矩阵，加蓝牙断开/重连；分开记录 SDK 状态和听感|A3–A8 / R2–R6|
|生效时机|采集中修改不改变当前快照、不触发重启；自动模式空闲3秒STOP后的下一次听写采用新值；短暂停顿/手动持续采集仍用旧值|A5 / R4,R7|
|持久化隔离|进程重启恢复偏好、选回“系统默认”移除对应覆盖；输入切回“跟随系统”、设备暂缺或停止均保留设置|A4,A7 / R2–R4|

日志只记录匿名设备类别、系统版本、效果类型、SDK 返回码与必要状态；不保存 PCM、序列号、内网 IP 或凭据。遵循 `.trellis/spec/backend/logging-guidelines.md`（当前模板）及 AGENTS.md:84–89。create/getEnabled 成功不等于 SCO 可听辨，AGC 可能与增益叠加削波，AEC 无 Windows 回放参考；均须保留在验收记录。无设备操作授权时仅标记真机未验证，不影响已完成的产品决策收敛。

### 真机验收记录：2026-11-04（前台应用阻塞）

- 本次获准通过 adb 连接、仅以 `install -r` 覆盖安装 debug APK、启动本项目 App、读取本项目 PID 日志及音频 dumpsys；仍禁止卸载、删除文件、git 写操作、产品代码修改和启动 Windows GUI。以下为本次实际结果，前文“未授权手机操作／未安装”为上一轮实施记录。
- adb：发现一台已连接且可用的设备 `<adb-serial>`，型号及设备代号统一匿名为 `<phone-model>`；无需额外连接或配对，当时未读取 Android 系统版本。
- 安装准备：已确认 applicationId 为 `io.github.lyyqwq.micbridge`，且存在 debug APK；当时尚未安装或核验设备上的版本。
- 前台门禁：`dumpsys window` 的 mCurrentFocus 与 mFocusedApp 均指向系统设置 `com.android.settings/.SubSettings`。按用户指令及 AGENTS.md:94，立即停止后续设备操作；未启动 App、点击、读取屏幕内容、截图或读取手机 logcat/audio_flinger。
- 设置面板：未打开，增益滑块、三个效果项及“下次开始采集时生效”均未真机观察；降噪（NS）、回声消除（AEC）、自动增益（AGC）的可用性均未知。未生成截图。
- Windows：只读进程检查确认 `micbridge` 与 `MicBridge.App` 均已运行，未启动 GUI。尚未确认手机 App 已连接，未读取 core.log；本轮没有触发采集，也没有证据确认是否已有采集在运行。START/STOP、效果 create/setEnabled/getEnabled 及 audio_flinger 录音会话预处理效果均未验证。
- 阻塞解除：需要用户将手机前台切到本项目 App 并暂停使用其他 App，再继续覆盖安装与设置面板验收；若后续确认没有采集运行，需要用户在 Typeless 等软件中开始一次听写，不由验收操作擅自启动采集。
- 第 9 步保持未完成。听感、首词、Ray-Ban/SCO、至少 5 分钟连续听写、10 次启停、释放、网络及蓝牙重连、后台/锁屏、偏好持久化及下次 START 生效均未验证；SDK 效果状态与实际听感须分别记录。

#### 后续限定授权：仅安装与版本核验

- 按后续明确授权，在系统设置仍为前台的情况下仅覆盖安装并读取包信息、Android 版本；未启动 App、点击、截图或触发采集。本次结果更新上文“尚未安装／未读取版本”的状态。
- 对 `<adb-serial>` 覆盖安装 debug APK 成功，退出码 0；无权限或签名错误，无重试、无卸载。
- 安装前后包信息核验：versionName 均为 `0.1.0`，包更新时间已变化，确认覆盖安装完成。
- `getprop ro.build.version.release` 返回 `16`，`getprop ro.build.version.sdk` 返回 `36`，即 Android 16 / SDK 36。
- 安装已无阻塞；App 启动、设置面板和采集效果仍未验证。本次限定授权到安装与版本核验为止，后续界面验收仍须遵守前台门禁，采集需由用户发起听写。

## 风险文件与回滚点

K/ = android/app/src/main/kotlin/io/github/lyyqwq/micbridge/。

|挂点|审查重点|后续回滚点|
|---|---|---|
|K/audio/AudioCaptureController.kt:38,77,110|符号/字节序、每块开销、快照固定、stop/read 竞争、部分效果泄漏|先0 dB与系统默认并重启采集；生命周期回归则撤回新增处理和句柄所有权|
|K/audio/MicrophoneService.kt:42|store 注入与后台 START 一致，查询不启动采集|恢复原 capture 构造|
|拟新增 store/effects/gain 文件|单一权威、commit 失败内存行为、无读线程 I/O|停止读取新配置，保留非敏感键，无须删除|
|K/MainActivity.kt:54,96,181|主线程 result、Activity 重建、仅两个方法|停用新方法调用，保留原通道|
|lib/platform_bridge.dart:119,462|可选接口、null 与 bool 区分、Windows 隔离|撤回新能力实现，保留 MicSnapshot|
|lib/main.dart:365,541；lib/dashboard_widgets.dart:529|busy、晚到响应、布局及设备选择返回 int|隐藏独立设置入口|
|test/ 与 android/protocol-check/verify.ps1:25|无新增依赖、保留旧断言/源列表|撤回新增挂接而不移除既有覆盖|

## 实施记录

- task.py start：退出成功，degraded 模式，状态改为 in_progress；缺少 session identity 导致 active-task pointer 未持久化，未手改 task.json。
- verify.ps1：5 个检查入口全部通过；新增音频入口含增益/count、通道参数、确认快照/失败恢复、效果失败/释放 4 组。
- flutter analyze：No issues found；flutter test：91 项全部通过，其中本任务新增 16 项；flutter build apk --debug：通过，产物 build/app/outputs/flutter-apk/app-debug.apk，未安装。
- task.py validate：implement.jsonl 4 项、check.jsonl 3 项全部通过；git diff --check 通过，仅有 LF/CRLF 提示。
- 存储失败证据：自检使用生产 ConfirmedAudioSettings.save，模拟失败提交已改变内存以及恢复再次失败，均不发布新快照；实际 SharedPreferences 磁盘故障尚未在 Android 上注入。
- 会话隔离证据：readLoop 每轮和发送前检查 recorder 身份，catch 在同一个 lock 内仅于 recorder === record 时调用 cleanupLocked；STOP 先清空 recorder/effects，后续 START 新建 recorder，旧 reader 无权清理新会话。未用纯逻辑测试冒充 AudioRecord 真机竞态验收。
- 文案按用户裁定统一为“此效果在该设备上不可用”，已同步 design.md；其他契约无偏离。设置 sheet 单独持有呈现状态，通过 load/save 回调调用唯一 adapter，dashboard 的 _run 保护打开过程和后台命令。
- 未执行第 9 步；内置麦/Ray-Ban 听感、首词、5 分钟连续听写、10 次启停、断连/后台/锁屏、持久化及下次 START 生效矩阵均留给用户。SDK 结果不等于实际听感，AGC 叠加增益仍可能削波，AEC 无 Windows 回放参考。
- 用户真机反馈后的界面调整（2026-11-04）：原“系统默认标注 + 设为开/设为关、显式后改为 Switch + 恢复系统默认”在首次点击后控件变样，按用户选定方案改为每个效果固定的 Material `SegmentedButton`「系统默认 / 开 / 关」（null / true / false，单选、不允许空选），旧控件与文案全部移除；全页说明改为“选项表示保存的偏好……”。不可用或保存中整段禁用，失败回到已确认选中项；桥接、Kotlin 存储与采集未改。测试改为验证初始选中系统默认、三段写入 null/true/false、不可用禁用、失败回退，以及 412×915、1.3 倍字号下三段单行不溢出。flutter analyze 无问题、flutter test 91 项通过、flutter build apk --debug 通过，`adb install -r` 返回 Success；未启动 App、未观察真机界面。
