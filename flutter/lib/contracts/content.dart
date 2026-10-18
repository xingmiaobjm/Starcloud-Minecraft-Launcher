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

abstract class ContentManager {
  Future<VersionContent> detail(String versionId);

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
