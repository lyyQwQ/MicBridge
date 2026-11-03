import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/platform_bridge.dart';
import 'package:micbridge/dashboard_widgets.dart';
import 'widget_test.dart' as h;

class DelayedSelection extends h.TestPlatform {
  final selection = Completer<void>();
  @override
  Future<void> selectInput(int id) => selection.future;
}
void main() {
  h.testDashboard('old failed status must not overwrite newer streaming event', (tester) async {
    final p = h.TestPlatform();
    await h.mountDashboard(tester, p);
    final pending = Completer<MicSnapshot>();
    p.statusWait = pending;
    await tester.tap(find.byTooltip('刷新'));
    await tester.pump();
    p.emit(const MicSnapshot(state: MicState.streaming, connected: true));
    await tester.pump();
    p.statusWait = null;
    pending.completeError(StateError('old status failure'));
    await tester.pump();
    expect(find.byKey(dashboardErrorKey), findsNothing);
  });
  h.testDashboard('poll recovery must preserve earlier command error', (tester) async {
    final p = h.TestPlatform()
      ..current = const MicSnapshot(state: MicState.idle, connected: true)
      ..startError = StateError('Android did not confirm audio capture');
    await h.mountDashboard(tester, p);
    await tester.tap(h.primaryAction);
    await tester.pumpAndSettle();
    p.statusError = StateError('poll failure');
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
    p.statusError = null;
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
    expect(find.text('等待设备响应超时，请检查对端应用和网络后重试。'), findsOneWidget);
  });
  h.testDashboard('late selection acknowledgement must preserve native fallback', (tester) async {
    final p = DelayedSelection()
      ..availableInputs = const [
        InputDevice(id: -1, name: '跟随系统', address: '', type: 0),
        InputDevice(id: 7, name: 'Headset', address: '', type: 7),
      ]
      ..current = const MicSnapshot(state: MicState.idle, connected: true, selectedInputId: -1);
    await h.mountDashboard(tester, p, target: TargetPlatform.android);
    await h.openMicrophones(tester);
    await tester.tap(find.text('Headset'));
    await tester.pumpAndSettle();
    p.availableInputs = const [InputDevice(id: -1, name: '跟随系统', address: '', type: 0)];
    p.emit(const MicSnapshot(state: MicState.idle, connected: true, selectedInputId: -1));
    await tester.pumpAndSettle();
    p.selection.complete();
    await tester.pumpAndSettle();
    expect(await h.microphoneSelection(tester), -1);
  });
  h.testDashboard('withdrawn pairing must not submit a stale denial', (tester) async {
    final p = h.TestPlatform()..decisionError = StateError('配对请求已失效或决定无效，请在手机上重新连接');
    await h.mountDashboard(tester, p);
    p.requests.add(h.request('withdrawn'));
    await tester.pumpAndSettle();
    p.emit(const MicSnapshot(state: MicState.idle, connected: true));
    await tester.pumpAndSettle();
    expect(p.decisions, isEmpty);
    expect(find.byKey(dashboardErrorKey), findsNothing);
  });
}
