/// 运行状态：当前会话状态 + 实时日志。
import { EmptyState } from '../ui'
import type { GameSessionApi } from '../state/session'

export default function RunningPage(props: { session: GameSessionApi }) {
  const session = props.session
  const running = session.pid !== undefined && session.pid > 0

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <div className="card" style={{ padding: '18px 20px', marginBottom: 12 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <div
            className="dot"
            style={{
              background: running ? 'var(--success)' : 'var(--text-tertiary)',
              animation: running ? 'pulse 1.6s infinite' : undefined
            }}
          />
          <div style={{ flex: 1, minWidth: 0 }}>
            <div className="t-strong truncate">{session.status || '当前没有运行中的游戏'}</div>
            <div className="t-cap truncate">
              {running ? `进程 pid ${session.pid}` : '在首页选择版本与账户后启动游戏'}
            </div>
          </div>
          {session.logs.length > 0 && (
            <button className="btn btn-subtle" onClick={session.clearLogs}>
              ✕ 清空日志
            </button>
          )}
        </div>
      </div>
      <div
        className="card card-flush"
        style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}
      >
        <div className="card-head">
          <div
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
        {session.logs.length === 0 ? (
          <EmptyState title="暂无日志" hint="游戏启动后，这里会实时显示输出。" />
        ) : (
          <div className="log">
            {session.logs.map((line, index) => (
              <div key={index} style={{ animation: 'fadeIn 150ms ease both' }}>
                {line}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
