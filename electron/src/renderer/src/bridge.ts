/// 全局类型：桥接返回体（与 C# Starcloud.Bridge 一一对应）。

export interface RemoteVersion {
  id: string
  type: string
  url: string
  releaseTime: string
  installed?: boolean
}

export interface InstalledVersion {
  id: string
  sizeBytes: number
  directory?: string
  jsonPath?: string
  jarExists?: boolean
  lastPlayedAt?: string
  isDefault?: boolean
  isolated?: boolean
  inheritsFrom?: string
  modLoader?: string
  isModded?: boolean
}

export interface GameAccount {
  uuid: string
  name: string
  type: string
  isDefault: boolean
  serverName?: string
  serverAlias?: string
  hasSession?: boolean
  skinId?: string
  skinModel?: string
}

export interface VersionCatalog {
  remote: RemoteVersion[]
  installed: Array<{ id: string; sizeBytes: number }>
}

export interface AccountList {
  accounts: GameAccount[]
  current?: string
}

export interface LaunchSession {
  sessionId: number
  pid: number
  logFile: string
}

export interface SaveInfo {
  folderName: string
  levelName: string
  gameRoot: string
  versionId: string
  isolated: boolean
  sizeBytes: number
  iconPath?: string
  lastPlayedAt?: string
}

export interface SaveList {
  saves: SaveInfo[]
}

export interface MultiplayerProviderInfo {
  id: string
  displayName: string
  vendor: string
  vendorUrl?: string
  description?: string
  builtIn: boolean
}

export interface MultiplayerSnapshot {
  providers: MultiplayerProviderInfo[]
  activeId: string
  nickname: string
  nodeRegion: string
  core: { installed: boolean; version?: string; sessionRunning: boolean }
  state: {
    phase: string
    room?: string
    address?: string
    difficulty?: string
    players?: string[]
    error?: string
    isBusy: boolean
  }
  lastError?: string
}

export interface ExternalPlugin {
  id: string
  name: string
  version: string
  apiVersion: string
  author: string
  description: string
  permissions: string[]
  runtime?: string
  official: boolean
  enabled: boolean
  disclaimerAccepted: boolean
  loaded: boolean
  panelCount: number
  dir: string
  dependencies: Record<string, string>
}

export interface PluginList {
  plugins: ExternalPlugin[]
  pageTipShown: boolean
}

export interface ProjectionPreview {
  path: string
  sizeX: number
  sizeY: number
  sizeZ: number
  truncated: boolean
  totalBlocks: number
  distinctBlocks: number
  stats: Array<{ id: string; count: number }>
  preview: Array<{ x: number; y: number; z: number; id: string }>
  previewTruncated: boolean
  unsupported?: string
}

export interface NovaConfig {
  baseUrl: string
  apiKey: string
  model: string
}

export interface SettingsSnapshot {
  gameDir: string
  javaExecutable: string
  maxMemoryMb: number
  downloadSource: string
}

export interface NovaPlanItem {
  title: string
  detail: string
}

export interface NovaEventData {
  delta?: string
  tool?: string
  done?: { text: string; fromCloud: boolean; plan: NovaPlanItem[] }
  failed?: { error: string }
}

declare global {
  interface Window {
    bridge: {
      request: (method: string, params?: unknown) => Promise<unknown>
      appVersion: () => Promise<string>
    }
    bridgeEvents: {
      subscribe: (
        handler: (event: string, params: Record<string, unknown>) => void
      ) => void
    }
  }
}

export function request<T>(method: string, params: unknown = {}): Promise<T> {
  return window.bridge.request(method, params) as Promise<T>
}

// ── 版本详情页（内容管理 + 实例参数）────────────────────────────────────────
// 字段与 C# Starcloud.Bridge/Program.cs 的 version.* / content.* 返回体一一对应。
// 注意：桥接序列化用 WhenWritingNull，C# 的 null 字段会被**省略**，故一律可选。

/** 内容类别：mod → mods / resourcepack → resourcepacks / shader → shaderpacks。 */
export type ContentKind = 'mod' | 'resourcepack' | 'shader'

/** 实例隔离三态：null（跟随全局）/ shared（共享 .minecraft）/ isolated（独立版本目录）。 */
export type InstanceIsolation = 'shared' | 'isolated'

/** 档位三态：global（跟随全局）/ manual（自定义）/ auto（自动推荐）。 */
export type InstanceMode = 'global' | 'manual' | 'auto'

/** version.detail 的单个内容文件（`ContentFiles()`）。 */
export interface ContentFile {
  /** 磁盘上的真实文件名，停用时带 `.disabled` 后缀。 */
  fileName: string
  /** 去掉 `.disabled` 的展示名。 */
  displayName: string
  /** 是否启用（等价于文件名没有 `.disabled` 后缀）。 */
  enabled: boolean
  sizeBytes: number
  /** 绝对路径，可直接交给「打开目录」。 */
  path: string
}

