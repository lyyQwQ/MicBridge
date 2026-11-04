import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'platform_bridge.dart';

/// 独立设置页面只拥有呈现状态，通过回调读写已确认偏好，不持有采集资源。
class AudioSettingsSheet extends StatefulWidget {
  const AudioSettingsSheet({super.key, required this.load, required this.save});

  final Future<MicAudioSettings> Function() load;
  final Future<MicAudioSettings> Function(MicAudioSettings) save;

  @override
  State<AudioSettingsSheet> createState() => _AudioSettingsSheetState();
}

class _AudioSettingsSheetState extends State<AudioSettingsSheet> {
  MicAudioSettings? _confirmed;
  int _previewGain = 0;
  bool _busy = false;
  bool _saved = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    unawaited(_run(widget.load));
  }

  Future<void> _run(
    Future<MicAudioSettings> Function() operation, {
    bool saving = false,
  }) async {
    // _busy 保证同一页面只有一个请求；关闭后 mounted 为 false，晚到回应直接丢弃。
    if (_busy || !mounted) return;
    setState(() {
      _busy = true;
      _error = null;
      _saved = false;
    });
    try {
      final confirmed = await operation();
      if (!mounted) return;
      setState(() {
        _confirmed = confirmed;
        _previewGain = confirmed.gainDb;
        _saved = saving;
      });
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _previewGain = _confirmed?.gainDb ?? 0;
        _error = switch (error) {
          PlatformException(code: 'settings_invalid') => '设置参数无效，请重试',
          _ => saving ? '保存失败，已恢复上次确认的设置' : '无法读取麦克风设置，请重试',
        };
      });
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _save(MicAudioSettings next) {
    if (_confirmed == null) return;
    unawaited(_run(() => widget.save(next), saving: true));
  }

  @override
  Widget build(BuildContext context) {
    final settings = _confirmed;
    final theme = Theme.of(context);
    return SafeArea(
      child: FractionallySizedBox(
        heightFactor: 0.9,
        child: ListView(
          key: const ValueKey('audioSettingsScroll'),
          padding: const EdgeInsets.fromLTRB(24, 0, 24, 24),
          children: [
            Row(
              children: [
                Expanded(
                  child: Text('麦克风设置', style: theme.textTheme.titleLarge),
                ),
                IconButton(
                  key: const ValueKey('audioSettingsClose'),
                  tooltip: '关闭设置',
                  onPressed: () => Navigator.of(context).pop(),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
            const Text('下次开始采集时生效'),
            const SizedBox(height: 8),
            const Text('选项表示保存的偏好，实际效果取决于系统和设备'),
            if (_busy)
              const LinearProgressIndicator(
                key: ValueKey('audioSettingsBusy'),
                semanticsLabel: '正在处理麦克风设置',
              ),
            if (_error case final error?)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 12),
                child: Semantics(
                  liveRegion: true,
                  child: Text(
                    error,
                    key: const ValueKey('audioSettingsError'),
                    style: TextStyle(color: theme.colorScheme.error),
                  ),
                ),
              ),
            if (settings == null && !_busy)
              TextButton.icon(
                key: const ValueKey('audioSettingsRetry'),
                onPressed: () => unawaited(_run(widget.load)),
                icon: const Icon(Icons.refresh),
                label: const Text('重试'),
              ),
            if (_saved)
              Semantics(
                liveRegion: true,
                child: const Text('已保存', key: ValueKey('audioSettingsSaved')),
              ),
            if (settings != null) ...[
              const SizedBox(height: 24),
              Text('增益：$_previewGain dB', style: theme.textTheme.titleMedium),
              Slider(
                key: const ValueKey('audioGain'),
                min: 0,
                max: MicAudioSettings.maxGainDb.toDouble(),
                divisions: MicAudioSettings.maxGainDb,
                value: _previewGain.toDouble(),
                label: '$_previewGain dB',
                semanticFormatterCallback: (value) => '增益 ${value.round()} dB',
                onChanged: _busy
                    ? null
                    : (value) => setState(() => _previewGain = value.round()),
                onChangeEnd: _busy
                    ? null
                    : (value) {
                        final gain = value.round();
                        if (gain != settings.gainDb) {
                          _save(settings.withGain(gain));
                        }
                      },
              ),
              const Text('增益过高可能破音，请降低增益'),
              for (final effect in MicAudioEffect.values)
                _EffectPreference(
                  effect: effect,
                  value: settings.effects[effect],
                  available: settings.available.contains(effect),
                  busy: _busy,
                  onChanged: (value) =>
                      _save(settings.withEffect(effect, value)),
                ),
            ],
          ],
        ),
      ),
    );
  }
}

/// 无状态效果偏好行；三段选择始终同形，系统默认（null）不冒充实际开或关。
class _EffectPreference extends StatelessWidget {
  const _EffectPreference({
    required this.effect,
    required this.value,
    required this.available,
    required this.busy,
    required this.onChanged,
  });

  final MicAudioEffect effect;
  final bool? value;
  final bool available;
  final bool busy;
  final ValueChanged<bool?> onChanged;

  @override
  Widget build(BuildContext context) {
    final enabled = available && !busy;
    return Padding(
      key: ValueKey('audioEffect.${effect.name}'),
      padding: const EdgeInsets.only(top: 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(effect.label, style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 8),
          Semantics(
            container: true,
            label: '${effect.label}保存的偏好',
            child: SegmentedButton<bool?>(
              key: ValueKey('audioEffect.${effect.name}.choice'),
              expandedInsets: EdgeInsets.zero,
              segments: [
                for (final (choice, name, label) in const [
                  (null, 'default', '系统默认'),
                  (true, 'on', '开'),
                  (false, 'off', '关'),
                ])
                  ButtonSegment(
                    value: choice,
                    label: Text(
                      label,
                      key: ValueKey('audioEffect.${effect.name}.$name'),
                    ),
                  ),
              ],
              selected: {value},
              onSelectionChanged: enabled
                  ? (selection) => onChanged(selection.single)
                  : null,
            ),
          ),
          if (!available)
            const Text('此效果在该设备上不可用', key: ValueKey('audioEffectUnavailable')),
        ],
      ),
    );
  }
}
