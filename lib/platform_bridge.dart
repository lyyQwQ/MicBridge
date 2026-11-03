import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

enum MicState { disconnected, idle, starting, streaming, stopping, error }

MicState micStateFromWire(String value) {
  final normalized = value == 'connected_idle' ? 'idle' : value;
  return MicState.values.firstWhere(
    (state) => state.name == normalized,
    orElse: () => MicState.error,
  );
}

class TrustedDevice {
  const TrustedDevice({
    required this.deviceId,
    required this.deviceName,
    required this.pairingId,
  });

  final String deviceId;
  final String deviceName;
  final String pairingId;

  factory TrustedDevice.fromMap(Map<Object?, Object?> value) => TrustedDevice(
    deviceId: value['deviceId'] as String,
    deviceName: value['deviceName'] as String,
    pairingId: value['pairingId'] as String,
  );
}

class PendingPairing {
  const PendingPairing({
    required this.requestId,
    required this.deviceId,
    required this.deviceName,
    required this.expiresAt,
  });

  final String requestId;
  final String deviceId;
  final String deviceName;
  final DateTime expiresAt;

  factory PendingPairing.fromMap(Map<Object?, Object?> value) => PendingPairing(
    requestId: value['requestId'] as String,
    deviceId: value['deviceId'] as String,
    deviceName: value['deviceName'] as String,
    expiresAt: DateTime.parse(value['expiresAt'] as String),
  );
}

class MicSnapshot {
  const MicSnapshot({
    required this.state,
    this.device = '',
    this.detail = '',
    this.connected = false,
    this.trustedDevice,
    this.pendingPairing,
    this.trustState = 'unpaired',
    this.audioDevice = '',
    this.audioTransport = 'tcp',
    this.droppedAudioFrames = 0,
    this.listenerActive = false,
    this.selectedInputId,
    this.inputSelectionDetail = '',
    this.lastHost = '',
    this.lastPort = 17891,
  });

  final MicState state;
  final String device;
  final String detail;
  final bool connected;
  final TrustedDevice? trustedDevice;
  final PendingPairing? pendingPairing;
  final String trustState;
  final String audioDevice;
  final String audioTransport;
  final int droppedAudioFrames;
  final bool listenerActive;
  final int? selectedInputId;
  final String inputSelectionDetail;
  final String lastHost;
  final int lastPort;

  factory MicSnapshot.fromMap(Map<Object?, Object?> value) => MicSnapshot(
    state: micStateFromWire(value['state'] as String? ?? 'error'),
    device: value['device'] as String? ?? '',
    detail: value['detail'] as String? ?? '',
    connected: value['connected'] as bool? ?? false,
    trustedDevice: value['trustedDevice'] is Map
        ? TrustedDevice.fromMap(
            Map<Object?, Object?>.from(value['trustedDevice'] as Map),
          )
        : null,
    pendingPairing: value['pendingPairing'] is Map
        ? PendingPairing.fromMap(
            Map<Object?, Object?>.from(value['pendingPairing'] as Map),
          )
        : null,
    trustState: value['trustState'] as String? ?? 'unpaired',
    audioDevice: value['audioDevice'] as String? ?? '',
    audioTransport: value['audioTransport'] as String? ?? 'tcp',
    droppedAudioFrames: value['droppedAudioFrames'] as int? ?? 0,
    listenerActive: value['listenerActive'] as bool? ?? false,
    selectedInputId: value['selectedInputId'] as int?,
    inputSelectionDetail: value['inputSelectionDetail'] as String? ?? '',
    lastHost: value['lastHost'] as String? ?? '',
    lastPort: value['lastPort'] as int? ?? 17891,
  );
}

abstract interface class MicPlatform {
  Stream<MicSnapshot> get states;
  Stream<List<ComputerEndpoint>> get computers;
  Stream<PendingPairing> get pairingRequests;
  Future<MicSnapshot> status();
  Future<List<InputDevice>> inputs();
  Future<void> selectInput(int id);
  Future<void> discoverComputers();
  Future<void> connect({
    String host = '127.0.0.1',
    int port = 17891,
    String? expectedDeviceId,
  });
  Future<void> decidePairing(String requestId, {required bool allow});
  Future<void> forgetDevice();
  Future<void> start();
  Future<void> stop();
  Future<void> requestPermissions();
  Future<void> dispose();
}

