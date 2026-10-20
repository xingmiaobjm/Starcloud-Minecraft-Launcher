/// 首页：左「已安装版本」卡 + 右「账户 / 启动」卡 + 底「运行日志」。
/// 规格对齐 Win 端 HomePage.xaml：
/// · 左栏 Grid "* / 360"，四行 = 启动自检 → 插件挂载 → 首页卡片 → 版本列表卡
/// · 版本行 Grid "Auto / 260px 1fr auto"：32px 方块图标 + 名称(260) + 芯片 + 两行数据列
/// · 版本行右键菜单：问 Nova / 版本详情 / 打开游戏目录 / 删除此版本
/// · 右栏：正版/外置/离线 三段滑动指示器 + 头像 + 品牌黄大启动按钮（右侧下拉段）
import { useEffect, useState } from 'react'

import { request } from '../bridge'
import type { AccountList, GameAccount, InstalledVersion } from '../bridge'
import type { GameSessionApi } from '../state/session'
import { Segmented, useContextMenu, useToasts } from '../ui'
import grassIcon from '../assets/mc-icons/natural_grass_block.png'
import amethystIcon from '../assets/mc-icons/block_of_amethyst.png'
import cobbleIcon from '../assets/mc-icons/masonry_cobblestone.png'
import dirtIcon from '../assets/mc-icons/natural_dirt.png'
import neoforgeIcon from '../assets/mc-icons/neoforge.png'
import forgeIcon from '../assets/mc-icons/forge.png'
import fabricIcon from '../assets/mc-icons/fabric.png'
import quiltIcon from '../assets/mc-icons/quilt.png'


type Mode = 'microsoft' | 'external' | 'offline'

const MODE_LABEL: Record<Mode, string> = {
  microsoft: '正版',
  external: '外置',
  offline: '离线'
}

/**
 * 版本行图标（对齐 Win 端 VersionRowHelpers）：
 * 加载器图标优先（neoforge/forge/fabric/quilt），原版按版本类型
 * （正式版草方块 / 快照紫水晶 / 远古版圆石与泥土）。
 * 图标文件与 Win 端同一套 MC-Icons 素材。
 */
const LOADER_ICONS: Record<string, string> = {
  neoforge: neoforgeIcon,
  forge: forgeIcon,
  fabric: fabricIcon,
  quilt: quiltIcon
}
const TYPE_ICONS: Record<string, string> = {
  release: grassIcon,
  snapshot: amethystIcon,
  old_beta: cobbleIcon,
  old_alpha: dirtIcon
}

function iconFor(version: { id: string; loader?: string; type?: string }): string {
  const loader = version.loader ?? ''
  if (loader !== '' && LOADER_ICONS[loader] !== undefined) return LOADER_ICONS[loader]
  const lower = version.id.toLowerCase()
  if (lower.includes('neoforge')) return neoforgeIcon
  if (lower.includes('forge')) return forgeIcon
  if (lower.includes('fabric')) return fabricIcon
  if (lower.includes('quilt')) return quiltIcon
  const type = version.type ?? (lower.includes('-rc') || lower.includes('-pre') ? 'snapshot' : 'release')
  return TYPE_ICONS[type] ?? grassIcon
}

/** 类型标签（对齐 Win 端 DetectKind）：加载器优先，否则版本类型。 */
function tagFor(version: { id: string; loader?: string; type?: string }): string {
  const loader = version.loader ?? ''
  const LOADER_LABEL: Record<string, string> = {
    neoforge: 'NeoForge',
    forge: 'Forge',
    fabric: 'Fabric',
    quilt: 'Quilt'
  }
  if (loader !== '' && LOADER_LABEL[loader] !== undefined) return LOADER_LABEL[loader]
  const lower = version.id.toLowerCase()
  if (lower.includes('neoforge')) return 'NeoForge'
  if (lower.includes('fabric')) return 'Fabric'
  if (lower.includes('quilt')) return 'Quilt'
  if (lower.includes('forge')) return 'Forge'
  const type = version.type ?? (lower.includes('-rc') || lower.includes('-pre') ? 'snapshot' : 'release')
  const TYPE_LABEL: Record<string, string> = {
    release: '正式版',
    snapshot: '快照版',
    old_beta: '远古 Beta',
    old_alpha: '远古 Alpha'
  }
  return TYPE_LABEL[type] ?? '正式版'
}

