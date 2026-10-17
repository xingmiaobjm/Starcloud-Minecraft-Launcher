/// 装配：路径三根 → 调度器 → 内核 → 内置插件 → 启动。
///
/// 这是唯一"知道业务名字"的地方（哪些内置插件要登记），符合规范 §1：
/// 宿主决定装哪些插件；内核本身零业务词。
library;

import 'package:flutter/foundation.dart' show debugPrint;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'kernel/paths.dart';
import 'kernel/plugin_host.dart';
import 'kernel/scheduler.dart';
import 'plugins/builtin/plugin_manager/plugin_manager_plugin.dart';
import 'plugins/builtin/shell_pages/shell_pages_plugin.dart';

/// 内核实例（main 里 override；其余层只读）。
final kernelProvider = Provider<Kernel>(
  (ref) => throw UnimplementedError('kernelProvider 必须在 main 中 override'),
);

/// 构建并启动内核。宿主同步启动时只允许纯内存构造的插件保持 eager（铁律 2）。
Future<Kernel> bootstrapKernel() async {
  final paths = KernelPaths.linux();
  paths.ensureDirectories();

  final kernel = Kernel(
    paths: paths,
    scheduler: EventLoopScheduler(),
    logSink: (level, message) => debugPrint('[kernel/$level] $message'),
  );

  // 编译期登记（规范 §0.3 决策①）。
  kernel.registerPlugin(shellPagesDescriptor(), ShellPagesPlugin.new);
  kernel.registerPlugin(pluginManagerDescriptor(), PluginManagerPlugin.new);

  await kernel.start();
  if (kernel.hasEssentialFailure) {
    // 规范 §4.5：essential 失败时宿主可选择提示后退出。基座期只记录，
    // 壳仍会起来（空导航会显示"没有可用的页面插件"）。
    debugPrint('[kernel/error] 有必需插件激活失败，界面可能不完整');
  }
  return kernel;
}