class InputDevice {
  const InputDevice({
    required this.id,
    required this.name,
    required this.address,
    required this.type,
  });
  final int id;
  final String name;
  final String address;
  final int type;
}

class ComputerEndpoint {
  const ComputerEndpoint({
    required this.name,
    required this.host,
    required this.port,
    this.deviceId = '',
    this.role = 'windows',
    this.sources = const [],
    this.sinks = const [],
    this.transports = const [],
  });

  final String name;
  final String host;
  final int port;
  final String deviceId;
  final String role;
  final List<String> sources;
  final List<String> sinks;
  final List<String> transports;
}

MicPlatform createMicPlatform() =>
    Platform.isAndroid ? _AndroidMicPlatform() : _WindowsMicPlatform();

typedef WindowsProcessStarter = Future<Process> Function(
  String path,
  List<String> arguments,
);

@visibleForTesting
MicPlatform createWindowsMicPlatformForTesting({
  required WindowsProcessStarter startProcess,
}) => _WindowsMicPlatform(startProcess: startProcess);

class _WindowsMicPlatform implements MicPlatform {
  _WindowsMicPlatform({WindowsProcessStarter? startProcess})
    : _startProcess =
          startProcess ?? ((path, arguments) => Process.start(path, arguments));

  final WindowsProcessStarter _startProcess;
  Process? _helper;
  Future<void>? _initializing;
  StreamSubscription<String>? _lines;
  final _stateController = StreamController<MicSnapshot>.broadcast();
  final _computerController =
      StreamController<List<ComputerEndpoint>>.broadcast();
  final _pairingController = StreamController<PendingPairing>.broadcast();
  final _pending = <String, Completer<Map<String, Object?>>>{};
  int _nextId = 0;
  int _generation = 0;
  bool _disposed = false;

  @override
  Stream<MicSnapshot> get states => _stateController.stream;
  @override
  Stream<List<ComputerEndpoint>> get computers => _computerController.stream;
  @override
  Stream<PendingPairing> get pairingRequests => _pairingController.stream;

  Future<void> _ensureConnected() {
    if (_disposed) return Future.error(StateError('Windows 桥接已释放'));
    if (_helper != null) return Future.value();
    return _initializing ??= _launch(++_generation);
  }

  Future<void> _launch(int generation) async {
    try {
      final packaged =
          '${File(Platform.resolvedExecutable).parent.path}/core/MicBridge.App.exe';
      final path =
          Platform.environment['MICBRIDGE_CORE_PATH'] ??
          (File(packaged).existsSync()
              ? packaged
              : '${Directory.current.path}/desktop/src/MicBridge.App/bin/Release/net10.0-windows/MicBridge.App.exe');
      final helper = await Future<Process>.sync(
        () => _startProcess(path, const []),
      );
      unawaited(
        helper.stdin.done.then<void>(
          (_) => _failed(generation, StateError('Windows 核心输入已结束')),
          onError: (Object error) => _failed(generation, error),
        ),
      );
      if (_disposed) {
        _closeAndKill(helper);
        throw StateError('Windows 桥接已释放');
      }
      _helper = helper;
      _lines = helper.stdout
          .transform(utf8.decoder)
          .transform(const LineSplitter())
          .listen(
            (line) {
              if (generation == _generation) _handleLine(line, generation);
            },
            onError: (Object error) => _failed(generation, error),
            onDone: () => _failed(generation, StateError('Windows 核心输出已结束')),
          );
      helper.stderr.drain<void>().ignore();
      unawaited(
        helper.exitCode.then<void>(
          (code) => _failed(generation, StateError('Windows 核心已退出 ($code)')),
          onError: (Object error) => _failed(generation, error),
        ),
      );
    } finally {
      if (generation == _generation) _initializing = null;
    }
  }

  void _closeAndKill(Process helper) {
    try {
      helper.stdin.close().timeout(const Duration(seconds: 1)).ignore();
    } catch (_) {
      // 同步关闭异常也不能阻止终止进程。
    } finally {
      helper.kill();
    }
  }

