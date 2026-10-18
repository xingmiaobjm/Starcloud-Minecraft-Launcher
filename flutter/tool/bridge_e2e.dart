/// 桥接 E2E：不经过 Flutter 界面，直接驱动 C# 核心完成
/// "设置 → 版本清单 → 安装 → Java 探测 → 启动"全链路。
///
/// 用法（在 flutter/ 下）：
///   dart run tool/bridge_e2e.dart install --version 1.20.4
///   dart run tool/bridge_e2e.dart launch  --version 1.20.4
///   dart run tool/bridge_e2e.dart all
///   dart run tool/bridge_e2e.dart download      # 单文件下载进度
///   dart run tool/bridge_e2e.dart versions      # 版本列表/删除保护
///   dart run tool/bridge_e2e.dart saves         # 存档列表/备份/删除
///
/// 环境变量：
///   STARCLOUD_BRIDGE_DLL   桥接宿主路径（默认 ../bridge-linux-x64/Starcloud.Bridge.dll）
///   STARCLOUD_E2E_DATA     C# 数据根（默认 /tmp/starcloud-e2e）
///   STARCLOUD_E2E_GAME     .minecraft 目录（默认 ~/.minecraft）
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:starcloud_launcher/bridge/core_bridge.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/contracts/content.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_accounts.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_content.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_download.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_minecraft.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_multiplayer.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_saves.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_settings.dart';
import 'package:starcloud_launcher/plugins/builtin/csharp_core/bridge_skins.dart';

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
  final launcher = BridgeLauncher(bridge);

  try {
    await settings.refresh();
    await settings.setGameDir(gameDir);
    // BMCLAPI：国内网络下的官方清单镜像（C# 侧同一套源切换逻辑）。
    await settings.setDownloadSource(DownloadSource.bmclapi);
    stdout.writeln('[e2e] settings: gameDir=${settings.gameDir()} '
        'memory=${settings.memoryMb()} source=${settings.downloadSource().name}');

    if (mode == 'versions') {
      // 造两个临时版本验证删除保护：parent 被 child 的 inheritsFrom 依赖。
      final root = p.join(gameDir, 'versions');
      void makeVersion(String id, {String? inheritsFrom}) {
        final dir = Directory(p.join(root, id))..createSync(recursive: true);
        File(p.join(dir.path, '$id.json')).writeAsStringSync(jsonEncode({
          'id': id,
          'type': 'release',
          'inheritsFrom': ?inheritsFrom,
        }));
        File(p.join(dir.path, '$id.jar')).writeAsBytesSync(List.filled(4096, 0));
      }

      makeVersion('__e2e_parent__');
      makeVersion('__e2e_child__', inheritsFrom: '__e2e_parent__');

      final before = await catalog.installedVersions();
      for (final v in before) {
        stdout.writeln('[ver] ${v.id} size=${v.sizeBytes} jar=${v.jarExists} '
            'default=${v.isDefault} isolated=${v.isolated} '
            'inherits=${v.inheritsFrom} dir=${v.directory}');
      }
      try {
        await catalog.deleteVersion('__e2e_parent__');
        stdout.writeln('[e2e] ERROR: 父版本被依赖时删除未被拒绝');
      } catch (e) {
        stdout.writeln('[e2e] delete parent refused as expected: $e');
      }
      await catalog.deleteVersion('__e2e_child__');
      await catalog.deleteVersion('__e2e_parent__');
      await catalog.setDefaultVersion('1.20.4');
      final after = await catalog.installedVersions();
      stdout.writeln('[e2e] after cleanup: '
          '${after.map((v) => v.id).join(', ')}');
      stdout.writeln('[e2e] default now: '
          '${after.where((v) => v.isDefault).map((v) => v.id).join(', ')}');
      if (Directory(p.join(root, '__e2e_parent__')).existsSync()) {
        throw StateError('临时版本目录未删除');
      }
    }

    if (mode == 'saves') {
      final saves = BridgeSaveCatalog(bridge);
      final world = Directory(p.join(gameDir, 'saves', '__e2e_world__'))
        ..createSync(recursive: true);
      // 故意写一个非 gzip 的 level.dat：解析失败时回退为目录名，仍应列出。
      File(p.join(world.path, 'level.dat'))
          .writeAsBytesSync(List.filled(64, 0));
      File(p.join(world.path, 'data.bin')).writeAsBytesSync(List.filled(1024, 1));

      final listed = await saves.listSaves();
      final matches =
          listed.where((s) => s.folderName == '__e2e_world__').toList();
      if (matches.isEmpty) throw StateError('存档列表里没有临时世界');
      final save = matches.single;
      stdout.writeln('[save] ${save.levelName} root=${save.gameRoot} '
          'version=${save.versionId} isolated=${save.isolated} '
          'size=${save.sizeBytes}');

      final zip = await saves.backupSave(save);
      stdout.writeln('[e2e] backup: $zip (${File(zip).lengthSync()} bytes)');
      File(zip).deleteSync();

      await saves.deleteSave(save);
      if (Directory(world.path).existsSync()) {
        throw StateError('存档目录未删除');
      }
      stdout.writeln('[e2e] save deleted');

      // 越界根目录 / 非法目录名必须被宿主拒绝。
      for (final probe in [
        {'gameRoot': '/tmp', 'folderName': '__e2e_world__'},
        {'gameRoot': gameDir, 'folderName': '../escape'},
      ]) {
        try {
          await bridge.request('saves.delete', probe);
          stdout.writeln('[e2e] ERROR: 非法目标未被拒绝：$probe');
        } catch (e) {
          stdout.writeln('[e2e] refused as expected: $e');
        }
      }
    }

    if (mode == 'accounts') {
      final store = BridgeAccountStore(bridge);
      await store.refresh();
      await store.addOffline('E2EUser1');
      await store.refresh();
      final account =
          store.accounts().firstWhere((a) => a.name == 'E2EUser1');
      await store.setCurrent(account.uuid);
      stdout.writeln('[acc] accounts: ${store.accounts().map((a) => '${a.name}/${a.kind.name}').join(', ')}');
      stdout.writeln('[acc] current: ${store.current()?.name}');

      // 微软设备码：只验证能拿到 code，不等待用户授权。
      final auth = BridgeAccountAuth(bridge, store);
      final code = await auth.startMicrosoftLogin();
      stdout.writeln('[acc] ms userCode=${code.userCode} '
          'uri=${code.verificationUri} interval=${code.interval}');

      await store.remove(account.uuid);
      await store.refresh();
      stdout.writeln('[acc] after remove: '
          '${store.accounts().map((a) => a.name).join(', ')}');
    }

    if (mode == 'version') {
      final content = BridgeContentManager(bridge);
      var settings = await content.settings(version);
      stdout.writeln('[ver] initial isolation=${settings.isolation} '
          'mem=${settings.memoryMode} eff=${settings.effective.minMem}-'
          '${settings.effective.maxMem}');

      settings = await content.saveSettings(version, {
        'isolation': 'shared',
        'memoryMode': 'manual',
        'memoryMinMb': 1024,
        'memoryMaxMb': 6144,
        'windowMode': 'manual',
        'windowWidth': 1280,
        'windowHeight': 720,
      });
      stdout.writeln('[ver] saved isolation=${settings.isolation} '
          'mem=${settings.memoryMode} ${settings.memoryMinMb}-${settings.memoryMaxMb} '
          'window=${settings.windowWidth}x${settings.windowHeight} '
          'eff=${settings.effective.minMem}-${settings.effective.maxMem} '
          'root=${settings.effective.gameRoot}');

      // 恢复跟随全局，避免影响后续启动 E2E。
      await content.saveSettings(version, {
        'isolation': null,
        'memoryMode': 'global',
        'memoryMinMb': null,
        'memoryMaxMb': null,
        'javaMode': 'global',
        'javaPath': null,
        'windowMode': 'global',
        'windowWidth': null,
        'windowHeight': null,
        'fullscreen': null,
      });

      final updates = await content.checkUpdates(version);
      stdout.writeln('[ver] updates: ${updates.length}');
      var deps = await content.checkDependencies(version);
      stdout.writeln('[ver] deps: missing=${deps.missing.length} '
          'incompatible=${deps.incompatible.length}');

      // 装一个真实模组（Fabric API），验证依赖报告，然后清理。
      final result = await content.install(
        kind: ContentKind.mod,
        projectId: 'P7dR8mSH',
        title: 'Fabric API',
        targetVersionId: version,
      );
      stdout.writeln('[ver] installed mod: '
          '${result.installed.map((i) => i.fileName).join(', ')} '
          'missing=${result.missing.length}');
      deps = await content.checkDependencies(version);
      stdout.writeln('[ver] deps after install: '
          'missing=${deps.missing.map((m) => m.title).join(', ')} '
          'incompatible=${deps.incompatible.join(', ')}');
      for (final installed in result.installed) {
        await content.delete(version, ContentKind.mod, installed.fileName);
      }
      stdout.writeln('[ver] cleanup done');

      // 依赖报告：装 Sodium Extra（会自动带上 Sodium），再删掉 Sodium，
      // 检查器应报告缺失的必需依赖。
      final extra = await content.install(
        kind: ContentKind.mod,
        projectId: 'PtjYWJkn',
        title: 'Sodium Extra',
        targetVersionId: version,
      );
      stdout.writeln('[ver] sodium extra: '
          '${extra.installed.map((i) => '${i.title}/${i.fileName}').join(', ')}');
      for (final installed in extra.installed) {
        if (installed.projectId == 'AANobbMI') {
          await content.delete(version, ContentKind.mod, installed.fileName);
        }
      }
      deps = await content.checkDependencies(version);
      stdout.writeln('[ver] deps after removing Sodium: '
          'missing=${deps.missing.map((m) => m.title).join(', ')} '
          'checked=${deps.checkedMods} resolved=${deps.resolvedMods} '
          'inspected=${deps.inspectedVersions}');
      for (final installed in extra.installed) {
        try {
          await content.delete(version, ContentKind.mod, installed.fileName);
        } catch (_) {
          // 已在上一步删除的 Sodium 忽略
        }
      }
      stdout.writeln('[ver] dependency test cleanup done');
    }

    if (mode == 'skins') {
      final store = BridgeAccountStore(bridge);
      await store.refresh();
      await store.addOffline('SkinUser');
      await store.refresh();
      final account =
          store.accounts().firstWhere((a) => a.name == 'SkinUser');
      final skins = BridgeSkinManager(bridge, store);

      final png = p.join(
        Directory.current.path,
        '..',
        'android',
        'src',
        'Starcloud.Launcher.Phone',
        'Assets',
        'Skins',
        'default',
        'steve.png',
      );
      final entry = await skins.importPath(png, name: 'E2E Steve');
      stdout.writeln('[skin] imported ${entry.name} '
          '${entry.width}x${entry.height} model=${entry.model}');

      await skins.assignToAccount(account.uuid, entry.id);
      final assigned = store.accounts().firstWhere((a) => a.uuid == account.uuid);
      stdout.writeln('[skin] assigned skinId=${assigned.skinId} '
          'model=${assigned.skinModel}');

      await skins.assignToAccount(account.uuid, entry.id, model: 'slim');
      final overridden = store.accounts().firstWhere((a) => a.uuid == account.uuid);
      stdout.writeln('[skin] override model=${overridden.skinModel}');

      await skins.assignToAccount(account.uuid, null);
      final cleared = store.accounts().firstWhere((a) => a.uuid == account.uuid);
      stdout.writeln('[skin] cleared skinId=${cleared.skinId}');

      await skins.delete(entry.id);
      stdout.writeln('[skin] library size after delete: '
          '${(await skins.list()).length}');
      await store.remove(account.uuid);
      await store.refresh();
    }

    if (mode == 'content') {
      final content = BridgeContentManager(bridge);
      var detail = await content.detail(version);
      stdout.writeln('[content] ${detail.versionId} mc=${detail.mcVersion} '
          'loader=${detail.loader} root=${detail.gameRoot}');

      // 本地文件：造一个假模组 → 停用 → 启用 → 删除。
      final modsDir =
          Directory(p.join(detail.gameRoot, 'mods'))..createSync(recursive: true);
      final fake = File(p.join(modsDir.path, '__e2e_mod__.jar'))
        ..writeAsBytesSync(List.filled(4096, 7));
      detail = await content.detail(version);
      stdout.writeln('[content] mods: '
          '${detail.mods.map((f) => '${f.displayName}/${f.enabled}').join(', ')}');

      var files = await content.toggle(
          version, ContentKind.mod, '__e2e_mod__.jar', false);
      stdout.writeln('[content] after disable: '
          '${files.map((f) => '${f.displayName}/${f.enabled}').join(', ')}');
      final disabled = files.firstWhere((f) => f.displayName == '__e2e_mod__.jar');
      files = await content.toggle(
          version, ContentKind.mod, disabled.fileName, true);
      final enabled = files.firstWhere((f) => f.displayName == '__e2e_mod__.jar');
      files = await content.delete(
          version, ContentKind.mod, enabled.fileName);
      stdout.writeln('[content] after delete: '
          '${files.map((f) => f.displayName).join(', ')}');
      if (fake.existsSync()) throw StateError('假模组未删除');

      // 在线：搜索 + 安装一个资源包，再清理。
      final hits = await content.search(
        kind: ContentKind.resourcepack,
        query: 'faithful',
        gameVersion: detail.mcVersion,
        limit: 3,
      );
      stdout.writeln('[content] search: ${hits.map((h) => h.title).join(', ')}');
      if (hits.isNotEmpty) {
        final result = await content.install(
          kind: ContentKind.resourcepack,
          projectId: hits.first.projectId,
          title: hits.first.title,
          targetVersionId: version,
          onProgress: (stage) => stdout.writeln('[content] $stage'),
        );
        stdout.writeln('[content] installed: '
            '${result.installed.map((i) => i.fileName).join(', ')} '
            'missing=${result.missing.length}');
        detail = await content.detail(version);
        stdout.writeln('[content] resourcepacks: '
            '${detail.resourcepacks.map((f) => f.fileName).join(', ')}');
        for (final file in detail.resourcepacks) {
          if (result.installed.any((i) => i.fileName == file.fileName)) {
            await content.delete(
                version, ContentKind.resourcepack, file.fileName);
          }
        }
      }
    }

    if (mode == 'multiplayer') {
      final mp = BridgeMultiplayer(bridge);
      var snap = await mp.snapshot();
      stdout.writeln('[mp] providers: '
          '${snap.providers.map((p) => '${p.id}/${p.displayName}').join(', ')}');
      stdout.writeln('[mp] active=${snap.activeId} core=${snap.core.installed} '
          'phase=${snap.state.phase.name} nickname=${snap.nickname}');
      snap = await mp.setNickname('E2EPlayer');
      snap = await mp.setNodeRegion('cn');
      stdout.writeln('[mp] after settings: nickname=${snap.nickname} '
          'region=${snap.nodeRegion}');
      snap = await mp.selectProvider('terracotta');
      stdout.writeln('[mp] provider=${snap.activeId}');
      await mp.dispose();
    }

    if (mode == 'download') {
      final engine = BridgeDownloadEngine(bridge);
      final seen = <String>[];
      final sub = engine.events.listen((e) =>
          seen.add('${e.received}/${e.total} done=${e.done}'));
      // 默认拿 1.20.4 客户端 jar（约 24MB），保证能看到多次进度事件。
      final url = _arg(args, '--url') ??
          'https://bmclapi2.bangbang93.com/version/1.20.4/client';
      final dest = p.join(dataDir, 'download-test.bin');
      await engine.downloadFile(url, dest);
      await Future<void>.delayed(const Duration(milliseconds: 300));
      await sub.cancel();
      await engine.dispose();
      stdout.writeln('[e2e] download events: ${seen.length}');
      for (final line in seen.take(5)) {
        stdout.writeln('[dl] $line');
      }
      stdout.writeln('[e2e] last: ${seen.isEmpty ? 'none' : seen.last}');
      stdout.writeln('[e2e] file: ${File(dest).lengthSync()} bytes');
    }

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
      // 显式带账户 uuid：走 C# 的凭证解析（离线 → legacy 占位令牌）。
      final store = BridgeAccountStore(bridge);
      await store.refresh();
      await store.addOffline('StarcloudE2E');
      final account =
          store.accounts().firstWhere((a) => a.name == 'StarcloudE2E');
      // 本地皮肤注入：回环服务 + --userProperties；启动后确认游戏仍能正常起来。
      final skins = BridgeSkinManager(bridge, store);
      final skinPath = p.join(
        Directory.current.path,
        '..',
        'android',
        'src',
        'Starcloud.Launcher.Phone',
        'Assets',
        'Skins',
        'default',
        'steve.png',
      );
      final skin = await skins.importPath(skinPath, name: 'E2E Launch Skin');
      await skins.assignToAccount(account.uuid, skin.id);
      stdout.writeln('[e2e] local skin assigned: ${skin.name}');
      final session = await launcher.launch(
        versionId: version,
        playerName: account.name,
        accountUuid: account.uuid,
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
      // 无头环境里不跑完整游戏：观察到退出或 90s 后结束（冷启动可能较慢）。
      final code = await done.future.timeout(const Duration(seconds: 90),
          onTimeout: () {
        stdout.writeln('[e2e] timeout waiting for exit; killing java');
        Process.runSync('pkill', ['-f', 'net.minecraft.client.main.Main']);
        return -2;
      });
      await sub.cancel();
      stdout.writeln('[e2e] game exit=$code');
      await skins.delete(skin.id);
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
