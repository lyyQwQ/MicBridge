# State Management

## Overview

Flutter is the only UI on Android and Windows and owns only presentation state. Platform adapters expose typed `MicSnapshot` and `ComputerEndpoint` streams plus command methods; Flutter never parses PCM, HELLO frames, TXT records, or owns Android/Windows audio resources. The Windows .NET 10 process is a windowless host with no dashboard of its own.

## State Categories

- Local UI state: text field, busy flag, enumerated input list, selected input device ID, discovered peer list, and the current `MicSnapshot`.
- Platform state: `disconnected`, `idle`, `starting`, `streaming`, `stopping`, or `error`.
- Derived state: Start is enabled for an authenticated connection in `idle` or recoverable `error` and no command in progress. Android defaults to Follow System (`selectedInputId = -1`), so no manual device selection is required. A disconnected error never enables Start. Stop is enabled for `starting` or `streaming` while the dashboard is not busy.
- `ConnectedIdle` is rendered as `已连接，未采集` so connection and capture phases are not conflated. All capture controls use 采集 wording (`开始采集` / `停止采集` / `重试采集`).
- `_run` remains the single command re-entry guard for Flutter commands.
- Local errors (`_connectionError`) follow time order and record their source (`_statusError`). A *changed* snapshot (state, connected, or detail differs from the previous one) that is connected or `error` clears any local error, because it is newer. Repeated identical snapshots (Windows 2 s polling) and disconnected snapshots never clear a user-command error. Errors from a failed status read (polling or refresh) are cleared by the next successful status read even when the snapshot is unchanged; that success never clears a user-command error. `_run` clears the error when a new command starts. The dashboard shows exactly one error: the local error if present, otherwise the snapshot detail when `state == error`.

## When to Use Global State

Do not add a state-management package for this MVP. Keep one platform adapter per dashboard and use its stream subscription. Promote state only if a second screen needs the same live snapshot.

## Server State

- The Android/.NET connection is authoritative. On disconnect, adapters emit `disconnected`; command failures emit `error` with diagnostic detail. UI must not infer `streaming` from a button press.

- Windows Flutter starts the windowless `MicBridge.App.exe` host without a mode argument and consumes the .NET core through line-delimited JSON over stdin/stdout. Flutter owns the approval dialog; the core owns approval decisions and trusted-device storage.
- Android uses the Kotlin `MethodChannel`/`EventChannel` adapter.
- Discovery exposes `Stream<List<ComputerEndpoint>>`; tapping a discovery row on either platform connects immediately through `_selectComputer(endpoint)` + `_connect()`, which keeps `expectedDeviceId` only while host/port still match that row. Rows are disabled while busy. On Windows, the IPC adapter publishes discovered `role=android` peers. Discovery failure never disables manual TCP.
- Android discovery emits a complete current peer list, including removals after the discovery TTL; endpoint updates for one stable device ID replace the old host/port.
- Android input enumeration can be empty; Follow System remains a valid preference, and native START reports `input_device_not_found` if there is no usable input. Physical Android device IDs are valid only for the active enumeration.
- Android platform bridge contract: `devices` returns `{id, name, address, type, channels}` rows for selectable physical inputs plus the Follow System row (`id = -1`). `selectInput({id})` persists explicit device identity or clears the preference for Follow System. A saved device missing at launch or START temporarily uses system routing without erasing the preference; when it returns, native selection is restored.
- `MicSnapshot.selectedInputId` and `inputSelectionDetail` synchronize native selection and the missing-device explanation. Requery inputs after device events, coalesce events during an in-flight query, and discard stale enumeration results. `lastHost` / `lastPort` prefill the last authenticated endpoint only before the user has edited the field.
- Discovery is a convenience, not an authorization state; TCP HELLO and the connection adapter remain authoritative.
- Pairing UI contains only pending/trusted metadata, never the long-term key. Windows handles `pairing_request` and recovers `pendingPairing` from status; each request opens one approval dialog and dismissing it denies the request. Both platforms expose an explicit forget action in the remembered-device row menu (`设备操作`), including in `repair_required` without a trusted device.
- Android foreground connect may request first pairing. A running service may reconnect a remembered computer without starting audio; authentication failure requires a clear repair message. Discovery names and addresses never establish trust.
- mDNS and UDP discovery failures leave the peer list empty/partial and preserve manual IP fallback. UDP discovery uses port `17893`; it does not carry credentials or audio.
- User-visible Windows and Flutter dashboard labels use simplified Chinese; protocol fields, capability names, logs, and error codes remain English.

