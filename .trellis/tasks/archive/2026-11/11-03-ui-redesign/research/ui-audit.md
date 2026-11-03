## 结论

两端的问题主要在信息层级和布局，配色是次要的。两端都把一份「分步配置向导」当成了日常首页，但这个应用平时应该不用操作。结果是「是否就绪」和「是否在采集」这两件事，在首屏要么看不到，要么只能靠一个 34px 的小图标判断。**推荐方向 A「状态优先的单屏面板」**。

## 两端现状概要

- **界面代码**：
  - 全部在 `lib/main.dart`，共 720 行。
  - 一个 State 类里有约 30 个字段（`main.dart:52-80`）。
  - `build()` 有 210 行（`main.dart:354-565`），里面 `_isWindows`/`_isAndroid` 分支交错。
  - 私有组件只有 4 个：`_StepCard`、`_StatusCard`、`_InfoRow`、`_InlineError`（`main.dart:568-720`）。
- **主题**：内联写死，只有深色，没有 darkTheme 和 themeMode（`main.dart:16-37`）。状态颜色直接用 `Colors.greenAccent` 这类写死的颜色（`main.dart:628-640`）。
- **依赖**：只有 `flutter` 和 `material`。没有 cupertino_icons、字体包或状态管理包（`pubspec.yaml`）。
- **Windows 窗口**：默认 1280×720（`windows/runner/main.cpp:29`），标题栏显示包名 `rayban_voice_mic`（`main.cpp:30`）。内容是居中的单列，最大宽度 760（`main.dart:377`）。
- **测试**：`test/widget_test.dart` 共 499 行、20 个测试。查找控件大量依赖中文文案，比如 `'开始通话'`×5、`'电脑地址'`×4、`'连接电脑'`×3、`'端口'`×3、`'CABLE Output 录音检测：检测到录音中'`、`'已记住：Home device'`、`'尚未记住设备'`、`'忘记电脑'`、`'连接手机'`、`'结束通话'`。测试画布是 1400×1800（`widget_test.dart:52`），所以「控件在首屏外」这类问题测不出来。
- **规范**：`.trellis/spec/frontend/component-guidelines.md` 和 `directory-structure.md` 还是空白模板。`state-management.md:12` 规定空闲状态必须显示为「已连接，未通话」，`:31` 规定两端都要有忘记设备、关闭配对弹窗等于拒绝，`:48` 规定 Windows 要保留手动 TCP 连接。改这些文案要同时改规范。

## 实际画面的获取情况

- **Windows 实机**：
  - 已有截图 `build/diag/ui-audit-windows.png`。
  - 我在后台、只读地补截了一张采集中的画面 `build/diag/ui-audit-windows-streaming.png`，并读了一次 AX 树。
  - 没有置顶窗口，也没有点击或输入。
- **Android 实机**：没有截到。`mCurrentFocus` 是 `NotificationShade`（通知栏），`mFocusedApp` 才是本应用，按约定不截图、不切换。
- **代替方案**：我写了一个临时渲染脚本 `build/diag/ui_render_test.dart`，复用测试里的 `TestPlatform`，用等线字体代替 Roboto，在 412×915 和 1259×680 画布上渲染了 11 张图，都在 `build/diag/` 下：
  - `ui-audit-render-android-{firstrun,discovered,pairing,idle,idle-full,streaming,error}.png`
  - `ui-audit-render-windows-{unpaired,pairing-dialog,streaming-full,narrow}.png`
  - 拼图 `ui-audit-render-{android,windows}-sheet.png`、`ui-audit-render-android-error-streaming.png`
- **注意**：渲染图的字体不是手机实际字体，只能反映布局，字形不准。

## 问题清单（按严重程度）

### 高

1. **[交互] Windows 首屏看不到采集控制。** 首屏被状态卡、信任卡和半张连接卡占满，开始/停止按钮和「CABLE Output 录音检测」都在首屏外。
   - 截图：`ui-audit-windows-streaming.png`
   - 代码：卡片顺序在 `main.dart:381-545`，窗口尺寸在 `main.cpp:29`
2. **[交互] Android 已连接后，在 915 高的屏幕上「开始通话」也在首屏外**（约在 1040px 处）。原因是信任说明、忘记按钮和麦克风单选列表排在它前面。
   - 截图：`render-android-idle.png`、`render-android-idle-full.png`
3. **[视觉] 状态卡不随状态变色。**
   - 底色固定为 `0xff202b45`（`main.dart:640`），只有 34px 的图标变色（`main.dart:645`）。
   - 「正在采集」用的 lightBlueAccent 和主题主色 `0xff7c9cff` 很接近，和空闲状态区分不开。
   - 副标题兜底文案「等待系统确认音频路由」在**未连接**时也会显示（`main.dart:660-661`），会误导用户。
   - 截图：`render-android-firstrun.png`、`render-windows-unpaired.png`
