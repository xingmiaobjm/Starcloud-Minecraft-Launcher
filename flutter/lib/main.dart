/// 入口：先启动插件内核，再挂 Fluent 壳。
///
/// 顺序是刻意的：内核 `start()` 之后贡献点表已经就绪，壳的第一帧就能渲染出
/// 完整的导航（不会先闪一下空窗口）。
library;

import 'dart:io' show Platform, ProcessSignal, exit;
import 'dart:ui' show AppExitResponse;

import 'package:flutter/widgets.dart'
    show AppLifecycleListener, WidgetsFlutterBinding, runApp;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'bootstrap.dart';
import 'shell/app.dart';

/// 退出钩子必须持有引用：监听器注册在 binding 上，但显式保留便于排查。
AppLifecycleListener? _lifecycle;

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final kernel = await bootstrapKernel();

  Future<void> shutdown() async {
    await kernel.stop();
  }

  // 窗口关闭 / 系统退出请求：先按逆序停用插件（csharp_core 会回收桥接进程）。
  _lifecycle = AppLifecycleListener(
    onExitRequested: () async {
      await shutdown();
      return AppExitResponse.exit;
    },
  );

  // 终端 Ctrl+C / SIGTERM（开发时 `flutter run` 与桌面会话注销）：同样收尾。
  if (!Platform.isWindows) {
    for (final signal in [ProcessSignal.sigterm, ProcessSignal.sigint]) {
      signal.watch().listen((_) async {
        _lifecycle?.dispose();
        await shutdown();
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
