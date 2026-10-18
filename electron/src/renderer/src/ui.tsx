/// 通用 UI 原子：页面骨架、空态、Toast、进度条、加载骨架屏。
/// 所有页面共用，保证视觉与动效一致。
import React, { useEffect, useRef, useState } from 'react'
import {
  Card,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  ProgressBar,
  Spinner,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  InfoRegular
} from '@fluentui/react-icons'
import { brandYellow, motion } from './theme'

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    minHeight: 0,
    boxSizing: 'border-box'
  },
  pageTitle: {
    fontSize: '26px',
    fontWeight: 700,
    letterSpacing: '0.2px',
    margin: 0
  },
  pageSub: {
    fontSize: '12px',
    opacity: 0.6,
    marginTop: '2px'
  },
  scroll: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    paddingRight: '4px'
  },
  empty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '10px',
    flex: 1,
    minHeight: '180px',
    opacity: 0.7
  },
  toastHost: {
    position: 'fixed',
    right: '20px',
    bottom: '20px',
    zIndex: 1000,
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    maxWidth: '380px'
  },
  skeletonRow: {
    height: '48px',
    borderRadius: '6px',
    marginBottom: '8px'
  }
})

export function useStyles2() {
  return useStyles()
}

/** 页面骨架：标题 + 可滚动内容区。 */
export function PageShell(props: {
  title: string
  subtitle?: string
  actions?: React.ReactNode
  children: React.ReactNode
}) {
  const styles = useStyles()
  return (
    <div className={styles.page}>
      <div
        style={{
          display: 'flex',
          alignItems: 'flex-start',
          gap: '16px',
          padding: '4px 0 12px'
        }}
      >
        <div style={{ flex: 1, minWidth: 0 }}>
          <h1 className={styles.pageTitle}>{props.title}</h1>
          {props.subtitle && <div className={styles.pageSub}>{props.subtitle}</div>}
        </div>
        {props.actions}
      </div>
      <div className={styles.scroll}>{props.children}</div>
    </div>
  )
}

export function EmptyState(props: {
  title: string
  hint?: string
  action?: React.ReactNode
}) {
  const styles = useStyles()
  return (
    <div className={styles.empty}>
      <Text size={400} weight="semibold">
        {props.title}
      </Text>
      {props.hint && (
        <Text size={200} style={{ opacity: 0.7, textAlign: 'center' }}>
          {props.hint}
        </Text>
      )}
      {props.action}
    </div>
  )
}

export function LoadingCard({ label }: { label?: string }) {
  const styles = useStyles()
  return (
    <Card style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
      <Spinner size="tiny" />
      <Text size={200}>{label ?? '加载中…'}</Text>
    </Card>
  )
}

/** 骨架屏：列表占位，避免加载时布局跳动。 */
export function SkeletonList({ rows = 5 }: { rows?: number }) {
  const styles = useStyles()
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
        <div
          key={index}
          className={styles.skeletonRow}
          style={{
            background: 'rgba(255,255,255,0.04)',
            opacity: 1 - index * 0.12,
            animation: `fadeIn ${motion.normal}ms ${motion.ease} both`
          }}
        />
      ))}
    </div>
  )
}

export type ToastKind = 'success' | 'error' | 'info'

export interface ToastItem {
  id: number
  kind: ToastKind
  text: string
}

/** 全局 Toast：任何页面调用 push('成功文案')。 */
export function useToasts() {
  const [items, setItems] = useState<ToastItem[]>([])
  const seq = useRef(1)
  const push = (kind: ToastKind, text: string) => {
    const id = seq.current++
    setItems((prev) => [...prev, { id, kind, text }])
    setTimeout(() => setItems((prev) => prev.filter((t) => t.id !== id)), 4200)
  }
  const host = <ToastHost items={items} onDismiss={(id) => setItems((prev) => prev.filter((t) => t.id !== id))} />
  return { push, host }
}

function ToastHost(props: { items: ToastItem[]; onDismiss: (id: number) => void }) {
  const styles = useStyles()
  if (props.items.length === 0) return null
  return (
    <div className={styles.toastHost}>
      {props.items.map((item) => (
        <MessageBar
          key={item.id}
          intent={item.kind}
          style={{ animation: `slideUp ${motion.slow}ms ${motion.easeOut} both` }}
        >
          {item.kind === 'success' && <CheckmarkCircleRegular />}
          {item.kind === 'error' && <ErrorCircleRegular />}
          {item.kind === 'info' && <InfoRegular />}
          <MessageBarBody>{item.text}</MessageBarBody>
          <MessageBarActions
            containerAction={
              <button
                onClick={() => props.onDismiss(item.id)}
                style={{
                  background: 'none',
                  border: 'none',
                  color: 'inherit',
                  cursor: 'pointer'
                }}
              >
                ✕
              </button>
            }
          />
        </MessageBar>
      ))}
    </div>
  )
}

