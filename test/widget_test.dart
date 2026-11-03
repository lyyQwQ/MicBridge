import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/dashboard_widgets.dart';
import 'package:micbridge/main.dart';
import 'package:micbridge/platform_bridge.dart';

const trustedDevice = TrustedDevice(
  deviceId: 'device-1',
  deviceName: 'Home device',
  pairingId: 'pairing-1',
);

PendingPairing request(
  String id, {
  Duration lifetime = const Duration(minutes: 1),
}) => PendingPairing(
  requestId: id,
  deviceId: 'phone-1',
  deviceName: 'My-Phone',
  expiresAt: DateTime.now().toUtc().add(lifetime),
);

void testDashboard(
  String description,
  Future<void> Function(WidgetTester) callback,
) {
  testWidgets(description, (tester) async {
    final previousPlatform = debugDefaultTargetPlatformOverride;
    try {
      await callback(tester);
    } finally {
      try {
        await tester.pumpWidget(const SizedBox());
        await tester.pumpAndSettle();
      } finally {
        // Restore before the binding verifies invariants, including when the
        // test body or widget cleanup throws. addTearDown runs too late.
        debugDefaultTargetPlatformOverride = previousPlatform;
        tester.view.resetPhysicalSize();
        tester.view.resetDevicePixelRatio();
        tester.view.resetPadding();
        tester.platformDispatcher.clearPlatformBrightnessTestValue();
        tester.platformDispatcher.clearTextScaleFactorTestValue();
      }
    }
  });
}

/// [size] 为逻辑尺寸（DPR=1）。等待批准的 Android 状态含持续进度动画，需传 settle: false 后用有界 pump。
Future<void> mountDashboard(
  WidgetTester tester,
  TestPlatform platform, {
  TargetPlatform target = TargetPlatform.windows,
  Size size = const Size(1400, 1800),
  bool settle = true,
}) async {
  debugDefaultTargetPlatformOverride = target;
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  await tester.pumpWidget(MicBridgeApp(platform: platform));
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    await pumpFor(tester);
  }
}

