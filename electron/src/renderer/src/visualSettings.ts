/// 视觉相关设置的应用器：动效档位 / 背景图 / 模糊与不透明度。
///
/// 这些设置存在 LauncherSettings 里（与 Win 端共用），但作用于渲染层，
/// 所以拉取后直接写到 <html> 的属性与 CSS 变量上：
///   data-motion = off      → 压平全部动画（对应动效档位「极少」）
///   --bg-image / -opacity / -blur → .mica 的背景层
import { request } from './bridge'
import type { SettingsSnapshot } from './bridge'

export interface VisualSettings {
  motionLevel: string
  backgroundMode: string
  backgroundImagePath: string
  backgroundImageOpacity: number
  backgroundBlurRadius: number
  backgroundFollowTheme: boolean
}

/** 把设置写到 DOM（幂等，可反复调用）。 */
export function applyVisualSettings(s: VisualSettings): void {
  const root = document.documentElement

  // 动效档位：极少 = 关闭动画；其余保持默认（后续档位可继续细化）
  const level = s.motionLevel === '' ? 'balanced' : s.motionLevel
  root.setAttribute('data-motion', level === 'minimal' ? 'off' : 'on')

  // 背景：none / builtin / image
  const mode = s.backgroundMode === '' ? 'none' : s.backgroundMode
  root.setAttribute('data-bg', mode)
  if (mode === 'image' && s.backgroundImagePath !== '') {
    // 本地路径在渲染层直接当 file:// 用会被 CSP 拦；这里转成 file URL 交给 CSS，
    // 仅在本机桌面应用里使用（CSP 的 img-src 已放开 file: 由主进程注入背景时处理）。
    const normalized = s.backgroundImagePath.replace(/\\/g, '/')
    const url = normalized.startsWith('/') ? `file://${normalized}` : `file:///${normalized}`
    root.style.setProperty('--bg-image', `url("${url}")`)
  } else {
    root.style.removeProperty('--bg-image')
  }
  root.style.setProperty('--bg-opacity', `${Math.max(0, Math.min(1, s.backgroundImageOpacity))}`)
  root.style.setProperty('--bg-blur', `${Math.max(0, Math.min(64, s.backgroundBlurRadius))}px`)
  root.setAttribute('data-bg-follow-theme', s.backgroundFollowTheme ? '1' : '0')
}

/** 从桥接读一次并应用（启动时调用）。 */
export async function loadAndApplyVisualSettings(): Promise<void> {
  try {
    const snap = await request<SettingsSnapshot>('settings.get')
    applyVisualSettings(snap)
  } catch {
    /* 桥接不可用（浏览器调试）时保持默认 */
  }
}
