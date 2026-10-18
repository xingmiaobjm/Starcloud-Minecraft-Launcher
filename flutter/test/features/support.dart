/// 页面测试共用的假契约（Nova 页等新测试使用；旧测试保留各自的局部实现）。
library;

import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/content.dart';
import 'package:starcloud_launcher/contracts/lab.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/contracts/multiplayer.dart';
import 'package:starcloud_launcher/contracts/nova.dart';
import 'package:starcloud_launcher/contracts/skins.dart';

KernelDeps fakeDeps({
  MinecraftCatalog? catalog,
  MinecraftInstaller? installer,
  GameLauncher? launcher,
  AccountStore? accounts,
  LauncherSettings? settings,
  SaveCatalog? saves,
  AccountAuth? auth,
  MultiplayerControl? multiplayer,
  ContentManager? content,
  SkinManager? skins,
  NovaClient? nova,
  LabService? lab,
}) =>
    KernelDeps(
      catalog: catalog ?? FakeCatalog(),
      installer: installer ?? FakeInstaller(),
      launcher: launcher ?? FakeLauncher(),
      accounts: accounts ?? FakeAccounts(),
      settings: settings ?? FakeSettings(),
      saves: saves ?? FakeSaves(),
      auth: auth ?? FakeAuth(),
      multiplayer: multiplayer ?? FakeMultiplayer(),
      content: content ?? FakeContent(),
      skins: skins ?? FakeSkins(),
      nova: nova ?? FakeNova(),
      lab: lab ?? FakeLab(),
    );

class FakeCatalog implements MinecraftCatalog {
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

class FakeInstaller implements MinecraftInstaller {
  @override
  Future<void> install(
    String versionId, {
    void Function(InstallProgress progress)? onProgress,
  }) async {}
}

class FakeLauncher implements GameLauncher {
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

class FakeAccounts implements AccountStore {
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

class FakeSettings implements LauncherSettings {
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

class FakeSaves implements SaveCatalog {
  @override
  Future<List<SaveInfo>> listSaves() async => const [];
  @override
  Future<void> openSaveFolder(SaveInfo save) async {}
  @override
  Future<String> backupSave(SaveInfo save) async => '';
  @override
  Future<void> deleteSave(SaveInfo save) async {}
}

class FakeAuth implements AccountAuth {
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

class FakeMultiplayer implements MultiplayerControl {
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

class FakeContent implements ContentManager {
  @override
  Future<VersionContent> detail(String versionId) async => VersionContent(
        versionId: versionId,
        gameRoot: '/mc',
        isolated: true,
        mcVersion: '1.20.4',
        loader: 'vanilla',
      );
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

class FakeSkins implements SkinManager {
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

class FakeNova implements NovaClient {
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

class FakeLab implements LabService {
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
