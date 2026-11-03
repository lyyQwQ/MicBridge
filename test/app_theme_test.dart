import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/main.dart';

void main() {
  tearDown(() => debugDefaultTargetPlatformOverride = null);

  for (final brightness in Brightness.values) {
    test('Windows uses Microsoft YaHei UI in $brightness', () {
      debugDefaultTargetPlatformOverride = TargetPlatform.windows;
      expect(
        appTheme(brightness).textTheme.bodyMedium?.fontFamily,
        'Microsoft YaHei UI',
      );
    });

    test('Android keeps the default font in $brightness', () {
      debugDefaultTargetPlatformOverride = TargetPlatform.android;
      expect(
        appTheme(brightness).textTheme.bodyMedium?.fontFamily,
        ThemeData(brightness: brightness).textTheme.bodyMedium?.fontFamily,
      );
    });
  }
}
