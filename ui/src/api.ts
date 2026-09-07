// 后端命令调用封装（@tauri-apps/api v2）

import { invoke } from "@tauri-apps/api/core";
import type {
  DeviceCode,
  DownloadSourceId,
  DownloadSourceStatus,
  GuiPluginInfo,
  InstalledVersion,
  JavaInfo,
  JavaRuntime,
  LoaderInfo,
  LoginPoll,
  MemorySuggestion,
  ModFile,
  ModSearchHit,
  PreflightIssue,
  Settings,
  StoredAccount,
  TaskInfo,
  TerracottaStatePayload,
  VersionInfo,
  VersionSettings,
} from "./types";

/** 版本列表（远程 + 本地状态） */
export const listVersions = () => invoke<VersionInfo[]>("list_versions");

/** 启动器版本号（单一来源：src-tauri/Cargo.toml，由后端编译期注入） */
export const getAppVersion = () => invoke<string>("app_version");

/** 已安装版本 */
export const listInstalled = () => invoke<InstalledVersion[]>("list_installed");

/** 安装版本（后台任务，进度走事件） */
export const installVersion = (versionId: string) =>
  invoke<void>("install_version", { versionId });

/** 取消进行中的版本安装 */
export const cancelInstall = (versionId: string) =>
  invoke<void>("cancel_install", { versionId });

/** 下载 Minecraft 服务端 jar（后台任务，进度走事件） */
export const downloadServer = (versionId: string) =>
  invoke<void>("download_server", { versionId });

/** 删除版本 */
export const removeVersion = (versionId: string) =>
  invoke<void>("remove_version", { versionId });

/** 为指定 MC 版本安装 Fabric 加载器（后台任务，进度走事件） */
export const installFabric = (mcVersion: string) =>
  invoke<void>("install_fabric", { mcVersion });

/** 可用 Mod 加载器列表（来自后端可扩展注册表，新增加载器无需改前端） */
export const listLoaders = () => invoke<LoaderInfo[]>("list_loaders");

/** 为指定 MC 版本安装加载器（loader: vanilla/fabric/forge/quilt/neoforge，后台任务，进度走事件） */
export const installLoader = (mcVersion: string, loader: string) =>
  invoke<void>("install_loader", { mcVersion, loader });

/** 搜索 Mod / 整合包 / 数据包 / 光影包 / 材质包（category: mod/modpack/datapack/shader/resourcepack，空=不限；categories: Modrinth 类型标签逗号分隔，如 magic,adventure，空=不限；offset: 分页偏移，每页 50 条（后端 limit 固定值，见 src-tauri/src/commands.rs search_mods）） */
export const searchMods = (
  query: string,
  mcVersion: string,
  loader: string,
  category: string,
  categories: string,
  platform: string,
  offset = 0,
) =>
  invoke<ModSearchHit[]>("search_mods", {
    query,
    mcVersion,
    loader,
    category,
    categories,
    platform,
    offset,
  });

/** 加载本地缓存的热门模组（启动器打开时立即展示，无需网络） */
export const loadPopularMods = () =>
  invoke<{ mods: ModSearchHit[]; total_count: number; last_updated: string } | null>(
    "load_popular_mods",
  );

/** 批量翻译 Mod 标题为中文（与输入等长的数组；None=无需翻译/未收录）。搜索返回后异步调用，避免阻塞滚动加载 */
export const translateModTitles = (titles: string[]) =>
  invoke<(string | null)[]>("translate_mod_titles", { titles });

/** 批量翻译 Mod 描述为中文（通过 MCIM 翻译 API）。
 * 输入为 (platform, id) 数组，返回与输入等长的 Option<string> 数组。 */
export const translateModDescriptions = (items: [string, string][]) =>
  invoke<(string | null)[]>("translate_mod_descriptions", { items });

/** 列出 Mod 可用版本文件 */
export const listModVersions = (
  platform: string,
  modId: string,
  mcVersion: string,
  loader: string,
) => invoke<ModFile[]>("list_mod_versions", { platform, modId, mcVersion, loader });

/** 在系统默认浏览器中打开外部链接（如 MC 百科） */
export const openUrl = (url: string) => invoke<void>("open_url", { url });

/** 打开系统原生「选择文件夹」对话框，返回所选目录绝对路径（取消返回 null） */
export const pickDirectory = () => invoke<string | null>("pick_directory");

/** 通过 MC 百科搜索页查找资源详情页 URL（未收录返回 null） */
export const mcmodLookup = (title: string) =>
  invoke<string | null>("mcmod_lookup", { title });

/** 下载 Mod 到 mods 目录（后台任务，进度走事件） */
export const downloadMod = (params: {
  platform: string;
  fileId: string;
  fileName: string;
  url: string;
  size: number;
  title: string;
}) => invoke<void>("download_mod", { ...params });

/** 启动游戏（后台任务，日志/退出走事件） */
export const launchGame = (versionId: string, username: string, dryRun = false) =>
  invoke<void>("launch_game", { versionId, username, dryRun });

/** 停止游戏 */
export const stopGame = () => invoke<void>("stop_game");

/** 调度中心任务列表（priority 升序，0 最高） */
export const listTasks = () => invoke<TaskInfo[]>("list_tasks");

/** 拖拽重排任务优先级（按前端顺序） */
export const setTaskOrder = (taskIds: string[]) =>
  invoke<void>("set_task_order", { taskIds });

