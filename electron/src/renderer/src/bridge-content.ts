/// 内容管理相关类型（桥接 ContentLibraryService / 版本详情）。
/// 字段与 C# Starcloud.Bridge 的 camelCase 返回体一一对应；
/// `WhenWritingNull` 会省略 null 字段，所以可空字段一律写成可选属性。

export type ContentKind = 'mod' | 'resourcepack' | 'shader'

export type InstanceIsolation = 'shared' | 'isolated'

export type InstanceMode = 'global' | 'manual' | 'auto'

export interface ContentFile {
  fileName: string
  /** 去掉 `.disabled` 后缀的展示名。 */
  displayName: string
  enabled: boolean
  sizeBytes: number
  path: string
}

export interface VersionDetail {
  versionId: string
  gameRoot: string
  isolated: boolean
  mcVersion: string
  loader: string
  loaderVersion?: string
  lastPlayedAt?: string
  mods: ContentFile[]
  resourcepacks: ContentFile[]
  shaderpacks: ContentFile[]
}

export interface EffectiveInstanceSettings {
  gameRoot: string
  isolated: boolean
  minMem: number
  maxMem: number
  /** auto 档位时为 null（跟随版本 JSON 推荐）。 */
  java?: string
  windowW?: number
  windowH?: number
  fullscreen?: boolean
}

export interface VersionSettings {
  versionId: string
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

/** version.saveSettings 的 patch：null 表示恢复跟随全局。 */
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

/** 启停/删除后桥接直接回传重算后的完整列表。 */
export interface ContentFilesResult {
  files: ContentFile[]
}

export interface ContentUpdate {
  fileName: string
  currentVersion: string
  latestVersion: string
  latestFileName: string
  latestUrl: string
  latestSha1?: string
}

export interface UpdateModResult {
  ok: boolean
  fileName: string
  oldFileName: string
  latestVersion: string
}

export interface MissingDependencyInfo {
  projectId: string
  title: string
  versionId?: string
  fileName?: string
}

export interface DependencyReport {
  missing: MissingDependencyInfo[]
  optional: Array<{ projectId: string; title: string }>
  incompatible: string[]
  truncated: boolean
  checkedMods?: number
  resolvedMods?: number
  inspectedVersions?: number
}

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

export interface InstalledContent {
  projectId: string
  title: string
  fileName: string
  versionNumber?: string
  path?: string
}

export interface MissingContent {
  projectId: string
  reason: string
}

export interface ContentInstallResult {
  installed: InstalledContent[]
  missing: MissingContent[]
}

/** version.updateMod / content.install 期间推送的 content.progress 参数。 */
export interface ContentProgressEvent {
  projectId: string
  stage: string
}

/** version.openContentFolder / versions.openFolder 的返回。 */
export interface OpenFolderResult {
  ok: boolean
  path: string
}