## Common Mistakes

- Do not duplicate protocol/TXT parsing in widgets.
- Do not silently map an unknown state to `streaming`.
- Do not enable Start while the adapter is disconnected or busy.
- Do not auto-connect a discovered peer unless its role is `android` and its mDNS capability/protocol fields pass validation.

## Scenario: Windows Flutter Dashboard and Windowless Host

- `CoreSnapshot` is authoritative; the UI renders the adapter's `MicSnapshot` and never sets `streaming` optimistically.
- `CoreController.SnapshotChanged` flows through `LocalControlServer` JSON state events to the Flutter adapter; widgets do not touch sockets or audio resources.
- Flutter sends `start`, `stop`, and `connect` commands through IPC. The host forwards them to `CoreController.DispatchAsync("START" | "STOP")` and the typed `ConnectAsync` endpoint command.
- The dashboard shows the current peer and the live `listenerActive` result (`CABLE Output 录音检测：检测到录音中／未检测到录音`) inside the status panel, and keeps manual TCP available when discovery is unavailable.
- Keep `MicBridge.App` in `desktop/MicBridge.slnx`: it supplies the windowless host. `windows/CMakeLists.txt` publishes that project into the Flutter executable's `core/` directory, matching `lib/platform_bridge.dart`.
- The host uses `OutputType=WinExe` and never allocates a console. It explicitly opens inherited stdin/stdout with UTF-8 readers/writers; stdout uses UTF-8 without a BOM and automatic flushing for JSON IPC. Diagnostics use stderr, not stdout.
- On stdin EOF or an IPC `shutdown` request, host cleanup dispatches STOP with a three-second cancellation deadline, then disposes `LocalControlServer` to drain queued responses, and finally disposes `CoreController`. The IPC `shutdown` handler also bounds its STOP attempt before queuing its acknowledgement; three seconds is a STOP-attempt deadline, not a total process-exit deadline.
- If core startup fails to bind a listener, the host logs the startup error and keeps IPC available for `status` and shutdown. A subsequent orderly shutdown or stdin EOF follows the same cleanup sequence and returns exit code `0`.

### Retired View Tests and Retained Behavior Coverage

- The old view, application XAML/theme, view model, and `MainWindowViewModelTests.cs` are archived under `.bak/2026-11-02/` with their original relative paths. Archive only UI-specific files; retain the host, IPC, core, and their tests.
- Dispatcher event coalescing, private pending fields, and `ICommand.CanExecute` assertions belong to the retired view implementation and are archived with it. This does not retire core protocol or state-machine coverage.
- Preserve discovered-peer removal at the IPC boundary: after a removal, both the `peers` event and the next `discover` response must omit the device. Flutter consumes the resulting complete list.
- Preserve authenticated error retry in core tests: `AutoCaptureControllerTests.ManualRetryAfterCaptureErrorWorksOnSameAuthenticatedConnection` exercises a failed START followed by a successful retry on the same authenticated socket. Flutter button-state tests own presentation gating for disconnected, busy, starting, and streaming states.

## Scenario: Status-First Dashboard

- `lib/main.dart` owns state, theme (`ColorScheme.fromSeed` teal `#14B8A6`, light + dark, `ThemeMode.system`) and platform subscriptions. `lib/dashboard_widgets.dart` holds stateless presentation (status panel, peer list, device row, microphone sheet, collapsed manual section, responsive layout) and pure mappings (`primaryActionFor`, `statusToneFor`, `describeError`); widgets never own `MicPlatform`.
- One primary button follows `primaryActionFor`: disconnected → connect (while the user has not edited host/port, a discovered remembered peer wins over the prefilled `lastHost` and connects with its `expectedDeviceId`, covering a changed IP; otherwise the filled address; empty address expands manual input), pairing → disabled wait, idle → 开始采集, connected error → 重试采集, disconnected error → 重新连接, starting/streaming → 停止采集, stopping → disabled. Enablement still comes from `canStart`/`canStop`/busy.
- Status tones come from `ColorScheme`: neutral `surfaceContainer`, pairing `tertiaryContainer` (tertiary overridden with an amber `fromSeed` palette because the teal seed yields a blue tertiary), connected `primaryContainer`, streaming solid `primary` with a bounded pulse (8 half-cycles, skipped when animations are disabled), error `errorContainer`. Text/background pairs must keep ≥ 4.5:1 in both themes (`test/dashboard_widgets_test.dart`).
- Error text is translated from stable native/bridge fragments; unknown strings show one fallback hint and never raw exception text. Non-error disconnected `detail` is shown only when it is a known error (as a hint) or Chinese guidance.
- Layout switches to two columns at ≥ 900 logical width from `LayoutBuilder`. The status title/subtitle on both platforms is a `Semantics(liveRegion: true)` whose label changes only with the state.
- Windows pairing reminder: `lib/window_attention.dart` calls the runner-only `micbridge/window` MethodChannel (`flashPairing` / `stopFlashing`), outside `MicPlatform` and the JSON IPC. Dart flashes once per request after de-duplication and expiry checks and stops when the dialog route ends or the dashboard is disposed; failures are swallowed. The runner uses `FlashWindowEx(FLASHW_TRAY, 3)` on the top-level HWND only when it is not the foreground window, stops on `WM_ACTIVATE`, and unbinds the channel before destroying the Flutter controller. Never activate, raise, or restore the window for this reminder.

