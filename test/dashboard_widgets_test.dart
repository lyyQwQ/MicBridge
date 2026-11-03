import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/dashboard_widgets.dart';
import 'package:micbridge/main.dart';
import 'package:micbridge/platform_bridge.dart';

import 'widget_test.dart';

double contrast(Color a, Color b) {
  final l1 = a.computeLuminance(), l2 = b.computeLuminance();
  return (math.max(l1, l2) + 0.05) / (math.min(l1, l2) + 0.05);
}

const _idle = MicSnapshot(
  state: MicState.idle,
  connected: true,
  selectedInputId: -1,
  trustState: 'trusted',
  trustedDevice: trustedDevice,
);
const _streaming = MicSnapshot(
  state: MicState.streaming,
  connected: true,
  selectedInputId: -1,
  trustState: 'trusted',
  trustedDevice: trustedDevice,
);
const _inputs = [InputDevice(id: -1, name: '跟随系统', address: '', type: 0)];

void main() {
  for (final target in [TargetPlatform.android, TargetPlatform.windows]) {
    testDashboard(
      '$target manual floating labels remain inside expanded body at 1.3',
      (tester) async {
        tester.platformDispatcher.textScaleFactorTestValue = 1.3;
        final platform = TestPlatform()
          ..availableInputs = _inputs
          ..current = const MicSnapshot(
            state: MicState.idle,
            connected: true,
            selectedInputId: -1,
            lastHost: 'example.local',
          );
        await mountDashboard(
          tester,
          platform,
          target: target,
          size: const Size(412, 915),
        );
        await openManual(tester);
        await tester.enterText(
          find.widgetWithText(
            TextField,
            target == TargetPlatform.windows ? '手机 IP 地址' : '电脑地址',
          ),
          'example.local',
        );
        FocusManager.instance.primaryFocus?.unfocus();
        await tester.pumpAndSettle();
        final section = find.byType(ManualSection);
        final clip = find.descendant(of: section, matching: find.byType(ClipRect));
        final body = tester.getRect(clip);
        final header = tester.getRect(
          find.descendant(of: section, matching: find.byType(ListTile)),
        );
        for (final label in [
          target == TargetPlatform.windows ? '手机 IP 地址' : '电脑地址',
          '端口',
        ]) {
          final finder = find.descendant(of: section, matching: find.text(label));
          final box = tester.renderObject<RenderBox>(finder);
          // 浮动标签带缩放变换，必须测量绘制矩形而非未缩放的布局尺寸。
          final rect = MatrixUtils.transformRect(
            box.getTransformTo(null),
            Offset.zero & box.size,
          );
          expect(rect.top, greaterThanOrEqualTo(body.top), reason: '$label 顶部被裁剪');
          expect(rect.bottom, lessThanOrEqualTo(body.bottom));
          expect(rect.left, greaterThanOrEqualTo(body.left));
          expect(rect.right, lessThanOrEqualTo(body.right));
          expect(rect.overlaps(header), isFalse, reason: '$label 与标题行重叠');
        }
      },
    );
  }

  test('state maps to one primary action', () {
    DashboardAction map(
      MicState state, {
      bool connected = false,
      bool pairing = false,
    }) => primaryActionFor(
      MicSnapshot(state: state, connected: connected),
      awaitingApproval: pairing,
    );
    expect(map(MicState.disconnected), DashboardAction.connect);
    expect(
      map(MicState.disconnected, pairing: true),
      DashboardAction.waitForApproval,
    );
    expect(map(MicState.idle, connected: true), DashboardAction.start);
    expect(map(MicState.starting, connected: true), DashboardAction.stop);
    expect(map(MicState.streaming, connected: true), DashboardAction.stop);
    expect(map(MicState.stopping, connected: true), DashboardAction.stopping);
    expect(map(MicState.error, connected: true), DashboardAction.retry);
    expect(map(MicState.error), DashboardAction.reconnect);
    expect(map(MicState.error, pairing: true), DashboardAction.waitForApproval);
  });

  test('known error strings become actionable hints and unknown strings use one fallback', () {
    expect(
      describeError('FormatException: 请输入有效的电脑地址和端口'),
      '请输入有效的设备地址和 1–65535 范围内的端口。',
    );
    expect(
      describeError(
        'PlatformException(start_failed, input_device_not_found, null, null)',
      ),
      contains('麦克风不可用'),
    );
    expect(
      describeError(
        'PlatformException(connect_failed, java.net.ConnectException, null, null)',
      ),
      contains('无法连接设备'),
    );
    expect(describeError('连接电脑失败：超时 (SocketTimeoutException)'), contains('超时'));
    expect(
      describeError('Bad state: Android did not confirm audio capture'),
      contains('等待设备响应超时'),
    );
    expect(describeError('控制连接失败：电脑已断开连接 (EOFException)'), contains('连接已断开'));
    expect(
      describeError('连接电脑失败：设备信任存储不可用 (StorageException)'),
      contains('配对记录不可用'),
    );
    expect(describeError('Bad state: Windows 核心已退出 (1)'), contains('后台服务已退出'));
    expect(describeError('Bad state: 未知'), unknownErrorHint);
    expect(describeError(''), unknownErrorHint);
    expect(describeDetail('正在连接电脑…'), '正在连接电脑…');
    expect(describeDetail('auth_failed: rejected'), isNull);
  });

  test(
    'status colors keep text contrast at least 4.5:1 in light and dark themes',
    () {
      for (final brightness in Brightness.values) {
        final scheme = appTheme(brightness).colorScheme;
        expect(
          HSLColor.fromColor(scheme.tertiaryContainer).hue,
          inInclusiveRange(20, 60),
          reason: '等待批准使用暖色',
        );
        for (final tone in StatusTone.values) {
          final c = toneColors(scheme, tone);
          expect(
            contrast(c.foreground, c.background),
            greaterThanOrEqualTo(4.5),
            reason: '$brightness $tone text',
          );
          expect(
            contrast(c.onButton, c.button),
            greaterThanOrEqualTo(4.5),
            reason: '$brightness $tone button',
          );
        }
        expect(
          contrast(scheme.onErrorContainer, scheme.errorContainer),
          greaterThanOrEqualTo(4.5),
        );
        // 详情卡为 outlined（surface 底），列表副标题用 onSurfaceVariant。
        expect(
          contrast(scheme.onSurfaceVariant, scheme.surface),
          greaterThanOrEqualTo(4.5),
        );
        expect(
          contrast(scheme.onSurface, scheme.surface),
          greaterThanOrEqualTo(4.5),
        );
      }
    },
  );

  testDashboard(
    'every state shows one contextual primary button with existing gating',
    (tester) async {
      final platform = TestPlatform()..availableInputs = _inputs;
      await mountDashboard(tester, platform, target: TargetPlatform.android);
      Future<void> expectAction(
        MicSnapshot snapshot,
        String label, {
        required bool enabled,
        bool settle = true,
      }) async {
        platform.emit(snapshot);
        settle ? await tester.pumpAndSettle() : await pumpFor(tester);
        expect(primaryAction, findsOneWidget);
        expect(primaryLabeled(label), findsOneWidget, reason: '$snapshot');
        expect(primaryButton(tester).onPressed != null, enabled, reason: label);
      }

      await expectAction(
        const MicSnapshot(state: MicState.disconnected),
        '连接电脑',
        enabled: true,
      );
      expect(inStatus(find.text('未连接')), findsOneWidget);
      expect(find.text('等待系统确认音频路由'), findsNothing);
      await expectAction(
        const MicSnapshot(state: MicState.disconnected, trustState: 'pairing'),
        '等待配对批准',
        enabled: false,
        settle: false,
      );
      expect(inStatus(find.text('请在电脑上允许配对')), findsOneWidget);
      expect(inStatus(find.byType(LinearProgressIndicator)), findsOneWidget);
      expect(find.textContaining('秒'), findsNothing, reason: '不虚构剩余时间');
      await expectAction(_idle, '开始采集', enabled: true);
      await expectAction(
        const MicSnapshot(
          state: MicState.starting,
          connected: true,
          selectedInputId: -1,
        ),
        '停止采集',
        enabled: true,
      );
      await expectAction(_streaming, '停止采集', enabled: true);
      await expectAction(
        const MicSnapshot(
          state: MicState.stopping,
          connected: true,
          selectedInputId: -1,
        ),
        '正在停止采集',
        enabled: false,
      );
      await expectAction(
        const MicSnapshot(
          state: MicState.error,
          connected: true,
          selectedInputId: -1,
          detail: 'x',
        ),
        '重试采集',
        enabled: true,
      );
      await expectAction(
        const MicSnapshot(state: MicState.error, detail: 'x'),
        '重新连接',
        enabled: true,
      );

      platform.startWait = Completer<void>();
      await expectAction(_idle, '开始采集', enabled: true);
      await tester.tap(primaryAction);
      await tester.pump();
      expect(primaryLabeled('开始采集'), findsOneWidget);
      expect(primaryButton(tester).onPressed, isNull, reason: 'busy 禁止重入');
      platform.startWait!.complete();
      await tester.pumpAndSettle();
      expect(platform.starts, 1);
    },
  );

  for (final (name, snapshot, label) in [
    ('idle', _idle, '开始采集'),
    ('streaming', _streaming, '停止采集'),
  ]) {
    testDashboard(
      'Android 412x915 shows the $name primary button within the first screen',
      (tester) async {
        final platform = TestPlatform()
          ..availableInputs = _inputs
          ..current = snapshot;
        // 模拟状态栏 24 与导航栏 48 逻辑像素。
        tester.view.padding = const FakeViewPadding(top: 24, bottom: 48);
        await mountDashboard(
          tester,
          platform,
          target: TargetPlatform.android,
          size: const Size(412, 915),
        );
        final rect = tester.getRect(primaryAction);
        final appBarBottom = tester.getRect(find.byType(AppBar)).bottom;
        expect(rect.top, greaterThanOrEqualTo(appBarBottom));
        expect(rect.bottom, lessThanOrEqualTo(915 - 48));
        expect(rect.left, greaterThanOrEqualTo(0));
        expect(rect.right, lessThanOrEqualTo(412));
        expect(primaryLabeled(label), findsOneWidget);
        expect(find.textContaining('3 秒'), findsOneWidget);
        await tester.tap(primaryAction);
        await tester.pumpAndSettle();
        expect(platform.starts + platform.stops, 1);
      },
    );
  }

  testDashboard(
    'Windows 520x720 keeps status, live detection and primary action on screen',
    (tester) async {
      final platform = TestPlatform()..current = _streaming;
      await mountDashboard(tester, platform, size: const Size(520, 720));
      expect(find.byKey(singleColumnKey), findsOneWidget);
      expect(tester.getRect(primaryAction).bottom, lessThanOrEqualTo(720));
      expect(
        tester
            .getRect(inStatus(find.textContaining('CABLE Output 录音检测')))
            .bottom,
        lessThanOrEqualTo(720),
      );
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.stops, 1);
    },
  );

  for (final width in [899.0, 900.0, 901.0]) {
    testDashboard('layout at ${width.toInt()} logical width', (tester) async {
      await mountDashboard(
        tester,
        TestPlatform()..current = _idle,
        size: Size(width, 720),
      );
      expect(
        find.byKey(twoColumnKey),
        width >= 900 ? findsOneWidget : findsNothing,
      );
      expect(
        find.byKey(singleColumnKey),
        width >= 900 ? findsNothing : findsOneWidget,
      );
      if (width >= 900) {
        // 左列状态与主操作，右列设备、音频和诊断。
        expect(tester.getRect(primaryAction).right, lessThan(width / 2));
        expect(
          tester.getRect(find.text('手动连接与诊断')).left,
          greaterThan(width / 2),
        );
      }
    });
  }

  for (final brightness in Brightness.values) {
    testDashboard('colors the whole status card in $brightness theme', (
      tester,
    ) async {
      tester.platformDispatcher.platformBrightnessTestValue = brightness;
      await mountDashboard(tester, TestPlatform()..current = _idle);
      final scheme = Theme.of(tester.element(primaryAction)).colorScheme;
      expect(
        tester.widget<Card>(find.byKey(statusPanelKey)).color,
        scheme.primaryContainer,
      );
    });
  }

  for (final target in [TargetPlatform.android, TargetPlatform.windows]) {
    testDashboard('$target renders 1.3x text without overflow', (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 1.3;
      final size = target == TargetPlatform.android
          ? const Size(412, 915)
          : const Size(520, 720);
      final platform = TestPlatform()
        ..availableInputs = _inputs
        ..current = const MicSnapshot(
          state: MicState.error,
          connected: true,
          selectedInputId: -1,
          detail: 'input_device_not_found',
          trustState: 'trusted',
          trustedDevice: trustedDevice,
        );
      await mountDashboard(tester, platform, target: target, size: size);
      await openManual(tester);
      expect(tester.takeException(), isNull);
      await tester.scrollUntilVisible(
        primaryAction,
        -100,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.tap(primaryAction);
      await tester.pumpAndSettle();
      expect(platform.starts, 1);
    });
  }

  testDashboard('status is a live region announced once per distinct state', (
    tester,
  ) async {
    final semantics = tester.ensureSemantics();
    final platform = TestPlatform()..current = _idle;
    await mountDashboard(tester, platform);
    expect(
      tester.getSemantics(find.byKey(statusLiveKey)),
      isSemantics(isLiveRegion: true),
    );
    final label = tester
        .getSemantics(find.byKey(statusLiveKey))
        .getSemanticsData()
        .label;
    platform.emit(_idle);
    await tester.pumpAndSettle();
    expect(
      tester.getSemantics(find.byKey(statusLiveKey)).getSemanticsData().label,
      label,
    );
    platform.emit(_streaming);
    await tester.pumpAndSettle();
    expect(
      tester.getSemantics(find.byKey(statusLiveKey)).getSemanticsData().label,
      isNot(label),
    );
    semantics.dispose();
  });
}
