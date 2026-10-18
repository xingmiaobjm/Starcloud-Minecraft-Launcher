import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/content.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/contracts/skins.dart';
import 'package:starcloud_launcher/contracts/multiplayer.dart';
import 'package:starcloud_launcher/shell/pages/multiplayer_page.dart';

/// 联机页：状态渲染、创建房间、加入房间。
void main() {
  testWidgets('联机状态与操作', (tester) async {
    final multiplayer = _FakeMultiplayer();
    await tester.pumpWidget(ProviderScope(
      overrides: [
        kernelDepsProvider.overrideWithValue(_deps(multiplayer)),
      ],
      child: const FluentApp(home: MultiplayerPage()),
    ));
    await tester.pumpAndSettle();

    expect(find.text('陶瓦联机'), findsOneWidget);
    expect(find.text('空闲'), findsOneWidget);
    expect(find.text('创建房间'), findsOneWidget);

    await tester.tap(find.text('创建房间'));
    await tester.pumpAndSettle();
    expect(multiplayer.hosted, isTrue);
    expect(find.text('ABC123'), findsOneWidget);

    await tester.tap(find.text('加入房间'));
    await tester.pumpAndSettle();
    final dialog = find.byType(ContentDialog);
    expect(dialog, findsOneWidget);
    await tester.enterText(
      find.descendant(of: dialog, matching: find.byType(TextBox)),
      'ROOM42',
    );
    await tester.tap(find.descendant(of: dialog, matching: find.text('加入')));
    await tester.pumpAndSettle();
    expect(multiplayer.joined, 'ROOM42');
  });
}

KernelDeps _deps(MultiplayerControl multiplayer) => KernelDeps(
      catalog: _FakeCatalog(),
      installer: _FakeInstaller(),
      launcher: _FakeLauncher(),
      accounts: _FakeAccounts(),
      settings: _FakeSettings(),
      saves: _FakeSaves(),
      auth: _FakeAuth(),
      multiplayer: multiplayer,
      content: _FakeContent(),
      skins: _FakeSkins(),
    );

class _FakeSkins implements SkinManager {
  @override
  Future<List<SkinEntry>> list() async => const [];
  @override
  Future<SkinEntry> importPath(String path, {String? name}) async =>
      throw UnimplementedError();
  @override
  Future<SkinEntry> importUrl(String url, {String? name}) async =>
      throw UnimplementedError();
  @override
  Future<SkinEntry> rename(String id, String name) async =>
      throw UnimplementedError();
  @override
  Future<SkinEntry> setModel(String id, String model) async =>
      throw UnimplementedError();
  @override
  Future<void> delete(String id) async {}
  @override
  Future<void> assignToAccount(String uuid, String? skinId, {String? model}) async {}
  @override
  Future<List<StationSkin>> stationList(String accountUuid) async => const [];
  @override
  Future<String> stationApply(String accountUuid, String skinId) async => '';
}

class _FakeContent implements ContentManager {
  @override
  Future<VersionSettings> settings(String versionId) async => VersionSettings(
        versionId: versionId,
        effective: const EffectiveInstanceSettings(
          gameRoot: '/mc',
          isolated: true,
          minMem: 512,
          maxMem: 4096,
        ),
      );
  @override
  Future<VersionSettings> saveSettings(
    String versionId,
    Map<String, Object?> patch,
  ) =>
      settings(versionId);
  @override
  Future<List<ContentUpdate>> checkUpdates(String versionId) async => const [];
  @override
  Future<String> updateMod(String versionId, String fileName) async => fileName;
  @override
  Future<DependencyReport> checkDependencies(String versionId) async =>
      const DependencyReport();

  @override
  Future<VersionContent> detail(String versionId) async => VersionContent(
        versionId: versionId,
        gameRoot: '/mc',
        isolated: true,
        mcVersion: '1.20.4',
        loader: 'vanilla',
      );
  @override
  Future<List<ContentFile>> toggle(
    String versionId,
    ContentKind kind,
    String fileName,
    bool enabled,
  ) async =>
      const [];
  @override
  Future<List<ContentFile>> delete(
    String versionId,
    ContentKind kind,
    String fileName,
  ) async =>
      const [];
  @override
  Future<void> openFolder(String versionId, ContentKind kind) async {}
  @override
  Future<List<ContentSearchHit>> search({
    required ContentKind kind,
    required String query,
    String? gameVersion,
    String? loader,
    int offset = 0,
    int limit = 20,
  }) async =>
      const [];
  @override
  Future<ContentInstallResult> install({
    required ContentKind kind,
    required String projectId,
    required String title,
    required String targetVersionId,
    void Function(String stage)? onProgress,
  }) async =>
      const ContentInstallResult();
}

class _FakeMultiplayer implements MultiplayerControl {
  MultiplayerSnapshot current = const MultiplayerSnapshot(
    providers: [
      MultiplayerProviderInfo(
        id: 'terracotta',
        displayName: '陶瓦联机',
        vendor: 'Terracotta',
        builtIn: true,
      ),
    ],
    activeId: 'terracotta',
    nickname: 'Player',
    nodeRegion: 'auto',
    core: MultiplayerCoreStatus(installed: true, sessionRunning: false),
    state: MultiplayerState(phase: MultiplayerPhase.idle),
  );
  final _changes = StreamController<MultiplayerSnapshot>.broadcast();
  bool hosted = false;
  String? joined;

  @override
  Stream<MultiplayerSnapshot> get changes => _changes.stream;

  @override
  Future<MultiplayerSnapshot> snapshot() async => current;

  @override
  Future<MultiplayerSnapshot> selectProvider(String id) async => current;

  @override
  Future<MultiplayerSnapshot> setNickname(String name) async => current;

  @override
  Future<MultiplayerSnapshot> setNodeRegion(String region) async => current;

  @override
  Future<MultiplayerSnapshot> host() async {
    hosted = true;
    current = MultiplayerSnapshot(
      providers: current.providers,
      activeId: current.activeId,
      nickname: current.nickname,
      nodeRegion: current.nodeRegion,
      core: const MultiplayerCoreStatus(installed: true, sessionRunning: true),
      state: const MultiplayerState(
        phase: MultiplayerPhase.hostReady,
        room: 'ABC123',
      ),
    );
    _changes.add(current);
    return current;
  }

  @override
  Future<MultiplayerSnapshot> join(String room) async {
    joined = room;
    return current;
  }

  @override
  Future<MultiplayerSnapshot> idle() async => current;

  @override
  Future<MultiplayerSnapshot> stop() async => current;

  @override
  Future<void> installCore({
    void Function(MultiplayerProgress progress)? onProgress,
  }) async {}

  @override
  Future<String> logs() async => '';

  @override
  Future<List<String>> nodes() async => const [];
}

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