## Scenario: Persistent Input Selection

### Scope / Trigger

Android selection must survive process restarts and temporary device absence across the native/Dart boundary.

### Signatures and Contracts

Use `devices`, `selectInput({id})`, and `MicSnapshot.selectedInputId` / `inputSelectionDetail`; `-1` represents Follow System, not an absent selection. Native SharedPreferences own the saved device identity; Flutter owns presentation only.

### Validation & Error Matrix

| Condition | UI and capture behavior |
| --- | --- |
| No saved device | Follow System selected; native START resolves the route |
| Saved device present | Current native device ID selected |
| Saved device absent | Follow System selected with temporary-fallback explanation; preference retained |
| No physical input | Native START reports no usable input |

### Good / Base / Bad Cases

- Good: a saved headset returns during enumeration; rerun enumeration and show the restored selection.
- Base: first launch selects Follow System without a manual choice.
- Bad: a delayed snapshot overwrites an address the user has already edited.

### Tests Required

Widget tests cover system selection, fallback explanation, native selection restoration, device-event/query races, endpoint prefill, and preservation of user edits.

### Wrong vs Correct

Wrong: persist only Android's transient device ID or clear an unavailable preference in Flutter. Correct: persist native device identity and render the native effective selection, retaining explicit intent while temporarily following the system.

## Scenario: Android 音频设置通道

### Scope / Trigger

音频设置是 Android 可选能力 `AudioSettingsPlatform`，复用 `micbridge/platform`，不扩展 `MicPlatform`、Windows IPC 或状态 EventChannel。存储与采集生命周期见 [error-handling.md](../backend/error-handling.md#scenario-android-麦克风设置与会话生命周期)。

### Signatures

- `getAudioSettings(null)`：无参数读取，不创建 AudioRecord、不启动采集。
- `setAudioSettings({gainDb, ns, aec, agc})`：完整替换偏好；两者均返回 `{preferences: {gainDb, ns, aec, agc}, available: {ns, aec, agc}}`。

### Contracts

`gainDb` 必须为 0–20 整数；三个效果必须为 null / true / false，四个键齐全且不允许额外键。`available` 的三个值为布尔值，仅代表 SDK 能力。设置面板固定显示「系统默认 / 开 / 关」三段单选，分别写入 null / true / false；不可用或保存中禁用，串行提交、失败恢复已确认值，销毁后忽略晚到回应。提示“下次开始采集时生效”；偏好不等于实际 enabled 状态。

### Validation & Error Matrix

| 条件 | 结果 |
| --- | --- |
| get 参数非 null；set 缺键、多键、类型或范围错误 | `settings_invalid` |
| 能力查询或读取异常 | `settings_read_failed` |
| 持久化失败 | `settings_persist_failed`，保留已确认值 |
| 响应结构或类型不符 | Dart 拒绝解码，不静默替换成默认值 |

### Good / Base / Bad Cases

Good：`{gainDb: 6, ns: true, aec: null, agc: false}` 完整保存。Base：0 dB、三个 null，选中系统默认。Bad：缺少 `ns` 或传入 `gainDb: 6.0`，拒绝请求。

### Tests Required

`test/audio_settings_test.dart` 断言请求与响应形状、非法值拒绝、三段写入 null/true/false、不可用禁用、失败回退、防重入、晚到回应及销毁保护；小屏明暗主题与放大字号下不溢出。

### Wrong vs Correct

错误：把 `available=true` 显示为“效果已开启”，或将 null 转成 false。正确：能力只控制可用性，三态忠实呈现保存的偏好。
