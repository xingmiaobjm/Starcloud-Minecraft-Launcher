/// 装配：路径三根 → 调度器 → 内核 → 内置插件 → 启动。
///
/// 这是唯一"知道业务名字"的地方（哪些内置插件要登记），符合规范 §1：
/// 宿主决定装哪些插件；内核本身零业务词。
library;

import 'package:flutter/foundation.dart' show debugPrint;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'contracts/minecraft.dart';
import 'kernel/paths.dart';
import 'kernel/plugin_host.dart';
import 'kernel/scheduler.dart';
import 'plugins/builtin/accounts/accounts_page_plugin.dart';
import 'plugins/builtin/accounts/accounts_plugin.dart';
import 'plugins/builtin/csharp_core/csharp_core_plugin.dart';
import 'plugins/builtin/download_page/download_page_plugin.dart';
import 'plugins/builtin/home/home_plugin.dart';
import 'plugins/builtin/plugin_manager/plugin_manager_plugin.dart';
import 'plugins/builtin/settings/settings_page_plugin.dart';
import 'plugins/builtin/shell_pages/shell_pages_plugin.dart';

/// 内核实例（main 里 override；其余层只读）。
final kernelProvider = Provider<Kernel>(
  (ref) => throw UnimplementedError('kernelProvider 必须在 main 中 override'),
);

/// 业务契约的解析出口（内核启动完成后才可读；UI 页面按契约消费，不碰实现）。
class KernelDeps {
  const KernelDeps({
    required this.catalog,
    required this.installer,
    required this.launcher,
    required this.accounts,
    required this.settings,
    required this.saves,
  });

  final MinecraftCatalog catalog;
  final MinecraftInstaller installer;
  final GameLauncher launcher;
  final AccountStore accounts;
  final LauncherSettings settings;
  final SaveCatalog saves;
}

final kernelDepsProvider = Provider<KernelDeps>((ref) {
  final services = ref.watch(kernelProvider).services;
  return KernelDeps(
    catalog: services.get<MinecraftCatalog>(),
    installer: services.get<MinecraftInstaller>(),
    launcher: services.get<GameLauncher>(),
    accounts: services.get<AccountStore>(),
    settings: services.get<LauncherSettings>(),
    saves: services.get<SaveCatalog>(),
  );
});

/// 编译期登记全部内置插件（测试与运行共用同一份清单，防止漂移）。
///
/// `includeCore: false` 供纯 UI/内核测试使用：C# 核心会拉起桥接进程，
/// 单元测试里用假的 engine 插件顶替（见 test/smoke_test.dart）。
void registerBuiltinPlugins(
  Kernel kernel, {
  bool includeCore = true,
  String? dataRoot,
}) {
  if (includeCore) {
    final root = dataRoot ?? KernelPaths.linux().dataRoot;
    kernel.registerPlugin(
      csharpCoreDescriptor(),
      () => CSharpCorePlugin(dataRoot: root),
    );
  }
  kernel.registerPlugin(accountsDescriptor(), AccountsPlugin.new);
  kernel.registerPlugin(homeDescriptor(), HomePlugin.new);
  kernel.registerPlugin(downloadPageDescriptor(), DownloadPagePlugin.new);
  kernel.registerPlugin(accountsPageDescriptor(), AccountsPagePlugin.new);
  kernel.registerPlugin(settingsPageDescriptor(), SettingsPagePlugin.new);
  kernel.registerPlugin(shellPagesDescriptor(), ShellPagesPlugin.new);
  kernel.registerPlugin(pluginManagerDescriptor(), PluginManagerPlugin.new);
}

/// 构建并启动内核。宿主同步启动时只允许纯内存构造的插件保持 eager（铁律 2）。
Future<Kernel> bootstrapKernel() async {
  final paths = KernelPaths.linux();
  paths.ensureDirectories();

  final kernel = Kernel(
    paths: paths,
    scheduler: EventLoopScheduler(),
    logSink: (level, message) => debugPrint('[kernel/$level] $message'),
  );

  registerBuiltinPlugins(kernel, dataRoot: paths.dataRoot);

  await kernel.start();
  if (kernel.hasEssentialFailure) {
    // 规范 §4.5：essential 失败时宿主可选择提示后退出。基座期只记录，
    // 壳仍会起来（空导航会显示"没有可用的页面插件"）。
    debugPrint('[kernel/error] 有必需插件激活失败，界面可能不完整');
  }
  return kernel;
}
