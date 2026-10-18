import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/shell/pages/version_list_page.dart';

/// 版本页的渲染与交互（用假契约顶替 C# 桥接）。
void main() {
  testWidgets('列表渲染 + 打开目录 + 删除确认', (tester) async {
    final catalog = _FakeCatalog([
      const InstalledVersion(
        id: '1.20.4',
        directory: '/mc/versions/1.20.4',
        isModded: false,
        sizeBytes: 24 * 1024 * 1024,
        isDefault: true,
      ),
      const InstalledVersion(
        id: 'fabric-1.20.4',
        directory: '/mc/versions/fabric-1.20.4',
        isModded: true,
        modLoader: 'fabric',
        inheritsFrom: '1.20.4',
      ),
    ]);

    await tester.pumpWidget(ProviderScope(
      overrides: [kernelDepsProvider.overrideWithValue(_deps(catalog))],
      child: const FluentApp(home: VersionListPage()),
    ));
    await tester.pumpAndSettle();

    expect(find.text('1.20.4'), findsOneWidget);
    expect(find.text('fabric-1.20.4'), findsOneWidget);
    expect(find.text('[默认]'), findsOneWidget);
    expect(find.text('[隔离]'), findsNWidgets(2));
    expect(find.textContaining('[继承 1.20.4]'), findsOneWidget);

    await tester.tap(find.text('打开目录').first);
    await tester.pumpAndSettle();
    expect(catalog.opened, ['1.20.4']);

    await tester.tap(find.text('删除').first);
    await tester.pumpAndSettle();
    final dialog = find.byType(ContentDialog);
    expect(dialog, findsOneWidget);
    expect(find.descendant(of: dialog, matching: find.textContaining('删除 1.20.4')),
        findsOneWidget);

    await tester.tap(find.descendant(of: dialog, matching: find.text('删除')));
    await tester.pumpAndSettle();
    expect(catalog.deleted, ['1.20.4']);
    expect(find.text('1.20.4'), findsNothing);
    expect(find.text('fabric-1.20.4'), findsOneWidget);
  });
}

KernelDeps _deps(MinecraftCatalog catalog) => KernelDeps(
      catalog: catalog,
      installer: _FakeInstaller(),
      launcher: _FakeLauncher(),
      accounts: _FakeAccounts(),
      settings: _FakeSettings(),
      saves: _FakeSaves(),
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
  @override
  Future<List<SaveInfo>> listSaves() async => const [];
  @override
  Future<void> openSaveFolder(SaveInfo save) async {}
  @override
  Future<String> backupSave(SaveInfo save) async => '';
  @override
  Future<void> deleteSave(SaveInfo save) async {}
}

class _FakeCatalog implements MinecraftCatalog {
  _FakeCatalog(List<InstalledVersion> versions) : _versions = [...versions];

  final List<InstalledVersion> _versions;
  final List<String> opened = [];
  final List<String> deleted = [];

  @override
  Future<List<RemoteVersion>> remoteVersions() async => const [];

  @override
  Future<List<InstalledVersion>> installedVersions() async => [..._versions];

  @override
  Future<void> openVersionFolder(String versionId) async => opened.add(versionId);

  @override
  Future<void> setDefaultVersion(String? versionId) async {}

  @override
  Future<void> deleteVersion(String versionId) async {
    deleted.add(versionId);
    _versions.removeWhere((v) => v.id == versionId);
  }
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
  String _gameDir = '/mc';
  @override
  String gameDir() => _gameDir;
  @override
  Future<void> setGameDir(String path) async => _gameDir = path;
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
