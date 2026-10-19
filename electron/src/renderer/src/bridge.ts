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
  /** 加载器：vanilla / neoforge / forge / fabric / quilt（桥接推断）。 */
  loader?: string
  /** 版本类型：release / snapshot / old_beta / old_alpha（取自版本 JSON）。 */
  type?: string
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
  /** 桥接字段名是 lastPlayed（历史命名），不是 lastPlayedAt。 */
  lastPlayed?: string
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
    bridge?: {
      request: (method: string, params?: unknown) => Promise<unknown>
      appVersion: () => Promise<string>
    }
    bridgeEvents?: {
      subscribe: (
        handler: (event: string, params: Record<string, unknown>) => void
      ) => void
    }
  }
}

const MOCK_NOW = Date.now()
const ago = (h: number): string => new Date(MOCK_NOW - h * 3600_000).toISOString()

const MOCK_VERSIONS: InstalledVersion[] = [
  { id: 'neoforge-26.2.0.88', sizeBytes: 26_843_545, lastPlayedAt: ago(5), isDefault: true },
  { id: '26.3-rc-3', sizeBytes: 39_690_000, lastPlayedAt: ago(30) },
  { id: '26.3-4', sizeBytes: 39_690_000, lastPlayedAt: ago(48) },
  { id: 'fabric-loader-0.16.9-26.3', sizeBytes: 41_210_000, lastPlayedAt: ago(72) },
  { id: '1.21.11', sizeBytes: 38_040_000, lastPlayedAt: ago(120) },
  { id: '1.20.4', sizeBytes: 24_445_539, lastPlayedAt: ago(24) }
]

const MOCK_ACCOUNTS: GameAccount[] = [
  { uuid: 'a1', name: 'Notch', type: 'microsoft', isDefault: true, hasSession: true },
  { uuid: 'c3', name: 'Steve', type: 'offline', isDefault: false, hasSession: true },
  {
    uuid: 'e5',
    name: '皮肤站玩家',
    type: 'external',
    isDefault: false,
    hasSession: false,
    serverName: 'LittleSkin'
  }
]

const CONTENT_FILE_MOCK = [
  {
    fileName: 'sodium-1.20.4.jar',
    displayName: 'sodium-1.20.4.jar',
    enabled: true,
    sizeBytes: 1_240_000,
    path: '/mods/sodium.jar'
  },
  {
    fileName: 'jei-1.20.4.jar.disabled',
    displayName: 'jei-1.20.4.jar',
    enabled: false,
    sizeBytes: 890_000,
    path: '/mods/jei.jar.disabled'
  },
  {
    fileName: 'rubidium-1.20.4.jar',
    displayName: 'rubidium-1.20.4.jar',
    enabled: true,
    sizeBytes: 2_100_000,
    path: '/mods/rubidium.jar'
  }
]

const LAB_BLOCK_IDS = [
  'minecraft:stone',
  'minecraft:cobblestone',
  'minecraft:oak_planks',
  'minecraft:glass',
  'minecraft:oak_stairs',
  'minecraft:torch',
  'minecraft:chest'
]

