import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/platform_bridge.dart';

import 'widget_test.dart' as h;

const _methods = MethodChannel('micbridge/platform');
const _entry = ValueKey('audioSettingsEntry');
const _gain = ValueKey('audioGain');
const _ns = MicAudioEffect.ns;
const _aec = MicAudioEffect.aec;
const _agc = MicAudioEffect.agc;

SemanticsNode? _adjustableNode(SemanticsNode node) {
  if (node.getSemanticsData().hasAction(SemanticsAction.increase)) return node;
  SemanticsNode? result;
  node.visitChildren((child) {
    result = _adjustableNode(child);
    return result == null;
  });
  return result;
}

Map<String, Object?> _response() => {
  'preferences': {'gainDb': 0, 'ns': null, 'aec': false, 'agc': true},
  'available': {'ns': true, 'aec': false, 'agc': true},
};

class _SettingsPlatform extends h.TestPlatform
    implements MicAudioSettingsPlatform {
  MicAudioSettings settings = const MicAudioSettings(
    available: {_ns, _aec, _agc},
  );
  Completer<MicAudioSettings>? loadWait;
  Completer<MicAudioSettings>? saveWait;
  Object? loadError;
  Object? saveError;
  int reads = 0;
  final writes = <MicAudioSettings>[];

  @override
  Future<MicAudioSettings> audioSettings() async {
    reads++;
    if (loadWait case final wait?) return wait.future;
    if (loadError case final error?) throw error;
    return settings;
  }

  @override
  Future<MicAudioSettings> saveAudioSettings(MicAudioSettings next) async {
    writes.add(next);
    if (saveWait case final wait?) return wait.future;
    if (saveError case final error?) throw error;
    settings = next;
    return next;
  }
}

Future<void> _open(WidgetTester tester, {bool settle = true}) async {
  await tester.ensureVisible(find.byKey(_entry));
  await tester.tap(find.byKey(_entry));
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    await h.pumpFor(tester);
  }
}

Finder _effect(String suffix) => find.byKey(ValueKey('audioEffect.$suffix'));

SegmentedButton<bool?> _choice(WidgetTester tester, String effect) =>
    tester.widget<SegmentedButton<bool?>>(_effect('$effect.choice'));

