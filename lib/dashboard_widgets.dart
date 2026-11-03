import 'package:flutter/material.dart';

import 'platform_bridge.dart';

/// 首屏主按钮的语义动作；使能仍由页面的 canStart/canStop 与 busy 决定。
enum DashboardAction {
  connect,
  reconnect,
  waitForApproval,
  start,
  retry,
  stop,
  stopping,
}

DashboardAction primaryActionFor(
  MicSnapshot snapshot, {
  required bool awaitingApproval,
}) {
  if (snapshot.state == MicState.stopping) return DashboardAction.stopping;
  if (snapshot.state == MicState.starting ||
      snapshot.state == MicState.streaming) {
    return DashboardAction.stop;
  }
  if (snapshot.connected) {
    return snapshot.state == MicState.error
        ? DashboardAction.retry
        : DashboardAction.start;
  }
  if (awaitingApproval) return DashboardAction.waitForApproval;
  return snapshot.state == MicState.error
      ? DashboardAction.reconnect
      : DashboardAction.connect;
}

String actionLabel(DashboardAction action, {required bool isWindows}) =>
    switch (action) {
      DashboardAction.connect => isWindows ? '连接手机' : '连接电脑',
      DashboardAction.reconnect => '重新连接',
      DashboardAction.waitForApproval => '等待配对批准',
      DashboardAction.start => '开始采集',
      DashboardAction.retry => '重试采集',
      DashboardAction.stop => '停止采集',
      DashboardAction.stopping => '正在停止采集',
    };

IconData _actionIcon(DashboardAction action) => switch (action) {
  DashboardAction.connect || DashboardAction.reconnect => Icons.link_rounded,
  DashboardAction.waitForApproval => Icons.hourglass_top_rounded,
  DashboardAction.start || DashboardAction.retry => Icons.mic_rounded,
  DashboardAction.stop || DashboardAction.stopping => Icons.stop_rounded,
};

/// 状态区色调；颜色全部取自 ColorScheme。
enum StatusTone { neutral, pending, ready, active, error }

StatusTone statusToneFor(
  MicSnapshot snapshot, {
  required bool awaitingApproval,
}) {
  if (snapshot.state == MicState.error) return StatusTone.error;
  if (snapshot.state == MicState.streaming) return StatusTone.active;
  if (snapshot.connected || snapshot.state != MicState.disconnected) {
    return StatusTone.ready;
  }
  return awaitingApproval ? StatusTone.pending : StatusTone.neutral;
}

typedef ToneColors = ({
  Color background,
  Color foreground,
  Color button,
  Color onButton,
});

ToneColors toneColors(ColorScheme s, StatusTone tone) => switch (tone) {
  StatusTone.neutral => (
    background: s.surfaceContainer,
    foreground: s.onSurface,
    button: s.primary,
    onButton: s.onPrimary,
  ),
  StatusTone.pending => (
    background: s.tertiaryContainer,
    foreground: s.onTertiaryContainer,
    button: s.tertiary,
    onButton: s.onTertiary,
  ),
  StatusTone.ready => (
    background: s.primaryContainer,
    foreground: s.onPrimaryContainer,
    button: s.primary,
    onButton: s.onPrimary,
  ),
  StatusTone.active => (
    background: s.primary,
    foreground: s.onPrimary,
    button: s.onPrimary,
    onButton: s.primary,
  ),
  StatusTone.error => (
    background: s.errorContainer,
    foreground: s.onErrorContainer,
    button: s.error,
    onButton: s.onError,
  ),
};

const unknownErrorHint = '操作未完成，请重试；若仍失败，请检查连接与设备，并查看诊断日志。';
const _connectHint = '无法连接设备，请检查地址、对端服务和防火墙。';
const _timeoutHint = '等待设备响应超时，请检查对端应用和网络后重试。';
const _trustHint = '配对记录不可用，请检查两端配对状态，必要时在双方忘记设备后重新配对。';
const _micHint = '麦克风不可用，请检查设备连接或选择「跟随系统」。';
const _permissionHint = '需要麦克风权限，请在右上角菜单打开「权限检查」。';

