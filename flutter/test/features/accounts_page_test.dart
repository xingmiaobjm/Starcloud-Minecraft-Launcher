import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/content.dart';
import 'package:starcloud_launcher/contracts/lab.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/contracts/nova.dart';
import 'package:starcloud_launcher/contracts/skins.dart';
import 'package:starcloud_launcher/contracts/multiplayer.dart';
import 'package:starcloud_launcher/shell/pages/accounts_page.dart';

/// 账户页：三类身份的展示 + 离线添加 + 移除确认。
void main() {
  testWidgets('账户列表与离线添加', (tester) async {
    final store = _FakeStore([
      const GameAccount(
        uuid: 'u-offline',
        name: 'Steve',
        kind: AccountKind.offline,
        hasSession: true,
      ),
      const GameAccount(
        uuid: 'u-ms',
        name: 'Alex',
        kind: AccountKind.microsoft,
        hasSession: false,
      ),
    ], currentUuid: 'u-offline');

    await tester.pumpWidget(ProviderScope(
      overrides: [kernelDepsProvider.overrideWithValue(_deps(store))],
      child: const FluentApp(home: AccountsPage()),
    ));
    await tester.pumpAndSettle();

    expect(find.text('Steve'), findsOneWidget);
    expect(find.text('Alex'), findsOneWidget);
    expect(find.text('[微软正版]'), findsOneWidget);
    expect(find.text('[凭证已过期]'), findsOneWidget);
    expect(find.text('[当前]'), findsOneWidget);

    await tester.tap(find.text('添加账户'));
    await tester.pumpAndSettle();
    final dialog = find.byType(ContentDialog);
    expect(dialog, findsOneWidget);

    await tester.enterText(find.byType(TextBox), 'TestUser');
    await tester.tap(find.descendant(of: dialog, matching: find.text('添加')));
    await tester.pumpAndSettle();

    expect(store.added, ['TestUser']);
    expect(find.text('TestUser'), findsOneWidget);
    expect(find.byType(ContentDialog), findsNothing);

    await tester.tap(find.text('移除').first);
    await tester.pumpAndSettle();
    final confirm = find.byType(ContentDialog);
    expect(confirm, findsOneWidget);
    await tester.tap(find.descendant(of: confirm, matching: find.text('取消')));
    await tester.pumpAndSettle();
    expect(store.removed, isEmpty);
  });
}

KernelDeps _deps(AccountStore accounts) => KernelDeps(
      catalog: _FakeCatalog(),
      installer: _FakeInstaller(),
      launcher: _FakeLauncher(),
      accounts: accounts,
      settings: _FakeSettings(),
      saves: _FakeSaves(),
      auth: _FakeAuth(),
      multiplayer: _FakeMultiplayer(),
      content: _FakeContent(),
      skins: _FakeSkins(),
      nova: _FakeNova(),
      lab: _FakeLab(),
    );

class _FakeLab implements LabService {
  @override
  Future<ProjectionPreview> load(String path) async =>
      ProjectionPreview(path: path);
  @override
  Future<String> generateWorldEater({
    int length = 64,
    int width = 16,
    int layers = 3,
    int spacing = 2,
    int layerGap = 1,
    int offset = 2,
  }) async =>
      '/tmp/lab.litematic';
}

class _FakeNova implements NovaClient {
  @override
  Future<NovaConfig> config() async => const NovaConfig();
  @override
  Future<NovaConfig> saveConfig(NovaConfig config) async => config;
  @override
  Stream<NovaEvent> chat({
    required String message,
    List<NovaMessage> history = const [],
    String? versionId,
  }) =>
      const Stream.empty();
  @override
  Future<String> executePlan(String planId) async => '';
  @override
  Future<void> discardPlan(String planId) async {}
}

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

class _FakeStore implements AccountStore {
  _FakeStore(List<GameAccount> accounts, {String? currentUuid})
      : _accounts = [...accounts],
        _current = currentUuid;

  final List<GameAccount> _accounts;
  String? _current;
  final List<String> added = [];
  final List<String> removed = [];

  @override
  List<GameAccount> accounts() => List.unmodifiable(_accounts);

  @override
  GameAccount? current() {
    for (final account in _accounts) {
      if (account.uuid == _current) return account;
    }
    return _accounts.isEmpty ? null : _accounts.first;
  }

  @override
  Future<void> addOffline(String name) async {
    added.add(name);
    final uuid = 'u-$name';
    _accounts.add(GameAccount(
      uuid: uuid,
      name: name,
      kind: AccountKind.offline,
      hasSession: true,
    ));
    _current = uuid;
  }

  @override
  Future<void> setCurrent(String uuid) async => _current = uuid;

  @override
  Future<void> remove(String uuid) async {
    removed.add(uuid);
    _accounts.removeWhere((a) => a.uuid == uuid);
    if (_current == uuid) _current = null;
  }
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
