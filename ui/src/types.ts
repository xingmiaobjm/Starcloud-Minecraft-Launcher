// 与后端命令/事件对应的类型定义

/** 版本条目（远程清单 + 本地安装状态） */
export interface VersionInfo {
  id: string;
  version_type: string;
  release_time: string;
  installed: boolean;
  size_bytes: number;
}

/** 已安装版本 */
export interface InstalledVersion {
  id: string;
  size_bytes: number;
}

/** 设置快照 */
export interface Settings {
  dot_minecraft: string;
  language: string;
  default_min_memory_mb: number;
  default_max_memory_mb: number;
  default_window_width: number;
  default_window_height: number;
  java_executable: string | null;
  extra_jvm_args: string[];
  download_concurrency: number;
  download_max_retries: number;
}

/** Java 检测结果 */
export interface JavaInfo {
  executable: string;
  major_version: number;
  version_string: string;
  is_64bit: boolean;
}

/** 已存账户 */
export interface StoredAccount {
  uuid: string;
  username: string;
  type: "offline" | "microsoft" | "mojang";
  access_token?: string | null;
  refresh_token?: string | null;
  is_default: boolean;
  last_used?: string | null;
}

/** 具名视图摘要（furry://view/{pid}/{view_id}/...） */
export interface PluginViewInfo {
  id: string;
  title: string;
  entry: string;
  /** 可嵌入插槽：page / drawer / home / settings / sidebar */
  slots: string[];
}

/** GUI 插件摘要 */
export interface GuiPluginInfo {
  id: string;
  name: string;
  version: string;
  /** 插件类别：view / shell / mixed */
  gui_type: "view" | "shell" | "mixed";
  title: string;
  entry: string;
  has_native: boolean;
  compatible: boolean;
  reason: string | null;
  enabled: boolean;
  /** 具名视图列表（可嵌入插槽） */
  views?: PluginViewInfo[];
  /** 主壳接管入口（相对 frontend/） */
  shell?: string | null;
  /** 全部可嵌入插槽（去重） */
  slots?: string[];
}

/** 微软设备码（登录页展示 user_code + verification_uri） */
export interface DeviceCode {
  user_code: string;
  device_code: string;
  verification_uri: string;
  message: string;
  interval: number;
  expires_in: number;
}

/** 微软登录轮询结果 */
export interface LoginPoll {
  /** "pending"（继续轮询）| "success" | "error"（终止） */
  state: "pending" | "success" | "error";
  message: string;
  account: StoredAccount | null;
}

// ---------------------------------------------------------------------------
// 事件负载
// ---------------------------------------------------------------------------

export interface StatusPayload {
  status: string;
  detail: string | null;
}

export interface ProgressPayload {
  version_id: string;
  downloaded_bytes: number;
  total_bytes: number;
  percentage: number;
  stage: string;
  status: string;
}

export interface InstallDonePayload {
  version_id: string;
  success: boolean;
  message: string;
}

export interface GameLogPayload {
  line: string;
  is_stderr: boolean;
}

export interface GameExitedPayload {
  exit_code: number | null;
  crashed: boolean;
  message: string;
}

export interface ErrorPayload {
  context: string;
  message: string;
}

/** 应用状态机 */
export type AppStatus = "idle" | "installing" | "launching" | "running" | "error";