// 现有原生/桥接字符串中的稳定片段；具体原因在前，connect_failed 包装在最后。
const _errorHints = <(String, String)>[
  ('请输入有效的电脑地址和端口', '请输入有效的设备地址和 1–65535 范围内的端口。'),
  // TrustHandshake.errorDetail 本身已是可操作中文，原样展示。
  ('电脑拒绝了配对，请在手机前台重新连接', '电脑拒绝了配对，请在手机前台重新连接。'),
  ('等待电脑批准超时，请在手机前台重新连接', '等待电脑批准超时，请在手机前台重新连接。'),
  ('电脑正在处理另一项配对，请稍后重新连接', '电脑正在处理另一项配对，请稍后重新连接。'),
  ('电脑已有其他设备配对', '电脑已有其他设备配对，请先在电脑上忘记设备，再在手机重新连接。'),
  ('两端设备信任不一致', '两端设备信任不一致，请在双方忘记设备后重新配对。'),
  ('设备信任保存失败', '设备信任保存失败，请在双方忘记设备后重新配对。'),
  ('电脑不支持设备信任认证', '电脑不支持设备信任认证，请更新两端应用。'),
  ('设备认证失败，请在双方检查配对并重新连接', '设备认证失败，请在双方检查配对并重新连接。'),
  ('设备信任存储不可用', _trustHint),
  ('自动重连已暂停，请在双方忘记设备后重新配对', _trustHint),
  ('input_device_not_found', _micHint),
  ('没有可用的麦克风输入', _micHint),
  ('请先授予麦克风权限', _permissionHint),
  ('请在手机前台授予麦克风权限', _permissionHint),
  ('请在手机前台点击连接', '请在手机前台打开本应用后再连接。'),
  ('电脑尚未完成认证', '电脑尚未完成认证，请等待连接完成或重新连接后再试。'),
  ('电脑已断开连接', '连接已断开，请检查电脑应用和网络后重新连接。'),
  ('Android did not confirm audio capture', _timeoutHint),
  ('TimeoutException', _timeoutHint),
  ('超时', _timeoutHint),
  ('无法连接，请检查电脑地址、服务和防火墙', _connectHint),
  ('无法解析电脑地址', _connectHint),
  ('电脑地址或端口无效', _connectHint),
  ('连接不可用，请检查网络和电脑服务', _connectHint),
  ('Windows 核心已退出', '电脑后台服务已退出，请重新打开本应用。'),
  ('connect_failed', _connectHint),
];

String? _knownHint(String text) {
  for (final (fragment, hint) in _errorHints) {
    if (text.contains(fragment)) return hint;
  }
  return null;
}

/// 把异常或快照 detail 转为原因与下一步；未知内容不展示原文。
String describeError(String text) => _knownHint(text) ?? unknownErrorHint;

/// 未连接时的 detail 可能是进度/指引，也可能是上次失败原因：已知错误转提示，中文说明原样展示，其余不显示。
String? describeDetail(String detail) {
  if (detail.isEmpty) return null;
  return _knownHint(detail) ??
      (RegExp(r'[\u4e00-\u9fff]').hasMatch(detail) ? detail : null);
}

String inputTypeLabel(int type) {
  if (type == 0) return '每次开始采集时使用系统当前的通话麦克风';
  if (type == 15) return '手机内置麦克风';
  if (type == 7 || type == 26) return '蓝牙耳机 / 智能眼镜';
  if (type == 3 || type == 4 || type == 28) return '有线耳机';
  return '其他输入设备';
}

const primaryActionKey = Key('primary-action');
const statusPanelKey = Key('status-panel');
const statusLiveKey = Key('status-live');
const dashboardErrorKey = Key('dashboard-error');
const twoColumnKey = Key('dashboard-two-column');
const singleColumnKey = Key('dashboard-single-column');

/// 首屏状态区：状态、短说明、单一主按钮；未连接时内嵌发现列表。
class StatusPanel extends StatelessWidget {
  const StatusPanel({
    super.key,
    required this.snapshot,
    required this.isWindows,
    required this.awaitingApproval,
    required this.hasPeers,
    required this.action,
    required this.onAction,
    this.error,
    this.children = const [],
  });

