import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/foundation.dart';

import 'audio_settings_sheet.dart';
import 'dashboard_widgets.dart';
import 'platform_bridge.dart';
import 'window_attention.dart';

void main() => runApp(const MicBridgeApp());

/// 青绿种子色生成浅/深两套；fromSeed 的 tertiary 偏蓝，改用琥珀色调表示等待批准。
ThemeData appTheme(Brightness brightness) {
  final amber = ColorScheme.fromSeed(
    seedColor: const Color(0xFFF59E0B),
    brightness: brightness,
  );
  final scheme =
      ColorScheme.fromSeed(
        seedColor: const Color(0xFF14B8A6),
        brightness: brightness,
      ).copyWith(
        tertiary: amber.primary,
        onTertiary: amber.onPrimary,
        tertiaryContainer: amber.primaryContainer,
        onTertiaryContainer: amber.onPrimaryContainer,
      );
  return ThemeData(
    colorScheme: scheme,
    useMaterial3: true,
    // Windows 显式使用微软雅黑 UI，避免中文回退到日文字形。
    fontFamily: defaultTargetPlatform == TargetPlatform.windows
        ? 'Microsoft YaHei UI'
        : null,
  );
}

class MicBridgeApp extends StatelessWidget {
  const MicBridgeApp({super.key, this.platform});
  final MicPlatform? platform;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'MicBridge',
      debugShowCheckedModeBanner: false,
      theme: appTheme(Brightness.light),
      darkTheme: appTheme(Brightness.dark),
      themeMode: ThemeMode.system,
      home: VoiceDashboard(platform: platform),
    );
  }
}

class VoiceDashboard extends StatefulWidget {
  const VoiceDashboard({super.key, this.platform});
  final MicPlatform? platform;

  @override
  State<VoiceDashboard> createState() => _VoiceDashboardState();
}

class _VoiceDashboardState extends State<VoiceDashboard> {
  late final _platform = widget.platform ?? createMicPlatform();
  bool get _isWindows => defaultTargetPlatform == TargetPlatform.windows;
  bool get _isAndroid => defaultTargetPlatform == TargetPlatform.android;
  final _host = TextEditingController();
  final _port = TextEditingController(text: '17891');
  final _manual = ExpansibleController();
  bool _manualTouched = false;
  bool _syncingManual = false;
  StreamSubscription<MicSnapshot>? _subscription;
  StreamSubscription<List<ComputerEndpoint>>? _computerSubscription;
  StreamSubscription<PendingPairing>? _pairingSubscription;
  final _shownPairingRequests = <String>{};
  PendingPairing? _pendingRequest;
  DialogRoute<bool>? _pairingRoute;
  final _withdrawnPairingRoutes = <DialogRoute<bool>>{};
  String? _activeRequestId;
  bool _pairingScheduled = false;
  Timer? _pairingExpiry;
  Timer? _statusTimer;
  bool _pollingStatus = false;
  int _stateRevision = 0;
  ComputerEndpoint? _selectedComputer;
  MicSnapshot _snapshot = const MicSnapshot(state: MicState.disconnected);
  List<InputDevice> _inputs = const [];
  int? _selectedInputId;
  List<ComputerEndpoint> _computers = const [];
  bool _busy = false;
  // 本地错误；较新的成功或错误快照到达时清除，见 _acceptSnapshot。
  String _connectionError = '';
  // 错误来自轮询/刷新读取 status 时为 true；下一次 status 读取成功即清除。
  bool _statusError = false;
  // 用户是否手动编辑过地址；未编辑时主按钮优先连接已发现的已记住设备。
  bool _hostEdited = false;
  bool _addressInitialized = false;
  bool _loadingInputs = false;
  bool _inputsRefreshPending = false;

  @override
  void initState() {
    super.initState();
    _subscription = _platform.states.listen(
      _acceptSnapshot,
      onError: _showError,
    );
    _pairingSubscription = _platform.pairingRequests.listen((request) {
      _stateRevision++;
      _pendingRequest = request;
      _schedulePairingDialog();
    }, onError: _showError);
    _computerSubscription = _platform.computers.listen((computers) {
      if (!mounted) return;
      setState(() => _computers = computers);
      _syncManual();
    });
    unawaited(
      _platform.discoverComputers().catchError((Object error) {
        _showError(error);
      }),
    );
    _refresh();
    if (_isWindows) {
      _statusTimer = Timer.periodic(const Duration(seconds: 2), (_) {
        unawaited(_pollStatus());
      });
    }
    WidgetsBinding.instance.addPostFrameCallback((_) => _syncManual());
  }

