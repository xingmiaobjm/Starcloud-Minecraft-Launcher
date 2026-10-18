/// 版本内容管理契约：模组 / 资源包 / 光影。
///
/// 本地文件操作与在线安装（Modrinth）都由 C# 的 ContentLibraryService 完成；
/// UI 只负责列表、开关、删除与搜索安装。
library;

enum ContentKind { mod, resourcepack, shader }

extension ContentKindX on ContentKind {
  String get wire => switch (this) {
        ContentKind.mod => 'mod',
        ContentKind.resourcepack => 'resourcepack',
        ContentKind.shader => 'shader',
      };

  String get label => switch (this) {
        ContentKind.mod => '模组',
        ContentKind.resourcepack => '资源包',
        ContentKind.shader => '光影',
      };

  String get folder => switch (this) {
        ContentKind.mod => 'mods',
        ContentKind.resourcepack => 'resourcepacks',
        ContentKind.shader => 'shaderpacks',
      };
}

class ContentFile {
  const ContentFile({
    required this.fileName,
    required this.displayName,
    required this.enabled,
    required this.sizeBytes,
    required this.path,
  });

  /// 磁盘上的真实文件名（停用时带 `.disabled`）。
  final String fileName;

  /// 去掉 `.disabled` 的展示名。
  final String displayName;
  final bool enabled;
  final int sizeBytes;
  final String path;
}

class VersionContent {
  const VersionContent({
    required this.versionId,
    required this.gameRoot,
    required this.isolated,
    required this.mcVersion,
    required this.loader,
    this.loaderVersion,
    this.lastPlayedAt,
    this.mods = const [],
    this.resourcepacks = const [],
    this.shaderpacks = const [],
  });

  final String versionId;
  final String gameRoot;
  final bool isolated;
  final String mcVersion;
  final String loader;
  final String? loaderVersion;
  final String? lastPlayedAt;
  final List<ContentFile> mods;
  final List<ContentFile> resourcepacks;
  final List<ContentFile> shaderpacks;

  List<ContentFile> filesFor(ContentKind kind) => switch (kind) {
        ContentKind.mod => mods,
        ContentKind.resourcepack => resourcepacks,
        ContentKind.shader => shaderpacks,
      };
}

class ContentSearchHit {
  const ContentSearchHit({
    required this.projectId,
    required this.slug,
    required this.title,
    required this.description,
    required this.author,
    required this.downloads,
    this.iconUrl,
    this.categories = const [],
    this.webUrl,
  });

  final String projectId;
  final String slug;
  final String title;
  final String description;
  final String author;
  final int downloads;
  final String? iconUrl;
  final List<String> categories;
  final String? webUrl;
}

class InstalledContent {
  const InstalledContent({
    required this.projectId,
    required this.title,
    required this.fileName,
    this.versionNumber,
    this.path,
  });

  final String projectId;
  final String title;
  final String fileName;
  final String? versionNumber;
  final String? path;
}

class MissingContent {
  const MissingContent({required this.projectId, required this.reason});
  final String projectId;
  final String reason;
}

class ContentInstallResult {
  const ContentInstallResult({
    this.installed = const [],
    this.missing = const [],
  });

  final List<InstalledContent> installed;
  final List<MissingContent> missing;
}

/// 实例层叠后的生效值（只读展示）。
class EffectiveInstanceSettings {
  const EffectiveInstanceSettings({
    required this.gameRoot,
    required this.isolated,
    required this.minMem,
    required this.maxMem,
    this.java,
    this.windowW,
    this.windowH,
    this.fullscreen,
  });

  final String gameRoot;
  final bool isolated;
  final int minMem;
  final int maxMem;
  final String? java;
  final int? windowW;
  final int? windowH;
  final bool? fullscreen;
}