  final MicSnapshot snapshot;
  final bool isWindows;
  final bool awaitingApproval;
  final bool hasPeers;
  final DashboardAction action;
  final VoidCallback? onAction;
  final String? error;
  final List<Widget> children;

  (String, IconData, String) _describe() {
    final autoRule = isWindows
        ? '任何录音软件从 CABLE Output 录音时自动采集，录音结束 3 秒后释放；手动开始需手动停止。'
        : '电脑录音软件从 CABLE Output 录音时会自动采集，录音结束 3 秒后释放；手动开始需手动停止。';
    return switch (snapshot.state) {
      MicState.error => (
        '需要处理',
        Icons.error_outline_rounded,
        snapshot.connected ? '连接仍在，可重试采集。' : '连接已中断，请检查后重新连接。',
      ),
      MicState.streaming => ('正在采集', Icons.mic_rounded, autoRule),
      MicState.starting => ('正在启动采集', Icons.sync_rounded, '等待手机确认麦克风…'),
      MicState.stopping => ('正在停止采集', Icons.stop_circle_outlined, '正在释放音频路由…'),
      MicState.idle => (
        '已连接，未采集',
        Icons.check_circle_outline_rounded,
        autoRule,
      ),
      MicState.disconnected when awaitingApproval =>
        isWindows
            ? ('收到配对请求', Icons.hourglass_top_rounded, '请在弹出的窗口中允许或拒绝。')
            : ('等待电脑批准', Icons.hourglass_top_rounded, '请在电脑上允许配对'),
      MicState.disconnected => (
        '未连接',
        Icons.link_off_rounded,
        describeDetail(snapshot.detail) ??
            (isWindows
                ? '首次请从手机发起连接；已记住的手机出现在下方时可直接连接。'
                : hasPeers
                ? '点击下方电脑即可连接。'
                : '没有发现电脑，请确认两端在同一 Wi-Fi，或手动输入地址。'),
      ),
    };
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme;
    final tone = statusToneFor(snapshot, awaitingApproval: awaitingApproval);
    final colors = toneColors(scheme, tone);
    final (title, icon, message) = _describe();
    // 错误态的说明由错误提示本身承担，避免两处重复。
    final showMessage = !(tone == StatusTone.error && error != null);
    return Card(
      key: statusPanelKey,
      margin: EdgeInsets.zero,
      elevation: 0,
      color: colors.background,
      child: DefaultTextStyle.merge(
        style: TextStyle(color: colors.foreground),
        child: IconTheme.merge(
          data: IconThemeData(color: colors.foreground),
          child: ListTileTheme.merge(
            textColor: colors.foreground,
            iconColor: colors.foreground,
            child: Padding(
              padding: const EdgeInsets.all(20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // 只把状态标题与说明放进播报区，相同快照不会改变标签，读屏不会重复播报。
                  Semantics(
                    key: statusLiveKey,
                    liveRegion: true,
                    container: true,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            Icon(icon, size: 28),
                            const SizedBox(width: 12),
                            Expanded(
                              child: Text(
                                title,
                                style: text.headlineSmall?.copyWith(
                                  color: colors.foreground,
                                  fontWeight: FontWeight.bold,
                                ),
                              ),
                            ),
                            if (tone == StatusTone.active)
                              PulseDot(color: colors.foreground),
                          ],
                        ),
                        if (showMessage) ...[
                          const SizedBox(height: 6),
                          Text(message),
                        ],
                      ],
                    ),
                  ),
                  if (tone == StatusTone.pending && !isWindows) ...[
                    const SizedBox(height: 12),
                    LinearProgressIndicator(
                      color: scheme.tertiary,
                      backgroundColor: colors.background,
                    ),
                  ],
                  if (error != null) ...[
                    const SizedBox(height: 12),
                    _ErrorNotice(
                      text: error!,
                      inline: tone == StatusTone.error,
                      foreground: colors.foreground,
                    ),
                  ],
                  if (isWindows) ...[
                    const SizedBox(height: 10),
                    Row(
                      children: [
                        Icon(
                          snapshot.listenerActive
                              ? Icons.graphic_eq_rounded
                              : Icons.hearing_disabled_outlined,
                          size: 18,
                        ),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            'CABLE Output 录音检测：${snapshot.listenerActive ? "检测到录音中" : "未检测到录音"}',
                          ),
                        ),
                      ],
                    ),
                  ],
                  const SizedBox(height: 16),
                  FilledButton.icon(
                    key: primaryActionKey,
                    onPressed: onAction,
                    style: FilledButton.styleFrom(
                      backgroundColor: colors.button,
                      foregroundColor: colors.onButton,
                      minimumSize: const Size.fromHeight(48),
                    ),
                    icon: Icon(_actionIcon(action)),
                    label: Text(actionLabel(action, isWindows: isWindows)),
                  ),
                  if (children.isNotEmpty) ...[
                    const SizedBox(height: 12),
                    ...children,
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _ErrorNotice extends StatelessWidget {
  const _ErrorNotice({
    required this.text,
    required this.inline,
    required this.foreground,
  });
  final String text;
  final bool inline;
  final Color foreground;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final color = inline ? foreground : scheme.onErrorContainer;
    final content = Row(
      key: dashboardErrorKey,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(Icons.error_outline_rounded, size: 20, color: color),
        const SizedBox(width: 8),
        Expanded(
          child: Text(text, style: TextStyle(color: color)),
        ),
      ],
    );
    if (inline) return content;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: scheme.errorContainer,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Padding(padding: const EdgeInsets.all(12), child: content),
    );
  }
}