/// 有界推进，不等待持续动画结束。
Future<void> pumpFor(
  WidgetTester tester, [
  Duration duration = const Duration(milliseconds: 600),
]) async {
  for (
    var elapsed = Duration.zero;
    elapsed < duration;
    elapsed += const Duration(milliseconds: 100)
  ) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

final primaryAction = find.byKey(primaryActionKey);
FilledButton primaryButton(WidgetTester tester) =>
    tester.widget<FilledButton>(primaryAction);
Finder primaryLabeled(String label) =>
    find.descendant(of: primaryAction, matching: find.text(label));
Finder inStatus(Finder finder) =>
    find.descendant(of: find.byKey(statusPanelKey), matching: finder);

Future<void> openManual(WidgetTester tester) async {
  await tester.tap(find.text('手动连接与诊断'));
  await tester.pumpAndSettle();
}

Future<void> forgetFromMenu(WidgetTester tester, String label) async {
  await tester.tap(find.byTooltip('设备操作'));
  await tester.pumpAndSettle();
  await tester.tap(find.text(label));
  await tester.pumpAndSettle();
}

Future<void> openMicrophones(WidgetTester tester) async {
  await tester.tap(find.text('麦克风'));
  await tester.pumpAndSettle();
}

/// 打开麦克风面板读取当前单选值后关闭。
Future<int?> microphoneSelection(WidgetTester tester) async {
  await openMicrophones(tester);
  final value = tester
      .widget<RadioGroup<int>>(find.byType(RadioGroup<int>))
      .groupValue;
  await tester.tapAt(const Offset(10, 10));
  await tester.pumpAndSettle();
  return value;
}

void main() {
  // 过期请求必须与真实宿主一致，即使是拒绝也返回 stale。
  testDashboard('fake host rejects decisions for expired requests', (
    tester,
  ) async {
    final platform = TestPlatform();
    await mountDashboard(tester, platform);
    platform.requests.add(
      request('expired-host', lifetime: const Duration(seconds: 2)),
    );
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
    await expectLater(
      platform.decidePairing('expired-host', allow: false),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          'pairing_request_stale',
        ),
      ),
    );
  });
  test('snapshot decodes trusted and pending device identities and expiry', () {
    final snapshot = MicSnapshot.fromMap({
      'state': 'connected_idle',
      'connected': true,
      'trustState': 'trusted',
      'selectedInputId': 7,
      'lastHost': '192.168.1.x',
      'lastPort': 17895,
      'trustedDevice': {
        'deviceId': 'phone-1',
        'deviceName': 'My-Phone',
        'pairingId': 'pairing-1',
      },
      'pendingPairing': {
        'requestId': 'request-1',
        'deviceId': 'phone-2',
        'deviceName': 'Other phone',
        'expiresAt': '2026-11-02T12:34:56Z',
      },
    });
    expect(snapshot.state, MicState.idle);
    expect(snapshot.trustedDevice?.deviceId, 'phone-1');
    expect(snapshot.trustedDevice?.deviceName, 'My-Phone');
    expect(snapshot.trustedDevice?.pairingId, 'pairing-1');
    expect(snapshot.pendingPairing?.requestId, 'request-1');
    expect(snapshot.pendingPairing?.deviceId, 'phone-2');
    expect(snapshot.pendingPairing?.deviceName, 'Other phone');
    expect(
      snapshot.pendingPairing?.expiresAt,
      DateTime.utc(2026, 11, 2, 12, 34, 56),
    );
    expect(snapshot.trustState, 'trusted');
    expect(snapshot.selectedInputId, 7);
    expect(snapshot.lastHost, '192.168.1.x');
    expect(snapshot.lastPort, 17895);
    final empty = MicSnapshot.fromMap({'state': 'disconnected'});
    expect(empty.trustedDevice, isNull);
    expect(empty.pendingPairing, isNull);
    expect(empty.trustState, 'unpaired');
  });

  testDashboard(
    'desktop capture follows authenticated core state and live listener detection',
    (tester) async {
      final platform = TestPlatform();
      await mountDashboard(tester, platform);
      expect(primaryLabeled('连接手机'), findsOneWidget);
      expect(find.text('开始采集'), findsNothing);
      expect(find.byIcon(Icons.copy), findsNothing);
      platform.emit(
        const MicSnapshot(
          state: MicState.idle,
          connected: true,
          trustedDevice: trustedDevice,
          trustState: 'trusted',
          audioDevice: 'VB-Cable',
          listenerActive: true,
        ),
      );
      await tester.pumpAndSettle();
      expect(primaryLabeled('开始采集'), findsOneWidget);
      expect(primaryButton(tester).onPressed, isNotNull);
      expect(inStatus(find.text('已连接，未采集')), findsOneWidget);
      expect(inStatus(find.text('CABLE Output 录音检测：检测到录音中')), findsOneWidget);
      expect(find.text('Home device'), findsOneWidget);
      platform.emit(
        const MicSnapshot(
          state: MicState.idle,
          connected: true,
          trustedDevice: trustedDevice,
          trustState: 'trusted',
        ),
      );
      await tester.pumpAndSettle();
      expect(inStatus(find.text('CABLE Output 录音检测：未检测到录音')), findsOneWidget);
      platform.emit(
        const MicSnapshot(
          state: MicState.idle,
          connected: true,
          trustedDevice: trustedDevice,
          trustState: 'trusted',
          listenerActive: true,
        ),
      );
      await tester.pumpAndSettle();
      expect(inStatus(find.text('CABLE Output 录音检测：检测到录音中')), findsOneWidget);
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.starts, 1);
      platform.emit(
        const MicSnapshot(state: MicState.streaming, connected: true),
      );
      await tester.pumpAndSettle();
      expect(primaryLabeled('停止采集'), findsOneWidget);
      expect(find.text('开始采集'), findsNothing);
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.stops, 1);
    },
  );

  testDashboard(
    'Android retries capture errors only while connected with an input selected',
    (tester) async {
      final platform = TestPlatform()
        ..availableInputs = const [
          InputDevice(id: 7, name: '智能眼镜', address: 'headset', type: 7),
        ]
        ..current = const MicSnapshot(state: MicState.error, connected: true);
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      expect(primaryLabeled('重试采集'), findsOneWidget);
      expect(primaryButton(tester).onPressed, isNull);
      await openMicrophones(tester);
      await tester.tap(find.text('智能眼镜'));
      await tester.pumpAndSettle();
      expect(primaryButton(tester).onPressed, isNotNull);
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.starts, 1);

      platform.emit(const MicSnapshot(state: MicState.error, connected: false));
      await tester.pumpAndSettle();
      expect(primaryLabeled('重新连接'), findsOneWidget);
      expect(find.text('重试采集'), findsNothing);
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.starts, 1);
      expect(platform.connections, isEmpty);
    },
  );

  testDashboard(
    'Android follows system without an explicit input and shows temporary fallback',
    (tester) async {
      final platform = TestPlatform()
        ..availableInputs = const [
          InputDevice(id: -1, name: '跟随系统', address: '', type: 0),
          InputDevice(id: 7, name: '<glasses>', address: 'headset', type: 7),
        ]
        ..current = const MicSnapshot(
          state: MicState.idle,
          connected: true,
          selectedInputId: -1,
        );
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      expect(find.text('跟随系统'), findsOneWidget);
      expect(await microphoneSelection(tester), -1);
      expect(primaryLabeled('开始采集'), findsOneWidget);
      expect(primaryButton(tester).onPressed, isNotNull);
      platform.emit(
        const MicSnapshot(
          state: MicState.error,
          connected: true,
          selectedInputId: -1,
          inputSelectionDetail: '已保存的设备未连接，暂按系统默认',
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('跟随系统\n已保存的设备未连接，暂按系统默认'), findsOneWidget);
      expect(primaryLabeled('重试采集'), findsOneWidget);
      expect(primaryButton(tester).onPressed, isNotNull);
      platform.emit(
        const MicSnapshot(
          state: MicState.idle,
          connected: true,
          selectedInputId: 7,
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('<glasses>'), findsOneWidget);
      expect(await microphoneSelection(tester), 7);
      expect(find.textContaining('已保存的设备未连接，暂按系统默认'), findsNothing);
    },
  );

  testDashboard(
    'Android refreshes again when a saved input returns during enumeration',
    (tester) async {
      const system = InputDevice(id: -1, name: '跟随系统', address: '', type: 0);
      const headset = InputDevice(
        id: 7,
        name: '<glasses>',
        address: 'headset',
        type: 7,
      );
      final platform = TestPlatform()
        ..availableInputs = const [system]
        ..current = const MicSnapshot(
          state: MicState.idle,
          connected: true,
          selectedInputId: -1,
        );
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      final staleInputs = Completer<List<InputDevice>>();
      platform.inputsWait = staleInputs;
      platform.emit(platform.current);
      await tester.pump();
      platform.availableInputs = const [system, headset];
      platform.emit(
        const MicSnapshot(
          state: MicState.idle,
          connected: true,
          selectedInputId: 7,
        ),
      );
      await tester.pump();
      platform.inputsWait = null;
      staleInputs.complete(const [system]);
      await tester.pumpAndSettle();
      expect(find.text('<glasses>'), findsOneWidget);
      expect(await microphoneSelection(tester), 7);
      expect(primaryButton(tester).onPressed, isNotNull);
    },
  );

  testDashboard(
    'Android restores native input and prefills the last endpoint without replacing edits',
    (tester) async {
      final platform = TestPlatform()
        ..availableInputs = const [
          InputDevice(id: 7, name: '<glasses>', address: 'headset', type: 7),
        ]
        ..current = const MicSnapshot(
          state: MicState.error,
          connected: true,
          selectedInputId: 7,
          lastHost: '192.168.1.x',
          lastPort: 17895,
        );
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      await openManual(tester);
      final host = find.widgetWithText(TextField, '电脑地址');
      final port = find.widgetWithText(TextField, '端口');
      expect(tester.widget<TextField>(host).controller!.text, '192.168.1.x');
      expect(tester.widget<TextField>(port).controller!.text, '17895');
      expect(find.text('<glasses>'), findsOneWidget);
      expect(primaryLabeled('重试采集'), findsOneWidget);
      expect(primaryButton(tester).onPressed, isNotNull);

      await tester.enterText(host, '192.168.1.y');
      await tester.enterText(port, '17900');
      platform.emit(platform.current);
      await tester.pumpAndSettle();
      await tester.tap(find.byTooltip('刷新'));
      await tester.pumpAndSettle();
      expect(tester.widget<TextField>(host).controller!.text, '192.168.1.y');
      expect(tester.widget<TextField>(port).controller!.text, '17900');
    },
  );

  testDashboard(
    'Android keeps address edits made before saved endpoint arrives',
    (tester) async {
      final platform = TestPlatform();
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      final host = find.widgetWithText(TextField, '电脑地址');
      await tester.enterText(host, '192.168.1.y');
      platform.emit(
        const MicSnapshot(
          state: MicState.disconnected,
          lastHost: '192.168.1.x',
          lastPort: 17895,
        ),
      );
      await tester.pumpAndSettle();
      expect(tester.widget<TextField>(host).controller!.text, '192.168.1.y');
      expect(
        tester
            .widget<TextField>(find.widgetWithText(TextField, '端口'))
            .controller!
            .text,
        '17891',
      );
    },
  );

  testDashboard(
    'Android expands manual input only until the user chooses otherwise',
    (tester) async {
      final platform = TestPlatform();
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      expect(find.widgetWithText(TextField, '电脑地址'), findsOneWidget);
      platform.peers.add(const [
        ComputerEndpoint(name: 'Home PC', host: '192.168.1.x', port: 17891),
      ]);
      await tester.pumpAndSettle();
      expect(find.widgetWithText(TextField, '电脑地址'), findsNothing);
      await openManual(tester);
      platform.peers.add(const []);
      await tester.pumpAndSettle();
      platform.peers.add(const [
        ComputerEndpoint(name: 'Home PC', host: '192.168.1.x', port: 17891),
      ]);
      await tester.pumpAndSettle();
      expect(find.widgetWithText(TextField, '电脑地址'), findsOneWidget);
    },
  );

  testDashboard(
    'status hydrates a pending request and duplicates prompt only once',
    (tester) async {
      final pending = request('request-1');
      final platform = TestPlatform()
        ..current = MicSnapshot(
          state: MicState.disconnected,
          trustState: 'pairing',
          pendingPairing: pending,
        );
      await mountDashboard(tester, platform);
      expect(find.text('允许 My-Phone 连接？'), findsOneWidget);
      platform.requests.add(pending);
      platform.emit(platform.current);
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsOneWidget);
      await tester.tap(find.widgetWithText(FilledButton, '允许'));
      await tester.pumpAndSettle();
      expect(platform.decisions, [('request-1', true)]);
      platform.requests.add(pending);
      platform.emit(
        MicSnapshot(state: MicState.disconnected, pendingPairing: pending),
      );
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      expect(platform.decisions, hasLength(1));
    },
  );

  testDashboard(
    'event request can be denied and a new request can be allowed',
    (tester) async {
      final platform = TestPlatform();
      await mountDashboard(tester, platform);
      platform.requests.add(request('first'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(TextButton, '拒绝'));
      await tester.pumpAndSettle();
      expect(platform.decisions, [('first', false)]);
      platform.requests.add(request('second'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, '允许'));
      await tester.pumpAndSettle();
      expect(platform.decisions, [('first', false), ('second', true)]);
    },
  );

  testDashboard('dismissing the dialog sends deny', (tester) async {
    final platform = TestPlatform();
    await mountDashboard(tester, platform);
    platform.requests.add(request('dismissed'));
    await tester.pumpAndSettle();
    await tester.tapAt(const Offset(10, 10));
    await tester.pumpAndSettle();
    expect(find.byType(AlertDialog), findsNothing);
    expect(platform.decisions, [('dismissed', false)]);
  });

  testDashboard('expired or withdrawn requests cannot be approved', (
    tester,
  ) async {
    final platform = TestPlatform();
    await mountDashboard(tester, platform);
    platform.requests.add(
      request('already-expired', lifetime: const Duration(seconds: -1)),
    );
    await tester.pumpAndSettle();
    expect(find.byType(AlertDialog), findsNothing);
    platform.requests.add(
      request('expiring', lifetime: const Duration(seconds: 2)),
    );
    await tester.pumpAndSettle();
    expect(find.byType(AlertDialog), findsOneWidget);
    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
    expect(find.byType(AlertDialog), findsNothing);
    expect(platform.decisions, isEmpty);
    platform.requests.add(request('withdrawn'));
    await tester.pumpAndSettle();
    platform.emit(const MicSnapshot(state: MicState.disconnected));
    await tester.pumpAndSettle();
    expect(find.byType(AlertDialog), findsNothing);
    expect(platform.decisions, isEmpty);
    expect(find.byKey(dashboardErrorKey), findsNothing);
  });

  testDashboard('pairing decision is submitted while connect is waiting', (
    tester,
  ) async {
    final platform = TestPlatform()..connectWait = Completer<void>();
    await mountDashboard(tester, platform);
    await openManual(tester);
    await tester.enterText(
      find.widgetWithText(TextField, '手机 IP 地址'),
      '192.168.1.z',
    );
    await tester.tap(primaryAction);
    await tester.pumpAndSettle();
    expect(platform.connections, [('192.168.1.z', 17891)]);
    platform.requests.add(request('during-connect'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, '允许'));
    await tester.pumpAndSettle();
    expect(platform.decisions, [('during-connect', true)]);
    platform.connectWait!.complete();
    await tester.pumpAndSettle();
  });

  testDashboard(
    'failed decision is visible once and does not prompt the same request again',
    (tester) async {
      final platform = TestPlatform()
        ..decisionError = StateError('approval failed');
      await mountDashboard(tester, platform);
      final pending = request('failed');
      platform.requests.add(pending);
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, '允许'));
      await tester.pumpAndSettle();
      expect(find.text(unknownErrorHint), findsOneWidget);
      expect(find.textContaining('approval failed'), findsNothing);
      platform.requests.add(pending);
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      expect(platform.decisions, hasLength(1));
    },
  );

  for (final target in [TargetPlatform.windows, TargetPlatform.android]) {
    testDashboard(
      '$target forgets the saved peer from the device menu and renders native state',
      (tester) async {
        final label = target == TargetPlatform.windows ? '忘记设备' : '忘记电脑';
        final platform = TestPlatform()
          ..current = const MicSnapshot(
            state: MicState.idle,
            connected: true,
            trustedDevice: trustedDevice,
            trustState: 'trusted',
          );
        await mountDashboard(tester, platform, target: target);
        expect(find.text('Home device'), findsOneWidget);
        await forgetFromMenu(tester, label);
        expect(platform.forgets, 1);
        expect(find.text('Home device'), findsNothing);
        expect(find.text('尚未记住设备'), findsOneWidget);
        expect(find.byTooltip('设备操作'), findsNothing);
        platform.emit(
          const MicSnapshot(
            state: MicState.disconnected,
            trustState: 'repair_required',
          ),
        );
        await tester.pumpAndSettle();
        await forgetFromMenu(tester, label);
        expect(platform.forgets, 2);
        expect(find.text('尚未记住设备'), findsOneWidget);
      },
    );
  }

  testDashboard(
    'Android connects with host and port and explains stale trust',
    (tester) async {
      final platform = TestPlatform();
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      expect(find.byType(TextField), findsNWidgets(2));
      await tester.enterText(
        find.widgetWithText(TextField, '电脑地址'),
        '192.168.1.x',
      );
      await tester.enterText(find.widgetWithText(TextField, '端口'), '17891');
      await tester.tap(primaryLabeled('连接电脑'));
      await tester.pumpAndSettle();
      expect(platform.connections, [('192.168.1.x', 17891)]);
      platform.emit(
        const MicSnapshot(
          state: MicState.disconnected,
          trustedDevice: trustedDevice,
          trustState: 'repair_required',
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('信任记录不一致，请双方忘记设备后重新连接'), findsOneWidget);
      expect(find.byTooltip('设备操作'), findsOneWidget);
    },
  );

  testDashboard('manual input validates address and port on both platforms', (
    tester,
  ) async {
    for (final (target, hostLabel) in [
      (TargetPlatform.windows, '手机 IP 地址'),
      (TargetPlatform.android, '电脑地址'),
    ]) {
      final platform = TestPlatform();
      await mountDashboard(tester, platform, target: target);
      if (target == TargetPlatform.windows) await openManual(tester);
      await tester.enterText(
        find.widgetWithText(TextField, hostLabel),
        '192.168.1.x',
      );
      await tester.enterText(find.widgetWithText(TextField, '端口'), '70000');
      await tester.tap(find.text('连接此地址'));
      await tester.pumpAndSettle();
      expect(platform.connections, isEmpty);
      expect(find.text('请输入有效的设备地址和 1–65535 范围内的端口。'), findsOneWidget);
      await tester.enterText(
        find.widgetWithText(TextField, hostLabel),
        'bad host',
      );
      await tester.enterText(find.widgetWithText(TextField, '端口'), '17891');
      await tester.tap(find.text('连接此地址'));
      await tester.pumpAndSettle();
      expect(platform.connections, isEmpty);
      await tester.enterText(
        find.widgetWithText(TextField, hostLabel),
        '192.168.1.x',
      );
      await tester.tap(find.text('连接此地址'));
      await tester.pumpAndSettle();
      expect(platform.connections, [('192.168.1.x', 17891)]);
      expect(platform.expectedIds, [null]);
      expect(find.byKey(dashboardErrorKey), findsNothing);
      await tester.pumpWidget(const SizedBox());
      await tester.pumpAndSettle();
    }
  });

  testDashboard('Windows polling recovers a request whose events were missed', (
    tester,
  ) async {
    final platform = TestPlatform();
    await mountDashboard(tester, platform);
    platform.current = MicSnapshot(
      state: MicState.disconnected,
      pendingPairing: request('missed'),
      trustState: 'pairing',
    );
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
    expect(find.text('允许 My-Phone 连接？'), findsOneWidget);
    await tester.tap(find.widgetWithText(TextButton, '拒绝'));
    await tester.pumpAndSettle();
    expect(platform.decisions, [('missed', false)]);
  });

  testDashboard(
    'a stale status response cannot withdraw a newer request event',
    (tester) async {
      final platform = TestPlatform();
      await mountDashboard(tester, platform);
      final response = Completer<MicSnapshot>();
      platform.statusWait = response;
      await tester.tap(find.byTooltip('刷新'));
      await tester.pump();
      platform.requests.add(request('newer'));
      await tester.pumpAndSettle();
      platform.statusWait = null;
      response.complete(const MicSnapshot(state: MicState.disconnected));
      await tester.pumpAndSettle();
      expect(find.text('允许 My-Phone 连接？'), findsOneWidget);
      expect(platform.decisions, isEmpty);
      await tester.tap(find.widgetWithText(TextButton, '拒绝'));
      await tester.pumpAndSettle();
    },
  );

  testDashboard(
    'Android connects directly from a discovered row and keeps identity only for that address',
    (tester) async {
      final platform = TestPlatform()..connectWait = Completer<void>();
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      platform.peers.add(const [
        ComputerEndpoint(
          name: 'Home PC',
          host: '192.168.1.x',
          port: 17891,
          deviceId: 'pc-1',
        ),
      ]);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Home PC'));
      await tester.pumpAndSettle();
      expect(platform.connections, [('192.168.1.x', 17891)]);
      expect(platform.expectedIds, ['pc-1']);
      // busy 时条目禁用，不会重入连接。
      await tester.tap(find.text('Home PC'));
      await tester.pumpAndSettle();
      expect(platform.connections, hasLength(1));
      platform.connectWait!.complete();
      platform.connectWait = null;
      await tester.pumpAndSettle();
      await openManual(tester);
      await tester.enterText(
        find.widgetWithText(TextField, '电脑地址'),
        '192.168.1.w',
      );
      await tester.tap(find.text('连接此地址'));
      await tester.pumpAndSettle();
      expect(platform.expectedIds, ['pc-1', null]);
    },
  );

  testDashboard(
    'Windows connects a discovered phone directly and the primary button reaches the remembered phone',
    (tester) async {
      final platform = TestPlatform()
        ..current = const MicSnapshot(
          state: MicState.disconnected,
          trustedDevice: trustedDevice,
          trustState: 'trusted',
        );
      await mountDashboard(tester, platform);
      platform.peers.add(const [
        ComputerEndpoint(
          name: 'Other phone',
          host: '192.168.1.u',
          port: 17891,
          deviceId: 'other',
          role: 'android',
        ),
        ComputerEndpoint(
          name: 'Home device',
          host: '192.168.1.v',
          port: 17891,
          deviceId: 'device-1',
          role: 'android',
        ),
      ]);
      await tester.pumpAndSettle();
      await tester.tap(inStatus(find.text('Other phone')));
      await tester.pumpAndSettle();
      expect(platform.connections, [('192.168.1.u', 17891)]);
      expect(platform.expectedIds, ['other']);
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.connections.last, ('192.168.1.v', 17891));
      expect(platform.expectedIds.last, 'device-1');
    },
  );

  testDashboard(
    'Android primary button follows a remembered computer to its new address until the user edits',
    (tester) async {
      final platform = TestPlatform()
        ..current = const MicSnapshot(
          state: MicState.disconnected,
          trustedDevice: trustedDevice,
          trustState: 'trusted',
          lastHost: '192.168.1.x',
        );
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      final host = find.widgetWithText(TextField, '电脑地址');
      expect(tester.widget<TextField>(host).controller!.text, '192.168.1.x');
      // 电脑换了 IP 后被重新发现；预填的旧地址未被用户编辑。
      platform.peers.add(const [
        ComputerEndpoint(
          name: 'Home PC',
          host: '192.168.1.z',
          port: 17891,
          deviceId: 'device-1',
        ),
      ]);
      await tester.pumpAndSettle();
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.connections, [('192.168.1.z', 17891)]);
      expect(platform.expectedIds, ['device-1']);

      await openManual(tester);
      await tester.enterText(host, '192.168.1.y');
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.connections.last, ('192.168.1.y', 17891));
      expect(platform.expectedIds.last, isNull);
    },
  );

  testDashboard(
    'Android permission check stays in the menu and is disabled while busy',
    (tester) async {
      final platform = TestPlatform()..connectWait = Completer<void>();
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      for (final snapshot in const [
        MicSnapshot(state: MicState.idle, connected: true),
        MicSnapshot(state: MicState.streaming, connected: true),
        MicSnapshot(state: MicState.error, detail: 'x'),
        MicSnapshot(state: MicState.disconnected),
      ]) {
        platform.emit(snapshot);
        await tester.pumpAndSettle();
        expect(find.byTooltip('更多'), findsOneWidget);
      }
      await tester.tap(find.byTooltip('更多'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('权限检查'));
      await tester.pumpAndSettle();
      expect(platform.permissionRequests, 1);

      await tester.enterText(
        find.widgetWithText(TextField, '电脑地址'),
        '192.168.1.x',
      );
      await tester.tap(primaryAction);
      await tester.pump();
      await tester.tap(find.byTooltip('更多'));
      await tester.pumpAndSettle();
      expect(find.text('权限检查'), findsOneWidget);
      expect(
        tester
            .widget<PopupMenuItem<void>>(find.byType(PopupMenuItem<void>))
            .enabled,
        isFalse,
      );
      await tester.tap(find.text('权限检查'), warnIfMissed: false);
      await tester.pumpAndSettle();
      expect(platform.permissionRequests, 1);
      await tester.tapAt(const Offset(10, 400));
      platform.connectWait!.complete();
      await tester.pumpAndSettle();
    },
  );

  group('error timing', () {
    testDashboard(
      'a later success snapshot clears an earlier connect failure',
      (tester) async {
        final platform = TestPlatform()
          ..connectError = PlatformException(
            code: 'connect_failed',
            message: 'socket refused',
          );
        await mountDashboard(tester, platform, target: TargetPlatform.android);
        await tester.enterText(
          find.widgetWithText(TextField, '电脑地址'),
          '192.168.1.x',
        );
        await tester.tap(primaryAction);
        await tester.pumpAndSettle();
        expect(find.text('无法连接设备，请检查地址、对端服务和防火墙。'), findsOneWidget);
        expect(find.textContaining('socket refused'), findsNothing);
        platform.emit(
          const MicSnapshot(
            state: MicState.idle,
            connected: true,
            selectedInputId: -1,
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(dashboardErrorKey), findsNothing);
        expect(inStatus(find.text('已连接，未采集')), findsOneWidget);
        // 之后断连也不会让旧的本地错误重现。
        platform.emit(const MicSnapshot(state: MicState.disconnected));
        await tester.pumpAndSettle();
        expect(find.byKey(dashboardErrorKey), findsNothing);
      },
    );

    testDashboard('a later error snapshot replaces an earlier local failure', (
      tester,
    ) async {
      final platform = TestPlatform()
        ..connectError = PlatformException(
          code: 'connect_failed',
          message: 'socket refused',
        );
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      await tester.enterText(
        find.widgetWithText(TextField, '电脑地址'),
        '192.168.1.x',
      );
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(find.text('无法连接设备，请检查地址、对端服务和防火墙。'), findsOneWidget);
      platform.emit(
        const MicSnapshot(
          state: MicState.error,
          detail: '连接电脑失败：等待电脑批准超时，请在手机前台重新连接 (Rejected)',
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byKey(dashboardErrorKey), findsOneWidget);
      expect(find.text('等待电脑批准超时，请在手机前台重新连接。'), findsOneWidget);
      expect(find.text('无法连接设备，请检查地址、对端服务和防火墙。'), findsNothing);
    });

    testDashboard(
      'a local failure after success stays visible across repeated polls',
      (tester) async {
        final platform = TestPlatform()
          ..current = const MicSnapshot(state: MicState.idle, connected: true)
          ..startError = StateError('Android did not confirm audio capture');
        await mountDashboard(tester, platform);
        await tester.tap(primaryAction);
        await tester.pumpAndSettle();
        expect(find.text('等待设备响应超时，请检查对端应用和网络后重试。'), findsOneWidget);
        // 轮询返回相同的成功快照，不是更新的结果。
        await tester.pump(const Duration(seconds: 2));
        await tester.pumpAndSettle();
        await tester.pump(const Duration(seconds: 2));
        await tester.pumpAndSettle();
        expect(find.byKey(dashboardErrorKey), findsOneWidget);
        platform.emit(
          const MicSnapshot(state: MicState.streaming, connected: true),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(dashboardErrorKey), findsNothing);
      },
    );

    testDashboard(
      'a disconnected snapshot keeps the local failure and unknown errors use one fallback',
      (tester) async {
        final platform = TestPlatform()
          ..connectError = StateError('weird internal failure');
        await mountDashboard(tester, platform);
        await openManual(tester);
        await tester.enterText(
          find.widgetWithText(TextField, '手机 IP 地址'),
          '192.168.1.v',
        );
        await tester.tap(primaryAction);
        await tester.pumpAndSettle();
        platform.emit(
          const MicSnapshot(state: MicState.disconnected, detail: '正在连接'),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(dashboardErrorKey), findsOneWidget);
        expect(find.text(unknownErrorHint), findsOneWidget);
        expect(find.textContaining('weird internal failure'), findsNothing);
        expect(find.textContaining('Bad state'), findsNothing);
      },
    );

    testDashboard(
      'a successful poll clears a poll failure but not a user command failure',
      (tester) async {
        final platform = TestPlatform()
          ..current = const MicSnapshot(state: MicState.idle, connected: true);
        await mountDashboard(tester, platform);
        platform.statusError = StateError('status failed');
        await tester.pump(const Duration(seconds: 2));
        await tester.pumpAndSettle();
        expect(find.text(unknownErrorHint), findsOneWidget);
        // 下一次轮询成功，快照与之前相同，仍应清除轮询错误。
        platform.statusError = null;
        await tester.pump(const Duration(seconds: 2));
        await tester.pumpAndSettle();
        expect(find.byKey(dashboardErrorKey), findsNothing);

        platform.startError = StateError(
          'Android did not confirm audio capture',
        );
        await tester.tap(primaryAction);
        await tester.pumpAndSettle();
        expect(find.text('等待设备响应超时，请检查对端应用和网络后重试。'), findsOneWidget);
        await tester.pump(const Duration(seconds: 2));
        await tester.pumpAndSettle();
        await tester.pump(const Duration(seconds: 2));
        await tester.pumpAndSettle();
        expect(find.text('等待设备响应超时，请检查对端应用和网络后重试。'), findsOneWidget);
      },
    );

    testDashboard('a successful refresh clears a refresh failure', (
      tester,
    ) async {
      final platform = TestPlatform()
        ..statusError = StateError('status failed');
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      expect(find.text(unknownErrorHint), findsOneWidget);
      platform.statusError = null;
      await tester.tap(find.byTooltip('刷新'));
      await tester.pumpAndSettle();
      expect(find.byKey(dashboardErrorKey), findsNothing);
    });

    testDashboard(
      'a permission request failure shows the fallback and keeps the menu entry',
      (tester) async {
        final platform = TestPlatform()
          ..permissionError = PlatformException(code: 'denied_by_os');
        await mountDashboard(tester, platform, target: TargetPlatform.android);
        await tester.tap(find.byTooltip('更多'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('权限检查'));
        await tester.pumpAndSettle();
        expect(platform.permissionRequests, 1);
        expect(find.byKey(dashboardErrorKey), findsOneWidget);
        expect(find.text(unknownErrorHint), findsOneWidget);
        expect(find.textContaining('denied_by_os'), findsNothing);
        expect(find.byTooltip('更多'), findsOneWidget);
      },
    );
  });
}

class TestPlatform implements MicPlatform {
  final events = StreamController<MicSnapshot>.broadcast();
  final requests = StreamController<PendingPairing>.broadcast();
  final peers = StreamController<List<ComputerEndpoint>>.broadcast();
  final _activeRequests = <String, PendingPairing>{};
  final _expiredRequests = <String>{};
  final _requestTimers = <Timer>[];
  MicSnapshot current = const MicSnapshot(state: MicState.disconnected);
  List<InputDevice> availableInputs = const [];
  final decisions = <(String, bool)>[];
  final connections = <(String, int)>[];
  Completer<void>? connectWait;
  Completer<void>? startWait;
  Completer<MicSnapshot>? statusWait;
  Completer<List<InputDevice>>? inputsWait;
  final expectedIds = <String?>[];
  Object? decisionError;
  Object? connectError;
  Object? startError;
  Object? statusError;
  Object? permissionError;
  int starts = 0, stops = 0, forgets = 0, permissionRequests = 0;

  void emit(MicSnapshot snapshot) {
    current = snapshot;
    _activeRequests.removeWhere(
      (id, _) => id != snapshot.pendingPairing?.requestId,
    );
    if (snapshot.pendingPairing case final pending?) _trackRequest(pending);
    events.add(snapshot);
  }

  @override
  Stream<MicSnapshot> get states => events.stream;
  @override
  Stream<PendingPairing> get pairingRequests => requests.stream.map((request) {
    _trackRequest(request);
    current = MicSnapshot(
      state: current.state,
      device: current.device,
      detail: current.detail,
      connected: current.connected,
      trustedDevice: current.trustedDevice,
      pendingPairing: request,
      trustState: current.trustState,
      audioDevice: current.audioDevice,
      audioTransport: current.audioTransport,
      droppedAudioFrames: current.droppedAudioFrames,
      listenerActive: current.listenerActive,
      selectedInputId: current.selectedInputId,
      inputSelectionDetail: current.inputSelectionDetail,
      lastHost: current.lastHost,
      lastPort: current.lastPort,
    );
    return request;
  });

  void _trackRequest(PendingPairing request) {
    if (_activeRequests.containsKey(request.requestId) ||
        _expiredRequests.contains(request.requestId)) {
      return;
    }
    _activeRequests[request.requestId] = request;
    _requestTimers.add(
      Timer(request.expiresAt.difference(DateTime.now()), () {
        _activeRequests.remove(request.requestId);
        _expiredRequests.add(request.requestId);
      }),
    );
  }

  @override
  Stream<List<ComputerEndpoint>> get computers => peers.stream;
  @override
  Future<MicSnapshot> status() async {
    if (current.pendingPairing case final pending?) _trackRequest(pending);
    if (statusError case final error?) throw error;
    return statusWait == null ? current : await statusWait!.future;
  }

  @override
  Future<List<InputDevice>> inputs() async =>
      inputsWait == null ? availableInputs : await inputsWait!.future;
  @override
  Future<void> selectInput(int id) async {}
  @override
  Future<void> discoverComputers() async {}
  @override
  Future<void> connect({
    String host = '127.0.0.1',
    int port = 17891,
    String? expectedDeviceId,
  }) async {
    expectedIds.add(expectedDeviceId);
    connections.add((host, port));
    await connectWait?.future;
    if (connectError case final error?) throw error;
  }

  @override
  Future<void> decidePairing(String requestId, {required bool allow}) async {
    decisions.add((requestId, allow));
    final pending = _activeRequests[requestId];
    if (pending == null || !pending.expiresAt.isAfter(DateTime.now())) {
      throw StateError('pairing_request_stale');
    }
    if (decisionError case final error?) throw error;
    current = const MicSnapshot(state: MicState.disconnected);
  }

  @override
  Future<void> forgetDevice() async {
    forgets++;
    emit(const MicSnapshot(state: MicState.disconnected));
  }

  @override
  Future<void> start() async {
    starts++;
    await startWait?.future;
    if (startError case final error?) throw error;
  }

  @override
  Future<void> stop() async {
    stops++;
  }

  @override
  Future<void> requestPermissions() async {
    permissionRequests++;
    if (permissionError case final error?) throw error;
  }

  @override
  Future<void> dispose() async {
    for (final timer in _requestTimers) {
      timer.cancel();
    }
    await events.close();
    await requests.close();
    await peers.close();
  }
}