/** 顶部细进度条：页面有后台任务时显示。 */
export function TopProgress({ show }: { show: boolean }) {
  if (!show) return null
  return (
    <div style={{ position: 'fixed', top: 0, left: 0, right: 0, zIndex: 2000 }}>
      <ProgressBar thickness="medium" style={{ color: brandYellow }} />
    </div>
  )
}

/** 页面切换动画：淡入 + 上移 6px。 */
export function PageFade({ children, keyId }: { children: React.ReactNode; keyId: string }) {
  return (
    <div
      key={keyId}
      style={{
        height: '100%',
        minHeight: 0,
        animation: `pageIn ${motion.slow}ms ${motion.easeOut} both`
      }}
    >
      {children}
    </div>
  )
}

/** 全局注入动画关键帧（应用启动时调用一次）。 */
export function injectKeyframes() {
  if (document.getElementById('starcloud-keyframes')) return
  const style = document.createElement('style')
  style.id = 'starcloud-keyframes'
  style.textContent = `
@keyframes pageIn {
  from { opacity: 0; transform: translateY(6px); }
  to   { opacity: 1; transform: translateY(0); }
}
@keyframes fadeIn {
  from { opacity: 0; }
  to   { opacity: 1; }
}
@keyframes slideUp {
  from { opacity: 0; transform: translateY(12px) scale(0.98); }
  to   { opacity: 1; transform: translateY(0) scale(1); }
}
@keyframes pulse {
  0%, 100% { opacity: 1; }
  50%      { opacity: 0.45; }
}
@keyframes spin {
  to { transform: rotate(360deg); }
}
@keyframes rowIn {
  from { opacity: 0; transform: translateX(-6px); }
  to   { opacity: 1; transform: translateX(0); }
}
@keyframes glowPulse {
  0%, 100% { box-shadow: 0 0 0 0 rgba(255,194,31,0.35); }
  50%      { box-shadow: 0 0 0 6px rgba(255,194,31,0); }
}
@keyframes breathe {
  0%, 100% { transform: scale(1); opacity: 0.9; }
  50%      { transform: scale(1.05); opacity: 1; }
}
`
  document.head.appendChild(style)
}

/** 暗色滚动条 + 键盘焦点可见（Fluent 默认滚动条在暗色下偏亮）。 */
export function injectGlobalStyle() {
  if (document.getElementById('starcloud-global')) return
  const style = document.createElement('style')
  style.id = 'starcloud-global'
  style.textContent = `
::-webkit-scrollbar { width: 10px; height: 10px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb {
  background: rgba(255,255,255,0.14);
  border-radius: 6px;
  border: 2px solid transparent;
  background-clip: content-box;
}
::-webkit-scrollbar-thumb:hover { background: rgba(255,255,255,0.24); background-clip: content-box; }
* { scrollbar-width: thin; scrollbar-color: rgba(255,255,255,0.16) transparent; }
button:focus-visible, [role='button']:focus-visible {
  outline: 2px solid rgba(255,194,31,0.75);
  outline-offset: 2px;
}
`
  document.head.appendChild(style)
}

/** 页面级错误边界：某个页渲染炸了不至于白屏。 */
export class ErrorBoundary extends React.Component<
  { children: React.ReactNode; label: string },
  { error?: Error }
> {
  constructor(props: { children: React.ReactNode; label: string }) {
    super(props)
    this.state = {}
  }

  static getDerivedStateFromError(error: Error) {
    return { error }
  }

  render(): React.ReactNode {
    if (this.state.error !== undefined) {
      return (
        <div style={{ padding: '40px', textAlign: 'center' }}>
          <div style={{ fontSize: '16px', fontWeight: 600, marginBottom: '8px' }}>
            {this.props.label} 页面出错了
          </div>
          <div style={{ opacity: 0.7, fontSize: '12px', marginBottom: '16px' }}>
            {String(this.state.error)}
          </div>
          <button
            onClick={() => this.setState({ error: undefined })}
            style={{
              background: brandYellow,
              color: '#1b1b1b',
              border: 'none',
              borderRadius: '6px',
              padding: '8px 18px',
              fontWeight: 600,
              cursor: 'pointer'
            }}
          >
            重试
          </button>
        </div>
      )
    }
    return this.props.children
  }
}
