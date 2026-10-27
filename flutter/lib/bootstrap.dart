/// 装配：路径三根 → 调度器 → 内核 → 内置插件 → 启动。
///
/// 这是唯一"知道业务名字"的地方（哪些内置插件要登记），符合规范 §1：
/// 宿主决定装哪些插件；内核本身零业务词。
library;

import 'package:flutter/foundation.dart' show debugPrint;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'contracts/content.dart';
import 'contracts/lab.dart';
import 'contracts/minecraft.dart';
import 'contracts/multiplayer.dart';
import 'contracts/nova.dart';
import 'contracts/plugins.dart';
import 'contracts/skins.dart';
import 'kernel/paths.dart';
import 'kernel/plugin_host.dart';
import 'kernel/scheduler.dart';
import 'plugins/builtin/accounts/accounts_page_plugin.dart';
import 'plugins/builtin/csharp_core/csharp_core_plugin.dart';
import 'plugins/builtin/download_page/download_page_plugin.dart';
import 'plugins/builtin/home/home_plugin.dart';
import 'plugins/builtin/plugin_manager/plugin_manager_plugin.dart';
import 'plugins/builtin/settings/settings_page_plugin.dart';
import 'plugins/builtin/shell_pages/shell_pages_plugin.dart';

/// 内核实例（main 里 override；其余层只读）。
///
/// 注意：这个 provider 在首帧就可用 —— 内核的**启动**是异步的（要拉起 C# 桥接进程），
/// 启动完成后才拿到业务契约。壳用 main.dart 的 kernelBootState 判断要不要显示「正在连接核心」。
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
    required this.auth,
    required this.multiplayer,
    required this.content,
    required this.skins,
    required this.nova,
    required this.lab,
    required this.plugins,
  });

  final MinecraftCatalog catalog;
  final MinecraftInstaller installer;
  final GameLauncher launcher;
  final AccountStore accounts;
  final LauncherSettings settings;
  final SaveCatalog saves;
  final AccountAuth auth;
  final MultiplayerControl multiplayer;
  final ContentManager content;
  final SkinManager skins;
  final NovaClient nova;
  final LabService lab;
  final PluginStore plugins;
}

final kernelDepsProvider = Provider<KernelDeps>((ref) {
  final services = ref.watch(kernelProvider).services;
  // 内核还没启动完（桥接进程还在拉起）时 services 里还没有契约，
  // 直接 get 会抛 MissingContractException 并让整个页面崩掉。
  // 这里给出明确的「核心未就绪」异常，页面据此显示提示而不是白屏。
  T require<T>(String hint) {
    try {
      return services.get<T>();
    } on MissingContractException {
      throw MissingContractException(T, '核心尚未就绪（$hint）');
    }
  }

  return KernelDeps(
    catalog: require<MinecraftCatalog>('catalog'),
    installer: require<MinecraftInstaller>('installer'),
    launcher: require<GameLauncher>('launcher'),
    accounts: require<AccountStore>('accounts'),
    settings: require<LauncherSettings>('settings'),
    saves: require<SaveCatalog>('saves'),
    auth: require<AccountAuth>('auth'),
    multiplayer: require<MultiplayerControl>('multiplayer'),
    content: require<ContentManager>('content'),
    skins: require<SkinManager>('skins'),
    nova: require<NovaClient>('nova'),
    lab: require<LabService>('lab'),
    plugins: require<PluginStore>('plugins'),
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
  kernel.registerPlugin(homeDescriptor(), HomePlugin.new);
  kernel.registerPlugin(downloadPageDescriptor(), DownloadPagePlugin.new);
  kernel.registerPlugin(accountsPageDescriptor(), AccountsPagePlugin.new);
  kernel.registerPlugin(settingsPageDescriptor(), SettingsPagePlugin.new);
  kernel.registerPlugin(shellPagesDescriptor(), ShellPagesPlugin.new);
  kernel.registerPlugin(pluginManagerDescriptor(), PluginManagerPlugin.new);
}

/// 构建内核（**不启动**）。启动是异步的，见 [startKernel]。
///
/// 拆成两步是为了让首帧不必等桥接进程：csharp_core 插件的初始化要拉起 C# 桥接，
/// 慢的时候要好几秒；原来 main 里「构建 + 启动」一次做完之后才 runApp，
/// 用户看到的是「点了图标没反应」。现在先出壳（显示「正在连接核心…」），内核就绪后再切内容。
Kernel buildKernel() {
  final paths = KernelPaths.linux();
  paths.ensureDirectories();

  final kernel = Kernel(
    paths: paths,
    scheduler: EventLoopScheduler(),
    logSink: (level, message) => debugPrint('[kernel/$level] $message'),
  );

  registerBuiltinPlugins(kernel, dataRoot: paths.dataRoot);
  return kernel;
}

/// 启动内核并返回启动结果。宿主同步启动时只允许纯内存构造的插件保持 eager（铁律 2）。
Future<void> startKernel(Kernel kernel) async {
  await kernel.start();
  if (kernel.hasEssentialFailure) {
    // 规范 §4.5：essential 失败时宿主可选择提示后退出。基座期只记录，
    // 壳仍会起来（空导航会显示"没有可用的页面插件"）。
    debugPrint('[kernel/error] 有必需插件激活失败，界面可能不完整');
  }
}

