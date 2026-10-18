/// 品牌主题：在 Fluent v9 暗色基础上覆盖品牌黄与圆角/动效曲线。
import { createDarkTheme } from '@fluentui/react-components'
import type { BrandVariants, Theme } from '@fluentui/react-components'

export const brandYellow = '#FFC21F'
export const brandYellowHover = '#FFD35C'

/** 统一动效曲线（Fluent 的 decelerate + 标准时长）。 */
export const motion = {
  fast: 120,
  normal: 200,
  slow: 320,
  ease: 'cubic-bezier(0.33, 0, 0.67, 1)' as const,
  easeOut: 'cubic-bezier(0.1, 0.9, 0.2, 1)' as const
}

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
