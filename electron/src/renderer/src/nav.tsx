/// 侧边导航（对齐 Win 端 NavigationView LeftCompact）。
///
/// ── 图标来源（与 Win 端 MainWindow.xaml 逐一对应） ────────────────────
/// | 页面      | 图标来源                     | 字形 / 文件                        |
/// |-----------|------------------------------|------------------------------------|
/// | 首页      | 鸿蒙图标库                   | U+EC77                            |
/// | 版本      | 鸿蒙图标库                   | U+EA94                            |
/// | 下载      | 鸿蒙图标库                   | U+E90C                            |
/// | 存档      | 鸿蒙图标库                   | U+EA90                            |
/// | 联机      | 鸿蒙图标库                   | U+EB91                            |
/// | 实验室    | **自绘 SVG**                  | Assets/Brand/lab-nav.svg           |
/// | 投影管理  | **自绘 SVG**                  | Assets/Brand/projection-nav.svg    |
/// | 运行状态  | 鸿蒙图标库                   | U+EC3B（带运行中绿点）            |
/// | 账户      | 鸿蒙图标库                   | U+EB8F                            |
/// | 插件      | 鸿蒙图标库                   | U+EABC                            |
/// | Nova      | **品牌标记图片**（多色画不出） | Assets/Brand/starcloud-mark-small  |
/// | 启动器日志| 鸿蒙图标库                   | U+EC64                            |
/// | 设置      | 鸿蒙图标库                   | U+EC04                            |
///
/// 用字形字体而不是 Unicode 符号：符号字形来自系统 fallback 字体，
/// 与 Win 端的鸿蒙图标库不是同一套画风，混用会显得拼凑。
import React, { useState } from 'react'

import labIcon from './assets/brand/lab-nav.svg'
import labIconDark from './assets/brand/lab-nav-dark.svg'
import projectionIcon from './assets/brand/projection-nav.svg'
import projectionIconDark from './assets/brand/projection-nav-dark.svg'
import novaMark from './assets/brand/starcloud-mark-small.svg'

export type PageKey =
  | 'home'
  | 'versions'
  | 'download'
  | 'saves'
  | 'multiplayer'
  | 'lab'
  | 'projections'
  | 'running'
  | 'accounts'
  | 'plugins'
  | 'nova'
  | 'logs'
  | 'settings'

type IconSpec =
  /** 鸿蒙图标字形码位 */
  | { kind: 'glyph'; code: string }
  /** 自绘 SVG（实验室 / 投影管理），亮暗各一份 */
  | { kind: 'svg'; light: string; dark: string }
  /** 品牌标记（Nova，多色） */
  | { kind: 'brand'; src: string; size?: number }

interface NavEntry {
  key: PageKey
  label: string
  icon: IconSpec
  group: 'game' | 'extra' | 'footer'
  order: number
}

const NAV: NavEntry[] = [
  { key: 'home', label: '首页', group: 'game', order: 0, icon: { kind: 'glyph', code: String.fromCharCode(0xEC77) } },
  { key: 'versions', label: '版本', group: 'game', order: 1, icon: { kind: 'glyph', code: String.fromCharCode(0xEA94) } },
  { key: 'download', label: '下载', group: 'game', order: 2, icon: { kind: 'glyph', code: String.fromCharCode(0xE90C) } },
  { key: 'saves', label: '存档', group: 'game', order: 3, icon: { kind: 'glyph', code: String.fromCharCode(0xEA90) } },
  { key: 'multiplayer', label: '联机', group: 'extra', order: 0, icon: { kind: 'glyph', code: String.fromCharCode(0xEB91) } },
  { key: 'lab', label: '实验室', group: 'extra', order: 1, icon: { kind: 'svg', light: labIcon, dark: labIconDark } },
  { key: 'projections', label: '投影管理', group: 'extra', order: 2, icon: { kind: 'svg', light: projectionIcon, dark: projectionIconDark } },
  { key: 'running', label: '运行状态', group: 'extra', order: 3, icon: { kind: 'glyph', code: String.fromCharCode(0xEC3B) } },
  { key: 'accounts', label: '账户', group: 'extra', order: 4, icon: { kind: 'glyph', code: String.fromCharCode(0xEB8F) } },
  { key: 'plugins', label: '插件', group: 'extra', order: 5, icon: { kind: 'glyph', code: String.fromCharCode(0xEABC) } },
  { key: 'nova', label: 'Nova', group: 'extra', order: 6, icon: { kind: 'brand', src: novaMark, size: 16 } },
  { key: 'logs', label: '启动器日志', group: 'footer', order: 0, icon: { kind: 'glyph', code: String.fromCharCode(0xEC64) } },
  { key: 'settings', label: '设置', group: 'footer', order: 1, icon: { kind: 'glyph', code: String.fromCharCode(0xEC04) } }
]

export const PAGE_LABEL: Record<PageKey, string> = NAV.reduce(
  (acc, n) => ({ ...acc, [n.key]: n.label }),
  {} as Record<PageKey, string>
)

export default function Nav(props: {
  page: PageKey
  dark: boolean
  /** 运行中（给「运行状态」加绿点，Win 端 RunningDot）。 */
  running: boolean
  onNavigate: (key: PageKey) => void
}): JSX.Element {
  const [open, setOpen] = useState(false)

  const icon = (spec: IconSpec): JSX.Element => {
    if (spec.kind === 'brand') {
      return (
        <span className="nav-icon nav-nova">
          <img src={spec.src} alt="" width={spec.size ?? 16} height={spec.size ?? 16} />
        </span>
      )
    }
    if (spec.kind === 'svg') {
      return (
        <span className="nav-icon">
          <img src={props.dark ? spec.dark : spec.light} alt="" width={18} height={18} />
        </span>
      )
    }
    return <span className="nav-icon">{spec.code}</span>
  }

  const item = (entry: NavEntry): JSX.Element => {
    const active = props.page === entry.key
    return (
      <button
        key={entry.key}
        className={`nav-item${active ? ' nav-active' : ''}`}
        data-key={entry.key}
        data-label={entry.label}
        title={entry.label}
        onClick={() => props.onNavigate(entry.key)}
      >
        {icon(entry.icon)}
        <span className="nav-label">{entry.label}</span>
        {entry.key === 'running' && props.running && <span className="nav-run-dot" />}
      </button>
    )
  }

  const game = NAV.filter((n) => n.group === 'game').sort((a, b) => a.order - b.order)
  const extra = NAV.filter((n) => n.group === 'extra').sort((a, b) => a.order - b.order)
  const footer = NAV.filter((n) => n.group === 'footer').sort((a, b) => a.order - b.order)

  return (
    <nav className={`nav acrylic${open ? ' nav-open' : ''}`} data-theme-aware>
      {/* NavigationView 的 hamburger：展开 / 收起（Win 端 OpenPaneLength=232） */}
      <button
        className="nav-mode"
        title={open ? '收起导航' : '展开导航'}
        aria-label={open ? '收起导航' : '展开导航'}
        onClick={() => setOpen((v) => !v)}
      >
        <span className="nav-icon" style={{ fontFamily: 'var(--font-icon)' }}>
          {open ? '' : ''}
        </span>
        {open && <span className="nav-label">收起</span>}
      </button>

      <div className="nav-group">{game.map(item)}</div>
      <div className="nav-sep" />
      <div className="nav-group">{extra.map(item)}</div>
      <div className="nav-spacer" />
      <div className="nav-group">{footer.map(item)}</div>
    </nav>
  )
}
