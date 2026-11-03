# 技术设计

## 边界与文件切分

- `lib/main.dart` 保留入口、ColorScheme.fromSeed 浅/深主题、ThemeMode.system、现有 State 与平台订阅；保留 `_run`、配对路由、计时器、轮询和刷新逻辑。页面只组合展示组件并传入快照/派生值/已有回调。
- 新建 `lib/dashboard_widgets.dart`：状态区、主按钮、发现列表、设备摘要、音频选择入口、折叠连接/诊断、响应式布局。组件不持有 MicPlatform，不订阅流，不写入信任/采集状态；用纯展示映射生成状态文案、按钮与错误提示。
- 新建 `lib/window_attention.dart`：仅封装 Windows runner 窗口提醒，Android 为无操作；不加入 MicPlatform 接口，不修改 `lib/platform_bridge.dart`、JSON IPC 或 Kotlin 通道。
- `windows/runner/flutter_window.cpp/.h` 注册并持有 MethodChannel；`main.cpp` 改初始尺寸与标题。原则上无需新增 C++ 文件、插件或 CMake 依赖。
- `.trellis/spec/frontend/state-management.md` 同步空闲文案、发现点击直接连接、提醒与状态机的边界；其他未完成规范模板不在本次重写。

## 现有通道核查与 FlashWindowEx 方案

已核查：`windows/runner/flutter_window.cpp:12–39` 仅创建控制器、注册插件和设置子窗口，尚无手写通道。`lib/platform_bridge.dart:172–197` 的 Windows 适配器启动无窗口 .NET 并读取 stdout；`:284–287` 的 MethodChannel/EventChannel 属于 Android。现有 Windows IPC 不持有 Flutter 窗口 HWND，无法复用为 runner 通道。

因此新增 `rayban_voice/window` MethodChannel，使用 Flutter SDK 自带 StandardMethodCodec，不新增依赖。推荐方法 `flashPairing` / `stopFlashing`，无音频、凭据或配对决定参数。在 `FlutterWindow::OnCreate` 的控制器创建成功之后注册，以 `GetHandle()` 的顶层窗口而非 Flutter 子窗口作为 FLASHWINFO.hwnd。通道成员生命周期先于 controller 销毁结束。

原生侧 `flashPairing` 先检查 `GetForegroundWindow() == GetHandle()`，已在前台则跳过；否则调用 FlashWindowEx，使用 `FLASHW_TRAY`、每个请求 3 次、系统默认间隔，不含 FLASHW_CAPTION/FLASHW_ALL。`stopFlashing` 使用 FLASHW_STOP；用户切回窗口时在原生激活消息处理内停止闪烁。`OnDestroy` 显式先解绑 MethodChannel handler、停止闪烁并释放通道，再销毁 flutter_controller_，防止回调访问失效对象。禁止 SetForegroundWindow、SetActiveWindow、置顶 SetWindowPos 或为提醒调用 ShowWindow。FlashWindowEx 返回值表示此前活动状态，不能把返回 false 直接解释为操作失败。未知方法返回 NotImplemented。每请求 3 次与前台跳过作为本轮审批项。

Dart 接入点为 `lib/main.dart:157–187`：现有 `_showPairingDialog` 已按请求 ID 去重并检查 expiresAt（`:159–164`），在通过这些检查后加非阻塞提醒调用；不要在 `_schedulePairingDialog` 每次调度时触发，否则轮询可能重复提醒。路由结束时调用停止，dispose 时兜底；提醒异常在独立封装内消化，不进入 `_showError`、不打断配对决定。现有 `removeRoute`、超时 Timer、push 返回值、关闭即 false、`decidePairing` 顺序保持原样（`:139–144`、`:184–201`）。提醒只是附加副作用，不能重写弹窗生命周期。用串行提醒命令或代次保护，防止迟到的 stop 清掉下一请求的提醒。

## 展示布局

