import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/platform_bridge.dart';

import 'widget_test.dart' as h;

const _system = InputDevice(id: -1, name: '跟随系统', address: '', type: 0);
const _old = InputDevice(id: 7, name: '旧蓝牙麦克风', address: '', type: 7);
const _replacement = InputDevice(id: 26, name: '新蓝牙麦克风', address: '', type: 26);

class _SelectionPlatform extends h.TestPlatform {
  final selections = <int>[];

  @override
  Future<void> selectInput(int id) async {
    selections.add(id);
  }
}

Finder get _sheet => find.byType(BottomSheet);
Finder _inSheet(Finder matching) =>
    find.descendant(of: _sheet, matching: matching);
RadioGroup<int> _group(WidgetTester tester) =>
    tester.widget<RadioGroup<int>>(_inSheet(find.byType(RadioGroup<int>)));

void main() {
  h.testDashboard(
    'open input sheet follows replacement list and native selection',
    (tester) async {
      final platform = _SelectionPlatform()
        ..availableInputs = const [_system, _old]
        ..current = const MicSnapshot(
          state: MicState.idle,
          connected: true,
          selectedInputId: 7,
          inputSelectionDetail: '原来的选择说明',
        );
      await h.mountDashboard(tester, platform, target: TargetPlatform.android);
      await h.openMicrophones(tester);
      final sheetElement = tester.element(_sheet);
      final staleSelection = _group(tester).onChanged;
      expect(_inSheet(find.text(_old.name)), findsOneWidget);
      expect(_group(tester).groupValue, 7);

      platform.availableInputs = const [_system, _replacement];
      platform.emit(
        const MicSnapshot(
          state: MicState.idle,
          connected: true,
          selectedInputId: -1,
          inputSelectionDetail: '设备已断开，暂时跟随系统',
        ),
      );
      await tester.pumpAndSettle();

      // 保持同一个面板打开，检查列表、单选状态和说明同步变化。
      expect(tester.element(_sheet), same(sheetElement));
      expect(_inSheet(find.text(_old.name)), findsNothing);
      expect(_inSheet(find.text(_replacement.name)), findsOneWidget);
      expect(_inSheet(find.text('原来的选择说明')), findsNothing);
      expect(_inSheet(find.text('设备已断开，暂时跟随系统')), findsOneWidget);
      expect(_group(tester).groupValue, -1);

      // 即使旧行的回调已经排队，消失的设备也不能提交或关闭面板。
      staleSelection(7);
      await tester.pumpAndSettle();
      expect(platform.selections, isEmpty);
      expect(_sheet, findsOneWidget);

      await tester.tap(_inSheet(find.text(_replacement.name)));
      await tester.pumpAndSettle();
      expect(platform.selections, [26]);
      expect(_sheet, findsNothing);
      expect(tester.takeException(), isNull);
    },
  );

  h.testDashboard(
    'open input sheet updates selection and detail without a list change',
    (tester) async {
      final platform = _SelectionPlatform()
        ..availableInputs = const [_system, _old]
        ..current = const MicSnapshot(
          state: MicState.idle,
          selectedInputId: -1,
        );
      await h.mountDashboard(tester, platform, target: TargetPlatform.android);
      await h.openMicrophones(tester);

      platform.emit(
        const MicSnapshot(
          state: MicState.idle,
          selectedInputId: 7,
          inputSelectionDetail: '已恢复蓝牙麦克风',
        ),
      );
      await tester.pumpAndSettle();
      expect(_group(tester).groupValue, 7);
      expect(_inSheet(find.text('已恢复蓝牙麦克风')), findsOneWidget);
      expect(platform.selections, isEmpty);
    },
  );

  h.testDashboard(
    'open input sheet clears removed devices when enumeration becomes empty',
    (tester) async {
      final platform = _SelectionPlatform()
        ..availableInputs = const [_old]
        ..current = const MicSnapshot(state: MicState.idle, selectedInputId: 7);
      await h.mountDashboard(tester, platform, target: TargetPlatform.android);
      await h.openMicrophones(tester);
      final staleSelection = _group(tester).onChanged;

      platform.availableInputs = const [];
      platform.emit(
        const MicSnapshot(state: MicState.idle, selectedInputId: 7),
      );
      await tester.pumpAndSettle();
      expect(_sheet, findsOneWidget);
      expect(_inSheet(find.byType(RadioListTile<int>)), findsNothing);
      expect(_group(tester).groupValue, isNull);
      staleSelection(7);
      await tester.pumpAndSettle();
      expect(platform.selections, isEmpty);
      expect(_sheet, findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );
}
