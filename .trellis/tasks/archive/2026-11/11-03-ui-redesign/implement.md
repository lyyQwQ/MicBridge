# 实施计划

## 启动门槛

1. 用户审批本任务规划后才允许 task start；当前保持 planning，未设为 current。
2. 确认 Android 发现列表闪烁修复先合入：记录修复提交/补丁依据、检查连续发现快照与重复刷新不清空稳定列表，TTL 移除仍正常。当前仓库未找到独立修复任务，不能假定已完成。
3. 读取本任务 PRD、design、归档审计和两个上下文清单。核对 git diff，保护 `main.dart`、AGENTS、README 和自动采集策略等既有未提交修改；重新定位行号与测试数量。

## 实施顺序

1. **确定展示映射与测试入口**：按 design 的状态表定义纯展示映射及稳定 Key；使能规则使用现有 canStart/canStop；决定错误翻译优先级，不改状态模型。
2. **主题与组件拆分**：main.dart 保留入口、主题和状态；dashboard_widgets.dart 承接状态卡、设备摘要、发现条目、麦克风面板、折叠表单与响应式布局。统一间距、左对齐和采集文案，低频内容折叠。
3. **接回已有功能**：Android 发现条目复用 `_selectComputer(endpoint)` + `_connect()`，保留 expectedDeviceId，busy 时禁用；两端 UI 校验地址/端口。保留用户编辑、已记住手机连接、两端忘记、手动 TCP、跟随系统与输入恢复；Android ⋮ 始终保留权限检查，Windows 首屏状态区保留 listenerActive 实时检测。保持配对生命周期语义和原有顺序，不重构 `_run`、轮询或刷新。
4. **Windows 外壳**：main.cpp 改中文标题与约 520×720；flutter_window.cpp/.h 新增窗口专用 MethodChannel。去重/过期检查后非阻塞提醒；每请求 3 次，GetForegroundWindow()==GetHandle() 时跳过；用户切回、路由结束、dispose 时停止。OnDestroy 先解绑并释放通道再销毁 controller；检查迟到调用。宽窗断点 900 采用 LayoutBuilder 可用逻辑宽度。
5. **同步规范与测试**：更新 state-management.md 中空闲状态文案、发现条目直接连接与 runner 提醒边界；执行下列测试改动，保留原功能断言。
6. **集中验证**：完成代码后统一运行格式检查、静态分析、测试和双端构建；失败时仅针对失败与修复扩展验证。完成同构建截图对比、实机配对提醒验证，逐条回填 R1–R10 证据；不以单元测试代替实机验收。

## 测试改动清单

- `test/widget_test.dart`：更新旧「通话」及卡片层级查找；对折叠内容先展开，对麦克风面板先打开，不直接删掉原断言。增加两端点击发现项恰好一次连接、合法/非法输入、已记住手机连接入口、两端忘记和错误唯一显示覆盖。
- 新增 `test/dashboard_widgets_test.dart`（或按现有测试结构合并）：状态→主按钮表驱动测试；412×915 首屏主按钮完整可见且可命中；520×720、899/900/901 内容宽度布局；浅深主题与 1.3 倍字号；liveRegion、单一主按钮、未知错误与常见错误的可操作提示。不能只依赖大画布测试。
- 新增 `test/window_attention_test.dart`：mock MethodChannel 验证 Windows 方法与参数、不支持平台无调用、MissingPluginException/PlatformException 不传播。widget 集成覆盖同一 requestId 事件与快照不重复、新请求触发、已过期不触发、批准/拒绝/关闭/撤回/超时停止、提醒失败仍能完成决定、dispose 及迟到 stop。
- 保留现有配对路由过期、关闭即拒绝、轮询恢复、请求去重、忘记、设备刷新竞态、跟随系统与设备回退恢复测试；保持 platform_bridge 与宿主测试原契约。
- `build/diag/ui_render_test.dart`：更新展示文案/选择器与新尺寸，复用现有模拟平台，渲染首次、发现、待审批、待命、采集、错误两套主题；新图使用 ui-redesign 前缀，保留旧审计图。脚本是诊断辅助，不替代正式 widget 测试。
- 原生提醒用真实 Windows 验证：窗口最小化、其他应用前台时收到请求；仅任务栏闪烁；活动 HWND 前后相同；关闭/超时后停止。mock 通道不能证明 FlashWindowEx 的实际效果。
- 新增权限检查调用测试（所有状态可找到菜单项、非 busy 点击调用一次、busy 禁用）、listenerActive false→true→false 实时切换测试，并断言 Windows 检测结果仍在首屏。
- 新增错误时序测试：连接失败→成功连接快照隐藏旧错误且之后断连不重现；连接失败→新 error 快照只显示新错误；成功后本地失败可见、不被重复轮询快照清除；未连接快照不清除本地错误；使用实际字符串和 StateError 包装测试具体标识与统一兜底。规则见 design「错误展示」（已获批准修改清除时机）。
- mountDashboard 的 1400×1800 改为可传入的逻辑尺寸参数（可保留默认值以迁移旧测试），设置 DPR=1；412×915 显式传入，断言按钮完整矩形在可用区域内，实际 tap 验证回调一次。持续动画使用固定帧数/时长上限的 pump；helper、清理和相关旧测试中的 pumpAndSettle 一并检查，避免进度动画造成等待超时。
- Windows 实机增加前台请求不闪烁、后台请求闪烁 3 次、用户切回立即停止与销毁期间不再调用通道的检查；mock 不能证明 GetForegroundWindow 与原生 teardown 有效。

