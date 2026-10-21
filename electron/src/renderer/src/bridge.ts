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

// ── 登录（对齐桥接 auth.*） ────────────────────────────────────────────────

/** 账户登录后的身份摘要（auth.* 的 account 字段，比 AccountList 精简）。 */
export interface AuthAccount {
  uuid: string
  name: string
  /** microsoft / external / offline */
  type: string
  /** 外置账户的认证服务器展示名 */
  serverName?: string
}

/** 微软设备码会话：auth.microsoft.start 的返回体。 */
export interface DeviceCodeSession {
  /** 展示给用户、需要在网页上输入的一次性代码 */
  userCode: string
  /** 回传给 auth.microsoft.poll 的设备码（不要展示给用户） */
  deviceCode: string
  /** 验证站点（Win 端：复制验证码并用它开浏览器） */
  verificationUri: string
  /** 建议的轮询间隔（秒） */
  interval: number
}

export type AuthPollState = 'pending' | 'success'

/** auth.microsoft.poll 的返回体：pending 时只有 message，success 时带 account。 */
export interface MicrosoftPollResult {
  state: AuthPollState
  /** pending：等待授权 / 降速轮询等说明；失败会直接抛错，message 即原因 */
  message?: string
  account?: AuthAccount
}

/** auth.yggdrasil.login 的返回体（失败直接抛错，错误原文即后端消息）。 */
export interface ExternalLoginResult {
  state: string
  account: AuthAccount
}

/** accounts.setSkin：设置成功后回传落盘后的账户。 */
export interface SetSkinResult {
  account: GameAccount
}

// ── 皮肤库（对齐桥接 skins.*） ──────────────────────────────────────────────

/** 皮肤型号：auto = 按纹理自动判断纤细手臂；classic / slim 为强制。 */
export type SkinModel = 'auto' | 'classic' | 'slim'

/** 皮肤库条目（skins.list / skins.* 操作的单条返回）。 */
export interface SkinEntry {
  id: string
  name: string
  model: SkinModel
  /** 皮肤库目录内的文件名（<id>.png） */
  fileName: string
  /** 绝对路径：渲染层读不到文件系统，靠它向主进程换缩略图 */
  path: string
  /** 来源：file（本地导入）/ url（链接导入）/ preset（启动器自带） */
  source: string
  sourceUrl?: string
  width: number
  height: number
  /** ISO-8601 */
  addedAt: string
}

export interface SkinList {
  skins: SkinEntry[]
}

/** skins.importPath / importUrl / rename / setModel 的返回体。 */
export interface SkinResult {
  skin: SkinEntry
}

export interface OkResult {
  ok: boolean
}

/** skins.station.list：皮肤站（LittleSkin 等）衣柜里的皮肤。 */
export interface SkinStationEntry {
  id: string
  name: string
  model?: string
  /** 皮肤站给出的纹理地址 */
  url?: string
  slim?: boolean
}

export interface SkinStationList {
  skins: SkinStationEntry[]
}

/**
 * skins.station.apply。
 * 注意：桥接实际返回的是 `{ message }`（服务端回执），不是 `{ ok }`；
 * 早期规格里写的 `ok` 这里一并留着，两种形状都能收。
 */
export interface SkinStationApplyResult {
  message?: string
  ok?: boolean
}

/** 主进程把本地 PNG 读成的 data URL（skin:icon IPC）。 */
export interface SkinIconResult {
  dataUrl: string
}

// ── 内容管理类型转出（定义见 bridge-content.ts） ────────────────────────────

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
  minMemoryMb: number
  maxMemoryMb: number
  downloadSource: string
  downloadConcurrency: number
  downloadMaxRetries: number
  downloadCustomMirrorUrl: string
  versionIsolated: boolean
  useBuiltinJvmArgs: boolean
  extraJvmArgs: string[]
  language: string
  theme: string
  accountWalkAnimation: boolean
  microsoftLoginEmbedded: boolean
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
    /** 主进程直连能力（皮肤缩略图 / 系统浏览器），浏览器调试模式下不存在。 */
    native?: NativeApi
  }
}

/** preload 用 contextBridge 暴露的本机能力。 */
export interface NativeApi {
  /** 读本地图片并回传 data URL（渲染层没有文件系统权限）。 */
  skinIcon: (path: string) => Promise<string>
  /** 用系统默认浏览器打开链接（设备码验证站点）。 */
  openExternal: (url: string) => Promise<void>
  /** 系统文件选择框；返回 null 表示用户取消。 */
  pickFile: (title: string) => Promise<string | null>
}