function mockOf(method: string, p: Record<string, unknown>): unknown {
  switch (method) {
    case 'catalog.installed':
      return { installed: MOCK_VERSIONS }
    case 'catalog.versions':
      return {
        remote: Array.from({ length: 24 }, (_, i) => ({
          id: `1.${21 - Math.floor(i / 8)}.${i % 8}`,
          type: i % 5 === 0 ? 'snapshot' : 'release',
          url: '',
          releaseTime: new Date(MOCK_NOW - i * 604800_000).toISOString(),
          installed: i < 2
        })),
        installed: MOCK_VERSIONS.map(({ id, sizeBytes }) => ({ id, sizeBytes }))
      }
    case 'accounts.list':
      return { accounts: MOCK_ACCOUNTS, current: 'a1' } satisfies AccountList
    case 'accounts.addOffline':
    case 'accounts.setCurrent':
    case 'accounts.remove':
    case 'accounts.setSkin':
    case 'settings.apply':
    case 'version.saveSettings':
      return {}
    case 'launch.start':
      return { sessionId: 1, pid: 4242, logFile: '/tmp/game.log' }
    case 'settings.get':
      return {
        gameDir: 'D:\\Games\\.minecraft',
        javaExecutable: '',
        maxMemoryMb: 6144,
        downloadSource: 'auto'
      }
    case 'java.list':
      return { runtimes: [{ versionString: '17.0.20.1', majorVersion: 17 }] }
    case 'saves.list':
      return {
        saves: ['生存 · 主世界', '建筑 · 城市', '红石 · 农场', '探索 · 群岛'].map(
          (levelName, i) => ({
            folderName: `world-${i + 1}`,
            levelName,
            gameRoot: 'D:\\Games\\.minecraft',
            versionId: i % 2 === 0 ? '1.20.4' : '',
            isolated: false,
            sizeBytes: 42_000_000 + i * 7_000_000,
            lastPlayedAt: ago(i * 48)
          })
        )
      }
    case 'saves.openFolder':
      return { ok: true, path: 'D:\\Games\\.minecraft\\saves' }
    case 'saves.backup':
      return { ok: true, path: 'backup.zip', sizeBytes: 12_000_000 }
    case 'saves.delete':
      return { ok: true, freedBytes: 42_000_000 }
    case 'version.detail': {
      const id = String(p.versionId ?? '1.20.4')
      return {
        versionId: id,
        gameRoot: `D:\\Games\\.minecraft\\versions\\${id}`,
        isolated: true,
        mcVersion: '1.20.4',
        loader: 'neoforge',
        lastPlayedAt: ago(24),
        mods: CONTENT_FILE_MOCK,
        resourcepacks: [
          {
            fileName: 'Faithful-64x.zip',
            displayName: 'Faithful-64x.zip',
            enabled: true,
            sizeBytes: 3_900_000,
            path: '/rp/faithful.zip'
          }
        ],
        shaderpacks: [
          {
            fileName: 'complementary.zip',
            displayName: 'BSL 8.zip',
            enabled: true,
            sizeBytes: 7_300_000,
            path: '/shaders/bsl.zip'
          }
        ]
      }
    }
    case 'version.settings':
      return {
        versionId: String(p.versionId ?? '1.20.4'),
        isolation: 'isolated',
        memoryMode: 'manual',
        memoryMinMb: 1024,
        memoryMaxMb: 6144,
        javaMode: 'auto',
        windowMode: 'global',
        extraJvmArgs: [],
        extraGameArgs: [],
        effective: {
          gameRoot: 'D:\\Games\\.minecraft\\versions\\1.20.4',
          isolated: true,
          minMem: 1024,
          maxMem: 6144,
          windowW: 854,
          windowH: 480,
          fullscreen: false
        }
      }
    case 'version.toggleContent':
    case 'version.deleteContent':
      return { files: CONTENT_FILE_MOCK }
    case 'version.openContentFolder':
      return { ok: true, path: 'D:\\Games\\.minecraft\\versions\\1.20.4\\mods' }
    case 'version.checkUpdates':
      return {
        updates: [
          {
            fileName: 'rubidium-1.20.4.jar',
            currentVersion: '1.0.0',
            latestVersion: '1.1.2',
            latestFileName: 'rubidium-1.20.4.jar',
            latestUrl: ''
          }
        ]
      }
    case 'version.updateMod':
      return {
        ok: true,
        fileName: String(p.fileName),
        oldFileName: String(p.fileName),
        latestVersion: '1.1.2'
      }
    case 'version.checkDependencies':
      return {
        missing: [],
        optional: [{ projectId: 'jei', title: 'JEI' }],
        incompatible: [],
        truncated: false
      }
    case 'versions.delete':
      return { deleted: true, freedBytes: 39_690_000, wasDefault: false }
    case 'versions.openFolder':
      return { ok: true, path: 'D:\\Games\\.minecraft\\versions' }
    case 'versions.setDefault':
      return { ok: true, lastVersion: String(p.versionId) }
    case 'content.search':
      return {
        totalHits: 128,
        hits: ['Sodium', 'Iris Shaders', 'JourneyMap', 'Fabric API', 'Lithium', 'Phi'].map(
          (title, i) => ({
            projectId: `p${i}`,
            slug: `mod-${i}`,
            title,
            description: '显著降低内存占用、改善卡顿的优化类模组，适合长期生存档。',
            author: [
              'jellysquid3',
              'IMS',
              'TeamPlex',
              'FabricMC',
              'CaffeineMC',
              'FoundationGames'
            ][i],
            downloads: 248033070 - i * 3100000,
            iconUrl: '',
            displayCategories: ['optimization'],
            loaders: [],
            webUrl: ''
          })
        )
      }
    case 'content.install':
      return {
        installed: [
          {
            projectId: String(p.projectId),
            title: String(p.title ?? ''),
            fileName: 'mod.jar',
            path: '/mods/mod.jar'
          }
        ],
        missing: []
      }
    case 'multiplayer.state':
      return {
        providers: [
          {
            id: 'terracotta',
            displayName: '陶瓦联机',
            vendor: 'Terracotta',
            vendorUrl: '',
            description: '基于 EasyTier 的 P2P 联机，无需公网 IP 或端口映射',
            builtIn: true
          }
        ],
        activeId: 'terracotta',
        nickname: 'Steve',
        nodeRegion: 'auto',
        core: { installed: true, version: '1.4.0', sessionRunning: false },
        state: { phase: 'idle', isBusy: false }
      }
    case 'multiplayer.setNickname':
    case 'multiplayer.setNodeRegion':
    case 'multiplayer.selectProvider':
    case 'multiplayer.host':
    case 'multiplayer.join':
    case 'multiplayer.idle':
    case 'multiplayer.stop':
      return mockOf('multiplayer.state', {})
    case 'multiplayer.logs':
      return { logs: '[terracotta] 核心已启动\n[terracotta] 等待房间指令…' }
    case 'plugins.list':
      return {
        plugins: [
          {
            id: 'dev.starcloud.demo',
            name: '示例插件',
            version: '1.2.0',
            apiVersion: '2.0',
            author: 'Starcloud',
            description: '演示面板与动作调用',
            permissions: ['launcher:ui_panel', 'launcher:storage'],
            runtime: 'js',
            official: true,
            enabled: true,
            disclaimerAccepted: true,
            loaded: true,
            panelCount: 1,
            dir: '/plugins/demo',
            dependencies: {}
          },
          {
            id: 'third.party',
            name: '第三方示例',
            version: '0.4.1',
            apiVersion: '2.0',
            author: 'someone',
            description: '未验证的第三方插件',
            permissions: ['launcher:content_read'],
            runtime: 'py',
            official: false,
            enabled: false,
            disclaimerAccepted: false,
            loaded: false,
            panelCount: 0,
            dir: '/plugins/third',
            dependencies: {}
          }
        ],
        pageTipShown: false
      }
    case 'plugins.install':
      return {
        id: 'x',
        name: String(p.path ?? ''),
        version: '1.0.0',
        apiVersion: '2.0',
        author: '',
        description: '',
        permissions: [],
        official: false,
        enabled: false,
        disclaimerAccepted: false,
        loaded: false,
        panelCount: 0,
        dir: '',
        dependencies: {}
      }
    case 'plugins.setEnabled':
    case 'plugins.uninstall':
    case 'plugins.openFolder':
      return { ok: true }
    case 'lab.load':
      return {
        path: String(p.path ?? 'demo.litematic'),
        sizeX: 24,
        sizeY: 12,
        sizeZ: 32,
        truncated: false,
        totalBlocks: 2841,
        distinctBlocks: LAB_BLOCK_IDS.length,
        stats: LAB_BLOCK_IDS.map((id, i) => ({
          id,
          count: [1204, 640, 512, 241, 122, 78, 44][i]
        })),
        preview: Array.from({ length: 800 }, (_, i) => ({
          x: i % 24,
          y: Math.floor(i / 24) % 12,
          z: Math.floor(i / (24 * 12)),
          id: LAB_BLOCK_IDS[i % LAB_BLOCK_IDS.length]
        })),
        previewTruncated: true
      } satisfies ProjectionPreview
    case 'lab.worldEater':
      return { ok: true, path: 'world-eater.litematic' }
    case 'nova.config.get':
      return { baseUrl: '', apiKey: '', model: '' }
    case 'nova.config.set':
      return {
        baseUrl: String(p.baseUrl ?? ''),
        apiKey: String(p.apiKey ?? ''),
        model: String(p.model ?? '')
      }
    case 'nova.chat':
      return { accepted: true }
    default:
      return {}
  }
}

