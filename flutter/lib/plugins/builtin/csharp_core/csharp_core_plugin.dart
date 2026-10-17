/// 内置插件：C# 核心（跨端复用的下载 / 安装 / 启动后端）。
///
/// 决策（2026-10-18）：**所有下载相关能力只在 Windows 端用 C# 写一次**
/// （`dotnet/src/Starcloud.Bridge` 复用 `Starcloud.Core`），Linux/macOS 的
/// Flutter 端拉起该进程直接复用，不在 Dart 里重写第二份实现。
///
/// 本插件是唯一的"进程宿主"：激活时启动桥接、把五个契约注册进内核；
/// 停用时回收进程。其它插件只按契约消费，不知道背后是 C#。
library;

import 'dart:io';

import 'package:path/path.dart' as p;

import '../../../bridge/core_bridge.dart';
import '../../../contracts/download_engine.dart';
import '../../../contracts/minecraft.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import 'bridge_download.dart';
import 'bridge_minecraft.dart';
import 'bridge_settings.dart';

/// 解析桥接宿主路径：环境变量 → 安装布局 → 仓库开发布局。
String resolveBridgeHost({Map<String, String>? environment}) {
  final env = environment ?? Platform.environment;
  final override = env['STARCLOUD_BRIDGE_DLL'];
  if (override != null && override.isNotEmpty) return p.normalize(override);

  final exeDir = p.dirname(Platform.resolvedExecutable);
  final cwd = Directory.current.path;
  final candidates = <String>[
    // 打包布局：<app>/bridge/Starcloud.Bridge.{dll,exe}
    p.join(exeDir, 'bridge', 'Starcloud.Bridge.dll'),
    p.join(exeDir, 'bridge', 'Starcloud.Bridge.exe'),
    p.join(exeDir, 'Starcloud.Bridge.dll'),
    p.join(exeDir, 'Starcloud.Bridge.exe'),
    // 仓库开发布局：flutter/ 与 bridge-linux-x64/ 同级
    p.join(cwd, '..', 'bridge-linux-x64', 'Starcloud.Bridge.dll'),
    p.join(cwd, '..', 'bridge-win-x64', 'Starcloud.Bridge.exe'),
    p.join(cwd, '..', 'bridge-osx-x64', 'Starcloud.Bridge.dll'),
    p.join(cwd, 'bridge-linux-x64', 'Starcloud.Bridge.dll'),
    p.join(cwd, 'bridge-win-x64', 'Starcloud.Bridge.exe'),
  ];
  for (final candidate in candidates) {
    final normalized = p.normalize(candidate);
    if (File(normalized).existsSync()) return normalized;
  }
  throw BridgeException(
    '未找到 C# 桥接宿主。请先发布：'
    'dotnet publish dotnet/src/Starcloud.Bridge -r linux-x64 -o bridge-linux-x64 '
    '（或用 STARCLOUD_BRIDGE_DLL 指定路径）',
  );
}

/// `dotnet` 可执行文件：环境变量 → 常见安装位置 → PATH。
String resolveDotnetHost({Map<String, String>? environment}) {
  final env = environment ?? Platform.environment;
  final override = env['STARCLOUD_DOTNET'];
  if (override != null && override.isNotEmpty) return override;
  for (final candidate in const [
    '/usr/bin/dotnet',
    '/usr/lib/dotnet/dotnet',
    '/usr/local/bin/dotnet',
  ]) {
    if (File(candidate).existsSync()) return candidate;
  }
  return 'dotnet';
}

class CSharpCorePlugin implements Plugin {
  CSharpCorePlugin({required this.dataRoot});

  /// 内核数据根（由宿主注入）：C# 与 Dart 必须落在同一个根下，
  /// 否则设置、账户、清单缓存会各写一份。
  final String dataRoot;

  CoreBridge? _bridge;
  BridgeDownloadEngine? _download;

  @override
  Future<void> initialize(PluginContext ctx) async {
    final bridge = await CoreBridge.start(
      dllPath: resolveBridgeHost(),
      dataDir: dataRoot,
      dotnetExecutable: resolveDotnetHost(),
    );
    _bridge = bridge;

    final settings = BridgeLauncherSettings(bridge);
    await settings.refresh();
    final download = BridgeDownloadEngine(bridge);
    _download = download;

    ctx.services.add<LauncherSettings>(settings);
    ctx.services.add<DownloadEngine>(download);
    ctx.services.add<MinecraftCatalog>(BridgeCatalog(bridge, settings));
    ctx.services.add<MinecraftInstaller>(BridgeInstaller(bridge));
    ctx.services.add<GameLauncher>(BridgeLauncher(bridge, settings));
    ctx.log('C# 核心就绪（${p.basename(bridge.dllPath)}，'
        'gameDir=${settings.gameDir()}）');
  }

  @override
  Future<void> shutdown() async {
    await _download?.dispose();
    _download = null;
    await _bridge?.dispose();
    _bridge = null;
  }
}

PluginDescriptor csharpCoreDescriptor() => PluginDescriptor.create(
      id: 'starcloud.csharp_core',
      name: 'C# 核心（跨端复用）',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.engine,
      provides: const [
        LauncherSettings,
        DownloadEngine,
        MinecraftCatalog,
        MinecraftInstaller,
        GameLauncher,
      ],
      permissions: const [
        'net.http',
        'fs.write:launcher',
        'game.launch',
        'process.spawn',
      ],
      essential: true,
      description: '下载/安装/启动/设置全部由 C# Starcloud.Core 提供',
      author: 'Starcloud',
    );
