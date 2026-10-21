/// 共用 UI 原子（纯 CSS 类，无框架依赖）。
/// 视觉规范见 winui.css 与 DESIGN.md（WinUI 3 令牌）。
import React, { useCallback, useEffect, useRef, useState } from 'react'
import { IconCheck, IconClose, IconInfo, IconWarning, IconChevronDown } from './icons'

// ── 页面骨架 ────────────────────────────────────────────────────────────────

export function PageShell(props: {
  title: string
  subtitle?: string
  actions?: React.ReactNode
  children: React.ReactNode
}): JSX.Element {
  return (
    <div className="page">
      <div className="page-head">
        <div style={{ flex: 1, minWidth: 0 }}>
          <h1 className="page-title">{props.title}</h1>
          {props.subtitle !== undefined && <div className="page-sub">{props.subtitle}</div>}
        </div>
        {props.actions}
      </div>
      <div className="page-body">{props.children}</div>
    </div>
  )
}

// ── 状态 ───────────────────────────────────────────────────────────────────

export function EmptyState(props: {
  title: string
  hint?: string
  action?: React.ReactNode
}): JSX.Element {
  return (
    <div className="empty">
      <div className="empty-title">{props.title}</div>
      {props.hint !== undefined && <div className="empty-hint">{props.hint}</div>}
      {props.action}
    </div>
  )
}

/**
 * 骨架屏：一次性渲染全部行。
 * 早期版本用 setInterval 逐行渐显，切换页面时会连续触发重排（用户看到的「抽搐」）；
 * 这里改成整块淡入，行高与真实行对齐，避免加载完成时的跳变。
 */
export function SkeletonList(props: {
  rows?: number
  avatar?: boolean
  /** 行高变体：普通列表 44，带缩略图的列表（存档）72 */
  tall?: boolean
}): JSX.Element {
  const rows = props.rows ?? 5
  return (
    <div style={{ animation: 'fadeIn 180ms cubic-bezier(0.33, 0, 0.67, 1) both' }}>
      {Array.from({ length: rows }).map((_, index) => (
        <div
          key={index}
          className="skel-row"
          style={{ minHeight: props.tall === true ? 72 : 44 }}
        >
          {props.avatar === true && (
            <div
              className="skel-thumb"
              style={
                props.tall === true
                  ? { width: 64, height: 64, borderRadius: 8 }
                  : undefined
              }
            />
          )}
          <div className="skel-text" style={{ maxWidth: `${60 - index * 6}%` }} />
        </div>
      ))}
    </div>
  )
}

/** 加载卡（部分页面用它占位）。 */
export function LoadingCard(props: { label?: string }): JSX.Element {
  return (
    <div className='card' style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
      <Spinner />
      <span className='t-cap'>{props.label ?? '加载中…'}</span>
    </div>
  )
}

export function Spinner(props: { size?: 'sm' | 'lg' }): JSX.Element {
  return <span className={props.size === 'lg' ? 'spinner spinner-lg' : 'spinner'} />
}

// ── 提示条（InfoBar 等价） ─────────────────────────────────────────────────

export type ToastKind = 'success' | 'error' | 'info' | 'warning'

export interface ToastItem {
  id: number
  kind: ToastKind
  text: string
}

const TOAST_ICON: Record<ToastKind, JSX.Element> = {
  success: <IconCheck size={14} />,
  error: <IconClose size={14} />,
  info: <IconInfo size={14} />,
  warning: <IconWarning size={14} />
}

export function InfoBar(props: {
  kind?: ToastKind
  title?: string
  onDismiss?: () => void
  children: React.ReactNode
}): JSX.Element {
  return (
    <div className={`info info-${props.kind ?? 'info'}`}>
      <span aria-hidden>{TOAST_ICON[props.kind ?? 'info']}</span>
      <div className="info-body">
        {props.title !== undefined && <div className="info-title">{props.title}</div>}
        {props.children}
      </div>
      {props.onDismiss !== undefined && (
        <button className="btn btn-subtle btn-sm" onClick={props.onDismiss}>
          <IconClose size={13} />
        </button>
      )}
    </div>
  )
}

/** 全局 Toast：页面内 `push('success', '文案')`，根部渲染 `{host}`。 */
export function useToasts(): {
  push: (kind: ToastKind, text: string) => void
  host: JSX.Element
} {
  const [items, setItems] = useState<ToastItem[]>([])
  const seq = useRef(1)
  // push 必须**引用稳定**：各页面把它放进 useCallback 依赖，若每次渲染都新建
  // 函数，load() 就会不断重建 → useEffect 反复触发 → 无限请求桥接
  // （表现为页面抽搐、桥接被灌满后全部加载失败）。这里用空依赖 useCallback。
  const push = useCallback((kind: ToastKind, text: string): void => {
    const id = seq.current++
    setItems((prev) => [...prev, { id, kind, text }])
    setTimeout(() => setItems((prev) => prev.filter((t) => t.id !== id)), 4200)
  }, [])
  const host = (
    <div className="toast-host">
      {items.map((item) => (
        <div key={item.id} className="card toast info info-{item.kind}">
          <span aria-hidden>{TOAST_ICON[item.kind]}</span>
          <div className="info-body">{item.text}</div>
          <button
            className="btn btn-subtle btn-sm"
            onClick={() => setItems((prev) => prev.filter((t) => t.id !== item.id))}
          >
            <IconClose size={13} />
          </button>
        </div>
      ))}
    </div>
  )
  return { push, host }
}

