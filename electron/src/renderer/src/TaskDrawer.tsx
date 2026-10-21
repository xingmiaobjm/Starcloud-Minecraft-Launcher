/// 调度中心抽屉（Win 端 MainWindow 右侧任务抽屉的等价物）。
/// 任务来源：install.* 事件（下载/安装）+ 当前游戏会话。
import { useEffect } from 'react'

import type { TaskItem } from './state/tasks'
import type { GameSessionApi } from './state/session'
import { ProgressBar } from './ui'

export default function TaskDrawer(props: {
  open: boolean
  tasks: TaskItem[]
  session: GameSessionApi
  onClose: () => void
}): JSX.Element | null {
  // Esc 关闭
  useEffect(() => {
    if (!props.open) return
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') props.onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [props.open, props])

  if (!props.open) return null

  const running = props.session.pid !== undefined && props.session.pid > 0
  const entries: Array<{
    key: string
    title: string
    status: string
    progress?: number
    failed?: boolean
    done?: boolean
  }> = []

  if (running) {
    entries.push({
      key: 'game-running',
      title: `游戏运行中${props.session.launchVersion !== undefined ? ` · ${props.session.launchVersion}` : ''}`,
      status: `pid ${props.session.pid}`,
      done: false
    })
  } else if (props.session.launching === true) {
    entries.push({
      key: 'game-launching',
      title: `正在启动${props.session.launchVersion !== undefined ? ` · ${props.session.launchVersion}` : ''}`,
      status: props.session.stage ?? '准备中',
      progress: props.session.progress
    })
  } else if (props.session.exitCode !== undefined) {
    entries.push({
      key: 'game-exited',
      title: '上次游戏会话',
      status: `已退出（exit=${props.session.exitCode}）`,
      done: true
    })
  }

  for (const t of props.tasks) {
    entries.push({
      key: t.id,
      title: t.title,
      status: t.status,
      progress: t.progress,
      failed: t.failed === true,
      done: t.finished === true
    })
  }

  return (
    <>
      <div
        style={{
          position: 'fixed',
          inset: 0,
          zIndex: 850,
          background: 'rgba(0,0,0,0.3)',
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
          width: 360,
          zIndex: 851,
          borderLeft: '1px solid var(--stroke-default)',
          boxShadow: 'var(--shadow-16)',
          display: 'flex',
          flexDirection: 'column',
          animation: 'slideInRight 240ms cubic-bezier(0.1,0.9,0.2,1) both'
        }}
      >
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 10,
            padding: '14px 16px',
            borderBottom: '1px solid var(--stroke-subtle)'
          }}
        >
          <span style={{ fontSize: 16, fontWeight: 600 }}>调度中心</span>
          <span className="t-cap">{entries.length} 项</span>
          <div style={{ flex: 1 }} />
          <button className="btn btn-subtle btn-sm" onClick={props.onClose}>
            ✕
          </button>
        </div>

        <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: 12 }}>
          {entries.length === 0 && (
            <div className="empty">
              <div className="empty-title">暂无任务</div>
              <div className="empty-hint">下载、安装与游戏会话会显示在这里。</div>
            </div>
          )}
          {entries.map((t) => (
            <div key={t.key} className="card" style={{ marginBottom: 10, padding: '12px 14px' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
                <span
                  className="dot"
                  style={{
                    background: t.failed
                      ? 'var(--danger)'
                      : t.done
                        ? 'var(--success)'
                        : 'var(--accent)',
                    animation: !t.done && !t.failed ? 'pulse 1.6s infinite' : undefined
                  }}
                />
                <span className="t-strong truncate" style={{ flex: 1 }}>
                  {t.title}
                </span>
              </div>
              <div className="t-cap">{t.status}</div>
              {t.progress !== undefined && (
                <div style={{ marginTop: 8 }}>
                  <ProgressBar value={t.progress} />
                </div>
              )}
            </div>
          ))}
        </div>

        <div
          className="t-cap"
          style={{ padding: '10px 16px', borderTop: '1px solid var(--stroke-subtle)' }}
        >
          任务只保留本次会话（与 Win 端一致）。
        </div>
      </div>
    </>
  )
}