4. **[交互] 首次配对容易漏掉。**
   - Windows 的批准请求只是窗口里的一个 AlertDialog（`main.dart:165-187`）。在 `desktop/` 和 `windows/runner/` 里搜不到 FlashWindow、托盘图标、Toast 或前台激活。窗口最小化时用户看不到请求，请求会悄悄过期。
   - Android 端只在卡片中间显示一行普通文字「等待 Windows 允许连接」（`main.dart:331`），状态卡仍然是「未连接」，没有转圈或倒计时。
   - 截图：`render-android-pairing.png`
5. **[视觉] Windows 连接卡排版错乱。**
   - Column 没设 `crossAxisAlignment`，文字行居中，和其他左对齐内容混在一起，按钮又窄又居中（`main.dart:394-410`）。
   - Windows 有两张连接卡，第二张的步骤徽章写的是中文「连接」（`main.dart:384-411`、`main.dart:392`），打乱了 01/02/03 序列。
   - 截图：`ui-audit-windows-streaming.png`

### 中

6. **[交互] 低频和技术信息常驻首页**：
   - 端口说明「TCP 17891 · 发现 UDP 17893」（`main.dart:405`）
   - 自动采集规则长句（`main.dart:406`）
   - 「音频：TCP · 丢帧」（`main.dart:408`）
   - 忘记设备的说明文字常显（`main.dart:347`）
   - Windows 的 IP 输入框默认填 `127.0.0.1`（`main.dart:56`）
7. **[交互] 错误显示重复、分散，而且是原始异常文本。**
   - `snapshot.detail` 同时出现在状态卡（`main.dart:658`）和页面底部（`main.dart:556-559`）。
   - `_connectionError` 又单独显示在连接卡里（`main.dart:409`、`main.dart:472`）。
   - 错误内容直接用 `'$error'`（`main.dart:233`、`main.dart:276`），用户会看到 SocketException 原文。
   - 截图：`render-android-error.png`
8. **[文案] 同一件事用了三个词**：「采集」（`main.dart:635`）、「通话」（`main.dart:481-541`）、「录音」（`main.dart:406-407`）。Android 端完全没有提到「电脑录音时会自动采集」。
9. **[交互] 两端点发现列表的行为不一致。**
   - Windows 点一下设备就直接连接（`main.dart:397`）。
   - Android 点一下只是填好地址，还要再点按钮，而且没有选中效果（`main.dart:430`）。
   - Windows 的连接按钮不校验地址（`main.dart:404`），Android 会校验（`main.dart:287`）。
10. **[交互/视觉]** 开始和结束两个按钮一直并排显示，同一时间只有一个能点（`main.dart:526-543`）。
11. **[文案]** 「已记住设备」和「已记住：X」两行意思重复（`main.dart:330`、`main.dart:340`）。
12. **[视觉]** Windows 宽窗口下内容是 760 宽的居中单列，两侧大面积留空（`main.dart:377`）。

### 低

13. **[可达性]**
    - 状态卡没有 `Semantics(liveRegion)`，状态变化时读屏不会播报（`main.dart:621-671`）。
    - 步骤徽章「01」会被读进语义标签，AX 树里是「01 连接 Android …」。
    - 端口输入框固定 92px 宽（`main.dart:449`），大字号下可能被截断。
    - 对比度我没有测量。
14. **[视觉]** 实机截图里「忘记设备」按钮左侧图标缺失，留了一块空白，测试渲染里是正常的。[推测] 可能是正在运行的 exe 是旧构建，未确认。
15. **[视觉]** 只有深色主题，不跟随系统的浅色/深色设置；颜色都写死，以后加浅色模式时要逐个替换。
16. **[视觉]** Windows 标题栏显示包名 `rayban_voice_mic`（`main.cpp:30`），和应用内的中文名不一致。

## 重设计方向

### A. 状态优先的单屏面板（推荐）

**Windows**（建议默认窗口改成约 520×720；宽度 ≥900 时右侧另起一列放连接和音频信息）：
```
┌ Ray-Ban 语音麦克风 ───────────── ⟳ ┐
│ ┌────────────────────────────────┐ │
│ │ ● 正在采集      ← 整卡按状态着色 │ │
│ │ <glasses> → CABLE Input      │ │
│ │ Typeless 录音中 · 停止 3 秒后释放 │ │
│ │                    [ ■ 停止 ]   │ │  ← 主按钮随状态变：连接/开始/停止/修复
│ └────────────────────────────────┘ │
│ 手机  <phone-model>  ✓已记住     [⋮]  │  ⋮ = 忘记设备
│ 音频  CABLE Input (VB-Audio)  ✓    │
│ ▸ 手动连接与诊断（IP · 端口 · 丢帧） │  ← 默认折叠
└────────────────────────────────────┘
```

