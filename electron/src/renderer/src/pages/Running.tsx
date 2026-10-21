/// 运行状态页：对齐 Win 端 RunningPage ——
/// 无运行时居中空态卡（图标 + 文案 + 去首页按钮）；有运行时展示会话卡 + 实时日志。
import { request } from '../bridge'
import { EmptyState, useToasts } from '../ui'
import type { GameSessionApi } from '../state/session'
import { IconRefresh, IconChevronDown, IconClose } from '../icons'

export default function RunningPage(props: {
  session: GameSessionApi
  onGoHome: () => void
}): JSX.Element {
  const { push, host } = useToasts()
  const session = props.session

  async function killGame(): Promise<void> {
    if (session.sessionId === undefined) return
    try {
      await request('game.kill', { sessionId: session.sessionId })
      push('info', '已结束游戏进程')
    } catch (e) {
      push('error', `结束失败：${String(e)}`)
    }
  }
  const running = session.pid !== undefined && session.pid > 0
  const hasLogs = session.logs.length > 0

  return (
    <div className="page">
      <div className="page-head">
        <div style={{ flex: 1 }} />
        <button className="btn btn-subtle" title="刷新" onClick={() => window.location.reload()}>
          <IconRefresh size={15} />
        </button>
        <button className="btn" title="采样设置">
          采样设置 <IconChevronDown size={14} />
        </button>
      </div>

      {!running && session.status === '' && !hasLogs ? (
        // 空态（Win 端同款居中卡）
        <div
          className="card"
          style={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center'
          }}
        >
          <div className="empty" style={{ minHeight: 0 }}>
            <div style={{ fontSize: 26, opacity: 0.5, lineHeight: 1 }}>☰</div>
            <div className="empty-title" style={{ fontSize: 16 }}>
              现在没有游戏在运行
            </div>
            <div className="empty-hint">去首页启动一个版本</div>
            <button className="btn btn-primary" onClick={props.onGoHome}>
              去首页
            </button>
          </div>
        </div>
      ) : (
        <>
          {/* 会话状态卡 */}
          <div className="card" style={{ padding: '18px 20px', marginBottom: 12 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
              <span
                className="dot"
                style={{
                  background: running ? 'var(--success)' : 'var(--text-tertiary)',
                  animation: running ? 'pulse 1.6s infinite' : undefined
                }}
              />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div className="t-strong truncate">
                  {session.status !== '' ? session.status : '当前没有运行中的游戏'}
                </div>
                <div className="t-cap truncate">
                  {running
                    ? `进程 pid ${session.pid}`
                    : session.exitCode !== undefined
                      ? `上次退出码 ${session.exitCode}`
                      : '在首页选择版本与账户后启动游戏'}
                </div>
              </div>
              {running && (
                <button className="btn btn-danger" onClick={() => void killGame()}>
                  结束游戏
                </button>
              )}
              {hasLogs && (
                <button className="btn btn-subtle" onClick={session.clearLogs}>
                  <IconClose size={13} /> 清空日志
                </button>
              )}
            </div>
          </div>

          {/* 实时日志 */}
          <div className="card card-flush" style={{ flex: 1, minHeight: 0 }}>
            <div className="card-head">
              <span
                className="dot"
                style={{
                  background: running ? 'var(--accent)' : 'var(--surface-hover)',
                  animation: running ? 'pulse 1.6s infinite' : undefined
                }}
              />
              <span className="card-title">实时日志</span>
              <div style={{ flex: 1 }} />
              <span className="chip">{session.logs.length} 行</span>
            </div>
            {hasLogs ? (
              <div className="log">
                {session.logs.map((line, index) => (
                  <div key={index}>{line}</div>
                ))}
              </div>
            ) : (
              <EmptyState title="暂无日志" hint="游戏启动后，这里会实时显示输出。" />
            )}
          </div>
        </>
      )}
      {host}
    </div>
  )
}
