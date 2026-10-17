/// 桥接 E2E：不经过 Flutter 界面，直接驱动 C# 核心完成
/// "设置 → 版本清单 → 安装 → Java 探测 → 启动"全链路。
///
/// 用法（在 flutter/ 下）：
///   dart run tool/bridge_e2e.dart install --version 1.20.4
///   dart run tool/bridge_e2e.dart launch  --version 1.20.4
///   dart run tool/bridge_e2e.dart all
///
/// 环境变量：
///   STARCLOUD_BRIDGE_DLL   桥接宿主路径（默认 ../bridge-linux-x64/Starcloud.Bridge.dll）
///   STARCLOUD_E2E_DATA     C# 数据根（默认 /tmp/starcloud-e2e）
///   STARCLOUD_E2E_GAME     .minecraft 目录（默认 ~/.minecraft）
library;

import 'dart:async';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:starcloud_launcher/bridge/core_bridge.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_minecraft.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_settings.dart';

Future<void> main(List<String> args) async {
  final mode = args.isEmpty ? 'all' : args.first;
  final version = _arg(args, '--version') ?? '1.20.4';
  final dataDir = Platform.environment['STARCLOUD_E2E_DATA'] ??
      '/tmp/starcloud-e2e';
  final gameDir = Platform.environment['STARCLOUD_E2E_GAME'] ??
      p.join(Platform.environment['HOME'] ?? '/root', '.minecraft');
  final dll = Platform.environment['STARCLOUD_BRIDGE_DLL'] ??
      p.normalize(p.join(Directory.current.path, '..', 'bridge-linux-x64',
          'Starcloud.Bridge.dll'));

  stdout.writeln('[e2e] bridge=$dll');
  stdout.writeln('[e2e] data=$dataDir game=$gameDir version=$version mode=$mode');

  final bridge = await CoreBridge.start(
    dllPath: dll,
    dataDir: dataDir,
    dotnetExecutable: _dotnet(),
  );
  final settings = BridgeLauncherSettings(bridge);
  final catalog = BridgeCatalog(bridge, settings);
  final installer = BridgeInstaller(bridge);
  final launcher = BridgeLauncher(bridge, settings);

  try {
    await settings.refresh();
    await settings.setGameDir(gameDir);
    // BMCLAPI：国内网络下的官方清单镜像（C# 侧同一套源切换逻辑）。
    await settings.setDownloadSource(DownloadSource.bmclapi);
    stdout.writeln('[e2e] settings: gameDir=${settings.gameDir()} '
        'memory=${settings.memoryMb()} source=${settings.downloadSource().name}');

    if (mode == 'install' || mode == 'all') {
      final remote = await catalog.remoteVersions();
      stdout.writeln('[e2e] remote versions: ${remote.length}');
      if (!remote.any((v) => v.id == version)) {
        throw StateError('远端清单里没有 $version');
      }
      var lastPrint = DateTime.fromMillisecondsSinceEpoch(0);
      var lastStage = '';
      await installer.install(version, onProgress: (progress) {
        final now = DateTime.now();
        if (progress.stage != lastStage ||
            now.difference(lastPrint) > const Duration(seconds: 2)) {
          lastStage = progress.stage;
          lastPrint = now;
          stdout.writeln('[install] ${progress.stage} · ${progress.detail}'
              '${progress.total != null ? ' (${progress.current}/${progress.total})' : ''}');
        }
      });
      stdout.writeln('[e2e] install done');
    }

    final installed = await catalog.installedVersions();
    stdout.writeln('[e2e] installed: '
        '${installed.map((v) => v.id).join(', ')}');

    final java = await launcher.detectJava();
    stdout.writeln('[e2e] java: ${java?.path} (${java?.version}, major ${java?.major})');

    if (mode == 'launch' || mode == 'all') {
      if (!installed.any((v) => v.id == version)) {
        throw StateError('未安装 $version，先跑 install');
      }
      final session = await launcher.launch(
        versionId: version,
        playerName: 'StarcloudE2E',
      );
      stdout.writeln('[e2e] launched pid=${session.pid}');
      final lines = <String>[];
      final done = Completer<int>();
      final sub = session.logLines.listen((line) {
        lines.add(line);
        if (lines.length <= 60) stdout.writeln('[game] $line');
        if (line.contains('Setting user') || line.contains('Stopping!')) {
          stdout.writeln('[e2e] marker: $line');
        }
      });
      unawaited(session.exitCode.then((code) {
        if (!done.isCompleted) done.complete(code);
      }).catchError((Object e) {
        if (!done.isCompleted) done.complete(-1);
      }));
      // 无头环境里不跑完整游戏：观察到渲染线程/退出标记或 45s 后结束。
      final code = await done.future.timeout(const Duration(seconds: 45),
          onTimeout: () {
        stdout.writeln('[e2e] timeout waiting for exit; killing java');
        Process.runSync('pkill', ['-f', 'net.minecraft.client.main.Main']);
        return -2;
      });
      await sub.cancel();
      stdout.writeln('[e2e] game exit=$code');
    }
  } finally {
    await bridge.dispose();
  }
  stdout.writeln('[e2e] OK');
}

String _dotnet() {
  final override = Platform.environment['STARCLOUD_DOTNET'];
  if (override != null && override.isNotEmpty) return override;
  for (final candidate in const ['/usr/bin/dotnet', '/usr/lib/dotnet/dotnet']) {
    if (File(candidate).existsSync()) return candidate;
  }
  return 'dotnet';
}

String? _arg(List<String> args, String name) {
  final index = args.indexOf(name);
  if (index < 0 || index + 1 >= args.length) return null;
  return args[index + 1];
}