  void _failed(int generation, Object error) {
    if (generation != _generation) return;
    ++_generation;
    for (final pending in _pending.values) {
      if (!pending.isCompleted) pending.completeError(error);
    }
    _pending.clear();
    final helper = _helper;
    _helper = null;
    _initializing = null;
    _lines?.cancel().ignore();
    _lines = null;
    if (!_disposed) {
      if (helper != null) _closeAndKill(helper);
      _stateController.add(
        MicSnapshot(state: MicState.error, detail: '$error'),
      );
    }
  }

  void _handleLine(String line, int generation) {
    try {
      final value = Map<String, Object?>.from(jsonDecode(line) as Map);
      if (value['event'] == 'state') {
        _stateController.add(
          MicSnapshot.fromMap(
            Map<Object?, Object?>.from(value['snapshot'] as Map),
          ),
        );
      } else if (value['event'] == 'pairing_request') {
        _pairingController.add(
          PendingPairing.fromMap(
            Map<Object?, Object?>.from(value['request'] as Map),
          ),
        );
      } else if (value['event'] == 'peers') {
        _publishPeers(value['peers']);
      } else {
        final id = value['id'];
        if (id is String) _pending.remove(id)?.complete(value);
      }
    } catch (error) {
      _failed(generation, error);
    }
  }

  void _publishPeers(Object? raw) {
    _computerController.add(
      (raw as List? ?? const [])
          .cast<Map>()
          .where((peer) => peer['role'] == 'android')
          .map(
            (peer) => ComputerEndpoint(
              name: peer['name'] as String,
              host: peer['host'] as String,
              port: peer['port'] as int,
              deviceId: peer['deviceId'] as String? ?? '',
              role: 'android',
            ),
          )
          .toList(),
    );
  }

  Future<Map<String, Object?>> _command(
    String command, [
    Map<String, Object?> args = const {},
  ]) async {
    await _ensureConnected();
    if (_disposed) throw StateError('Windows 桥接已释放');
    final helper = _helper;
    if (helper == null) throw StateError('Windows 核心未启动');
    return _send(helper, command, args);
  }

  Future<Map<String, Object?>> _send(
    Process helper,
    String command,
    Map<String, Object?> args,
  ) async {
    final generation = _generation;
    final id = '${_nextId++}';
    final completer = Completer<Map<String, Object?>>();
    _pending[id] = completer;
    try {
      try {
        helper.stdin.writeln(
          jsonEncode({'id': id, 'command': command, ...args}),
        );
      } catch (error) {
        _failed(generation, error);
      }
      final result = await completer.future.timeout(
        const Duration(seconds: 12),
      );
      if (result['ok'] != true) {
        throw StateError(result['message'] as String? ?? '操作失败');
      }
      return result;
    } finally {
      _pending.remove(id);
    }
  }

  @override
  Future<MicSnapshot> status() async => MicSnapshot.fromMap(
    Map<Object?, Object?>.from((await _command('status'))['snapshot'] as Map),
  );
  @override
  Future<List<InputDevice>> inputs() async => const [];
  @override
  Future<void> selectInput(int id) async =>
      throw UnsupportedError('请在 Android 端选择麦克风');
  @override
  Future<void> discoverComputers() async =>
      _publishPeers((await _command('discover'))['peers']);
  @override
  Future<void> connect({
    String host = '127.0.0.1',
    int port = 17891,
    String? expectedDeviceId,
  }) async => _command('connect', {
    'host': host,
    'port': port,
    'expectedDeviceId': ?expectedDeviceId,
  });
  @override
  Future<void> decidePairing(String requestId, {required bool allow}) async =>
      _command('pairing_decide', {
        'requestId': requestId,
        'decision': allow ? 'allow' : 'deny',
      });
  @override
  Future<void> forgetDevice() async => _command('forget_device');
  @override
  Future<void> start() async => _command('start');
  @override
  Future<void> stop() async => _command('stop');
  @override
  Future<void> requestPermissions() async {}
  @override
  Future<void> dispose() async {
    if (_disposed) return;
    _disposed = true;
    final helper = _helper;
    try {
      if (helper != null) {
        try {
          await _send(
            helper,
            'shutdown',
            const {},
          ).timeout(const Duration(seconds: 4));
        } catch (_) {
          // 无响应或断管后继续释放进程。
        } finally {
          try {
            await helper.stdin.close().timeout(const Duration(seconds: 1));
          } catch (_) {
            // 关闭失败不跳过退出等待。
          } finally {
            try {
              await helper.exitCode.timeout(const Duration(seconds: 5));
            } catch (_) {
              helper.kill();
            }
          }
        }
      }
    } finally {
      ++_generation;
      _helper = null;
      _initializing = null;
      for (final pending in _pending.values) {
        if (!pending.isCompleted) {
          pending.completeError(StateError('Windows 桥接已释放'));
        }
      }
      _pending.clear();
      await _lines?.cancel();
      _lines = null;
      await _stateController.close();
      await _computerController.close();
      await _pairingController.close();
    }
  }
}