/** 是否处于浏览器调试模式（没有 Electron preload 桥）。 */
export const isMockMode =
  typeof window !== 'undefined' && window.bridge === undefined

/** 浏览器模式下装上 mock 桥；Electron 下什么都不做。 */
export function installMockBridgeIfNeeded(): void {
  if (!isMockMode || typeof window === 'undefined') return

  const handlers: Array<(event: string, params: Record<string, unknown>) => void> = []
  const emit = (event: string, params: Record<string, unknown>): void => {
    for (const handler of handlers) handler(event, params)
  }

  window.bridge = {
    request: async (method: string, params: unknown = {}) => {
      // 一点延迟，好让骨架屏/加载态在调试时也可见
      await new Promise((r) => setTimeout(r, 60))
      if (method === 'launch.start') {
        setTimeout(() => emit('game.output', { line: '[INFO] Environment: Linux' }), 300)
        setTimeout(() => emit('game.output', { line: '[INFO] Setting user: Notch' }), 900)
        setTimeout(
          () => emit('game.output', { line: '[INFO] Backend library: LWJGL 3.3.2' }),
          1500
        )
      }
      return mockOf(method, params as Record<string, unknown>)
    },
    appVersion: async () => '0.3.4-mock'
  }
  window.bridgeEvents = { subscribe: (handler) => void handlers.push(handler) }

  // eslint-disable-next-line no-console
  console.info('[bridge] 浏览器调试模式：使用内置 mock 数据')
}