/// 采集中的有界脉冲：约 5.6 秒后停在实心状态；系统关闭动画时不播放。
class PulseDot extends StatefulWidget {
  const PulseDot({super.key, required this.color});
  final Color color;

  @override
  State<PulseDot> createState() => _PulseDotState();
}

class _PulseDotState extends State<PulseDot>
    with SingleTickerProviderStateMixin {
  late final _controller = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 700),
  );
  bool _started = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (!_started && !MediaQuery.disableAnimationsOf(context)) {
      _started = true;
      // 偶数次往返结束于起点，即完全不透明。
      _controller.repeat(reverse: true, count: 8);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return FadeTransition(
      opacity: Tween<double>(begin: 1, end: 0.3).animate(_controller),
      child: Container(
        width: 14,
        height: 14,
        decoration: BoxDecoration(color: widget.color, shape: BoxShape.circle),
      ),
    );
  }
}

/// 发现到的对端；点击即走页面已有的连接流程。
class PeerList extends StatelessWidget {
  const PeerList({
    super.key,
    required this.peers,
    required this.isWindows,
    required this.onSelect,
  });
  final List<ComputerEndpoint> peers;
  final bool isWindows;
  final ValueChanged<ComputerEndpoint>? onSelect;

  @override
  Widget build(BuildContext context) {
    return ConstrainedBox(
      constraints: const BoxConstraints(maxHeight: 216),
      child: ListView(
        shrinkWrap: true,
        padding: EdgeInsets.zero,
        children: [
          for (final peer in peers)
            ListTile(
              contentPadding: EdgeInsets.zero,
              enabled: onSelect != null,
              leading: Icon(
                isWindows ? Icons.smartphone_rounded : Icons.computer_rounded,
              ),
              title: Text(peer.name),
              subtitle: Text('${peer.host}:${peer.port}'),
              trailing: const Icon(Icons.chevron_right_rounded),
              onTap: onSelect == null ? null : () => onSelect!(peer),
            ),
        ],
      ),
    );
  }
}

/// 已记住设备的一行摘要；忘记操作在行尾菜单。
class DeviceSummary extends StatelessWidget {
  const DeviceSummary({
    super.key,
    required this.snapshot,
    required this.isWindows,
    required this.onForget,
  });
  final MicSnapshot snapshot;
  final bool isWindows;
  final VoidCallback? onForget;

