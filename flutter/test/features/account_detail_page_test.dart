import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/content.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/contracts/multiplayer.dart';
import 'package:starcloud_launcher/contracts/skins.dart';
import 'package:starcloud_launcher/shell/pages/account_detail_page.dart';

/// 账户详情页：当前皮肤、皮肤库、指定/取消、页内返回。
void main() {
  testWidgets('皮肤库与指定/取消', (tester) async {
    final skins = _FakeSkins();
    final store = _FakeStore();
    var backed = false;
    await tester.pumpWidget(ProviderScope(
      overrides: [
        kernelDepsProvider.overrideWithValue(_deps(skins, store)),
      ],
      child: FluentApp(
        home: AccountDetailPage(
          account: store.account,
          onBack: () => backed = true,
        ),
      ),
    ));
    await tester.pumpAndSettle();

    expect(find.text('当前皮肤'), findsOneWidget);
    expect(find.text('Steve'), findsWidgets);
    expect(find.text('使用中'), findsOneWidget);

    await tester.tap(find.text('取消皮肤'));
    await tester.pumpAndSettle();
    expect(skins.assigned, ['u1:null']);

    final useButton = find.text('使用').first;
    await tester.ensureVisible(useButton);
    await tester.pumpAndSettle();
    await tester.tap(useButton);
    await tester.pumpAndSettle();
    expect(skins.assigned.last, 'u1:s2');

    await tester.tap(find.byIcon(FluentIcons.back));
    await tester.pumpAndSettle();
    expect(backed, isTrue);
  });
}

KernelDeps _deps(SkinManager skins, AccountStore accounts) => KernelDeps(
      catalog: _FakeCatalog(),
      installer: _FakeInstaller(),
      launcher: _FakeLauncher(),
      accounts: accounts,
      settings: _FakeSettings(),
      saves: _FakeSaves(),
      auth: _FakeAuth(),
      multiplayer: _FakeMultiplayer(),
      content: _FakeContent(),
      skins: skins,
    );

class _FakeSkins implements SkinManager {
  final List<String> assigned = [];

  @override
  Future<List<SkinEntry>> list() async => const [
        SkinEntry(
          id: 's1',
          name: 'Steve',
          model: 'classic',
          fileName: 'steve.png',
          path: '/nonexistent/steve.png',
          width: 64,
          height: 64,
        ),
        SkinEntry(
          id: 's2',
          name: 'Alex',
          model: 'slim',
          fileName: 'alex.png',
          path: '/nonexistent/alex.png',
          width: 64,
          height: 64,
        ),
      ];

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
  Future<void> assignToAccount(String uuid, String? skinId, {String? model}) async {
    assigned.add('$uuid:${skinId ?? 'null'}');
  }

  @override
  Future<List<StationSkin>> stationList(String accountUuid) async => const [];
  @override
  Future<String> stationApply(String accountUuid, String skinId) async => '';
}

class _FakeStore implements AccountStore {
  final GameAccount account = const GameAccount(
    uuid: 'u1',
    name: 'Steve',
    kind: AccountKind.offline,
    hasSession: true,
    skinId: 's1',
    skinModel: SkinModels.auto,
  );

  @override
  List<GameAccount> accounts() => [account];
  @override
  GameAccount? current() => account;
  @override
  Future<void> addOffline(String name) async {}
  @override
  Future<void> remove(String uuid) async {}
  @override
  Future<void> setCurrent(String uuid) async {}
}

class _FakeContent implements ContentManager {
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
  @override
  Stream<MultiplayerSnapshot> get changes => const Stream.empty();
  @override
  Future<MultiplayerSnapshot> snapshot() async => const MultiplayerSnapshot(
        providers: [],
        activeId: '',
        nickname: '',
        nodeRegion: 'auto',
        core: MultiplayerCoreStatus(installed: false, sessionRunning: false),
        state: MultiplayerState(phase: MultiplayerPhase.idle),
      );
  @override
  Future<MultiplayerSnapshot> selectProvider(String id) => snapshot();
  @override
  Future<MultiplayerSnapshot> setNickname(String name) => snapshot();
  @override
  Future<MultiplayerSnapshot> setNodeRegion(String region) => snapshot();
  @override
  Future<MultiplayerSnapshot> host() => snapshot();
  @override
  Future<MultiplayerSnapshot> join(String room) => snapshot();
  @override
  Future<MultiplayerSnapshot> idle() => snapshot();
  @override
  Future<MultiplayerSnapshot> stop() => snapshot();
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