// ── 控件 ───────────────────────────────────────────────────────────────────

export function Toggle(props: {
  checked: boolean
  onChange: (v: boolean) => void
  disabled?: boolean
  label?: string
}): JSX.Element {
  return (
    <button
      className="toggle"
      role="switch"
      aria-checked={props.checked}
      aria-label={props.label}
      disabled={props.disabled}
      onClick={() => props.onChange(!props.checked)}
    />
  )
}

export function Checkbox(props: {
  checked: boolean
  onChange: (v: boolean) => void
  disabled?: boolean
  label?: string
}): JSX.Element {
  return (
    <button
      className="check"
      role="checkbox"
      aria-checked={props.checked}
      aria-label={props.label}
      disabled={props.disabled}
      onClick={() => props.onChange(!props.checked)}
    >
      {props.checked ? <IconCheck size={13} /> : null}
    </button>
  )
}

/** 分段控件（滑动指示器）。 */
export function Segmented<T extends string>(props: {
  value: T
  options: Array<{ value: T; label: string }>
  onChange: (v: T) => void
  disabled?: boolean
}): JSX.Element {
  const n = props.options.length
  const index = Math.max(
    0,
    props.options.findIndex((o) => o.value === props.value)
  )
  // 指示器宽 = 1/n 格 - 2px 内边距；位移按"格宽"推进（CSS 用 calc 百分比 + px）
  const slot = 100 / n
  return (
    <div className="seg" style={{ gridTemplateColumns: `repeat(${n}, 1fr)` }}>
      <div
        className="seg-ind"
        style={{
          width: `calc(${slot}% - 2px)`,
          // translateX 的百分比相对「指示器自身宽度」（恰好一格），
          // 所以每前进一格 = +100%，再补格子之间的 2px 间距
          transform: `translateX(calc(2px + ${index * 100}% + ${index * 2}px))`
        }}
      />
      {props.options.map((o) => (
        <button
          key={o.value}
          className={`seg-item${o.value === props.value ? ' seg-active' : ''}`}
          disabled={props.disabled}
          onClick={() => props.onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function ProgressBar(props: {
  value?: number
  indeterminate?: boolean
}): JSX.Element {
  return (
    <div className={`progress${props.indeterminate === true ? ' progress-indeterminate' : ''}`}>
      {props.value !== undefined && (
        <div className="progress-fill" style={{ width: `${Math.max(0, Math.min(100, props.value))}%` }} />
      )}
    </div>
  )
}

// ── 右键菜单（MenuFlyout 等价） ────────────────────────────────────────────

export interface MenuEntry {
  label: string
  onSelect: () => void
  danger?: boolean
  disabled?: boolean
}

/**
 * 右键菜单 hook（Win 端 MenuFlyout 等价）。
 * 用法：const menu = useContextMenu(items)；<div {...menu.bind()}>…</div>
 * 菜单在视口边缘自动翻转，避免被窗口裁掉（Win 端 Flyout 的 PopupPlacement 行为）。
 */
export function useContextMenu(items: MenuEntry[]): {
  bind: () => {
    onContextMenu: (e: React.MouseEvent) => void
    style: React.CSSProperties
  }
  close: () => void
  node: JSX.Element | null
} {
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const ref = useRef<HTMLDivElement>(null)
  const itemsRef = useRef(items)
  itemsRef.current = items

  useEffect(() => {
    if (pos === null) return
    const onDown = (e: MouseEvent): void => {
      if (ref.current !== null && !ref.current.contains(e.target as Node)) {
        setPos(null)
      }
    }
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') setPos(null)
    }
    const onResize = (): void => setPos(null)
    window.addEventListener('mousedown', onDown)
    window.addEventListener('keydown', onKey)
    window.addEventListener('resize', onResize)
    return () => {
      window.removeEventListener('mousedown', onDown)
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('resize', onResize)
    }
  }, [pos])

  const width = 200
  const height = items.length * 32 + 12
  const flipX = pos !== null && pos.x > window.innerWidth - width - 12
  const flipY = pos !== null && pos.y > window.innerHeight - height - 12

  const node =
    pos === null ? null : (
      <div
        ref={ref}
        className="flyout"
        style={{
          position: 'fixed',
          left: flipX ? pos.x - width : pos.x,
          top: flipY ? pos.y - height : pos.y,
          width,
          zIndex: 800,
          display: 'flex',
          flexDirection: 'column',
          gap: 2,
          padding: 6
        }}
      >
        {items.map((item) => (
          <button
            key={item.label}
            className={`nav-item${item.danger === true ? ' btn-danger' : ''}`}
            style={{ height: 32, width: '100%', fontSize: 13 }}
            disabled={item.disabled === true}
            onClick={() => {
              setPos(null)
              item.onSelect()
            }}
          >
            {item.label}
          </button>
        ))}
      </div>
    )

  return {
    bind: () => ({
      onContextMenu: (e: React.MouseEvent): void => {
        e.preventDefault()
        setPos({ x: e.clientX, y: e.clientY })
      },
      style: {}
    }),
    close: (): void => setPos(null),
    node
  }
}

// ── 浮层 ───────────────────────────────────────────────────────────────────

/** 对话框（ContentDialog 等价）：遮罩 + 居中卡片，点遮罩关闭。 */
export function Modal(props: {
  open: boolean
  title: string
  onClose: () => void
  children: React.ReactNode
  footer?: React.ReactNode
  width?: number
}): JSX.Element | null {
  if (!props.open) return null
  return (
    <div
      className="scrim"
      onClick={(e) => {
        if (e.target === e.currentTarget) props.onClose()
      }}
    >
      <div className="dialog" style={{ width: props.width ?? 480 }}>
        <div className="dialog-title">{props.title}</div>
        <div className="dialog-body">{props.children}</div>
        {props.footer !== undefined && <div className="dialog-actions">{props.footer}</div>}
      </div>
    </div>
  )
}

// ── 错误边界 ───────────────────────────────────────────────────────────────

/** 页面级错误边界：单页崩溃不白屏。 */
export class ErrorBoundary extends React.Component<
  { children: React.ReactNode; label: string },
  { error?: Error }
> {
  constructor(props: { children: React.ReactNode; label: string }) {
    super(props)
    this.state = {}
  }

  static getDerivedStateFromError(error: Error): { error: Error } {
    return { error }
  }

  render(): React.ReactNode {
    if (this.state.error !== undefined) {
      return (
        <div className="empty" style={{ paddingTop: 60 }}>
          <div className="empty-title">{this.props.label} 页面出错了</div>
          <div className="empty-hint">{String(this.state.error)}</div>
          <button
            className="btn btn-primary"
            onClick={() => this.setState({ error: undefined })}
          >
            重试
          </button>
        </div>
      )
    }
    return this.props.children
  }
}

// ── 自绘下拉 ───────────────────────────────────────────────────────────────

/**
 * 下拉选择（替代原生 <select>）。
 * 原生 select 的弹出层由系统绘制：深色主题下会出现系统蓝高亮、白底列表，
 * 与本套视觉完全不搭（用户反馈的主要问题）。这里用 `.flyout` + `.nav-item`
 * 画一个同风格的列表，并在空间不足时自动向上弹。
 */
export function Select<T extends string>(props: {
  value: T
  options: Array<{ value: T; label: string }>
  onChange: (v: T) => void
  disabled?: boolean
  width?: number
  /** 无选中项时的占位文案。 */
  placeholder?: string
}): JSX.Element {
  const [open, setOpen] = useState(false)
  const [up, setUp] = useState(false)
  const rootRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent): void => {
      if (rootRef.current !== null && !rootRef.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') setOpen(false)
    }
    window.addEventListener('mousedown', onDown)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('mousedown', onDown)
      window.removeEventListener('keydown', onKey)
    }
  }, [open])

  const current = props.options.find((o) => o.value === props.value)

  const toggle = (): void => {
    if (props.disabled === true) return
    // 下方空间不足就向上弹（下拉比视口底部更近时）
    const rect = rootRef.current?.getBoundingClientRect()
    if (rect !== undefined) {
      const estimated = Math.min(320, props.options.length * 34 + 12)
      setUp(window.innerHeight - rect.bottom < estimated + 16)
    }
    setOpen((v) => !v)
  }

  return (
    <div ref={rootRef} style={{ position: 'relative', width: props.width ?? 220 }}>
      <button
        className="input"
        style={{
          width: '100%',
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          cursor: props.disabled === true ? 'default' : 'pointer',
          opacity: props.disabled === true ? 0.5 : 1,
          textAlign: 'left'
        }}
        disabled={props.disabled}
        title={current?.label ?? props.placeholder ?? ''}
        onClick={toggle}
      >
        <span className="truncate" style={{ flex: 1 }}>
          {current?.label ?? props.placeholder ?? ''}
        </span>
        <IconChevronDown size={14} style={{ opacity: 0.6 }} />
      </button>

      {open && (
        <div
          className="flyout"
          style={{
            position: 'absolute',
            left: 0,
            right: 0,
            top: up ? undefined : 'calc(100% + 6px)',
            bottom: up ? 'calc(100% + 6px)' : undefined,
            maxHeight: 320,
            overflowY: 'auto',
            zIndex: 700,
            padding: 6
          }}
        >
          {props.options.map((o) => (
            <button
              key={o.value}
              className={`nav-item${o.value === props.value ? ' nav-active' : ''}`}
              style={{ height: 32, width: '100%', fontSize: 13 }}
              onClick={() => {
                props.onChange(o.value)
                setOpen(false)
              }}
            >
              <span className="truncate">{o.label}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