窄窗口顺序：应用标题/刷新 → 状态区与单一主操作 → 设备摘要 → 麦克风/输出摘要 → 折叠手动连接与诊断。发现列表可在未连接状态区内显示，但不挤走状态标题与主操作；条目较多时约束列表高度。Windows 内容宽度达到 900 后右列放连接、音频与诊断，左列保留状态主操作。使用 LayoutBuilder 的可用内容宽度，不按物理屏幕像素判断。

配色：种子色青绿 `#14B8A6`，`ColorScheme.fromSeed` 生成浅/深两套，`ThemeMode.system`。fromSeed 生成的 tertiary 色相约 208°（偏蓝），因此用琥珀种子 `#F59E0B` 的 primary 系覆盖 tertiary/onTertiary/tertiaryContainer/onTertiaryContainer。状态区整卡：未连接 surfaceContainer、等待批准 tertiaryContainer、已连接待命/启动/停止 primaryContainer、采集中 primary 实心（主按钮反色 onPrimary 底）并播放 8 个半周期后停在实心的脉冲点（系统关闭动画时不播放）、出错 errorContainer。所有颜色取自 ColorScheme，文字/按钮对比度由 `test/dashboard_widgets_test.dart` 断言 ≥4.5:1。

Android 麦克风单选列表用系统 Material 底部面板展开，默认跟随系统；保留当前选中、失联回退及恢复说明。忘记入口保留两端明确标签；Windows 已记住手机的连接入口保持可见。Android 无发现结果且未连接时手动输入展开，用户主动编辑后不由快照反复重置展开状态或地址。

900 断点明确指 LayoutBuilder 的可用逻辑宽度。Android 的 ⋮ 菜单在所有状态始终保留「权限检查」，调用现有 `_platform.requestPermissions()`，busy 时可禁用但不移除（现有入口 main.dart:548–553）。Windows 首屏状态区直接呈现 `_snapshot.listenerActive` 的实时结果「CABLE Output 录音检测：检测到录音中／未检测到录音」（main.dart:407），随快照变化，不能被自动采集规则的静态说明替代或收入折叠区。

Android 发现行回调明确复用 `_selectComputer(endpoint)` 后 `_connect()`，busy 时禁用行。沿用 `_connect` 的地址匹配与 expectedDeviceId 判定（main.dart:284–298），手动改地址后不携带旧设备 ID。保持配对生命周期语义和原有顺序；允许为提醒加入旁路副作用，不承诺源码逐行不变。

## 状态到主操作映射

优先展示待审批和 busy 反馈；按钮使能仍使用当前 canStart/canStop 判断（`lib/main.dart:355–363`），不新增状态转换。

| 场景 | 主按钮/反馈 | 行为 |
| --- | --- | --- |
| 未连接 | 连接设备 | 有合法现有目标时连接，否则展开手动输入；发现条目本身可直接连接 |
| 待审批 | 等待配对批准（禁用） | 显示进度，不重复连接 |
| 已连接 idle | 开始采集 | 使用现有开始回调 |
| starting | 停止采集，状态显示正在启动 | 沿用 canStop，不丢失取消能力 |
| streaming | 停止采集 | 使用现有停止回调 |
| stopping | 正在停止采集（禁用） | 等待平台结果 |
| error 且已连接、可开始 | 重试采集 | 沿用 canStart 的恢复路径 |
| error 且断连 | 重新连接/检查连接 | 不启用开始采集，使用现有连接入口 |
| busy | 保留上下文动作文案并禁用 | 不绕过 `_run` |

## 错误展示

现有 `_showError` 与 `_run` 已把异常转为字符串（main.dart `_showError`、`_run`），Windows IPC 失败包装成 `StateError(message)`（platform_bridge.dart `_command`）。经用户批准修改错误状态逻辑：仍以字符串保存 `_connectionError`（字符串已含 PlatformException code 与 StateError 消息，足以匹配，不另存类型），只改清除时机，不改平台协议。

