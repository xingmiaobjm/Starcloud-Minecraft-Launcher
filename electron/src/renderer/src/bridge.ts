/// 桥接协议类型（与 C# Starcloud.Bridge 的返回一一对应）。

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
  lastPlayedAt?: string
  isDefault?: boolean
  isolated?: boolean
  inheritsFrom?: string
  jarExists?: boolean
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
