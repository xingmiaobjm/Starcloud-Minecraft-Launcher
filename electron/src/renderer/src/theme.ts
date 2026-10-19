/// 主题：跟随系统（nativeTheme）+ 手动覆盖。
///
/// ── Linux 平台说明与坑点 ────────────────────────────────────────────────
/// 1. **`nativeTheme` 走 GTK portal**；WSLg / 精简桌面环境常常不上报暗色偏好，
///    表现为"系统明明是暗的，nativeTheme 却说亮"。所以默认 `system`，
///    同时给用户手动入口（标题栏按钮循环 浅色/深色/跟随系统）。
/// 2. **首帧闪烁**：若 `data-theme` 等 React 的 useEffect 才写，浏览器已经用
///    亮色合成过一帧（视觉上"闪一下白"）。对策：`resolveInitialTheme()` 在
///    `ReactDOM.render` **之前** await 出结果并写入 `<html data-theme>`，
///    让第一帧就是正确主题。
/// 3. **窗口底色**（主进程 `backgroundColor`）也要跟着改，否则缩放/最大化时
///    会露出另一套主题的底色——由 `notifyWindowBackground()` 回传主进程。
import { useCallback, useEffect, useState } from 'react'

export type ThemeMode = 'light' | 'dark' | 'system'
export type ResolvedTheme = 'light' | 'dark'

const STORAGE_KEY = 'starcloud.theme'

interface ThemeApi {
  get(): Promise<{ dark: boolean; source: string }>
  onChange(handler: (state: { dark: boolean; source: string }) => void): void
  setWindowBackground?(dark: boolean): void
}

function themeApi(): ThemeApi | undefined {
  return (globalThis as { theme?: ThemeApi }).theme
}

function systemPrefersDark(): boolean {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? true
}

export function readStoredMode(): ThemeMode {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (raw === 'light' || raw === 'dark' || raw === 'system') return raw
  } catch {
    /* localStorage 不可用（隐私模式等）时用默认 */
  }
  return 'system'
}

function applyTheme(resolved: ResolvedTheme): void {
  const root = document.documentElement
  if (root.getAttribute('data-theme') !== resolved) {
    root.setAttribute('data-theme', resolved)
  }
  root.style.colorScheme = resolved
}

/** 把窗口底色同步给主进程（坑点 3）。 */
function notifyWindowBackground(resolved: ResolvedTheme): void {
  themeApi()?.setWindowBackground?.(resolved === 'dark')
}

/**
 * 在 React 挂载前解析并写入主题（坑点 2）。
 * `main.tsx` 里 `await` 它再 render，第一帧就是正确主题。
 */
export async function resolveInitialTheme(): Promise<ResolvedTheme> {
  const mode = readStoredMode()
  let dark: boolean
  if (mode === 'system') {
    const api = themeApi()
    dark =
      api !== undefined
        ? await api
            .get()
            .then((s) => s.dark)
            .catch(() => systemPrefersDark())
        : systemPrefersDark()
  } else {
    dark = mode === 'dark'
  }
  const resolved: ResolvedTheme = dark ? 'dark' : 'light'
  applyTheme(resolved)
  notifyWindowBackground(resolved)
  return resolved
}

export interface ThemeController {
  mode: ThemeMode
  resolved: ResolvedTheme
  setMode: (m: ThemeMode) => void
  /** 循环：跟随系统 → 浅色 → 深色 → 跟随系统。 */
  cycle: () => void
}

/** 主题控制器：mode（用户选择）+ resolved（实际生效）。 */
export function useTheme(initial: ResolvedTheme): ThemeController {
  const [systemDark, setSystemDark] = useState(initial === 'dark')
  const [mode, setModeState] = useState<ThemeMode>(readStoredMode)

  const resolved: ResolvedTheme = mode === 'system' ? (systemDark ? 'dark' : 'light') : mode

  // 系统主题变化（nativeTheme 'updated' 或 matchMedia change）
  useEffect(() => {
    const api = themeApi()
    if (api !== undefined) {
      api.onChange((s) => setSystemDark(s.dark))
      return
    }
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const onChange = (e: MediaQueryListEvent): void => setSystemDark(e.matches)
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [])

  const setMode = useCallback((m: ThemeMode): void => {
    setModeState(m)
    try {
      localStorage.setItem(STORAGE_KEY, m)
    } catch {
      /* 忽略写入失败 */
    }
  }, [])

  const cycle = useCallback((): void => {
    setMode(mode === 'system' ? 'light' : mode === 'light' ? 'dark' : 'system')
  }, [mode, setMode])

  useEffect(() => {
    applyTheme(resolved)
    notifyWindowBackground(resolved)
  }, [resolved])

  return { mode, resolved, setMode, cycle }
}
