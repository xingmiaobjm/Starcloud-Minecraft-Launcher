/// 入口：先启动插件内核，再挂 Fluent 壳。
///
/// 顺序是刻意的：内核 `start()` 之后贡献点表已经就绪，壳的第一帧就能渲染出
/// 完整的导航（不会先闪一下空窗口）。
library;

import 'dart:io' show Platform, ProcessSignal, exit, stdout;
import 'dart:ui' show AppExitResponse;

import 'package:flutter/widgets.dart'
    show AppLifecycleListener, WidgetsFlutterBinding, runApp;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'bootstrap.dart';
import 'shell/app.dart';
import 'shell/webview_self_test.dart';

/// 退出钩子由 binding 持有（AppLifecycleListener 构造时注册为 observer），
/// 这里不保存引用；手动 dispose 会在框架收尾后触发 "used after being disposed"。
Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // 无头/半无头自检：验证 CEF 嵌入渲染（STARCLOUD_WEBVIEW_SELFTEST=1）。
  if (Platform.environment['STARCLOUD_WEBVIEW_SELFTEST'] == '1') {
    await runWebviewSelfTest();
    return;
  }

  final kernel = await bootstrapKernel();

  Future<void> shutdown() async {
    await kernel.stop();
  }

  // 窗口关闭 / 系统退出请求：先按逆序停用插件（csharp_core 会回收桥接进程）。
  AppLifecycleListener(
    onExitRequested: () async {
      await shutdown();
      return AppExitResponse.exit;
    },
  );

  // 终端 Ctrl+C / SIGTERM（开发时 `flutter run` 与桌面会话注销）：同样收尾。
  if (!Platform.isWindows) {
    for (final signal in [ProcessSignal.sigterm, ProcessSignal.sigint]) {
      signal.watch().listen((_) async {
        await shutdown();
        // exit() 不保证刷新 stdout：先把收尾日志写出去，便于取证。
        await stdout.flush();
        exit(0);
      });
    }
  }

  runApp(
    ProviderScope(
      overrides: [kernelProvider.overrideWithValue(kernel)],
      child: const StarcloudApp(),
    ),
  );
}
