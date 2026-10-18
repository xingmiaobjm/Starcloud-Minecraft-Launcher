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

export function onBridgeEvent(
  handler: (event: string, params: Record<string, unknown>) => void
): void {
  window.bridgeEvents.subscribe(handler)
}