  @override
  void dispose() {
    _subscription?.cancel();
    _computerSubscription?.cancel();
    _pairingSubscription?.cancel();
    _pairingExpiry?.cancel();
    _statusTimer?.cancel();
    if (_pairingRoute != null) unawaited(stopFlashing());
    _host.dispose();
    _port.dispose();
    _manual.dispose();
    _platform.dispose();
    super.dispose();
  }

  void _showError(Object error) => _setError('$error');

  void _setError(String text, {bool fromStatus = false}) {
    if (!mounted) return;
    if (fromStatus && _connectionError.isNotEmpty && !_statusError) return;
    setState(() {
      _connectionError = text;
      _statusError = fromStatus;
    });
  }

  /// Android 未连接且没有发现项时展开手动输入；用户展开/收起或编辑后不再自动改变。
  void _syncManual() {
    if (!mounted || _manualTouched) return;
    _syncingManual = true;
    if (_isAndroid && !_snapshot.connected && _computers.isEmpty) {
      _manual.expand();
    } else {
      _manual.collapse();
    }
    _syncingManual = false;
  }

  void _openManual() {
    _manualTouched = true;
    _manual.expand();
  }

  void _acceptSnapshot(MicSnapshot snapshot) {
    if (!mounted) return;
    _stateRevision++;
    final previous = _snapshot;
    // 只有发生变化的成功/错误快照才比本地错误新；轮询重复同一快照不清除本地错误，
    // 未连接快照也不清除，因为它不说明失败已恢复。
    final newer =
        snapshot.state != previous.state ||
        snapshot.connected != previous.connected ||
        snapshot.detail != previous.detail;
    setState(() {
      _snapshot = snapshot;
      if (newer && (snapshot.connected || snapshot.state == MicState.error)) {
        _connectionError = '';
        _statusError = false;
      }
      if (_isAndroid) {
        _selectedInputId = snapshot.selectedInputId;
        if (!_addressInitialized && snapshot.lastHost.isNotEmpty) {
          if (_host.text.isEmpty && _port.text == '17891') {
            _host.text = snapshot.lastHost;
            _port.text = snapshot.lastPort.toString();
          }
          _addressInitialized = true;
        }
      }
    });
    _syncManual();
    if (_isAndroid) unawaited(_refreshInputs());
    _pendingRequest = snapshot.pendingPairing;
    final route = _pairingRoute;
    if (route != null && _activeRequestId != _pendingRequest?.requestId) {
      _withdrawnPairingRoutes.add(route);
      route.navigator?.removeRoute(route);
    }
    _schedulePairingDialog();
  }

