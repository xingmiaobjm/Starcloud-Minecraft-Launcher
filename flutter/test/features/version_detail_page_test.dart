import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/content.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/contracts/nova.dart';
import 'package:starcloud_launcher/contracts/skins.dart';
import 'package:starcloud_launcher/contracts/multiplayer.dart';
import 'package:starcloud_launcher/shell/pages/version_detail_page.dart';

/// 版本详情页：内容列表、启停、页内返回与安装入口。
void main() {
  testWidgets('模组列表 + 启停 + 返回', (tester) async {
    final content = _FakeContent();
    var backed = false;
    await tester.pumpWidget(ProviderScope(
      overrides: [kernelDepsProvider.overrideWithValue(_deps(content))],
      child: FluentApp(
        home: VersionDetailPage(
          version: const InstalledVersion(
            id: '1.20.4',
            directory: '/mc/versions/1.20.4',
            isModded: false,
          ),
          onBack: () => backed = true,
        ),
      ),
    ));
    await tester.pumpAndSettle();

    expect(find.text('sodium.jar'), findsOneWidget);
    expect(find.text('optifine.jar'), findsOneWidget);
    expect(find.textContaining('已停用'), findsOneWidget);

    await tester.tap(find.text('停用'));
    await tester.pumpAndSettle();
    expect(content.toggled, ['sodium.jar:false']);
    expect(find.textContaining('已停用'), findsNWidgets(2));

    await tester.tap(find.text('资源包'));
    await tester.pumpAndSettle();
    expect(find.textContaining('还没有资源包'), findsOneWidget);

    await tester.tap(find.text('安装内容'));
    await tester.pumpAndSettle();
    expect(find.text('安装资源包（Modrinth）'), findsOneWidget);
    await tester.tap(find.text('关闭'));
    await tester.pumpAndSettle();

    // 切回模组，验证更新/依赖/实例设置入口。
    await tester.tap(find.text('模组'));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('检查更新'));
    await tester.tap(find.text('检查更新'));
    await tester.pumpAndSettle();
    expect(find.text('所有模组都是最新版本'), findsOneWidget);

    await tester.tap(find.text('检查依赖'));
    await tester.pumpAndSettle();
    expect(find.text('依赖完整'), findsOneWidget);
    await tester.tap(find.text('关闭'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('实例设置'));
    await tester.pumpAndSettle();
    expect(find.textContaining('实例设置 · 1.20.4'), findsOneWidget);
    await tester.tap(find.text('保存'));
    await tester.pumpAndSettle();
    expect(content.savedPatches, hasLength(1));
    expect(content.savedPatches.single['memoryMode'], 'global');

    await tester.tap(find.byIcon(FluentIcons.back));
    await tester.pumpAndSettle();
    expect(backed, isTrue);
  });
}

KernelDeps _deps(ContentManager content) => KernelDeps(
      catalog: _FakeCatalog(),
      installer: _FakeInstaller(),
      launcher: _FakeLauncher(),
      accounts: _FakeAccounts(),
      settings: _FakeSettings(),
      saves: _FakeSaves(),
      auth: _FakeAuth(),
      multiplayer: _FakeMultiplayer(),
      content: content,
      skins: _FakeSkins(),
      nova: _FakeNova(),
    );

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
  ) async {
    savedPatches.add(patch);
    return settings(versionId);
  }
  @override
  Future<List<ContentUpdate>> checkUpdates(String versionId) async => const [];
  @override
  Future<String> updateMod(String versionId, String fileName) async => fileName;
  @override
  Future<DependencyReport> checkDependencies(String versionId) async =>
      const DependencyReport();

  List<ContentFile> mods = const [
    ContentFile(
      fileName: 'sodium.jar',
      displayName: 'sodium.jar',
      enabled: true,
      sizeBytes: 1024,
      path: '/mc/mods/sodium.jar',
    ),
    ContentFile(
      fileName: 'optifine.jar.disabled',
      displayName: 'optifine.jar',
      enabled: false,
      sizeBytes: 2048,
      path: '/mc/mods/optifine.jar.disabled',
    ),
  ];
  final List<String> toggled = [];
  final List<Map<String, Object?>> savedPatches = [];

  @override
  Future<VersionContent> detail(String versionId) async => VersionContent(
        versionId: versionId,
        gameRoot: '/mc/versions/1.20.4',
        isolated: true,
        mcVersion: '1.20.4',
        loader: 'vanilla',
        mods: mods,
      );

  @override
  Future<List<ContentFile>> toggle(
    String versionId,
    ContentKind kind,
    String fileName,
    bool enabled,
  ) async {
    toggled.add('$fileName:$enabled');
    mods = [
      for (final file in mods)
        file.fileName == fileName
            ? ContentFile(
                fileName: file.fileName,
                displayName: file.displayName,
                enabled: enabled,
                sizeBytes: file.sizeBytes,
                path: file.path,
              )
            : file,
    ];
    return mods;
  }

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