  @override
  Widget build(BuildContext context) {
    final trusted = snapshot.trustedDevice;
    final repair = snapshot.trustState == 'repair_required';
    final canForget = trusted != null || repair;
    return ListTile(
      leading: Icon(
        isWindows ? Icons.smartphone_rounded : Icons.computer_rounded,
      ),
      title: Text(trusted?.deviceName ?? (repair ? '配对记录不一致' : '尚未记住设备')),
      subtitle: Text(
        repair
            ? '信任记录不一致，请双方忘记设备后重新连接'
            : trusted != null
            ? '已记住，之后自动重连'
            : isWindows
            ? '首次请从手机发起连接，并在此允许'
            : '首次连接需在电脑上允许',
      ),
      trailing: canForget
          ? PopupMenuButton<void>(
              tooltip: '设备操作',
              enabled: onForget != null,
              itemBuilder: (_) => [
                PopupMenuItem<void>(
                  onTap: onForget,
                  child: ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: const Icon(Icons.link_off_rounded),
                    title: Text(isWindows ? '忘记设备' : '忘记电脑'),
                    subtitle: const Text('仅清除本机记录，另一端也需忘记'),
                  ),
                ),
              ],
            )
          : null,
    );
  }
}

/// Android 麦克风摘要；点击后在底部面板单选，默认跟随系统。
class MicrophoneSummary extends StatefulWidget {
  const MicrophoneSummary({
    super.key,
    required this.inputs,
    required this.selectedId,
    required this.detail,
    required this.onSelect,
  });
  final List<InputDevice> inputs;
  final int? selectedId;
  final String detail;
  final ValueChanged<InputDevice>? onSelect;

  @override
  State<MicrophoneSummary> createState() => _MicrophoneSummaryState();
}

class _MicrophoneSummaryState extends State<MicrophoneSummary> {
  // 底部面板是独立路由，显式订阅父页面传入的最新属性。
  late final _live = ValueNotifier<MicrophoneSummary>(widget);

