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

/** Mod 加载器信息（来自后端可扩展注册表） */
export interface LoaderInfo {
  id: string;
  label: string;
  description: string;
}

/** Mod 搜索结果 */
export interface ModSearchHit {
  platform: "modrinth" | "curseforge";
  id: string;
  /** 项目 slug（拼官网地址用） */
  slug: string;
  title: string;
  /** 中文标题（优先内置词典 → MCIM → MC百科 → 机器翻译；无合适译名时为空） */
  title_zh?: string | null;
  description: string;
  /** 中文描述（优先 MCIM 翻译；无合适翻译时为 null） */
  description_zh?: string | null;
  author: string;
  downloads: number;
  icon_url: string | null;
}

/** Mod 可下载文件（版本） */
export interface ModFile {
  id: string;
  version: string;
  game_versions: string[];
  loaders: string[];
  file_name: string;
  size: number;
  url: string;
}

/** 设置快照 */
export interface Settings {
  dot_minecraft: string;
  language: string;
  default_min_memory_mb: number;
  default_max_memory_mb: number;
  java_executable: string | null;
  extra_jvm_args: string[];
  /** 游戏优化启动参数（开发者模式，追加到游戏主类参数之后） */
  extra_game_args: string[];
  /** JAVA 启用高性能显卡（Windows 图形偏好：独立显卡优先） */
  use_high_performance_gpu: boolean;
  download_concurrency: number;
  download_max_retries: number;
  /** 上次启动的版本 ID（首页默认选中） */
  last_version: string | null;
  /** 强制游戏语言为中文 */
  force_chinese: boolean;
  /** 全局版本隔离：每个版本使用独立游戏目录 versions/<id>/game（默认开启） */
  version_isolated: boolean;
  /** 开发者模式（开启后可调整最小内存等高级参数） */
  dev_mode: boolean;
  /** 动画倍率：0 关闭动画；0.1-2.0 倍率（1.0 正常） */
  animation_scale: number;
  /** 游戏运行时是否显示右下角日志按钮（点击查看启动器日志） */
  show_log_button: boolean;
  /** 版本清单本地缓存：开启后缓冲版本列表，关闭即「联机模式」始终从网络拉取 */
  version_cache_enabled: boolean;
  /** 是否启用内置 JVM 优化参数（G1GC / 内存预触 / IPv4 等） */
  use_builtin_jvm_args: boolean;
}

/** 内存推荐（按系统物理内存计算） */
export interface MemorySuggestion {
  total_mb: number;
  min_mb: number;
  max_mb: number;
}

/** 版本级启动设置（覆盖全局默认；未设置字段跟随全局默认） */
export interface VersionSettings {
  /** 最小内存 (MB)，null = 跟随全局 */
  min_memory_mb: number | null;
  /** 最大内存 (MB)，null = 跟随全局 */
  max_memory_mb: number | null;
  /** 游戏语言覆盖（如 zh_CN），null/空 = 跟随全局 */
  language: string | null;
  /** 版本隔离：独立游戏目录 */
  isolated: boolean;
  /** Java 可执行文件路径，null = 跟随全局 */
  java_executable: string | null;
  /** 版本级额外 JVM 参数（覆盖全局） */
  jvm_args: string[];
  /** 是否启用内置 JVM 优化参数（null = 跟随全局） */
  use_builtin_jvm_args: boolean | null;
}

/** 启动前自检结果项 */
export interface PreflightIssue {
  /** "error"（阻断启动）| "warning"（提示不阻断）| "info" */
  level: "error" | "warning" | "info";
  title: string;
  message: string;
}

/** Java 检测结果 */
export interface JavaInfo {
  executable: string;
  major_version: number;
  version_string: string;
  is_64bit: boolean;
}

/** Java 运行时（Java 管理器列表项：启动器已下载 + 系统检测） */
export interface JavaRuntime {
  executable: string;
  major_version: number;
  version_string: string;
  is_64bit: boolean;
  /** 是否由启动器管理（可删除） */
  managed: boolean;
  /** "launcher"（启动器已下载）| "system"（系统检测） */
  source: string;
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

/** 下载源 id（official / bmclapi / fastmirror / rainyun / custom） */
export type DownloadSourceId = "official" | "bmclapi" | "fastmirror" | "rainyun" | "custom";

/** 单个下载源测速结果（启动时自动测试 + 可手动重测） */
export interface SourceProbe {
  /** 下载源 id */
  source: DownloadSourceId;
  /** 是否可达 */
  reachable: boolean;
  /** 首字节延迟（毫秒） */
  latency_ms: number;
  /** 实测下载速度（字节/秒） */
  speed_bps: number;
  /** 失败原因（可达时为 null） */
  error: string | null;
}

/** 下载源状态：当前活动源 + 测速结果 */
export interface DownloadSourceStatus {
  /** 当前活动下载源 id */
  active: DownloadSourceId;
  results: SourceProbe[];
}

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
  /** 实时速度（字节/秒） */
  speed_bytes_per_sec: number;
  /** 估算剩余时间（秒） */
  eta_secs: number | null;
}

export interface InstallDonePayload {
  /** 前端"正在下载"状态的键（= 安装命令的版本 ID，Fabric 为 MC 版本号） */
  job_id: string;
  /** 实际安装的版本 ID（失败时回退为 job_id） */
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

/** 调度中心任务 */
export interface TaskInfo {
  id: string;
  kind: "install" | "fabric" | "download" | "launch" | "java";
  title: string;
  detail?: string | null;
  status: "running" | "done" | "error";
  priority: number;
  /** 发起来源（user / plugin / system 等） */
  source: string;
  /** 任务日志行（阶段变更 / 关键事件） */
  log: string[];
  /** 进度百分比 0-100（仅 running 任务有意义） */
  progress?: number | null;
  /** 当前阶段名（如「下载库文件」） */
  stage?: string | null;
  /** 实时下载速度（字节/秒） */
  speed?: number | null;
  /** 估算剩余秒数 */
  eta?: number | null;
  created_at: number;
  updated_at: number;
}

/** 任务列表更新负载 */
export interface TasksUpdatedPayload {
  tasks: TaskInfo[];
}

// ---------------------------------------------------------------------------
// 陶瓦联机
// ---------------------------------------------------------------------------

/** 联机核心状态（与后端 TerracottaState 枚举对应，kebab-case tag） */
export type TerracottaState =
  | { state: "waiting" }
  | { state: "host-scanning" }
  | { state: "host-starting" }
  | { state: "host-ok"; room: string; profiles: unknown[] }
  | { state: "guest-connecting" }
  | { state: "guest-starting"; difficulty: string }
  | { state: "guest-ok"; url: string; profiles: unknown[] }
  | { state: "exception"; type: string; message: string };

/** 陶瓦联机状态负载（轮询事件 + status 命令快照） */
export interface TerracottaStatePayload {
  /** 核心是否已安装 */
  installed: boolean;
  /** 已安装的核心版本（未安装为 null） */
  version: string | null;
  /** 核心会话是否运行中 */
  running: boolean;
  /** 当前联机状态（会话未运行时为 null） */
  state: TerracottaState | null;
  /** 最近一次查询错误 */
  error: string | null;
}

/** 应用状态机 */
export type AppStatus = "idle" | "installing" | "launching" | "running" | "error";
