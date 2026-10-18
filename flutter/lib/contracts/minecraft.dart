/// Minecraft 领域契约：版本目录 / 安装器 / 启动器 / 账户 / 设置。
///
/// 契约属于宿主（规范 §5.3）：本文件是宿主统一定义的"形状"，
/// 实现由 `starcloud.minecraft` / `starcloud.accounts` / `starcloud.settings`
/// 插件注册；UI 页面按契约消费，不 import 实现。
library;

/// 远端（manifest 里）的版本条目。
class RemoteVersion {
  const RemoteVersion({
    required this.id,
    required this.type,
    required this.url,
    required this.releaseTime,
  });

  final String id;
  final String type; // release / snapshot / old_beta / old_alpha
  final String url;
  final String releaseTime;
}

/// 本地已安装的版本。
class InstalledVersion {
  const InstalledVersion({
    required this.id,
    required this.directory,
    required this.isModded,
    this.modLoader,
    this.sizeBytes = 0,
    this.jsonPath,
    this.jarExists = true,
    this.lastPlayedAt,
    this.isDefault = false,
    this.isolated = true,
    this.inheritsFrom,
  });

  final String id;

  /// `versions/<id>/` 目录（version JSON 与 jar 所在）。
  final String directory;

  /// 按 id 前缀推断的展示标签（fabric/forge/neoforge/quilt → true）。
  final bool isModded;
  final String? modLoader;

  /// 客户端 jar 大小（共享 libraries/assets 不计入）。
  final int sizeBytes;
  final String? jsonPath;

  /// 客户端 jar 是否存在（加载器实例可能靠父版本的 jar 启动）。
  final bool jarExists;

  /// 最近一次游玩时间（ISO-8601；null = 从未记录）。
  final String? lastPlayedAt;

  /// 是否是"最近启动的版本"。
  final bool isDefault;

  /// 是否启用版本隔离（隔离时存档/mods 在版本目录内）。
  final bool isolated;

  /// 加载器实例继承的原版版本。
  final String? inheritsFrom;
}

/// 安装进度（阶段 + 可选比例）。
class InstallProgress {
  const InstallProgress(this.stage, this.detail, {this.current, this.total});

  /// 阶段名：manifest / version-json / client-jar / libraries / natives / assets
  final String stage;
  final String detail;
  final int? current;
  final int? total;
}

abstract class MinecraftCatalog {
  /// 远端可安装版本（按发布时间倒序）。
  Future<List<RemoteVersion>> remoteVersions();

  /// 本地已安装版本（读 `<gameDir>/versions/*/<id>.json`）。
  Future<List<InstalledVersion>> installedVersions();

  /// 删除一个本地版本目录（不清理共享 libraries/assets）。
  Future<void> deleteVersion(String versionId);

  /// 在系统文件管理器里打开版本目录。
  Future<void> openVersionFolder(String versionId);

  /// 设置"最近启动的版本"（null = 清除）。
  Future<void> setDefaultVersion(String? versionId);
}

abstract class MinecraftInstaller {
  /// 安装一个原版版本（含依赖继承、client jar、libraries、natives 解压、assets）。
  /// 重复调用等价于"补全"（已存在且 SHA1 正确的文件跳过）。
  Future<void> install(
    String versionId, {
    void Function(InstallProgress progress)? onProgress,
  });
}

/// 检测到的 Java 运行时。
class JavaRuntime {
  const JavaRuntime({required this.path, required this.version, required this.major});

  final String path;
  final String version; // 如 "17.0.10"
  final int major; // 8 / 17 / 21 …
}

/// 一次游戏会话的句柄。
class GameSession {
  const GameSession({
    required this.pid,
    required this.logLines,
    required this.exitCode,
  });

  final int pid;

  /// 游戏 stdout/stderr 的**行**流（合并两路，顺序不保证跨路）。
  final Stream<String> logLines;

  /// 进程退出码（进程结束时完成）。
  final Future<int> exitCode;
}

abstract class GameLauncher {
  /// 探测 Java：设置指定路径 → JAVA_HOME → PATH → /usr/lib/jvm 扫描。
  Future<JavaRuntime?> detectJava();

  /// 启动一个已安装版本（离线账户）。
  Future<GameSession> launch({
    required String versionId,
    required String playerName,
  });
}

// ── 存档 ─────────────────────────────────────────────────────────────────────

/// 一个世界（`saves/<folder>/`，含 level.dat）。
class SaveInfo {
  const SaveInfo({
    required this.folderName,
    required this.levelName,
    required this.gameRoot,
    required this.versionId,
    required this.isolated,
    this.sizeBytes = 0,
    this.iconPath,
    this.lastPlayedAt,
  });

  final String folderName;
  final String levelName;

  /// 存档所在的实例根（共享 `.minecraft` 或隔离版本目录）。
  final String gameRoot;

  /// 所属版本 id；空串 = 共享根。
  final String versionId;
  final bool isolated;
  final int sizeBytes;
  final String? iconPath;
  final String? lastPlayedAt;
}

abstract class SaveCatalog {
  /// 共享根 + 各隔离版本根下的全部世界（最近游玩在前）。
  Future<List<SaveInfo>> listSaves();

  Future<void> openSaveFolder(SaveInfo save);

  /// 备份为 zip，返回 zip 路径。
  Future<String> backupSave(SaveInfo save);

  Future<void> deleteSave(SaveInfo save);
}

// ── 账户（基座期只支持离线；微软设备码登录后续迭代）─────────────────────────

enum AccountKind { offline }

class GameAccount {
  const GameAccount({
    required this.uuid,
    required this.name,
    required this.kind,
  });

  /// 离线账户：UUID = `md5("OfflinePlayer:<name>")` 置版本 3（Minecraft 官方算法）。
  final String uuid;
  final String name;
  final AccountKind kind;
}

abstract class AccountStore {
  List<GameAccount> accounts();

  /// 当前选中的账户（没有则 null —— UI 引导用户先添加）。
  GameAccount? current();

  Future<void> addOffline(String name);

  Future<void> setCurrent(String uuid);

  Future<void> remove(String uuid);
}

// ── 设置 ─────────────────────────────────────────────────────────────────────

enum DownloadSource {
  /// 多源竞速（C# 侧默认；BMCLAPI/FastMirror 等按可达性自动选择）。
  auto,
  official,
  bmclapi,
}

abstract class LauncherSettings {
  /// 游戏根目录（.minecraft）。默认 `~/.minecraft`，可改。
  String gameDir();
  Future<void> setGameDir(String path);

  /// Java 可执行文件路径；空字符串 = 自动探测。
  String javaPath();
  Future<void> setJavaPath(String path);

  /// 游戏最大内存（MB）。
  int memoryMb();
  Future<void> setMemoryMb(int mb);

  /// 下载源。
  DownloadSource downloadSource();
  Future<void> setDownloadSource(DownloadSource source);

  /// 变更通知（设置页与依赖方刷新用）。
  void addListener(void Function() listener);
  void removeListener(void Function() listener);
}