  @override
  void didUpdateWidget(covariant MicrophoneSummary oldWidget) {
    super.didUpdateWidget(oldWidget);
    // 独立路由不能在父页面构建时同步触发 setState。
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _live.value = widget;
    });
  }

  @override
  void dispose() {
    _live.dispose();
    super.dispose();
  }

  Future<void> _open(BuildContext context) async {
    final id = await showModalBottomSheet<int>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      builder: (sheetContext) => ValueListenableBuilder<MicrophoneSummary>(
        valueListenable: _live,
        builder: (_, current, _) => SafeArea(
          child: RadioGroup<int>(
            groupValue:
                current.inputs.any((device) => device.id == current.selectedId)
                ? current.selectedId
                : null,
            onChanged: (id) {
              if (!mounted) return;
              final latest = widget;
              if (latest.onSelect != null &&
                  latest.inputs.any((device) => device.id == id)) {
                Navigator.of(sheetContext).pop(id);
              }
            },
            child: ListView(
              shrinkWrap: true,
              padding: const EdgeInsets.only(bottom: 16),
              children: [
                ListTile(
                  title: Text(
                    '选择麦克风',
                    style: Theme.of(sheetContext).textTheme.titleMedium,
                  ),
                  subtitle: current.detail.isEmpty
                      ? null
                      : Text(current.detail),
                ),
                for (final device in current.inputs)
                  RadioListTile<int>(
                    value: device.id,
                    enabled: current.onSelect != null,
                    secondary: Icon(
                      device.type == 7 || device.type == 26
                          ? Icons.bluetooth_audio_rounded
                          : Icons.mic_none_rounded,
                    ),
                    title: Text(device.name),
                    subtitle: Text(inputTypeLabel(device.type)),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
    if (!mounted || id == null) return;
    final device = widget.inputs.where((device) => device.id == id).firstOrNull;
    if (device != null) widget.onSelect?.call(device);
  }

  @override
  Widget build(BuildContext context) {
    final inputs = widget.inputs;
    final selectedId = widget.selectedId;
    final detail = widget.detail;
    final onSelect = widget.onSelect;
    final selected = inputs
        .where((device) => device.id == selectedId)
        .firstOrNull;
    final summary = inputs.isEmpty ? '没有可用输入设备' : selected?.name ?? '未选择，请点此选择';
    return ListTile(
      leading: Icon(
        inputs.isEmpty ? Icons.mic_off_rounded : Icons.mic_none_rounded,
      ),
      title: const Text('麦克风'),
      subtitle: Text(detail.isEmpty ? summary : '$summary\n$detail'),
      isThreeLine: detail.isNotEmpty,
      trailing: inputs.isEmpty ? null : const Icon(Icons.chevron_right_rounded),
      enabled: inputs.isNotEmpty && onSelect != null,
      onTap: inputs.isEmpty ? null : () => _open(context),
    );
  }
}

/// 默认折叠的手动连接与诊断。
class ManualSection extends StatelessWidget {
  const ManualSection({
    super.key,
    required this.controller,
    required this.isWindows,
    required this.host,
    required this.port,
    required this.onExpansionChanged,
    required this.onEdited,
    required this.onConnect,
    required this.diagnostics,
  });
  final ExpansibleController controller;
  final bool isWindows;
  final TextEditingController host;
  final TextEditingController port;
  final ValueChanged<bool> onExpansionChanged;
  final VoidCallback onEdited;
  final VoidCallback? onConnect;
  final List<String> diagnostics;

  @override
  Widget build(BuildContext context) {
    return ExpansionTile(
      controller: controller,
      onExpansionChanged: onExpansionChanged,
      leading: const Icon(Icons.tune_rounded),
      title: const Text('手动连接与诊断'),
      shape: const Border(),
      collapsedShape: const Border(),
      childrenPadding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
      expandedCrossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // 不固定宽度，大字号时按比例换行而非截断。
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              flex: 3,
              child: TextField(
                controller: host,
                onChanged: (_) => onEdited(),
                keyboardType: TextInputType.url,
                decoration: InputDecoration(
                  labelText: isWindows ? '手机 IP 地址' : '电脑地址',
                  border: const OutlineInputBorder(),
                ),
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              flex: 2,
              child: TextField(
                controller: port,
                onChanged: (_) => onEdited(),
                keyboardType: TextInputType.number,
                decoration: const InputDecoration(
                  labelText: '端口',
                  border: OutlineInputBorder(),
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        Align(
          alignment: AlignmentDirectional.centerStart,
          child: OutlinedButton.icon(
            onPressed: onConnect,
            icon: const Icon(Icons.link_rounded),
            label: const Text('连接此地址'),
          ),
        ),
        const SizedBox(height: 12),
        for (final line in diagnostics)
          Text(line, style: Theme.of(context).textTheme.bodySmall),
      ],
    );
  }
}

/// 可用逻辑宽度 ≥900 时两列：左列状态与主操作，右列连接、音频与诊断。
class DashboardLayout extends StatelessWidget {
  const DashboardLayout({
    super.key,
    required this.primary,
    required this.secondary,
  });
  final Widget primary;
  final List<Widget> secondary;

  @override
  Widget build(BuildContext context) {
    const padding = EdgeInsets.fromLTRB(20, 8, 20, 32);
    final details = Card.outlined(
      margin: EdgeInsets.zero,
      clipBehavior: Clip.antiAlias,
      child: Column(children: secondary),
    );
    return LayoutBuilder(
      builder: (context, constraints) {
        if (constraints.maxWidth >= 900) {
          return Row(
            key: twoColumnKey,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: ListView(
                  padding: padding.copyWith(right: 10),
                  children: [primary],
                ),
              ),
              Expanded(
                child: ListView(
                  padding: padding.copyWith(left: 10),
                  children: [details],
                ),
              ),
            ],
          );
        }
        return Align(
          alignment: Alignment.topCenter,
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 640),
            child: ListView(
              key: singleColumnKey,
              padding: padding,
              children: [primary, const SizedBox(height: 14), details],
            ),
          ),
        );
      },
    );
  }
}