/** preload 未注入时（浏览器调试）返回 null，相关功能退化为提示。 */
export function nativeApi(): NativeApi | null {
  const api = (globalThis as { native?: NativeApi }).native
  return api !== undefined && typeof api.skinIcon === 'function' ? api : null
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

const MOCK_SKINS: SkinEntry[] = [
  {
    id: 'sk0a1b2c3d4e5',
    name: '经典 Steve',
    model: 'classic',
    fileName: 'sk0a1b2c3d4e5.png',
    path: '/home/user/.starcloud/skins/sk0a1b2c3d4e5.png',
    source: 'file',
    width: 64,
    height: 64,
    addedAt: ago(52)
  },
  {
    id: 'sk1b2c3d4e5f6',
    name: '纤细 Alex',
    model: 'slim',
    fileName: 'sk1b2c3d4e5f6.png',
    path: '/home/user/.starcloud/skins/sk1b2c3d4e5f6.png',
    source: 'url',
    sourceUrl: 'https://littleskin.cn/skin/alex.png',
    width: 64,
    height: 64,
    addedAt: ago(9)
  },
  {
    id: 'sk2c3d4e5f6a7',
    name: '高清 128×128',
    model: 'auto',
    fileName: 'sk2c3d4e5f6a7.png',
    path: '/home/user/.starcloud/skins/sk2c3d4e5f6a7.png',
    source: 'file',
    width: 128,
    height: 128,
    addedAt: ago(200)
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
    case 'auth.microsoft.start':
      return {
        userCode: 'STAR-3F7K',
        deviceCode: 'mock-device-code',
        verificationUri: 'https://www.microsoft.com/link',
        interval: 5
      } satisfies DeviceCodeSession
    case 'auth.microsoft.poll':
      return { state: 'pending', message: '等待用户授权' } satisfies MicrosoftPollResult
    case 'auth.yggdrasil.login':
      return {
        state: 'success',
        account: {
          uuid: 'e5',
          name: String(p.username ?? 'Player'),
          type: 'external',
          serverName: 'Mock 皮肤站'
        }
      } satisfies ExternalLoginResult
    case 'skins.list':
      return { skins: MOCK_SKINS } satisfies SkinList
    case 'skins.importPath':
    case 'skins.importUrl': {
      const picked = MOCK_SKINS[0]
      return {
        skin: {
          ...picked,
          id: 'sknew' + String(p.url ?? p.path ?? '').length,
          name: String(p.name ?? '新导入的皮肤'),
          source: p.url !== undefined ? 'url' : 'file',
          sourceUrl: p.url !== undefined ? String(p.url) : undefined,
          addedAt: new Date(MOCK_NOW).toISOString()
        }
      } satisfies SkinResult
    }
    case 'skins.rename':
      return {
        skin: {
          ...(MOCK_SKINS.find((s) => s.id === p.id) ?? MOCK_SKINS[0]),
          name: String(p.name)
        }
      } satisfies SkinResult
    case 'skins.setModel':
      return {
        skin: {
          ...(MOCK_SKINS.find((s) => s.id === p.id) ?? MOCK_SKINS[0]),
          model: p.model === 'slim' ? 'slim' : 'classic'
        }
      } satisfies SkinResult
    case 'skins.delete':
      return { ok: true } satisfies OkResult
    case 'skins.station.list':
      return { skins: [] } satisfies SkinStationList
    case 'skins.station.apply':
      return { message: '皮肤已提交到皮肤站' } satisfies SkinStationApplyResult
    case 'accounts.setSkin':
      return {
        account: {
          ...(MOCK_ACCOUNTS.find((a) => a.uuid === p.uuid) ?? MOCK_ACCOUNTS[0]),
          skinId: p.skinId === undefined ? undefined : String(p.skinId),
          skinModel: p.model === undefined ? undefined : String(p.model)
        }
      } satisfies SetSkinResult
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

/** 剥掉 Electron IPC 的包装前缀，只把真正的失败原因给用户看。 */
function cleanError(e: unknown): Error {
  const raw = e instanceof Error ? e.message : String(e)
  const stripped = raw
    .replace(/^Error invoking remote method '[^']*':\s*/i, '')
    .replace(/^Error:\s*/i, '')
  return new Error(stripped === '' ? raw : stripped)
}

export function request<T>(method: string, params: unknown = {}): Promise<T> {
  if (typeof window === 'undefined' || window.bridge === undefined) {
    return Promise.reject(new Error('桥接不可用'))
  }
  return (window.bridge.request(method, params) as Promise<T>).catch((e: unknown) => {
    throw cleanError(e)
  })
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
