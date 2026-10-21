/// 调度中心（Win 端 MainWindow 右侧任务抽屉的等价物，0.3.7 版式）：
/// 宽面板 ≈ 46% 窗宽；头部「图标 + 标题 + 副标题（N 个任务进行中 · M 条通知）+ 关闭」；
/// 调度队列（任务卡：黄点 + 标题 + 右侧状态/百分比 + 进度条）；
/// 历史通知（时间 + 级别 + 消息，右上「清空通知」）；
/// 空队列时居中一个列表图标 + 「无」。
import { useEffect } from 'react'

import { ProgressBar } from './ui'
import type { GameSessionApi } from './state/session'
import type { NotificationItem, TaskItem } from './state/tasks'

interface Entry {
  key: string
  title: string
  status?: string
  hint?: string
  progress?: number
  failed?: boolean
  done?: boolean
}

export default function TaskDrawer(props: {
  open: boolean
  tasks: TaskItem[]
  notifications: NotificationItem[]
  session: GameSessionApi
  onClearNotifications: () => void
  onClose: () => void
}): JSX.Element | null {
  useEffect(() => {
    if (!props.open) return
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') props.onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [props.open, props])

  if (!props.open) return null

  const s = props.session
  const running = s.pid !== undefined && s.pid > 0

  // 调度队列：游戏会话 + 下载安装
  const queue: Entry[] = []
  if (s.launching === true) {
    queue.push({
      key: 'game-launching',
      title: `启动游戏${s.launchVersion !== undefined ? ` ${s.launchVersion}` : ''}`,
      status: `${Math.round(s.progress ?? 0)}%`,
      hint: s.stage,
      progress: s.progress
    })
  } else if (running) {
    queue.push({
      key: 'game-running',
      title: `游戏运行中${s.launchVersion !== undefined ? ` · ${s.launchVersion}` : ''}`,
      hint: `pid ${s.pid}`
    })
  }
  for (const t of props.tasks) {
    queue.push({
      key: t.id,
      title: t.title,
      status: t.progress !== undefined ? `${Math.round(t.progress)}%` : undefined,
      hint: t.status,
      progress: t.progress,
      failed: t.failed === true,
      done: t.finished === true
    })
  }

  const activeCount = queue.filter((t) => !t.done && !t.failed).length

  return (
    <>
      <div
        style={{
          position: 'fixed',
          inset: 0,
          zIndex: 850,
          background: 'rgba(0,0,0,0.35)',
          animation: 'fadeIn 150ms cubic-bezier(0.33,0,0.67,1) both'
        }}
        onClick={props.onClose}
      />
      <div
        className="acrylic"
        style={{
          position: 'fixed',
          top: 0,
          right: 0,
          bottom: 0,
          width: 'min(720px, 52vw)',
          minWidth: 420,
          zIndex: 851,
          borderLeft: '1px solid var(--stroke-default)',
          boxShadow: 'var(--shadow-16)',
          display: 'flex',
          flexDirection: 'column',
          animation: 'slideInRight 240ms cubic-bezier(0.1,0.9,0.2,1) both'
        }}
      >
        {/* 头部：图标 + 标题 + 副标题 + 关闭 */}
        <div
          style={{
            display: 'flex',
            alignItems: 'flex-start',
            gap: 10,
            padding: '18px 20px 10px'
          }}
        >
          <span style={{ color: 'var(--accent)', marginTop: 3 }}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-hidden>
              <path d="M4 6.5h16M4 12h16M4 17.5h16" />
            </svg>
          </span>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: 18, fontWeight: 600 }}>调度中心</div>
            <div className="t-cap">
              {activeCount > 0 && `${activeCount} 个任务进行中`}
              {activeCount > 0 && props.notifications.length > 0 && ' · '}
              {props.notifications.length} 条通知
            </div>
          </div>
          <button
            className="btn btn-subtle btn-sm"
            title="关闭"
            aria-label="关闭调度中心"
            onClick={props.onClose}
          >
            ⊗
          </button>
        </div>

        <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '4px 20px 16px' }}>
          {/* 调度队列 */}
          <div className="t-strong" style={{ fontSize: 15, marginBottom: 10 }}>
            调度队列
          </div>

          {queue.length === 0 ? (
            <div
              style={{
                padding: '40px 0',
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                gap: 10,
                color: 'var(--text-tertiary)'
              }}
            >
              <svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" aria-hidden>
                <path d="M4 6.5h16M4 12h16M4 17.5h16" />
              </svg>
              <div style={{ fontSize: 14 }}>无</div>
            </div>
          ) : (
            queue.map((t) => (
              <div
                key={t.key}
                className="card"
                style={{ marginBottom: 10, padding: '12px 14px' }}
              >
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span
                    className="dot"
                    style={{
                      background: t.failed
                        ? 'var(--danger)'
                        : t.done
                          ? 'var(--success)'
                          : 'var(--accent)',
                      flexShrink: 0
                    }}
                  />
                  <span className="t-strong truncate" style={{ flex: 1 }}>
                    {t.title}
                  </span>
                  {t.status !== undefined && (
                    <span className="t-body" style={{ color: 'var(--text-secondary)' }}>
                      {t.status}
                    </span>
                  )}
                </div>
                {t.hint !== undefined && t.hint !== t.status && (
                  <div className="t-cap" style={{ marginTop: 4 }}>
                    {t.hint}
                  </div>
                )}
                {t.progress !== undefined && !t.done && !t.failed && (
                  <div style={{ marginTop: 8 }}>
                    <ProgressBar value={t.progress} />
                  </div>
                )}
              </div>
            ))
          )}

          {/* 历史通知 */}
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              margin: '16px 0 10px'
            }}
          >
            <span className="t-strong" style={{ fontSize: 15 }}>
              历史通知
            </span>
            <div style={{ flex: 1 }} />
            {props.notifications.length > 0 && (
              <button className="btn btn-subtle btn-sm" onClick={props.onClearNotifications}>
                清空通知
              </button>
            )}
          </div>

          {props.notifications.length === 0 ? (
            <div className="t-cap">暂无通知。</div>
          ) : (
            props.notifications.map((n) => (
              <div
                key={n.id}
                style={{ display: 'flex', alignItems: 'baseline', gap: 8, padding: '3px 0' }}
              >
                <span className="t-meta" style={{ flexShrink: 0 }}>
                  {n.time}
                </span>
                <span
                  style={{
                    flexShrink: 0,
                    fontSize: 12,
                    color: n.level === '错误' ? 'var(--danger)' : 'var(--accent)'
                  }}
                >
                  {n.level}
                </span>
                <span className="t-body truncate" style={{ minWidth: 0 }} title={n.message}>
                  {n.message}
                </span>
              </div>
            ))
          )}
        </div>
      </div>
    </>
  )
}
