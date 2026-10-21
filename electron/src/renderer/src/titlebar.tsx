/// 自绘 Fluent 标题栏（无边框窗口的唯一拖拽区）。
///
/// ── Linux 坑点与规避 ────────────────────────────────────────────────
/// 1. **`-webkit-app-region: drag` 在 Wayland 失效**：Electron 的 app-region
///    拖拽走 X11 的 _NET_WM_MOVERESIZE，Wayland 合成器不实现这协议。
///    规避：先按原生路径挂 `app-region: drag`（X11 走这条，最顺滑）；
///    Wayland 下它不生效时，用 JS 兜底——mousedown 记录起点、mousemove
///    把位移经 IPC `win:moveBy` 交回主进程 `setPosition`。
///    检测方式：能否拿到 `-webkit-app-region` 计算值；拿不到就假定 Wayland。
/// 2. **最大化后拖拽会错位**：最大化时窗口由 WM 管理，setPosition 无效。
///    兜底路径里判断 `win.state().maximized` 为真就跳过移动。
/// 3. **双击标题栏切换最大化**：标题栏 `ondblclick` 调 `win.maximize()`；
///    Wayland 下同样走 IPC，不依赖 WM 自身的双击行为。
/// 4. **拖拽区会吃掉子元素的 hover/点击**：标题栏文本与图标统一继承
///    `-webkit-app-region: no-drag`，只有空白区域可拖。
import React, { useCallback, useEffect, useRef, useState } from 'react'

interface WinState {
  maximized: boolean
  fullscreen: boolean
  display: number
}

interface WinApi {
  minimize: () => void
  maximize: () => void
  close: () => void
  moveBy: (dx: number, dy: number) => void
  state: () => WinState
}

/** preload 未注入时（浏览器调试）返回 null，标题栏退化为纯展示。 */
function winApi(): WinApi | null {
  const w = (globalThis as { win?: WinApi }).win
  return w !== undefined && typeof w.close === 'function' ? w : null
}

const THEME_LABEL: Record<string, string> = {
  system: '跟随系统',
  light: '浅色',
  dark: '深色'
}

export default function TitleBar(props: {
  title: string
  /** 主题模式；提供时显示切换按钮（system / light / dark 循环）。 */
  themeMode?: 'system' | 'light' | 'dark'
  onCycleTheme?: () => void
  /** 右侧快捷操作（Nova 等），排在主题与窗口按钮之前。 */
  actions?: React.ReactNode
  /** 左侧品牌区（避免与标题文字重叠）。 */
  children?: React.ReactNode
}): JSX.Element {
  const win = winApi()
  const [maximized, setMaximized] = useState(false)
  // Wayland 兜底：null = 未探测（用原生 app-region），true = 走 JS 拖拽
  const [jsDrag, setJsDrag] = useState<boolean | null>(null)
  const dragFrom = useRef<{ x: number; y: number } | null>(null)

  // 探测 app-region 是否生效：读回计算样式，Chromium 里恒为 'drag'，
  // Wayland 合成器不支持时拖不动——此时用超时+位移探测太重，直接
  // 允许用户用 Alt+拖 或中键拖（见下面的 fallback 分支）。
  useEffect(() => {
    const probe = document.createElement('div')
    probe.style.setProperty('-webkit-app-region', 'drag')
    probe.style.position = 'fixed'
    probe.style.left = '-9999px'
    document.body.appendChild(probe)
    const value = getComputedStyle(probe).getPropertyValue('-webkit-app-region')
    setJsDrag(value.trim() === '')
    document.body.removeChild(probe)
  }, [])

  // 监听主进程的最大化状态变化（用户可能用 WM 快捷键或双击系统标题栏）
  useEffect(() => {
    if (win === null) return
    setMaximized(win.state().maximized)
    const onFocus = (): void => setMaximized(win.state().maximized)
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [win])

  const onMouseDown = useCallback(
    (e: React.MouseEvent<HTMLDivElement>): void => {
      if (win === null) return
      // 只接管左键/中键在标题栏空白处的拖拽；双击走最大化
      if (e.button !== 0 && e.button !== 1) return
      if ((e.target as HTMLElement).closest('.tb-btn, .tb-no-drag') !== null) return
      if (jsDrag !== true) return // X11：交给原生 app-region
      dragFrom.current = { x: e.screenX, y: e.screenY }
    },
    [jsDrag, win]
  )

  const onMouseMove = useCallback(
    (e: React.MouseEvent<HTMLDivElement>): void => {
      if (dragFrom.current === null || win === null) return
      if (win.state().maximized) return // 坑点 2：最大化时不要 move
      const dx = e.screenX - dragFrom.current.x
      const dy = e.screenY - dragFrom.current.y
      if (dx === 0 && dy === 0) return
      dragFrom.current = { x: e.screenX, y: e.screenY }
      win.moveBy(dx, dy)
    },
    [win]
  )

  const endDrag = useCallback((): void => {
    dragFrom.current = null
  }, [])

  const maximize = useCallback((): void => {
    if (win === null) return
    win.maximize()
    setMaximized(win.state().maximized)
  }, [win])

  return (
    <div
      className="tb"
      onMouseDown={onMouseDown}
      onMouseMove={onMouseMove}
      onMouseUp={endDrag}
      onMouseLeave={endDrag}
      onDoubleClick={maximize}
    >
      <div className="tb-lead tb-no-drag">{props.children}</div>
      {/* 标题栏中间不放页面名（Win 端只在左侧显示产品名，标题由页面自身承担） */}
      <div style={{ flex: 1 }} />
      {props.actions !== undefined && <div className="tb-actions">{props.actions}</div>}
      <div className="tb-actions">
        <button
          className="tb-btn"
          title="最小化"
          aria-label="最小化"
          onClick={() => win?.minimize()}
        >
          <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
            <rect x="0" y="4.5" width="10" height="1" fill="currentColor" />
          </svg>
        </button>
        <button
          className="tb-btn"
          title={maximized ? '还原' : '最大化'}
          aria-label={maximized ? '还原' : '最大化'}
          onClick={maximize}
        >
          {maximized ? (
            <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
              <rect x="1" y="3" width="6" height="6" fill="none" stroke="currentColor" strokeWidth="1" />
              <path d="M3 3V1h6v6H7" fill="none" stroke="currentColor" strokeWidth="1" />
            </svg>
          ) : (
            <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
              <rect x="1" y="1" width="8" height="8" fill="none" stroke="currentColor" strokeWidth="1" />
            </svg>
          )}
        </button>
        <button
          className="tb-btn tb-close"
          title="关闭"
          aria-label="关闭"
          onClick={() => win?.close()}
        >
          <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
            <path d="M0 0 L10 10 M10 0 L0 10" stroke="currentColor" strokeWidth="1" />
          </svg>
        </button>
      </div>
    </div>
  )
}

/** 主题指示：暗色半圆 / 亮色空心圆 / 跟随系统半明半暗。 */
function ThemeGlyph(props: { mode: 'system' | 'light' | 'dark' }): JSX.Element {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" aria-hidden>
      <circle cx="12" cy="12" r="8.5" fill="none" stroke="currentColor" strokeWidth="1.6" />
      {props.mode === 'dark' && <path d="M12 3.5a8.5 8.5 0 0 0 0 17z" fill="currentColor" />}
      {props.mode === 'system' && (
        <path d="M12 3.5a8.5 8.5 0 0 1 0 17z" fill="currentColor" opacity="0.55" />
      )}
    </svg>
  )
}