**Android**：
```
┌ Ray-Ban 语音麦克风        ⟳  ⋮ ┐  ⋮ = 权限检查 / 忘记电脑
│ ┌────────────────────────────┐ │
│ │       ◉ 已连接 · 待命        │ │  ← 着色的状态区
│ │  电脑开始录音时会自动采集     │ │
│ │     [ ● 手动开始采集 ]       │ │
│ └────────────────────────────┘ │
│ 💻 My-PC   ✓已记住       │
│ 🎙 麦克风：跟随系统          ›  │  ← 点开底部弹出单选列表
│ ▸ 手动输入地址                  │  ← 未连接且没发现电脑时自动展开
└────────────────────────────────┘
```

- **首次配对**：未连接时状态区就是发现列表，点一下设备直接连接，和 Windows 行为一致。等待批准时状态区显示转圈和「请在电脑上点允许」。
- **视觉风格**：
  - 状态色从 `ColorScheme` 取，比如 primaryContainer、tertiaryContainer、errorContainer，整卡着色。
  - 去掉步骤编号；开始和停止合并成一个随状态变化的按钮。
  - 低频设置放进 ExpansionTile 或 ⋮ 菜单。
  - 主题用 `fromSeed` 同时生成浅色和深色两套，设 `ThemeMode.system`。
  - 错误只在状态区显示一处，并把常见异常翻译成可操作的提示。
- **改动量**：中等。只重写展示层，不动 `_run`、配对弹窗、轮询和输入设备刷新逻辑，也不改平台接口。需要改一行 `main.cpp` 的窗口尺寸。
- **拆分文件**：建议把展示组件挪到 `lib/dashboard_widgets.dart`，`main.dart` 只保留应用入口、主题和状态逻辑，约 400 行。
- **测试影响**：
  - 约 12 个文案查找要改。
  - 增加两个测试：一是「状态 → 主按钮」的对应关系；二是在 412×915 画布上断言主按钮在首屏内。
  - 需要同步改 `state-management.md:12` 里的空闲文案，或者保留「已连接，未通话」。
- **风险**：
  - 「电脑主动连接已记住的手机」入口在 `⋮` 里会变得不明显。
  - 配对仍然只靠窗口内弹窗。要让最小化时也能注意到，需要原生 FlashWindow，属于 runner 层改动，可以另开。

### B. 原地整理（最小改动）

- 保持卡片结构不变，只做以下调整：
  - 状态卡加上主按钮。
  - 把 Windows 两张连接卡合并，并修正对齐。
  - 删掉步骤徽章。
  - 技术信息收进折叠区。
  - 状态卡按状态着色。
  - 统一用「采集」这个词。
  - 错误只显示一处。
- **两端布局**：顺序变为「状态卡+主按钮 → 设备/麦克风 → 折叠的高级设置」，仍然是单列卡片。
- **改动量**：小，大约改动 150 行以内，不需要拆文件。测试改 5–8 个文案。
- **缺点**：用户说的「丑」只能部分改善，整体还是一排表单卡片的观感。

### C. 分阶段页面（配对向导 + 极简待命页 + 设置页）

- 用 Navigator 分成三页：首次配对向导、日常的大圆形状态页、单独的设置页。
- **改动量**：大，基本要重写 `main.dart`。
- **风险**：配对弹窗依赖 `removeRoute` 和路由状态（`main.dart:141-142`、`main.dart:185`），和多页面路由会互相影响，容易出回归。很多测试要重写。
- **结论**：对一个平时不用操作的工具来说过度设计，不推荐。

**推荐 A 的理由**：它直接解决问题 1–5、7、8，也就是主要的布局和层级问题。它只是单页重排，配对和状态这些有竞态风险的逻辑一行不动，不加依赖。改动量比 B 多一个拆文件，但视觉提升明显。

## 约束与未确认的点

- **未验证**：Android 实机画面没截到，渲染用的是代替字体；对比度没有实测；忘记按钮图标缺失的原因没确认。
- **临时文件**：渲染脚本 `build/diag/ui_render_test.dart` 和 `build/diag/` 下的渲染图都是审计用的临时文件。要清理需要你授权；也可以用 `flutter test build/diag/ui_render_test.dart` 重新生成，供改版前后对比。
- **并行改动**：另一个代理正在把「15 秒」改成「3 秒」（`main.dart:406`）。方向 A 会删掉这句长文案，实施时需要和那边协调。
- **范围之外**：Windows 配对请求的系统级提醒（FlashWindow 或托盘）不在 Flutter 展示层能解决的范围内，没有列入改动量。