  void _schedulePairingDialog() {
    if (!mounted || !_isWindows || _pairingScheduled) return;
    _pairingScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _pairingScheduled = false;
      if (mounted) unawaited(_showPairingDialog());
    });
    WidgetsBinding.instance.ensureVisualUpdate();
  }

  Future<void> _showPairingDialog() async {
    final request = _pendingRequest;
    if (_pairingRoute != null ||
        request == null ||
        _shownPairingRequests.contains(request.requestId)) {
      return;
    }
    _shownPairingRequests.add(request.requestId);
    if (!request.expiresAt.isAfter(DateTime.now())) return;
    final route = DialogRoute<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text('允许 ${request.deviceName} 连接？'),
        content: const Text('仅在可信家庭局域网内允许你认识的设备。允许后会记住此设备，以后自动连接。'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('拒绝'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('允许'),
          ),
        ],
      ),
    );
    _pairingRoute = route;
    _activeRequestId = request.requestId;
    _pairingExpiry = Timer(request.expiresAt.difference(DateTime.now()), () {
      _withdrawnPairingRoutes.add(route);
      route.navigator?.removeRoute(route);
    });
    // 去重与过期检查之后才提醒，重复事件/轮询快照不会重复闪烁。
    unawaited(flashPairing());
    final allowed = await Navigator.of(context).push(route) ?? false;
    unawaited(stopFlashing());
    _pairingExpiry?.cancel();
    _pairingRoute = null;
    _activeRequestId = null;
    final withdrawn = _withdrawnPairingRoutes.remove(route);
    if (!mounted) return;
    if (withdrawn || !request.expiresAt.isAfter(DateTime.now())) {
      _schedulePairingDialog();
      return;
    }
    try {
      // A connect operation may still be waiting for this decision; do not use
      // the dashboard's busy guard here.
      await _platform.decidePairing(
        request.requestId,
        allow: allowed && request.expiresAt.isAfter(DateTime.now()),
      );
      await _refresh();
    } catch (error) {
      _showError(error);
    }
    _schedulePairingDialog();
  }

  Future<void> _forgetDevice() => _run(() async {
    await _platform.forgetDevice();
    await _refresh();
  });

  Future<void> _readStatus() async {
    final revision = _stateRevision;
    try {
      final status = await _platform.status();
      if (!mounted || revision != _stateRevision) return;
      // 读取恢复只清除读取错误，不清除用户操作错误。
      if (_statusError) {
        setState(() {
          _connectionError = '';
          _statusError = false;
        });
      }
      _acceptSnapshot(status);
    } catch (error) {
      // 较新的原生事件同样使旧读取失败失效。
      if (mounted && revision == _stateRevision) {
        _setError('$error', fromStatus: true);
      }
    }
  }

  Future<void> _pollStatus() async {
    if (_pollingStatus || !mounted) return;
    _pollingStatus = true;
    try {
      await _readStatus();
    } catch (error) {
      _setError('$error', fromStatus: true);
    } finally {
      _pollingStatus = false;
    }
  }

  Future<void> _refresh() async {
    try {
      await _readStatus();
      await _refreshInputs();
    } catch (error) {
      _setError('刷新状态失败：$error', fromStatus: true);
    }
  }

  Future<void> _refreshInputs() async {
    if (_loadingInputs) {
      _inputsRefreshPending = true;
      return;
    }
    _loadingInputs = true;
    try {
      do {
        _inputsRefreshPending = false;
        final inputs = await _platform.inputs();
        // A device event can arrive while enumeration is in flight. Do not
        // clear its newer selection using the older list; enumerate again.
        if (mounted && !_inputsRefreshPending) {
          setState(() {
            _inputs = inputs;
            if (!inputs.any((device) => device.id == _selectedInputId)) {
              _selectedInputId = null;
            }
          });
        }
      } while (mounted && _inputsRefreshPending);
    } catch (error) {
      _showError(error);
    } finally {
      _loadingInputs = false;
    }
  }

  Future<void> _run(Future<void> Function() operation) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _connectionError = '';
      _statusError = false;
    });
    try {
      await operation();
    } catch (error) {
      _setError('$error');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _connect() => _run(() {
    final host = _host.text.trim();
    final port = int.tryParse(_port.text.trim());
    if (host.isEmpty ||
        host.length > 255 ||
        host.contains(RegExp(r'\s')) ||
        port == null ||
        port < 1 ||
        port > 65535) {
      throw const FormatException('请输入有效的电脑地址和端口');
    }
    final selected = _selectedComputer;
    return _platform.connect(
      host: host,
      port: port,
      expectedDeviceId:
          selected != null &&
              selected.host == host &&
              selected.port == port &&
              selected.deviceId.isNotEmpty
          ? selected.deviceId
          : null,
    );
  });

  Future<void> _start() => _run(_platform.start);
  Future<void> _stop() => _run(_platform.stop);

  Future<void> _openAudioSettings(MicAudioSettingsPlatform settings) => _run(
    () => showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      builder: (_) => AudioSettingsSheet(
        load: settings.audioSettings,
        save: settings.saveAudioSettings,
      ),
    ),
  );

  void _selectComputer(ComputerEndpoint computer) {
    setState(() {
      _selectedComputer = computer;
      _addressInitialized = true;
      _host.text = computer.host;
      _port.text = computer.port.toString();
      _connectionError = '';
      _statusError = false;
    });
  }

  /// 两端发现条目都复用同一连接路径，保留对应 expectedDeviceId。
  void _connectPeer(ComputerEndpoint peer) {
    _selectComputer(peer);
    unawaited(_connect());
  }

  /// 主按钮的连接：地址未被用户编辑且发现了已记住设备时连接该条目（带身份，适应换 IP）；
  /// 否则连接已填地址；地址为空时展开手动输入。
  void _connectDefault() {
    final trustedId = _snapshot.trustedDevice?.deviceId;
    final trustedPeer = _computers
        .where((peer) => peer.deviceId.isNotEmpty && peer.deviceId == trustedId)
        .firstOrNull;
    if (!_hostEdited && trustedPeer != null) return _connectPeer(trustedPeer);
    if (_host.text.trim().isNotEmpty) return unawaited(_connect());
    _openManual();
  }

  Future<void> _selectInput(InputDevice device) => _run(() async {
    // 面板关闭与命令提交之间也可能收到设备移除事件。
    if (!_inputs.any((input) => input.id == device.id)) return;
    final revision = _stateRevision;
    await _platform.selectInput(device.id);
    if (mounted &&
        revision == _stateRevision &&
        _inputs.any((input) => input.id == device.id)) {
      setState(() => _selectedInputId = device.id);
    }
  });

  @override
  Widget build(BuildContext context) {
    final canStart =
        _snapshot.connected &&
        (_snapshot.state == MicState.idle ||
            _snapshot.state == MicState.error) &&
        !_busy &&
        (_isWindows || _selectedInputId != null);
    final canStop =
        !_busy &&
        (_snapshot.state == MicState.streaming ||
            _snapshot.state == MicState.starting);
    final awaitingApproval =
        _snapshot.trustState == 'pairing' || _snapshot.pendingPairing != null;
    final action = primaryActionFor(
      _snapshot,
      awaitingApproval: awaitingApproval,
    );
    final onAction = switch (action) {
      DashboardAction.connect ||
      DashboardAction.reconnect => _busy ? null : _connectDefault,
      DashboardAction.start ||
      DashboardAction.retry => canStart ? _start : null,
      DashboardAction.stop => canStop ? _stop : null,
      DashboardAction.waitForApproval || DashboardAction.stopping => null,
    };
    // 唯一错误位置：本地错误只在没被更新快照取代时存在，否则展示当前错误快照。
    final error = _connectionError.isNotEmpty
        ? describeError(_connectionError)
        : _snapshot.state == MicState.error
        ? describeError(_snapshot.detail)
        : null;
    final showPeers = !_snapshot.connected && _computers.isNotEmpty;
    return Scaffold(
      appBar: AppBar(
        title: const Text('MicBridge'),
        actions: [
          IconButton(
            onPressed: _busy ? null : _refresh,
            icon: const Icon(Icons.refresh),
            tooltip: '刷新',
          ),
          if (_isAndroid)
            PopupMenuButton<void>(
              tooltip: '更多',
              itemBuilder: (_) => [
                PopupMenuItem<void>(
                  enabled: !_busy,
                  // 权限异常走唯一错误位置并用统一兜底，不成为未处理异常。
                  onTap: () => unawaited(
                    _platform.requestPermissions().catchError(_showError),
                  ),
                  child: const ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: Icon(Icons.security_rounded),
                    title: Text('权限检查'),
                    subtitle: Text('麦克风、蓝牙和通知'),
                  ),
                ),
              ],
            ),
        ],
      ),
      body: SafeArea(
        top: false,
        child: DashboardLayout(
          primary: StatusPanel(
            snapshot: _snapshot,
            isWindows: _isWindows,
            awaitingApproval: awaitingApproval,
            hasPeers: _computers.isNotEmpty,
            action: action,
            onAction: onAction,
            error: error,
            children: [
              if (showPeers)
                PeerList(
                  peers: _computers,
                  isWindows: _isWindows,
                  onSelect: _busy ? null : _connectPeer,
                ),
            ],
          ),
          secondary: [
            DeviceSummary(
              snapshot: _snapshot,
              isWindows: _isWindows,
              onForget: _busy ? null : _forgetDevice,
            ),
            const Divider(height: 1),
            if (_isWindows)
              ListTile(
                leading: const Icon(Icons.speaker_rounded),
                title: const Text('音频输出'),
                subtitle: Text(
                  '${_snapshot.audioDevice.isEmpty ? "VB-Cable 等待打开" : _snapshot.audioDevice}\n麦克风请在手机端选择',
                ),
                isThreeLine: true,
              )
            else
              MicrophoneSummary(
                inputs: _inputs,
                selectedId: _selectedInputId,
                detail: _snapshot.inputSelectionDetail,
                onSelect: _busy
                    ? null
                    : (device) => unawaited(_selectInput(device)),
              ),
            if (_platform case final MicAudioSettingsPlatform settings
                when _isAndroid)
              ListTile(
                key: const ValueKey('audioSettingsEntry'),
                leading: const Icon(Icons.tune_rounded),
                title: const Text('麦克风设置'),
                trailing: const Icon(Icons.chevron_right_rounded),
                enabled: !_busy,
                onTap: _busy
                    ? null
                    : () => unawaited(_openAudioSettings(settings)),
              ),
            const Divider(height: 1),
            ManualSection(
              controller: _manual,
              isWindows: _isWindows,
              host: _host,
              port: _port,
              onExpansionChanged: (_) {
                if (!_syncingManual) _manualTouched = true;
              },
              onEdited: () {
                _addressInitialized = true;
                _manualTouched = true;
                _hostEdited = true;
              },
              onConnect: _busy ? null : () => unawaited(_connect()),
              diagnostics: [
                'TCP 17891 · 发现 UDP 17893',
                if (_isWindows)
                  '音频传输：${_snapshot.audioTransport.toUpperCase()} · 丢帧：${_snapshot.droppedAudioFrames}',
              ],
            ),
          ],
        ),
      ),
    );
  }
}
