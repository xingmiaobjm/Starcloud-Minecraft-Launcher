/// 共用 UI 原子（纯 CSS 类，无框架依赖）。
/// 视觉规范见 winui.css 与 DESIGN.md（WinUI 3 令牌）。
import React, { useEffect, useRef, useState } from 'react'

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

/** 骨架屏：与真实行同尺寸，加载时布局不跳。 */
export function SkeletonList(props: { rows?: number; avatar?: boolean }): JSX.Element {
  const rows = props.rows ?? 5
  const [visible, setVisible] = useState(0)
  useEffect(() => {
    let i = 0
    const timer = setInterval(() => {
      i += 1
      setVisible(Math.min(i, rows))
      if (i >= rows) clearInterval(timer)
    }, 60)
    return () => clearInterval(timer)
  }, [rows])
  return (
    <div>
      {Array.from({ length: visible }).map((_, index) => (
        <div key={index} className="skel-row" style={{ opacity: 1 - index * 0.12 }}>
          {props.avatar === true && <div className="skel-thumb" />}
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

const TOAST_ICON: Record<ToastKind, string> = {
  success: '✓',
  error: '✕',
  info: 'ⓘ',
  warning: '⚠'
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
          ✕
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
  const push = (kind: ToastKind, text: string): void => {
    const id = seq.current++
    setItems((prev) => [...prev, { id, kind, text }])
    setTimeout(() => setItems((prev) => prev.filter((t) => t.id !== id)), 4200)
  }
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
            ✕
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
      {props.checked ? '✓' : ''}
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
          transform: `translateX(calc(2px + ${index} * ${slot}%))`
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