/** 上次启动文案（对齐 Win 端 FormatLastPlayed）：
 *  今天 HH:mm / 昨天 HH:mm / N 天前 / N 个月前 / yyyy-MM-dd */
function lastPlayed(iso?: string): string {
  if (iso === undefined || iso === null || iso === '') return '从未启动'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '从未启动'
  const pad = (n: number): string => n.toString().padStart(2, '0')
  const hm = pad(d.getHours()) + ':' + pad(d.getMinutes())
  const today = new Date()
  const dayDiff = Math.floor(
    (new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime() -
      new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()) /
      86400000
  )
  if (dayDiff <= 0) return '今天 ' + hm
  if (dayDiff === 1) return '昨天 ' + hm
  if (dayDiff < 30) return dayDiff + ' 天前'
  if (dayDiff < 365) return Math.floor(dayDiff / 30) + ' 个月前'
  return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate())
}

/** 排序对齐 Win 端：默认版本置顶，其余按 id 倒序（忽略大小写）。 */
function sortVersions(list: InstalledVersion[]): InstalledVersion[] {
  return [...list].sort((a, b) => {
    const aLast = a.isDefault === true ? 1 : 0
    const bLast = b.isDefault === true ? 1 : 0
    if (aLast !== bLast) return bLast - aLast
    const ai = a.id.toLowerCase()
    const bi = b.id.toLowerCase()
    return ai < bi ? 1 : ai > bi ? -1 : 0
  })
}

