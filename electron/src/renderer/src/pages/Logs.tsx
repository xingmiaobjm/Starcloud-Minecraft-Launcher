/// 启动器日志：显示桥接侧日志（game.output 等事件）与当前会话日志。
import { useEffect, useRef, useState } from 'react'

import { onBridgeEvent } from '../bridge'
import { PageShell, EmptyState } from '../ui'

interface LogLine {
  id: number
  text: string
  time: string
}

export default function LogsPage() {
  const [lines, setLines] = useState<LogLine[]>([])
  const seq = useRef(1)

  useEffect(() => {
    let alive = true
    onBridgeEvent((event, params) => {
      if (!alive) return
      if (event === 'game.output') {
        setLines((prev) =>
          [
            ...prev,
            {
              id: seq.current++,
              text: String(params.line ?? ''),
              time: new Date().toLocaleTimeString('zh-CN')
            }
          ].slice(-500)
        )
      }
    })
    return () => {
      alive = false
    }
  }, [])

  // 会话日志也并入（同源：bridge 的 game.output 已经是唯一来源，这里兜底显示）
  const merged = lines

  return (
    <PageShell
      title="启动器日志"
      subtitle="桥接事件与游戏输出"
      actions={
        <button className="btn btn-subtle" onClick={() => setLines([])}>
          ✕ 清空
        </button>
      }
    >
      <div
        className="card card-flush"
        style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}
      >
        <div className="card-head">
          <div className="dot" style={{ background: 'var(--text-tertiary)' }} />
          <span className="card-title">输出</span>
          <div style={{ flex: 1 }} />
          <span className="chip">{merged.length} 行</span>
        </div>
        {merged.length === 0 ? (
          <EmptyState title="暂无日志" hint="启动游戏后这里会实时滚动输出。" />
        ) : (
          <div className="log" style={{ paddingTop: 0 }}>
            {merged.map((line) => (
              <div key={line.id} style={{ animation: 'fadeIn 150ms ease both' }}>
                {line.time !== '' && (
                  <span style={{ opacity: 0.45 }}>[{line.time}] </span>
                )}
                {line.text}
              </div>
            ))}
          </div>
        )}
      </div>
    </PageShell>
  )
}
