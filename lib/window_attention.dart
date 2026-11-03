import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

/// Windows runner 的窗口提醒通道；不承载音频、凭据或配对决定。
const windowAttentionChannel = MethodChannel('micbridge/window');

/// 新配对请求时让任务栏按钮闪烁；窗口已在前台时由原生侧跳过。
Future<void> flashPairing() => _invoke('flashPairing');

/// 配对弹窗结束或界面销毁时停止闪烁。
Future<void> stopFlashing() => _invoke('stopFlashing');

// 平台通道按调用顺序投递，弹窗串行打开，因此迟到的 stop 不会越过下一请求的 flash。
Future<void> _invoke(String method) async {
  if (defaultTargetPlatform != TargetPlatform.windows) return;
  try {
    await windowAttentionChannel.invokeMethod<void>(method);
  } catch (_) {
    // 提醒只是附加副作用；失败时保留原弹窗与配对决定流程。
  }
}