Future<void> _tap(WidgetTester tester, Finder finder) async {
  await tester.ensureVisible(finder);
  await tester.tap(finder);
  await tester.pumpAndSettle();
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
  tearDown(() => messenger.setMockMethodCallHandler(_methods, null));

  test(
    'Android adapter decodes preferences separately from availability',
    () async {
      final calls = <MethodCall>[];
      messenger.setMockMethodCallHandler(_methods, (call) async {
        calls.add(call);
        return _response();
      });
      final adapter =
          createAndroidMicPlatformForTesting() as MicAudioSettingsPlatform;
      final settings = await adapter.audioSettings();
      expect(settings.gainDb, 0);
      expect(settings.effects, {_aec: false, _agc: true});
      expect(settings.available, {_ns, _agc});
      await adapter.saveAudioSettings(
        settings.withGain(20).withEffect(_agc, null),
      );
      expect(calls.map((call) => call.method), [
        'getAudioSettings',
        'setAudioSettings',
      ]);
      expect(calls.first.arguments, isNull);
      expect(calls.last.arguments, {
        'gainDb': 20,
        'ns': null,
        'aec': false,
        'agc': null,
      });
    },
  );

  test('Android adapter rejects malformed settings responses', () async {
    final valid = _response();
    final preferences = valid['preferences']! as Map;
    final available = valid['available']! as Map;
    final malformed = <Object?>[
      null,
      [],
      {'preferences': preferences},
      {...valid, 'preferences': null},
      {
        ...valid,
        'preferences': {...preferences}..remove('ns'),
      },
      {
        ...valid,
        'preferences': {...preferences, 'gainDb': 1.5},
      },
      {
        ...valid,
        'preferences': {...preferences, 'gainDb': -1},
      },
      {
        ...valid,
        'preferences': {...preferences, 'gainDb': 21},
      },
      {
        ...valid,
        'preferences': {...preferences, 'ns': 'true'},
      },
      {
        ...valid,
        'available': {...available}..remove('ns'),
      },
      {
        ...valid,
        'available': {...available, 'ns': 0},
      },
    ];
    final adapter =
        createAndroidMicPlatformForTesting() as MicAudioSettingsPlatform;
    for (final raw in malformed) {
      messenger.setMockMethodCallHandler(_methods, (_) async => raw);
      await expectLater(adapter.audioSettings(), throwsFormatException);
      await expectLater(
        adapter.saveAudioSettings(const MicAudioSettings()),
        throwsFormatException,
      );
    }
  });

  test('Android adapter preserves native storage errors', () async {
    messenger.setMockMethodCallHandler(_methods, (_) async {
      throw PlatformException(code: 'settings_persist_failed');
    });
    final adapter =
        createAndroidMicPlatformForTesting() as MicAudioSettingsPlatform;
    await expectLater(
      adapter.saveAudioSettings(const MicAudioSettings()),
      throwsA(
        isA<PlatformException>().having(
          (error) => error.code,
          'code',
          'settings_persist_failed',
        ),
      ),
    );
  });

  h.testDashboard('Windows hides settings and never reads them', (
    tester,
  ) async {
    final platform = _SettingsPlatform();
    await h.mountDashboard(tester, platform);
    expect(find.byKey(_entry), findsNothing);
    expect(platform.reads, 0);
    expect(platform.writes, isEmpty);
  });

  h.testDashboard('Android defaults and three-way choices save full objects', (
    tester,
  ) async {
    final platform = _SettingsPlatform();
    await h.mountDashboard(tester, platform, target: TargetPlatform.android);
    await _open(tester);
    expect(platform.reads, 1);
    for (final effect in MicAudioEffect.values) {
      expect(_choice(tester, effect.name).selected, {null});
    }
    final slider = tester.widget<Slider>(find.byKey(_gain));
    expect(
      (slider.min, slider.max, slider.divisions, slider.value),
      (0, 20, 20, 0),
    );
    expect(slider.semanticFormatterCallback!(7), contains('7'));
    slider.onChanged!(7);
    await tester.pump();
    expect(platform.writes, isEmpty);
    expect(tester.widget<Slider>(find.byKey(_gain)).value, 7);
    slider.onChangeEnd!(7);
    await tester.pumpAndSettle();
    expect(platform.writes.single.toPreferences(), {
      'gainDb': 7,
      'ns': null,
      'aec': null,
      'agc': null,
    });
    await _tap(tester, _effect('ns.on'));
    expect(platform.writes.last.effects[_ns], isTrue);
    expect(_choice(tester, 'ns').selected, {true});
    await _tap(tester, _effect('ns.off'));
    expect(platform.writes.last.effects[_ns], isFalse);
    expect(_choice(tester, 'ns').selected, {false});
    await _tap(tester, _effect('ns.default'));
    expect(platform.writes.last.effects.containsKey(_ns), isFalse);
    expect(_choice(tester, 'ns').selected, {null});
    await _tap(tester, _effect('aec.off'));
    expect(platform.writes.last.effects[_aec], isFalse);
    expect(platform.writes, hasLength(5));
    expect(platform.starts, 0);
    expect(platform.stops, 0);
  });

  h.testDashboard(
    'Unavailable effects keep preferences and disable all edits',
    (tester) async {
      final platform = _SettingsPlatform()
        ..settings = const MicAudioSettings(
          effects: {_aec: true},
          available: {_ns},
        );
      await h.mountDashboard(tester, platform, target: TargetPlatform.android);
      await _open(tester);
      expect(_choice(tester, 'aec').selected, {true});
      expect(_choice(tester, 'aec').onSelectionChanged, isNull);
      expect(_choice(tester, 'agc').selected, {null});
      expect(_choice(tester, 'agc').onSelectionChanged, isNull);
      expect(_choice(tester, 'ns').onSelectionChanged, isNotNull);
      await _tap(tester, _effect('aec.off'));
      expect(
        find.byKey(const ValueKey('audioEffectUnavailable')),
        findsNWidgets(2),
      );
      expect(platform.writes, isEmpty);
    },
  );

  h.testDashboard('Read failure offers retry and blocks writing until loaded', (
    tester,
  ) async {
    final platform = _SettingsPlatform()..loadError = StateError('unavailable');
    await h.mountDashboard(tester, platform, target: TargetPlatform.android);
    await _open(tester);
    expect(find.byKey(const ValueKey('audioSettingsError')), findsOneWidget);
    expect(find.byKey(_gain), findsNothing);
    expect(platform.writes, isEmpty);
    platform.loadError = null;
    await _tap(tester, find.byKey(const ValueKey('audioSettingsRetry')));
    expect(find.byKey(_gain), findsOneWidget);
    expect(platform.reads, 2);
  });

  h.testDashboard(
    'Save failure rolls gain and effects back to confirmed state',
    (tester) async {
      final platform = _SettingsPlatform()
        ..settings = const MicAudioSettings(
          gainDb: 3,
          effects: {_ns: false},
          available: {_ns},
        )
        ..saveError = PlatformException(code: 'settings_persist_failed');
      await h.mountDashboard(tester, platform, target: TargetPlatform.android);
      await _open(tester);
      final slider = tester.widget<Slider>(find.byKey(_gain));
      slider.onChanged!(20);
      slider.onChangeEnd!(20);
      await tester.pumpAndSettle();
      expect(tester.widget<Slider>(find.byKey(_gain)).value, 3);
      expect(find.byKey(const ValueKey('audioSettingsError')), findsOneWidget);
      expect(find.byKey(const ValueKey('audioSettingsSaved')), findsNothing);
      await _tap(tester, _effect('ns.on'));
      expect(_choice(tester, 'ns').selected, {false});
      expect(_choice(tester, 'ns').onSelectionChanged, isNotNull);
      expect(platform.writes, hasLength(2));
    },
  );

  h.testDashboard(
    'Pending save disables controls and stale callbacks cannot reenter',
    (tester) async {
      final pending = Completer<MicAudioSettings>();
      final platform = _SettingsPlatform()..saveWait = pending;
      await h.mountDashboard(tester, platform, target: TargetPlatform.android);
      await _open(tester);
      final stale = _choice(tester, 'ns').onSelectionChanged!;
      stale({true});
      stale({true});
      await tester.pump();
      expect(platform.writes, hasLength(1));
      expect(tester.widget<Slider>(find.byKey(_gain)).onChanged, isNull);
      expect(_choice(tester, 'aec').onSelectionChanged, isNull);
      expect(find.byKey(const ValueKey('audioSettingsSaved')), findsNothing);
      pending.complete(platform.settings.withEffect(_ns, true));
      await tester.pumpAndSettle();
      expect(_choice(tester, 'ns').selected, {true});
      expect(find.byKey(const ValueKey('audioSettingsSaved')), findsOneWidget);
    },
  );

  for (final fail in [false, true]) {
    h.testDashboard('Closed page ignores late read response (failure=$fail)', (
      tester,
    ) async {
      final pending = Completer<MicAudioSettings>();
      final platform = _SettingsPlatform()..loadWait = pending;
      await h.mountDashboard(tester, platform, target: TargetPlatform.android);
      await _open(tester, settle: false);
      expect(find.byKey(_gain), findsNothing);
      await _tap(tester, find.byKey(const ValueKey('audioSettingsClose')));
      platform.loadWait = null;
      await _open(tester);
      if (fail) {
        pending.completeError(StateError('late read failure'));
      } else {
        pending.complete(platform.settings.withGain(20));
      }
      await tester.pumpAndSettle();
      expect(tester.widget<Slider>(find.byKey(_gain)).value, 0);
      expect(find.byKey(const ValueKey('audioSettingsError')), findsNothing);
      expect(tester.takeException(), isNull);
    });
  }

  for (final fail in [false, true]) {
    h.testDashboard(
      'Reopened page ignores late save response (failure=$fail)',
      (tester) async {
        final pending = Completer<MicAudioSettings>();
        final platform = _SettingsPlatform()..saveWait = pending;
        await h.mountDashboard(
          tester,
          platform,
          target: TargetPlatform.android,
        );
        await _open(tester);
        _choice(tester, 'ns').onSelectionChanged!({true});
        await tester.pump();
        await _tap(tester, find.byKey(const ValueKey('audioSettingsClose')));
        await _open(tester);
        if (fail) {
          pending.completeError(StateError('late save failure'));
        } else {
          pending.complete(
            platform.settings.withGain(20).withEffect(_ns, true),
          );
        }
        await tester.pumpAndSettle();
        expect(tester.widget<Slider>(find.byKey(_gain)).value, 0);
        expect(_choice(tester, 'ns').selected, {null});
        expect(find.byKey(const ValueKey('audioSettingsError')), findsNothing);
        expect(find.byKey(const ValueKey('audioSettingsSaved')), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );
  }

  h.testDashboard('Disposed dashboard ignores late save response', (
    tester,
  ) async {
    final pending = Completer<MicAudioSettings>();
    final platform = _SettingsPlatform()..saveWait = pending;
    await h.mountDashboard(tester, platform, target: TargetPlatform.android);
    await _open(tester);
    _choice(tester, 'ns').onSelectionChanged!({true});
    await tester.pump();
    await tester.pumpWidget(const SizedBox());
    pending.completeError(StateError('late save failure'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  for (final brightness in Brightness.values) {
    h.testDashboard('Settings fit 412x915 at 1.3 text scale in $brightness', (
      tester,
    ) async {
      tester.platformDispatcher.textScaleFactorTestValue = 1.3;
      tester.platformDispatcher.platformBrightnessTestValue = brightness;
      final platform = _SettingsPlatform();
      await h.mountDashboard(
        tester,
        platform,
        target: TargetPlatform.android,
        size: const Size(412, 915),
      );
      await _open(tester);
      final semantics = tester.ensureSemantics();
      await tester.pumpAndSettle();
      final node = tester.getSemantics(find.byKey(_gain));
      expect(_adjustableNode(node), isNotNull, reason: node.toStringDeep());
      await _tap(tester, _effect('agc.on'));
      await _tap(tester, _effect('agc.default'));
      expect(platform.writes.last.effects.containsKey(_agc), isFalse);
      // 三段同一行且「系统默认」不折行：标签高度与单字「关」一致。
      final label = tester.getRect(_effect('agc.default'));
      expect(label.height, tester.getRect(_effect('agc.off')).height);
      expect(label.top, tester.getRect(_effect('agc.off')).top);
      expect(
        tester.getRect(_effect('agc.choice')).right,
        lessThanOrEqualTo(412),
      );
      expect(tester.takeException(), isNull);
      semantics.dispose();
    });
  }
}