/// 版本实例设置（档案原值；null/global 表示跟随全局）。
class VersionSettings {
  const VersionSettings({
    required this.versionId,
    this.isolation,
    this.memoryMode = 'global',
    this.memoryMinMb,
    this.memoryMaxMb,
    this.javaMode = 'global',
    this.javaPath,
    this.windowMode = 'global',
    this.windowWidth,
    this.windowHeight,
    this.fullscreen,
    this.serverAddress,
    this.extraJvmArgs = const [],
    this.extraGameArgs = const [],
    required this.effective,
  });

  final String versionId;

  /// null / shared / isolated
  final String? isolation;

  /// global / manual / auto
  final String memoryMode;
  final int? memoryMinMb;
  final int? memoryMaxMb;

  /// global / manual / auto
  final String javaMode;
  final String? javaPath;

  /// global / manual / auto
  final String windowMode;
  final int? windowWidth;
  final int? windowHeight;
  final bool? fullscreen;
  final String? serverAddress;
  final List<String> extraJvmArgs;
  final List<String> extraGameArgs;
  final EffectiveInstanceSettings effective;
}

class ContentUpdate {
  const ContentUpdate({
    required this.fileName,
    required this.currentVersion,
    required this.latestVersion,
    required this.latestFileName,
    this.latestUrl,
  });

  final String fileName;
  final String currentVersion;
  final String latestVersion;
  final String latestFileName;
  final String? latestUrl;
}

class MissingDependencyInfo {
  const MissingDependencyInfo({
    required this.projectId,
    required this.title,
    this.versionId,
    this.fileName,
  });

  final String projectId;
  final String title;
  final String? versionId;
  final String? fileName;
}

class DependencyReport {
  const DependencyReport({
    this.missing = const [],
    this.optional = const [],
    this.incompatible = const [],
    this.truncated = false,
    this.checkedMods = 0,
    this.resolvedMods = 0,
    this.inspectedVersions = 0,
  });

  final List<MissingDependencyInfo> missing;

  /// 可选依赖的项目 id（只做提示）。
  final List<String> optional;

  /// 已安装但不兼容的模组标题。
  final List<String> incompatible;
  final bool truncated;

  /// 诊断：检查的 jar 数 / 成功识别身份的模组数 / 实际读到依赖清单的版本数。
  final int checkedMods;
  final int resolvedMods;
  final int inspectedVersions;
}

abstract class ContentManager {
  Future<VersionContent> detail(String versionId);

  /// 版本实例设置（档案原值 + 生效值）。
  Future<VersionSettings> settings(String versionId);

  /// 保存实例设置（patch 里只放要改的键；null 表示恢复跟随全局）。
  Future<VersionSettings> saveSettings(
    String versionId,
    Map<String, Object?> patch,
  );

  /// 检查已装模组的 Modrinth 更新。
  Future<List<ContentUpdate>> checkUpdates(String versionId);

  /// 更新单个模组，返回新的文件名。
  Future<String> updateMod(String versionId, String fileName);

  /// 依赖完整性检查（缺失的必需依赖 / 不兼容项）。
  Future<DependencyReport> checkDependencies(String versionId);


  /// 启用/停用（`.disabled` 后缀），返回该类别的最新文件列表。
  Future<List<ContentFile>> toggle(
    String versionId,
    ContentKind kind,
    String fileName,
    bool enabled,
  );

  Future<List<ContentFile>> delete(
    String versionId,
    ContentKind kind,
    String fileName,
  );

  Future<void> openFolder(String versionId, ContentKind kind);

  /// Modrinth 搜索（按版本/加载器过滤）。
  Future<List<ContentSearchHit>> search({
    required ContentKind kind,
    required String query,
    String? gameVersion,
    String? loader,
    int offset = 0,
    int limit = 20,
  });

  /// 安装到指定版本（含 Modrinth 的必需依赖，最多递归 5 层）。
  Future<ContentInstallResult> install({
    required ContentKind kind,
    required String projectId,
    required String title,
    required String targetVersionId,
    void Function(String stage)? onProgress,
  });
}