/** version.detail 的返回：实例上下文 + 三个内容目录的清单。 */
export interface VersionDetail {
  versionId: string
  gameRoot: string
  isolated: boolean
  mcVersion: string
  /** fabric / forge / quilt / neo-forge / vanilla 等。 */
  loader: string
  loaderVersion?: string
  /** ISO 8601（`DateTime.ToString("O")`），从未启动过则缺省。 */
  lastPlayedAt?: string
  mods: ContentFile[]
  resourcepacks: ContentFile[]
  shaderpacks: ContentFile[]
}

/** version.settings 的 `effective` 子对象：全局 + 版本覆盖层叠后的实际生效值。 */
export interface EffectiveInstanceSettings {
  gameRoot: string
  isolated: boolean
  minMem: number
  maxMem: number
  /** auto 档位下为 null（强制走版本 JSON 推荐）。 */
  java?: string
  windowW?: number
  windowH?: number
  fullscreen?: boolean
}

/** version.settings / version.saveSettings 的返回（`VersionSettingsPayload()`）。 */
export interface VersionSettings {
  versionId: string
  /** 缺省即 null = 跟随全局。 */
  isolation?: InstanceIsolation
  memoryMode: InstanceMode
  memoryMinMb?: number
  memoryMaxMb?: number
  javaMode: InstanceMode
  javaPath?: string
  windowMode: InstanceMode
  windowWidth?: number
  windowHeight?: number
  fullscreen?: boolean
  serverAddress?: string
  extraJvmArgs: string[]
  extraGameArgs: string[]
  effective: EffectiveInstanceSettings
}

/**
 * version.saveSettings 的 params（除 versionId 外全部可选，只写要改的键）。
 * 传 null 表示恢复跟随全局；非 manual 档位下数值应传 null 交由桥接忽略。
 */
export interface SaveVersionSettingsParams {
  versionId: string
  isolation?: InstanceIsolation | null
  memoryMode?: InstanceMode
  memoryMinMb?: number | null
  memoryMaxMb?: number | null
  javaMode?: InstanceMode
  javaPath?: string | null
  windowMode?: InstanceMode
  windowWidth?: number | null
  windowHeight?: number | null
  fullscreen?: boolean | null
}

/** version.toggleContent / version.deleteContent 的返回：重算后的该类别完整列表。 */
export interface ContentFilesResult {
  files: ContentFile[]
}

/** version.checkUpdates 的单个更新项（`UpdateCheckResult`）。 */
export interface ContentUpdate {
  fileName: string
  currentVersion: string
  latestVersion: string
  latestFileName: string
  latestUrl: string
  latestSha1?: string
}

/** version.updateMod 的返回：更新后的文件名（停用的模组会保留 `.disabled` 后缀）。 */
export interface UpdateModResult {
  ok: boolean
  fileName: string
  oldFileName: string
  latestVersion: string
}

/** version.checkDependencies 里缺失的必需依赖。 */
export interface MissingDependencyInfo {
  projectId: string
  title: string
  versionId?: string
  fileName?: string
}

/** version.checkDependencies 的返回。
 *  mods 目录不存在时桥接会提前返回，只带前四个字段，故后三项可选。 */
export interface DependencyReport {
  missing: MissingDependencyInfo[]
  /** 可选依赖的项目 id（仅提示，未安装不影响启动）。 */
  optional: Array<{ projectId: string; title: string }>
  /** 已安装但被标记为 incompatible 的模组标题。 */
  incompatible: string[]
  /** 模组超过 200 个、只检查了前 200 个。 */
  truncated: boolean
  checkedMods?: number
  resolvedMods?: number
  inspectedVersions?: number
}

/** content.search 的单个命中（`ModrinthHit`）。 */
export interface ContentSearchHit {
  projectId: string
  slug: string
  title: string
  description: string
  author: string
  downloads: number
  iconUrl?: string
  displayCategories: string[]
  loaders: string[]
  webUrl?: string
}

/** content.install 的 `installed` 单项（含递归拉进来的 required 依赖）。 */
export interface InstalledContent {
  projectId: string
  title: string
  fileName: string
  versionNumber?: string
  path?: string
}

/** content.install 的 `missing` 单项。 */
export interface MissingContent {
  projectId: string
  /** 「没有兼容版本」/「没有可下载文件」。 */
  reason: string
}

/** content.install 的返回。 */
export interface ContentInstallResult {
  installed: InstalledContent[]
  missing: MissingContent[]
}

/** version.updateMod / content.install 期间推送的 `content.progress` 事件参数。 */
export interface ContentProgressEvent {
  projectId: string
  /** 形如 `下载 xxx.jar`。 */
  stage: string
}

/** version.openContentFolder / versions.openFolder 的返回。 */
export interface OpenFolderResult {
  ok: boolean
  path: string
}

export function onBridgeEvent(
  handler: (event: string, params: Record<string, unknown>) => void
): void {
  window.bridgeEvents.subscribe(handler)
}