### 现有测试行为对应表

审计称 20 个测试；当前读取的 widget_test.dart 为 1 个 test + 17 个 testDashboard 声明，其中双端循环多实例化 1 个，共 19 个测试实例。下表列出 20 项保留行为（把 :350 修复态忘记单列，原本属于双端忘记测试），不虚构第 20 个测试。实施前重新核对最终基线，若已有并行新增测试也须保留。

| 项 | 现有行号 | 重设计后保留的行为与调整 |
| --- | --- | --- |
| 1 | :59 | 快照解码信任、待审批身份、过期时间和输入/地址字段，不变 |
| 2 | :97、:113 | 桌面按认证状态开始/停止；实时 listenerActive 文本保留并扩展双向切换 |
| 3 | :125 | Android 仅已连接且有效输入时可重试；通过麦克风面板选择 |
| 4 | :149 | 跟随系统、临时回退及恢复原输入，展开面板后断言 |
| 5 | :172 | 枚举期间设备返回会再次刷新，不被旧输入列表覆盖 |
| 6 | :194 | 原生输入恢复、上次地址预填不覆盖编辑 |
| 7 | :221 | 已编辑地址不被迟到快照覆盖 |
| 8 | :232 | status 恢复请求，事件/快照重复仅弹一次 |
| 9 | :256 | 首请求拒绝后新请求仍可允许 |
| 10 | :271 | 关闭弹窗发送拒绝 |
| 11 | :282 | 过期/撤回不可批准，用有界 pump 控制时间 |
| 12 | :303 | 等待 connect 时仍可审批，不套 `_run` 的 busy 限制 |
| 13 | :317 | 决定失败可见且同一请求不重弹；原始 approval failed 断言改为唯一兜底提示 |
| 14 | :333 Windows 实例 | Windows 从新菜单忘记已记住设备，调用与原生状态断言保留 |
| 15 | :333 Android 实例 | Android 从新菜单忘记电脑，调用与原生状态断言保留 |
| 16 | :350 双端子场景 | repair_required 仍可忘记；不得因无 trustedDevice 隐藏入口 |
| 17 | :365 | 手动 host/port 连接与信任修复说明，先展开手动输入 |
| 18 | :384 | 两秒轮询恢复遗漏配对请求，不改轮询语义 |
| 19 | :400 | 旧 status 响应不能撤回较新请求事件 |
| 20 | :418 | 点击发现条目即连接并携带 pc-1；删去首次额外连接按钮点击；手动改地址再连接 expectedDeviceId 为 null；新增 busy 时点击不连接 |


## 验证命令

在仓库根目录 PowerShell 执行。规划阶段只校验任务材料，以下是实施后的命令，不代表已经运行成功。

```powershell
# 对实际改动的 Dart 文件运行；新文件名如有调整须同步
 dart format --output=none --set-exit-if-changed lib/main.dart lib/dashboard_widgets.dart lib/window_attention.dart test/widget_test.dart test/dashboard_widgets_test.dart test/window_attention_test.dart
 flutter analyze
 flutter test
 flutter test build/diag/ui_render_test.dart
 flutter build apk --debug
 flutter build windows --release
 python .trellis/scripts/task.py validate 11-03-ui-redesign
```

Windows release 构建前先正常退出旧桌面进程，避免覆盖运行中 exe；启动新 GUI 需遵守 AGENTS.md 的既有授权要求。确认启动的是本次 release 路径，且打包 core 为本次构建，不使用旧进程截图。Android 使用本次 APK；安装/启动按既有设备操作授权执行，用户使用其他 App 时不操作手机。

本任务不改 .NET/Kotlin/协议，默认不重复重型核心与协议检查；如实际 diff 越界，先重新审查范围，再补对应检查：

```powershell
 dotnet test desktop/tests/RaybanVoiceMic.Core.Tests/RaybanVoiceMic.Core.Tests.csproj -c Release -p:UseSharedCompilation=false
 ./android/protocol-check/verify.ps1 -JavaExecutable <jdk>
```

.NET 全量检查超时至少 1200 秒。协议检查 `<jdk>` 为 JDK 17+ 的 java.exe 路径，先经 Android 构建准备缓存。

## 截图与人工验收

- Windows 对目标应用后台窗口截图，不激活、不置顶；记录 exe 来源、构建标识/时间、系统主题、窗口尺寸与 DPI。对比审计图和新渲染，重点核对首屏主操作、忘记图标、左对齐与宽窗两列。
- Android 截图前检查当前焦点窗口确为本应用；mFocusedApp 是本应用但 mCurrentFocus 为通知栏时不得截图。条件不满足时保留为待验收，不切换用户正在使用的应用，不保存其他应用内容。
- 两端分别核对发现→配对→待命→手动采集→停止→断连/重连、忘记；Windows 额外核对手动 TCP 和主动连接已记住手机，Android 额外核对跟随系统/选麦克风。
- 自动采集说明与实际行为核对 3 秒空闲释放；手动采集不能被自动模式文案误导。首字、连续听写、停止释放及后台/锁屏重连按现有实机流程回归，不声称未执行的结果。
- 采样文字/背景对比度，核对图标与替代字体差异。对每项标记通过、失败或未验证及原因。

## 风险与检查关口

重点审查 main.dart 的并行修改、配对附加副作用的竞态、折叠后功能不可发现、DPI/大字体首屏高度、错误翻译失真、旧构建误截图。风险处理和回退边界见 design.md。依赖未合入、用户未审批或任一硬约束出现实质变更时，不进入实施；实机条件缺失不能标记任务验收完成。
