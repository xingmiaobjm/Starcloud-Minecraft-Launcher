/// 入口：先出壳，再在后台启动插件内核。
///
/// 顺序是刻意的：内核 `start()` 要拉起 C# 桥接进程，慢的时候要好几秒。
/// 原来 main 里「构建 + 启动内核」一次做完之后才 runApp，用户看到的是「点了图标没反应」，
/// 最坏要等到桥接超时（30 秒）。现在首帧直接出壳（显示「正在连接核心…」），
/// 内核就绪后再切到业务页面。
library;

import 'dart:async';
import 'dart:io' show Platform, ProcessSignal, exit, stdout;
import 'dart:ui' show AppExitResponse;

import 'package:flutter/foundation.dart' show debugPrint;
import 'package:flutter/widgets.dart'
    show AppLifecycleListener, WidgetsFlutterBinding, runApp;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'bootstrap.dart';
import 'kernel/plugin_host.dart' show Kernel;
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

  final kernel = buildKernel();

  // 壳先起来：首帧不桥接阻塞
  runApp(
    ProviderScope(
      overrides: [kernelProvider.overrideWithValue(kernel)],
      child: const StarcloudApp(),
    ),
  );

  // 内核在后台启动：桥接进程慢的时候界面已经在跑
  unawaited(_startKernelInBackground(kernel));

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
}

/// 后台启动内核：完成后通知壳切到业务页面。
///
/// 这里不用 provider 写状态（ProviderScope 的容器在 WidgetsBinding 之外拿不到），
/// 改为直接通知 App 的 ValueNotifier —— 壳按它决定显示「正在连接核心…」还是内容。
final ValueNotifier<KernelBootState> kernelBootState =
    ValueNotifier<KernelBootState>(const KernelBootState.starting());

/// 内核启动状态：启动中 / 就绪 / 失败。
class KernelBootState {
  const KernelBootState.starting()
      : ready = false,
        error = null;
  const KernelBootState.ready()
      : ready = true,
        error = null;
  const KernelBootState.failed(String message)
      : ready = false,
        error = message;

  final bool ready;
  final String? error;
}

Future<void> _startKernelInBackground(Kernel kernel) async {
  try {
    await startKernel(kernel);
    kernelBootState.value = const KernelBootState.ready();
  } catch (e) {
    debugPrint('[main] 内核启动失败：$e');
    kernelBootState.value = KernelBootState.failed('$e');
  }
}