class _AndroidMicPlatform implements MicPlatform {
  static const _methods = MethodChannel('micbridge/platform');
  static const _events = EventChannel('micbridge/state');
  static const _discoveryEvents = EventChannel('micbridge/discovery');
  @override
  Stream<PendingPairing> get pairingRequests => const Stream.empty();
  @override
  Stream<MicSnapshot> get states => _events.receiveBroadcastStream().map(
    (event) => MicSnapshot.fromMap(Map<Object?, Object?>.from(event as Map)),
  );
  @override
  Stream<List<ComputerEndpoint>> get computers =>
      _discoveryEvents.receiveBroadcastStream().map((event) {
        final rows = event as List<Object?>;
        return rows
            .map((row) {
              final value = Map<Object?, Object?>.from(row! as Map);
              List<String> strings(Object? raw) => raw is List
                  ? raw.whereType<String>().toList(growable: false)
                  : const [];
              return ComputerEndpoint(
                name: value['name'] as String? ?? 'MicBridge peer',
                host: value['host'] as String? ?? '',
                port: value['port'] as int? ?? 17891,
                deviceId: value['deviceId'] as String? ?? '',
                role: value['role'] as String? ?? 'windows',
                sources: strings(value['sources']),
                sinks: strings(value['sinks']),
                transports: strings(value['transports']),
              );
            })
            .toList(growable: false);
      });

  @override
  Future<MicSnapshot> status() async => MicSnapshot.fromMap(
    Map<Object?, Object?>.from(await _methods.invokeMethod('status') as Map),
  );

  @override
  Future<List<InputDevice>> inputs() async {
    final rows =
        await _methods.invokeMethod<List<Object?>>('devices') ?? const [];
    return rows
        .map((row) {
          final value = Map<Object?, Object?>.from(row! as Map);
          return InputDevice(
            id: value['id'] as int? ?? -1,
            name: value['name'] as String? ?? 'Unknown',
            address: value['address'] as String? ?? '',
            type: value['type'] as int? ?? 0,
          );
        })
        .toList(growable: false);
  }

  @override
  Future<void> selectInput(int id) =>
      _methods.invokeMethod<void>('selectInput', {'id': id});

  @override
  Future<void> connect({
    String host = '127.0.0.1',
    int port = 17891,
    String? expectedDeviceId,
  }) async => _methods.invokeMethod<void>('connect', {
    'host': host,
    'port': port,
    'expectedDeviceId': ?expectedDeviceId,
  });

  @override
  Future<void> decidePairing(String requestId, {required bool allow}) async =>
      throw UnsupportedError('请在 Windows 端允许连接');

  @override
  Future<void> forgetDevice() => _methods.invokeMethod<void>('forgetDevice');

  @override
  Future<void> start() async => _methods.invokeMethod<void>('start');

  @override
  Future<void> stop() async => _methods.invokeMethod<void>('stop');

  @override
  Future<void> requestPermissions() async =>
      _methods.invokeMethod<void>('requestPermissions');
  @override
  Future<void> discoverComputers() =>
      _methods.invokeMethod<void>('discoverComputers');

  @override
  Future<void> dispose() async {}
}