// ── 对外调用入口 ─────────────────────────────────────────────────────────────

export function request<T>(method: string, params: unknown = {}): Promise<T> {
  if (typeof window === 'undefined' || window.bridge === undefined) {
    return Promise.reject(new Error('桥接不可用'))
  }
  return window.bridge.request(method, params) as Promise<T>
}

export function onBridgeEvent(
  handler: (event: string, params: Record<string, unknown>) => void
): void {
  if (typeof window === 'undefined' || window.bridgeEvents === undefined) return
  window.bridgeEvents.subscribe(handler)
}

// ── 内容管理类型转出（定义见 bridge-content.ts） ──
export type {
  ContentKind,
  InstanceIsolation,
  InstanceMode
} from './bridge-content'
export type {
  ContentFile,
  VersionDetail,
  EffectiveInstanceSettings,
  VersionSettings,
  SaveVersionSettingsParams,
  ContentFilesResult,
  ContentUpdate,
  UpdateModResult,
  MissingDependencyInfo,
  DependencyReport,
  ContentSearchHit,
  InstalledContent,
  MissingContent,
  ContentInstallResult,
  ContentProgressEvent,
  OpenFolderResult
} from './bridge-content'

/** 启动器版本号（标题栏显示）。 */
export function appVersion(): Promise<string> {
  if (typeof window === 'undefined' || window.bridge === undefined) {
    return Promise.resolve('0.3.4')
  }
  return window.bridge.appVersion()
}