function fmtSize(bytes: number): string {
  if (!bytes) return '未完成'
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

export default function HomePage(props: {
  session: GameSessionApi
  /** 跳转：点未就绪的自检项 / 空态引导 / 右键菜单都用它。 */
  onNavigate: (page: 'versions' | 'nova' | 'settings' | 'download' | 'accounts') => void
}): JSX.Element {
  const session = props.session
  const { push, host } = useToasts()
  const [installed, setInstalled] = useState<InstalledVersion[]>([])
  const [accounts, setAccounts] = useState<GameAccount[]>([])
  const [selected, setSelected] = useState<string | undefined>()
  const [mode, setMode] = useState<Mode>('offline')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const [java, setJava] = useState<{ version: string; major: number } | null | undefined>(
    undefined
  )
  const [gameDir, setGameDir] = useState('')
  const [avatarFailed, setAvatarFailed] = useState(false)

  async function reload(): Promise<void> {
    setLoading(true)
    try {
      const catalog = await request<{ installed: InstalledVersion[] }>('catalog.installed')
      setInstalled(sortVersions(catalog.installed))
      setSelected((prev) => {
        if (prev !== undefined && catalog.installed.some((v) => v.id === prev)) return prev
        return catalog.installed.find((v) => v.isDefault)?.id ?? catalog.installed[0]?.id
      })
    } catch (e) {
      setError(String(e))
    }
    try {
      const list = await request<AccountList>('accounts.list')
      setAccounts(list.accounts)
      const cur = list.accounts.find((a) => a.uuid === list.current) ?? list.accounts[0]
      if (cur !== undefined) {
        setMode(
          cur.type === 'microsoft'
            ? 'microsoft'
            : cur.type === 'external'
              ? 'external'
              : 'offline'
        )
      }
    } catch (e) {
      setError(String(e))
    }
    // 启动自检还需要游戏目录与 Java（Win 端 LeftNoticeArea）
    try {
      const settings = await request<{ gameDir: string }>('settings.get')
      setGameDir(settings.gameDir)
    } catch { /* 读取失败不阻塞主流程 */ }
    setLoading(false) // ← 主列表就绪即结束 loading，Java 在后台补
    // Java 扫描慢（多盘 + java -version），放最后并不阻断页面：
    // Bridge 是串行行式 JSON，若不分开会把上面的 catalog/accounts 一起阻住。
    void request<{ runtimes: Array<{ versionString: string; majorVersion: number }> }>('java.list')
      .then((r) => {
        const first = r.runtimes[0]
        setJava(first != null ? { version: first.versionString, major: first.majorVersion } : null)
      })
      .catch(() => setJava(null))
  }

  useEffect(() => {
    void reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 换账户后重试头像（避免上一个账户的 404 把新账户也锁在首字母）
  const modeAccounts = accounts.filter((a) =>
    mode === 'offline'
      ? a.type === 'offline'
      : mode === 'microsoft'
        ? a.type === 'microsoft'
        : a.type === 'external'
  )
  const account = modeAccounts[0]
  // 与 Win 端 AvatarHelper.UrlFor 同规则：微软账户按 UUID，其它按用户名
  const avatarId =
    account === undefined ? '' : account.type === 'microsoft' ? account.uuid : account.name
  const avatarUrl = avatarId === '' ? null : 'https://minotar.net/helm/' + encodeURIComponent(avatarId) + '/128'
  const running = session.pid !== undefined && session.pid > 0
  const canLaunch =
    !session.launching && !running && selected !== undefined && account !== undefined

  // 启动自检：版本 / 账户 / Java 三项，未就绪时给出可点击的跳转
  const checks = [
    {
      label: '游戏版本',
      ok: selected !== undefined,
      hint: selected ?? '去「下载」页安装',
      go: 'download' as const
    },
    {
      label: '游戏账户',
      ok: account !== undefined,
      hint: account?.name ?? '去「账户」页添加',
      go: 'accounts' as const
    },
    {
      label: 'Java',
      ok: java != null && java.major >= 17,
      hint: java === undefined ? '检测中…' : java === null ? '未安装 Java 17+' : java.version,
      go: 'settings' as const
    }
  ]
  const readyCount = checks.filter((c) => c.ok).length

  const menu = useContextMenu([
    { label: '问 Nova', onSelect: () => props.onNavigate('nova') },
    { label: '版本详情', onSelect: () => props.onNavigate('versions') },
    {
      label: '打开游戏目录',
      onSelect: () => {
        if (selected === undefined) return
        void request('versions.openFolder', { versionId: selected }).catch((e) =>
          push('error', String(e))
        )
      }
    },
    {
      label: '删除此版本',
      danger: true,
      onSelect: () => {
        if (selected === undefined) return
        const target = selected
        void request<{ freedBytes: number }>('versions.delete', { versionId: target })
          .then((r) => {
            push('success', `已删除 ${target}（释放 ${fmtSize(r.freedBytes)}）`)
            return reload()
          })
          .catch((e) => push('error', String(e)))
      }
    }
  ])

  const statusColor = running
    ? 'var(--success)'
    : session.launching
      ? 'var(--warning)'
      : session.status.startsWith('启动失败')
        ? 'var(--danger)'
        : 'var(--text-disabled)'

  return (
    <div className="page">
      <div className="page-head">
        <h1 className="page-title" style={{ flex: 1 }}>
          首页
        </h1>
      </div>

      {/* 满高两栏：卡片撑满窗口高度、列表在卡内滚动（对齐 Win 端 HomePage） */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: 24 }}>
          {/* ── 左栏（*）：启动自检 → 版本列表 ── */}
          <div
            style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}
          >
            {readyCount < checks.length && (
              <div className="card" style={{ marginBottom: 16 }}>
                <div
                  style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}
                >
                  <span style={{ fontSize: 15, fontWeight: 600 }}>启动自检</span>
                  <span className="t-cap">
                    {readyCount}/{checks.length} 项就绪
                  </span>
                  <div style={{ flex: 1 }} />
                  <button
                    className="btn btn-subtle btn-sm"
                    title="重新检测"
                    onClick={() => void reload()}
                  >
                    ↻
                  </button>
                </div>
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                  {checks.map((c) => (
                    <button
                      key={c.label}
                      className={`chip${c.ok ? ' chip-accent' : ' chip-danger'}`}
                      style={{ cursor: c.ok ? 'default' : 'pointer' }}
                      onClick={() => {
                        if (!c.ok) props.onNavigate(c.go)
                      }}
                    >
                      {c.ok ? '✓' : '⚠'} {c.label}：{c.hint}
                    </button>
                  ))}
                </div>
              </div>
            )}

            <div className="card card-flush" style={{ flex: 1, minHeight: 0 }}>
              <div className="card-head">
                <span className="card-title">已安装版本</span>
                <span className="chip">{installed.length}</span>
                <div style={{ flex: 1 }} />
                <button
                  className="btn btn-subtle btn-sm"
                  onClick={() => void reload()}
                >
                  ↻ 刷新
                </button>
              </div>
              <div
                style={{
                  flex: 1,
                  minHeight: 0,
                  overflowY: 'auto', overflowX: 'hidden',
                  padding: '0 10px 10px'
                }}
              >
                {loading && (
                  <div style={{ padding: 20, textAlign: 'center', opacity: 0.6 }}>加载中…</div>
                )}
                {!loading && installed.length === 0 && (
                  <div className="empty">
                    <div className="empty-title">还没有已安装的版本</div>
                    <div className="empty-hint">
                      去「下载」页选择一个版本安装。
                      {gameDir !== '' && <div>当前游戏目录：{gameDir}</div>}
                    </div>
                    <button
                      className="btn btn-primary"
                      onClick={() => props.onNavigate('download')}
                    >
                      前往下载
                    </button>
                  </div>
                )}
                {installed.map((version, index) => {
                  const isSelected = version.id === selected
                  const icon = iconFor(version)
                  return (
                    <div key={version.id} {...menu.bind()}>
                      <div
                        className={`row${isSelected ? ' row-active' : ''}`}
                        style={{
                          animationDelay: `${index * 39}ms`,
                          // Win 端列宽：图标 Auto / 名称 260 / 芯片 * / 数据 Auto
                          display: 'grid',
                          gridTemplateColumns: 'auto minmax(120px, 260px) minmax(0, 1fr) auto',
                          columnGap: 12
                        }}
                        onClick={() => setSelected(version.id)}
                      >
                        <div className="icon32">
                          <img src={icon} alt="" width={32} height={32} />
                        </div>
                        <span className="t-strong truncate">{version.id}</span>
                        <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                          <span className="chip">{tagFor(version)}</span>
                          {version.isDefault === true && (
                            <span className="chip chip-accent">默认</span>
                          )}
                        </div>
                        <div className="meta">
                          <span className="meta-k">上次启动</span>
                          <span className="meta-v">{lastPlayed(version.lastPlayedAt)}</span>
                          <span className="meta-k">大小</span>
                          <span className="meta-v">{fmtSize(version.sizeBytes)}</span>
                        </div>
                      </div>
                    </div>
                  )
                })}
              </div>
            </div>
          </div>

          {/* ── 右栏（360）：账户 / 启动 ── */}
          <div style={{ width: 360, flexShrink: 0, minHeight: 0, display: 'flex' }}>
            <div
              className="card"
              style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 14 }}
            >
              <Segmented<Mode>
                value={mode}
                options={[
                  { value: 'microsoft', label: MODE_LABEL.microsoft },
                  { value: 'external', label: MODE_LABEL.external },
                  { value: 'offline', label: MODE_LABEL.offline }
                ]}
                onChange={setMode}
              />

              <div
                style={{
                  flex: 1,
                  display: 'flex',
                  flexDirection: 'column',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: 10
                }}
              >
                {/* 头像：minotar 头部图（失败回退首字母） */}
                <div
                  style={{
                    width: 88,
                    height: 88,
                    borderRadius: 'var(--r-control)',
                    background:
                      account === undefined
                        ? 'var(--surface-hover)'
                        : 'linear-gradient(180deg,#3a2a1a,#241a10)',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    fontSize: 40,
                    color: account === undefined ? 'var(--text-tertiary)' : '#e0a76a',
                    overflow: 'hidden',
                    animation: session.launching ? 'pulse 1.4s infinite' : undefined
                  }}
                >
                  {avatarUrl !== null && !avatarFailed ? (
                    <img
                      src={avatarUrl}
                      alt={account?.name ?? ''}
                      width={88}
                      height={88}
                      style={{ objectFit: 'cover' }}
                      onError={() => setAvatarFailed(true)}
                    />
                  ) : (
                    account?.name[0]?.toUpperCase() ?? '☺'
                  )}
                </div>
                <div style={{ fontSize: 15, fontWeight: 600 }}>
                  {account?.name ?? '未选择账户'}
                </div>
                {account === undefined && (
                  <div className="t-cap">去「账户」页添加{MODE_LABEL[mode]}账户</div>
                )}
                {account !== undefined && modeAccounts.length > 1 && (
                  <div
                    style={{
                      display: 'flex',
                      gap: 6,
                      flexWrap: 'wrap',
                      justifyContent: 'center'
                    }}
                  >
                    {modeAccounts.map((a) => (
                      <button
                        key={a.uuid}
                        className="btn btn-subtle btn-sm"
                        onClick={() =>
                          void request('accounts.setCurrent', { uuid: a.uuid })
                        }
                      >
                        {a.name}
                      </button>
                    ))}
                  </div>
                )}
              </div>

              {session.status !== '' && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span
                    className="dot"
                    style={{
                      background: statusColor,
                      animation:
                        session.launching || running ? 'pulse 1.6s infinite' : undefined
                    }}
                  />
                  <span className="t-cap truncate">{session.status}</span>
                </div>
              )}

              {/* 启动按钮：主段 + 下拉段（对齐 Win 端 HomePage 的按钮组） */}
              <div style={{ display: 'flex', gap: 8 }}>
                <button
                  className="btn btn-primary"
                  style={{
                    flex: 1,
                    height: 62,
                    borderRadius: 'var(--r-overlay)',
                    flexDirection: 'column',
                    gap: 1,
                    display: 'flex',
                    fontSize: 16,
                    fontWeight: 700,
                    boxShadow:
                      running || session.launching
                        ? 'none'
                        : '0 4px 22px rgba(255,194,31,0.28)'
                  }}
                  disabled={!canLaunch}
                  onClick={() => {
                    if (selected === undefined || account === undefined) return
                    void session.launch({
                      versionId: selected,
                      playerName: account.name,
                      accountUuid: account.uuid
                    })
                  }}
                >
                  <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    ▶{' '}
                    {session.launching
                      ? '启动中…'
                      : running
                        ? '游戏运行中'
                        : '启动游戏'}
                  </span>
                  <span style={{ fontSize: 11, fontWeight: 400, opacity: 0.72 }}>
                    {selected ?? '未选择版本'}
                  </span>
                </button>
                <button
                  className="btn"
                  disabled={!canLaunch}
                  title="更多启动方式"
                  style={{
                    width: 52,
                    borderRadius: 'var(--r-overlay)',
                    background: 'rgba(255,255,255,0.18)',
                    color: 'var(--text-on-accent)',
                    borderColor: 'transparent'
                  }}
                >
                  ⌄
                </button>
              </div>
            </div>
          </div>
        </div>

        {/* 运行日志不放在首页：Win 端首页没有日志区，且启动时日志卡出现会挤压
            两栏布局（用户看到账户卡被"拉大"）。日志在「运行状态 / 启动器日志」页。 */}

        {error !== '' && (
          <div style={{ marginTop: 12 }}>
            <div className="info info-danger">
              <span aria-hidden>✕</span>
              <div className="info-body">{error}</div>
            </div>
          </div>
        )}
      {menu.node}
      {host}
    </div>
  )
}
