import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/shell/pages/saves_page.dart';

/// 存档页的渲染与交互（用假契约顶替 C# 桥接）。
void main() {
  testWidgets('跨版本列表 + 备份提示 + 删除确认', (tester) async {
    final saves = _FakeSaves([
      const SaveInfo(
        folderName: 'world1',
        levelName: '我的世界',
        gameRoot: '/mc',
        versionId: '',
        isolated: false,
        sizeBytes: 1024 * 1024,
        lastPlayedAt: '2026-10-18T10:00:00',
      ),
      const SaveInfo(
        folderName: 'world2',
        levelName: 'Fabric World',
        gameRoot: '/mc/versions/fabric-1.20.4',
        versionId: 'fabric-1.20.4',
        isolated: true,
        sizeBytes: 2048,
      ),
    ]);

    await tester.pumpWidget(ProviderScope(
      overrides: [kernelDepsProvider.overrideWithValue(_deps(saves))],
      child: const FluentApp(home: SavesPage()),
    ));
    await tester.pumpAndSettle();

    expect(find.text('我的世界'), findsOneWidget);
    expect(find.text('Fabric World'), findsOneWidget);
    expect(find.text('[共享]'), findsOneWidget);
    expect(find.text('[fabric-1.20.4]'), findsOneWidget);
    expect(find.text('[隔离]'), findsOneWidget);

    await tester.tap(find.text('备份').first);
    await tester.pumpAndSettle();
    expect(saves.backedUp, ['world1']);
    expect(find.textContaining('已备份到'), findsOneWidget);

    await tester.tap(find.text('删除').first);
    await tester.pumpAndSettle();
    final dialog = find.byType(ContentDialog);
    expect(dialog, findsOneWidget);
    expect(find.descendant(of: dialog, matching: find.textContaining('我的世界')),
        findsOneWidget);
    await tester.tap(find.descendant(of: dialog, matching: find.text('取消')));
    await tester.pumpAndSettle();
    expect(saves.deleted, isEmpty);
  });
}

KernelDeps _deps(SaveCatalog saves) => KernelDeps(
      catalog: _FakeCatalog(),
      installer: _FakeInstaller(),
      launcher: _FakeLauncher(),
      accounts: _FakeAccounts(),
      settings: _FakeSettings(),
      saves: saves,
      auth: _FakeAuth(),
    );

class _FakeAuth implements AccountAuth {
  @override
  Future<MicrosoftDeviceCode> startMicrosoftLogin() async =>
      throw UnimplementedError();
  @override
  Future<bool> pollMicrosoftLogin(MicrosoftDeviceCode code) async => false;
  @override
  Future<void> loginExternal({
    required String url,
    required String username,
    required String password,
  }) async {}
}

class _FakeSaves implements SaveCatalog {
  _FakeSaves(this._saves);
  final List<SaveInfo> _saves;
  final List<String> backedUp = [];
  final List<String> deleted = [];

  @override
  Future<List<SaveInfo>> listSaves() async => [..._saves];

  @override
  Future<void> openSaveFolder(SaveInfo save) async {}

  @override
  Future<String> backupSave(SaveInfo save) async {
    backedUp.add(save.folderName);
    return '/tmp/${save.folderName}.zip';
  }

  @override
  Future<void> deleteSave(SaveInfo save) async => deleted.add(save.folderName);
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
    String? accountUuid,
  }) async =>
      throw UnimplementedError();
}

class _FakeAccounts implements AccountStore {
  @override
  List<GameAccount> accounts() => const [];
  @override
  GameAccount? current() => null;
  @override
  Future<void> addOffline(String name) async {}
  @override
  Future<void> remove(String uuid) async {}
  @override
  Future<void> setCurrent(String uuid) async {}
}

class _FakeSettings implements LauncherSettings {
  @override
  String gameDir() => '/mc';
  @override
  Future<void> setGameDir(String path) async {}
  @override
  String javaPath() => '';
  @override
  Future<void> setJavaPath(String path) async {}
  @override
  int memoryMb() => 4096;
  @override
  Future<void> setMemoryMb(int mb) async {}
  @override
  DownloadSource downloadSource() => DownloadSource.auto;
  @override
  Future<void> setDownloadSource(DownloadSource source) async {}
  @override
  void addListener(void Function() listener) {}
  @override
  void removeListener(void Function() listener) {}
}
