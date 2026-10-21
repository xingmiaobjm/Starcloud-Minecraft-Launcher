/// 图标集：全部为内联 SVG（24×24，currentColor，1.6 描边，圆头圆角）。
///
/// 为什么不用 Unicode 符号：`↻ ⌃ ⌄ ▶ ✕` 这类字符由系统字体渲染，
/// 不同机器字形粗细/基线不一致（用户反馈"用符号的图标都是问题"），
/// 而且没法统一大小与对齐。SVG 能保证逐像素一致。
import type { CSSProperties } from 'react'

interface IconProps {
  size?: number
  style?: CSSProperties
  className?: string
}

function Svg(props: IconProps & { children: React.ReactNode; fill?: boolean }): JSX.Element {
  const size = props.size ?? 16
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill={props.fill === true ? 'currentColor' : 'none'}
      stroke={props.fill === true ? 'none' : 'currentColor'}
      strokeWidth={props.fill === true ? undefined : 1.6}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={props.className}
      style={{ flexShrink: 0, ...props.style }}
      aria-hidden
    >
      {props.children}
    </svg>
  )
}

/** 刷新（顺时针箭头）。 */
export function IconRefresh(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M20 12a8 8 0 1 1-2.34-5.66" />
      <path d="M20 4v5h-5" />
    </Svg>
  )
}

export function IconChevronUp(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M6 15l6-6 6 6" />
    </Svg>
  )
}

export function IconChevronDown(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M6 9l6 6 6-6" />
    </Svg>
  )
}

export function IconChevronRight(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M9 6l6 6-6 6" />
    </Svg>
  )
}

/** 播放（实心三角，启动按钮用）。 */
export function IconPlay(props: IconProps): JSX.Element {
  return (
    <Svg {...props} fill>
      <path d="M8 5.5v13a1 1 0 0 0 1.53.85l10-6.5a1 1 0 0 0 0-1.7l-10-6.5A1 1 0 0 0 8 5.5z" />
    </Svg>
  )
}

export function IconClose(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M6 6l12 12M18 6L6 18" />
    </Svg>
  )
}

export function IconMore(props: IconProps): JSX.Element {
  return (
    <Svg {...props} fill>
      <circle cx="12" cy="5" r="1.6" />
      <circle cx="12" cy="12" r="1.6" />
      <circle cx="12" cy="19" r="1.6" />
    </Svg>
  )
}

export function IconTrash(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2" />
      <path d="M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" />
      <path d="M10 11v6M14 11v6" />
    </Svg>
  )
}

export function IconFolder(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M3 7.5A1.5 1.5 0 0 1 4.5 6h4l2 2.5h8.5A1.5 1.5 0 0 1 20.5 10v7a1.5 1.5 0 0 1-1.5 1.5H4.5A1.5 1.5 0 0 1 3 17z" />
    </Svg>
  )
}

/** 导入（箭头落进托盘）。 */
export function IconImport(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M12 4v9" />
      <path d="M8.5 9.5L12 13l3.5-3.5" />
      <path d="M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3" />
    </Svg>
  )
}

export function IconLink(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M10.5 13.5a3.5 3.5 0 0 0 5 0l2.5-2.5a3.5 3.5 0 0 0-5-5L11.8 7" />
      <path d="M13.5 10.5a3.5 3.5 0 0 0-5 0L6 13a3.5 3.5 0 0 0 5 5l1.2-1.2" />
    </Svg>
  )
}

export function IconList(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M8 6h12M8 12h12M8 18h12" />
      <path d="M4 6h.01M4 12h.01M4 18h.01" />
    </Svg>
  )
}

/** 首页（房子）。 */
export function IconHome(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M4 10.5L12 4l8 6.5V19a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 19z" />
      <path d="M9.5 20.5v-6h5v6" />
    </Svg>
  )
}

export function IconSettings(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-2.87 1.2V21a2 2 0 1 1-4 0v-.09A1.7 1.7 0 0 0 7 19.4a1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.7 1.7 0 0 0 2.6 15H2.5a2 2 0 1 1 0-4h.09A1.7 1.7 0 0 0 4.6 9a1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.7 1.7 0 0 0 9 4.6h.09A1.7 1.7 0 0 0 10.3 2.6V2.5a2 2 0 1 1 4 0v.09A1.7 1.7 0 0 0 15 4.6a1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.7 1.7 0 0 0 19.4 9v.09a1.7 1.7 0 0 0 1.51 1.51h.09a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.51 1.4z" />
    </Svg>
  )
}

/** 用户（头 + 肩，账户入口用）。 */
export function IconUser(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <circle cx="12" cy="8" r="3.6" />
      <path d="M4.5 20.5c0-3.6 3.4-6 7.5-6s7.5 2.4 7.5 6" />
    </Svg>
  )
}

/** 星芒（Nova）。 */
export function IconSparkle(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M12 3.5l1.9 4.6 4.6 1.9-4.6 1.9L12 16.5l-1.9-4.6L5.5 10l4.6-1.9z" />
      <path d="M18 17l.9 2.1L21 20l-2.1.9L18 23l-.9-2.1L15 20l2.1-.9z" />
    </Svg>
  )
}

export function IconCheck(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M5 12.5l4.5 4.5L19 7.5" />
    </Svg>
  )
}

export function IconWarning(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M12 4.5l8.5 15H3.5z" />
      <path d="M12 10v4M12 17h.01" />
    </Svg>
  )
}

export function IconInfo(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 11v5M12 8h.01" />
    </Svg>
  )
}

export function IconSearch(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <circle cx="11" cy="11" r="6.5" />
      <path d="M16 16l4 4" />
    </Svg>
  )
}

export function IconPlus(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M12 5v14M5 12h14" />
    </Svg>
  )
}

/** 方块/体素（实验室、版本图标兜底）。 */
export function IconCube(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <path d="M12 3.5l8 4.2v8.6l-8 4.2-8-4.2V7.7z" />
      <path d="M4 7.7l8 4.3 8-4.3M12 12v8.5" />
    </Svg>
  )
}

/** 世界/存档（带网格的方块）。 */
export function IconWorld(props: IconProps): JSX.Element {
  return (
    <Svg {...props}>
      <rect x="3.5" y="3.5" width="17" height="17" rx="2" />
      <path d="M3.5 9.5h17M3.5 15h17M9.5 3.5v17M15 3.5v17" />
    </Svg>
  )
}

/** 主题：暗色半圆 / 亮色空心圆 / 跟随系统半明半暗。 */
export function IconTheme(props: IconProps & { mode: 'system' | 'light' | 'dark' }): JSX.Element {
  const mode = props.mode
  return (
    <Svg {...props}>
      <circle cx="12" cy="12" r="8.5" />
      {mode === 'dark' && <path d="M12 3.5a8.5 8.5 0 0 0 0 17z" fill="currentColor" stroke="none" />}
      {mode === 'system' && (
        <path
          d="M12 3.5a8.5 8.5 0 0 1 0 17z"
          fill="currentColor"
          stroke="none"
          opacity="0.55"
        />
      )}
    </Svg>
  )
}
