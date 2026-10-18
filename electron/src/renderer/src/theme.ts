/// 设计令牌 —— 严格对齐 Win 端（WinUI 3）的设计语言。
///
/// 来源：`App.xaml` / `MainWindow.xaml` / `Views/Controls/*` 与
/// `MotionFx.Pace = 1.5` 的实际取值（详见 `DESIGN.md`）。
/// 注意：XAML 里定义的 `ControlCornerRadius=12 / CardCornerRadius=20` 在真实 UI 中
/// 引用为 0，实际生效的是硬编码的 8/16/12 —— 这里照抄"实际值"。
import { createDarkTheme } from '@fluentui/react-components'
import type { BrandVariants, Theme } from '@fluentui/react-components'

export const brandYellow = '#FFC21F'
export const brandYellowHover = '#FFD35C'

/** 圆角：控件 8 / 卡片与列表行 8 / 浮层 16。 */
export const radius = {
  control: 8,
  card: 8,
  row: 8,
  overlay: 16
}

/**
 * 动效：全局节奏系数 Pace = 1.5（只放大时长，不改曲线形状）。
 * 曲线取自 MotionFx：进场 / 退场 / Control / Fade 四条。
 */
export const motion = {
  pace: 1.5,
  enter: 375,
  exit: 210,
  hoverIn: 300,
  hoverOut: 450,
  press: 100,
  stagger: 39,
  easeEnter: 'cubic-bezier(0.1, 0.9, 0.2, 1)',
  easeExit: 'cubic-bezier(0.7, 0, 1, 0.5)',
  easeControl: 'cubic-bezier(0.16, 1, 0.3, 1)',
  easeFade: 'cubic-bezier(0.33, 0, 0.67, 1)',
  // 兼容旧别名
  fast: 100,
  normal: 375,
  slow: 375,
  ease: 'cubic-bezier(0.33, 0, 0.67, 1)',
  easeOut: 'cubic-bezier(0.1, 0.9, 0.2, 1)'
}

/** 阴影：Win 端刻意不用 box-shadow，改用"同心半透明矩形 + 跟手光斑"。 */
export const elevation = {
  rest: '0 1px 2px rgba(0,0,0,0.12)',
  hover: '0 4px 12px rgba(0,0,0,0.28)'
}

/** 品牌色板（对应 Win 端 BrandVariants 10–160）。 */
export const starcloudBrand: BrandVariants = {
  10: '#0d0d0d',
  20: '#111111',
  30: '#161616',
  40: '#1b1b1b',
  50: '#202020',
  60: '#242424',
  70: '#2a2a2a',
  80: '#323232',
  90: '#3b3b3b',
  100: '#454545',
  110: '#585858',
  120: '#6b6b6b',
  130: '#8a8a8a',
  140: '#b4b4b4',
  150: '#e5e5e5',
  160: '#ffffff'
}

export const starcloudTheme: Theme = createDarkTheme(starcloudBrand)
