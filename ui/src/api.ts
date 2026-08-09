// 后端命令调用封装（@tauri-apps/api v2）

import { invoke } from "@tauri-apps/api/core";
import type {
  GuiPluginInfo,
  InstalledVersion,
  JavaInfo,
  Settings,
  StoredAccount,
  VersionInfo,
} from "./types";

/** 版本列表（远程 + 本地状态） */
export const listVersions = () => invoke<VersionInfo[]>("list_versions");

/** 已安装版本 */
export const listInstalled = () => invoke<InstalledVersion[]>("list_installed");

/** 安装版本（后台任务，进度走事件） */
export const installVersion = (versionId: string) =>
  invoke<void>("install_version", { versionId });

/** 删除版本 */
export const removeVersion = (versionId: string) =>
  invoke<void>("remove_version", { versionId });

/** 启动游戏（后台任务，日志/退出走事件） */
export const launchGame = (versionId: string, username: string, dryRun = false) =>
  invoke<void>("launch_game", { versionId, username, dryRun });

/** 停止游戏 */
export const stopGame = () => invoke<void>("stop_game");

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

/** 读取设置 */
export const getSettings = () => invoke<Settings>("get_settings");

/** 保存设置 */
export const setSettings = (settings: Settings) =>
  invoke<void>("set_settings", { settings });

/** GUI 插件列表 */
export const getGuiPlugins = () => invoke<GuiPluginInfo[]>("get_gui_plugins");

/** 核心插件列表 */
export const listPlugins = () => invoke<string[]>("list_plugins");

/** 最近游戏日志 */
export const getGameLogs = () => invoke<string>("get_game_logs");

/** 检测 Java */
export const checkJava = () => invoke<JavaInfo | null>("check_java");