时序规则：`_acceptSnapshot` 比较新旧快照的 state、connected、detail；发生变化且新快照为已连接或 error 时，它比此前的本地错误新，清除 `_connectionError`。相同快照（Windows 两秒轮询、事件与 status 重复）不算新结果，不清除；未连接快照不说明失败已恢复，不清除；`_run` 开始新命令时照旧清除。展示优先级：① `_connectionError` 非空时翻译并显示它；② 否则 snapshot.state == error 时翻译 snapshot.detail（空 detail 用兜底）；③ 无错误。结果：连接失败后收到成功快照隐藏旧错误，之后断连不会重现；收到新 error 快照只显示新错误；成功后本地命令失败可见，不被重复的成功快照遮蔽，直到出现更新的成功/错误快照。未连接状态下非 error 的 detail 作为说明：已知错误片段转为提示，中文进度/指引原样显示，其余不显示。

字符串映射使用有证据的完整标识/固定片段，不把 `Bad state:` 包装前缀当原因，不用自由英文关键字猜测权限或认证错误。优先识别具体片段再匹配 connect_failed 包装；不将 trustState 的 repair_required 当错误字符串。非错误路由/输入回退说明独立作为说明，错误仅一处显示。

| 可稳定识别的现有字符串 | 依据 | 展示提示 |
| --- | --- | --- |
| `请输入有效的电脑地址和端口` | main.dart:288 | 请输入有效的设备地址和 1–65535 范围内的端口。 |
| `input_device_not_found` | BluetoothRouteManager.kt:30、117 | 麦克风不可用，请检查设备连接或选择「跟随系统」。 |
| `connect_failed` | MainActivity.kt:82 的 PlatformException code | 连接失败，请检查两端是否在同一网络、对端应用是否运行及地址是否正确。 |
| `无法连接，请检查电脑地址、服务和防火墙`、`无法解析电脑地址`、`电脑地址或端口无效` | audio/ControlClient.kt:289–295 | 无法连接设备，请检查地址、对端服务和防火墙。 |
| `超时`（原生固定原因）、`Android did not confirm audio capture` | ControlClient.kt:288、CoreController.cs:550 | 等待设备响应超时，请检查对端应用和网络后重试。 |
| `电脑已断开连接` | ControlClient.kt:291 | 连接已断开，请检查电脑应用和网络后重新连接。 |
| `设备信任存储不可用`、`自动重连已暂停，请在双方忘记设备后重新配对` | ControlClient.kt:293、300 | 配对记录不可用，请检查两端配对状态，必要时在双方忘记设备后重新配对。 |

未知错误统一显示「操作未完成，请重试；若仍失败，请检查连接与设备，并查看诊断日志。」包括尚无证据可识别的权限异常与未知 Windows 消息，不编造 permission_denied 等未确认标识。权限检查入口始终可用。翻译测试包含 StateError 包装后的实际字符串，不依赖运行时异常类型；日志保留现有记录，UI 不重复原始异常。

## 依赖、风险与回退

- Android 发现闪烁修复是硬前置。研究未找到独立闪烁修复任务或合入证据；相关任务为 `10-28-bidirectional-discovery` 与 `10-28-stabilize-audio-ui`。实施前须确认实际修复提交/补丁、合入结果和列表稳定性，不把相关任务完成等同于缺陷已修复。
- `main.dart` 与 3 秒策略有未提交改动，实施前重读 diff；旧 `11-02-auto-capture-device-trust/prd.md` 的 1 秒描述不作为依据，以当前用户要求、AGENTS.md:67–68 和实现 3 秒为准。
- 原审计行号、测试数量可能已过时；实施时按符号重新定位。截图图标缺失原因未证实，先排除旧 exe 和字体差异。
- 窗口尺寸含非客户区及 DPI 影响，520×720 widget 测试之外需检查真实客户端可用高度；大字号保持可滚动与控件不裁切。
- 浅深主题的不同语义色未必足够区分，须人工测量对比度并检查读屏。错误归并不得遮蔽重要修复提示。
- 提醒调用失败必须降级为原有弹窗；新增副作用不能改变配对竞态。有限闪烁避免持续打扰，关闭/失效立即停止。
- 回退按 UI 组件、runner 提醒、测试/规范三个边界审查本任务差异；不重置用户修改、不删除审计材料。若通道影响稳定性，先停用提醒调用恢复原配对行为，再修复后重新验收 R3，不能宣称完整交付。
