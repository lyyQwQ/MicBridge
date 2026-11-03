import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/platform_bridge.dart';
import 'package:micbridge/window_attention.dart';

import 'widget_test.dart';

/// 记录窗口提醒通道调用；[fail] 时模拟原生失败。
List<String> recordAttention({bool fail = false}) {
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
  final calls = <String>[];
  messenger.setMockMethodCallHandler(windowAttentionChannel, (call) async {
    calls.add(call.method);
    if (fail) throw PlatformException(code: 'flash_failed');
    return null;
  });
  addTearDown(
    () => messenger.setMockMethodCallHandler(windowAttentionChannel, null),
  );
  return calls;
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  tearDown(() => debugDefaultTargetPlatformOverride = null);

  // 直接调用平台通道需真实事件循环，因此用 test 而非 testWidgets 的假时钟。
  test(
    'Windows invokes the window channel and other platforms do nothing',
    () async {
      final calls = recordAttention();
      debugDefaultTargetPlatformOverride = TargetPlatform.windows;
      await flashPairing();
      await stopFlashing();
      debugDefaultTargetPlatformOverride = TargetPlatform.android;
      await flashPairing();
      await stopFlashing();
      debugDefaultTargetPlatformOverride = null;
      expect(calls, ['flashPairing', 'stopFlashing']);
    },
  );

  test('missing plugin and platform failures never propagate', () async {
    debugDefaultTargetPlatformOverride = TargetPlatform.windows;
    await flashPairing();
    recordAttention(fail: true);
    await flashPairing();
    await stopFlashing();
    debugDefaultTargetPlatformOverride = null;
  });

  testDashboard('each new request flashes once and every dialog end stops', (
    tester,
  ) async {
    final calls = recordAttention();
    final platform = TestPlatform();
    await mountDashboard(tester, platform);
    final pending = request('first');
    platform.requests.add(pending);
    platform.emit(
      MicSnapshot(state: MicState.disconnected, pendingPairing: pending),
    );
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
    expect(calls, ['flashPairing'], reason: '事件、快照与轮询重复不重复提醒');
    await tester.tap(find.widgetWithText(FilledButton, '允许'));
    await tester.pumpAndSettle();
    expect(calls, ['flashPairing', 'stopFlashing']);

    platform.requests.add(request('denied'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(TextButton, '拒绝'));
    await tester.pumpAndSettle();
    platform.requests.add(request('dismissed'));
    await tester.pumpAndSettle();
    await tester.tapAt(const Offset(10, 10));
    await tester.pumpAndSettle();
    platform.requests.add(
      request('timeout', lifetime: const Duration(seconds: 2)),
    );
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
    platform.requests.add(request('withdrawn'));
    await tester.pumpAndSettle();
    platform.emit(const MicSnapshot(state: MicState.disconnected));
    await tester.pumpAndSettle();
    expect(calls, [
      for (var i = 0; i < 5; i++) ...['flashPairing', 'stopFlashing'],
    ]);
    expect(platform.decisions.map((d) => d.$1), [
      'first',
      'denied',
      'dismissed',
    ]);

    platform.requests.add(
      request('expired', lifetime: const Duration(seconds: -1)),
    );
    await tester.pumpAndSettle();
    expect(calls, hasLength(10), reason: '已过期请求不提醒');
  });

  testDashboard('a failing reminder still lets the decision complete', (
    tester,
  ) async {
    final calls = recordAttention(fail: true);
    final platform = TestPlatform();
    await mountDashboard(tester, platform);
    platform.requests.add(request('flaky'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, '允许'));
    await tester.pumpAndSettle();
    expect(calls, ['flashPairing', 'stopFlashing']);
    expect(platform.decisions, [('flaky', true)]);
    expect(find.byType(AlertDialog), findsNothing);
  });

  testDashboard('disposing with an open request stops the reminder', (
    tester,
  ) async {
    final calls = recordAttention();
    final platform = TestPlatform();
    await mountDashboard(tester, platform);
    platform.requests.add(request('open'));
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
    await tester.pumpAndSettle();
    expect(calls.first, 'flashPairing');
    expect(calls.last, 'stopFlashing');
  });
}