/** 账户列表 */
export const listAccounts = () => invoke<StoredAccount[]>("list_accounts");

/** 添加离线账户 */
export const addOfflineAccount = (username: string) =>
  invoke<StoredAccount>("add_offline_account", { username });

/** 删除账户 */
export const removeAccount = (uuid: string) => invoke<boolean>("remove_account", { uuid });

/** 设置默认账户 */
export const setDefaultAccount = (uuid: string) =>
  invoke<void>("set_default_account", { uuid });

/** 发起微软设备码登录 */
export const loginMicrosoftDevice = () => invoke<DeviceCode>("login_microsoft_device");

/** 单次轮询微软登录状态 */
export const pollMicrosoftLogin = (deviceCode: string) =>
  invoke<LoginPoll>("poll_microsoft_login", { deviceCode });

/** 读取设置 */
export const getSettings = () => invoke<Settings>("get_settings");

/** 保存设置 */
export const setSettings = (settings: Settings) =>
  invoke<void>("set_settings", { settings });

/** 内存一键推荐（按系统物理内存计算） */
export const suggestMemory = () => invoke<MemorySuggestion>("suggest_memory");

/** 读取版本级设置（未保存过返回空默认） */
export const getVersionSettings = (versionId: string) =>
  invoke<VersionSettings>("get_version_settings", { versionId });

/** 保存版本级设置（传空字段可恢复全局默认） */
export const setVersionSettings = (versionId: string, settings: VersionSettings) =>
  invoke<void>("set_version_settings", { versionId, settings });

/** 调度中心 - 删除任务 */
export const removeTask = (taskId: string) =>
  invoke<void>("remove_task", { taskId });

/** 启动前自检（Java / 磁盘 / 内存 / 版本） */
export const preflightCheck = (versionId: string) =>
  invoke<PreflightIssue[]>("preflight_check", { versionId });

/** GUI 插件列表 */
export const getGuiPlugins = () => invoke<GuiPluginInfo[]>("get_gui_plugins");

/** 安装 GUI 插件（URL 或本地 zip 路径） */
export const installPlugin = (source: string) =>
  invoke<GuiPluginInfo>("install_plugin", { source });

/** 删除 GUI 插件 */
export const removePlugin = (pluginId: string) =>
  invoke<void>("remove_plugin", { pluginId });

/** 启用/禁用 GUI 插件 */
export const setPluginEnabled = (pluginId: string, enabled: boolean) =>
  invoke<void>("set_plugin_enabled", { pluginId, enabled });

/** 核心插件列表 */
export const listPlugins = () => invoke<string[]>("list_plugins");

/** 最近游戏日志 */
export const getGameLogs = () => invoke<string>("get_game_logs");

/** 启动器本体日志（开发者模式，读取 launcher.log 末尾） */
export const getLauncherLogs = () => invoke<string>("get_launcher_logs");

/** 检测 Java */
export const checkJava = () => invoke<JavaInfo | null>("check_java");

/** 列出所有可用 Java 运行时（启动器已下载 + 系统检测） */
export const listJavaRuntimes = () => invoke<JavaRuntime[]>("list_java_runtimes");

/** 下载指定版本的 Java（后台任务，进度走事件） */
export const downloadJavaRuntime = (version: number) =>
  invoke<void>("download_java_runtime", { version });

/** 删除已下载的 Java 版本 */
export const removeJavaRuntime = (version: number) =>
  invoke<void>("remove_java_runtime", { version });

/** 切换窗口毛玻璃主题（浅色 = 白色玻璃；深色 = 灰色玻璃） */
export const setWindowTheme = (dark: boolean) =>
  invoke<void>("set_window_theme", { dark });

/** 查询当前下载源与最近一次测速结果 */
export const getDownloadSources = () =>
  invoke<DownloadSourceStatus>("get_download_sources");

/** 手动重新测速所有下载源并切换最快的可达源 */
export const probeDownloadSources = () =>
  invoke<DownloadSourceStatus>("probe_download_sources");

/** 手动切换下载源（official / bmclapi / fastmirror / mcslsync / rainyun / custom，覆盖自动测速的选择） */
export const setDownloadSource = (source: DownloadSourceId) =>
  invoke<void>("set_download_source", { source });

/** 获取 Mod 图标本地缓存路径（自动下载+缓存），返回本地文件路径供 convertFileSrc 使用 */
export const getCachedIcon = (url: string) =>
  invoke<string>("get_cached_icon", { url });

/** 清除版本清单本地缓存 */
export const clearVersionCache = () => invoke<void>("clear_version_cache");

/** 陶瓦联机状态快照（安装 / 运行 / 当前状态） */
export const terracottaStatus = () => invoke<TerracottaStatePayload>("terracotta_status");

/** 安装陶瓦联机核心（后台任务，进度走事件，任务键 = terracotta:core） */
export const terracottaInstall = () => invoke<void>("terracotta_install");

/** 创建联机房间（游戏内先开启局域网开放） */
export const terracottaStartHost = () => invoke<void>("terracotta_start_host");

/** 加入他人房间（room = 房主分享的邀请码） */
export const terracottaJoinRoom = (room: string) =>
  invoke<void>("terracotta_join_room", { room });

/** 停止联机并关闭核心会话 */
export const terracottaStop = () => invoke<void>("terracotta_stop");

/** 拉取联机核心日志（调试用） */
export const terracottaLogs = () => invoke<string>("terracotta_logs");
