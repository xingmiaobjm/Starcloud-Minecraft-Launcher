// 基座冒烟：全部 UI 内置插件登记后，内核能启动、导航贡献齐全、状态全 Active。
//
// C# 核心会拉起桥接进程，不适合单元测试 —— 这里用一个只注册契约的假 engine
// 顶替它（`includeCore: false`），验证的仍是"贡献点驱动导航"这条链路。
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/kernel/contribution_registry.dart';
import 'package:starcloud_launcher/kernel/descriptor.dart';
import 'package:starcloud_launcher/kernel/paths.dart';
import 'package:starcloud_launcher/kernel/plugin_context.dart';
import 'package:starcloud_launcher/kernel/plugin_host.dart';
import 'package:starcloud_launcher/kernel/scheduler.dart';

void main() {
  test('内核启动后导航由插件贡献（顶部 8 + 底部 2，页面 10）', () async {
    final dir = await Directory.systemTemp.createTemp('starcloud_smoke');
    final kernel = Kernel(
      paths: KernelPaths.linux(env: {'HOME': dir.path}),
      scheduler: EventLoopScheduler(),
    );
    // 与 bootstrap 同一份登记清单（防止两处漂移），但用假核心代替桥接进程。
    registerBuiltinPlugins(kernel, includeCore: false);
    kernel.registerPlugin(_fakeCoreDescriptor(), _FakeCorePlugin.new);
    await kernel.start();

    final nav = kernel.contributions.navItems();
    final footer = kernel.contributions.footerNavItems();
    expect(nav.length, 8, reason: '顶部 8 项（首页/版本/下载/存档/联机/运行状态/账户/插件）');
    expect(footer.length, 2, reason: '底部 启动器日志 + 设置（均由插件贡献）');
    expect(nav.map((c) => c.id), contains('plugin-manager'));
    expect(
      kernel.snapshots().every((s) => s.state == PluginState.active),
      isTrue,
    );
    // 页面贡献齐全（惰性工厂，不调用不创建）。
    expect(kernel.contributions.byKind(ContributionKind.page).length, 10);
    expect(kernel.hasEssentialFailure, isFalse);
  });
}

PluginDescriptor _fakeCoreDescriptor() => PluginDescriptor.create(
      id: 'test.fake_core',
      name: 'Fake C# core',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.engine,
      provides: const [
        LauncherSettings,
        MinecraftCatalog,
        MinecraftInstaller,
        GameLauncher,
      ],
    );

class _FakeCorePlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    final settings = _FakeSettings();
    ctx.services.add<LauncherSettings>(settings);
    ctx.services.add<MinecraftCatalog>(_FakeCatalog());
    ctx.services.add<MinecraftInstaller>(_FakeInstaller());
    ctx.services.add<GameLauncher>(_FakeLauncher());
  }

  @override
  Future<void> shutdown() async {}
}

class _FakeSettings implements LauncherSettings {
  String _gameDir = '/tmp/.minecraft';
  String _javaPath = '';
  int _memoryMb = 4096;
  DownloadSource _source = DownloadSource.official;

  @override
  String gameDir() => _gameDir;
  @override
  Future<void> setGameDir(String path) async => _gameDir = path;
  @override
  String javaPath() => _javaPath;
  @override
  Future<void> setJavaPath(String path) async => _javaPath = path;
  @override
  int memoryMb() => _memoryMb;
  @override
  Future<void> setMemoryMb(int mb) async => _memoryMb = mb;
  @override
  DownloadSource downloadSource() => _source;
  @override
  Future<void> setDownloadSource(DownloadSource source) async => _source = source;
  @override
  void addListener(void Function() listener) {}
  @override
  void removeListener(void Function() listener) {}
}

class _FakeCatalog implements MinecraftCatalog {
  @override
  Future<List<RemoteVersion>> remoteVersions() async => const [];
  @override
  Future<List<InstalledVersion>> installedVersions() async => const [];
  @override
  Future<void> deleteVersion(String versionId) async {}
  @override
  Future<void> openVersionFolder(String versionId) async {}
  @override
  Future<void> setDefaultVersion(String? versionId) async {}
}

class _FakeInstaller implements MinecraftInstaller {
  @override
  Future<void> install(
    String versionId, {
    void Function(InstallProgress progress)? onProgress,
  }) async {}
}

class _FakeLauncher implements GameLauncher {
  @override
  Future<JavaRuntime?> detectJava() async => null;
  @override
  Future<GameSession> launch({
    required String versionId,
    required String playerName,
  }) async =>
      throw UnimplementedError();
}
