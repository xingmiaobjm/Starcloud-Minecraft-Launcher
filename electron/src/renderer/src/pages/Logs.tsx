/// 启动器日志页：对齐 Win 端 LogsPage —— 顶部单选（启动器日志 / 游戏日志）
/// + 「打开日志目录」+ 全高等宽日志卡。
///
/// 说明：主进程日志（[nav]/[app] 之类）目前没有经 IPC 暴露给渲染层，
/// 「启动器日志」先显示本会话的桥接/启动记录；「游戏日志」订阅 game.output。
import { useEffect, useRef, useState } from 'react'

import { onBridgeEvent } from '../bridge'
import { useToasts } from '../ui'

interface LogLine {
  id: number
  at: string
  channel: string
  text: string
}

function stamp(d: Date): string {
  const p = (n: number, w = 2): string => n.toString().padStart(w, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(
    d.getHours()
  )}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`
}

export default function LogsPage(props: {
  session: {
    logs: string[]
    status: string
    pid?: number
  }
}): JSX.Element {
  const { push, host } = useToasts()
  const [tab, setTab] = useState<'launcher' | 'game'>('launcher')
  const [launcherLines, setLauncherLines] = useState<LogLine[]>([])
  const [gameLines, setGameLines] = useState<LogLine[]>([])
  const seq = useRef(1)

  // 启动器日志：记录本会话的桥接事件（不含 game.output，那些归游戏日志）
  useEffect(() => {
    let alive = true
    onBridgeEvent((event, params) => {
      if (!alive || event === 'game.output') return
      setLauncherLines((prev) => [
        ...prev.slice(-499),
        {
          id: seq.current++,
          at: stamp(new Date()),
          channel: event.split('.')[0] ?? 'app',
          text: `${event} ${JSON.stringify(params).slice(0, 200)}`
        }
      ])
    })
    return () => {
      alive = false
    }
  }, [])

  // 游戏日志：把会话里的行增量转成带序号的记录
  const lastGameCount = useRef(0)
  useEffect(() => {
    const incoming = props.session.logs
    if (incoming.length === lastGameCount.current) return
    const added = incoming.slice(lastGameCount.current)
    lastGameCount.current = incoming.length
    const now = new Date()
    setGameLines((prev) => [
      ...prev.slice(-999),
      ...added.map((line) => ({
        id: seq.current++,
        at: stamp(now),
        channel: 'game',
        text: line
      }))
    ])
  }, [props.session.logs])

  const lines = tab === 'launcher' ? launcherLines : gameLines

  return (
    <div className="page">
      <div className="page-head">
        {/* 单选（Win 端圆点 + 文案） */}
        {(
          [
            { id: 'launcher', label: '启动器日志' },
            { id: 'game', label: '游戏日志' }
          ] as Array<{ id: 'launcher' | 'game'; label: string }>
        ).map((t) => (
          <button
            key={t.id}
            className="btn btn-subtle"
            style={{ display: 'flex', alignItems: 'center', gap: 8 }}
            onClick={() => setTab(t.id)}
          >
            <span
              style={{
                width: 16,
                height: 16,
                borderRadius: '50%',
                border:
                  tab === t.id ? '5px solid var(--accent)' : '1.5px solid var(--stroke-strong)',
                background: 'transparent',
                flexShrink: 0
              }}
            />
            <span className={tab === t.id ? 't-strong' : 't-body'}>{t.label}</span>
          </button>
        ))}
        <button
          className="btn"
          onClick={() => push('info', '打开日志目录：主进程 openPath 接入后可用')}
        >
          打开日志目录
        </button>
        <div style={{ flex: 1 }} />
        <button
          className="btn btn-subtle btn-sm"
          onClick={() => {
            if (tab === 'launcher') setLauncherLines([])
            else setGameLines([])
          }}
        >
          ✕ 清空
        </button>
      </div>

      <div className="card card-flush" style={{ flex: 1, minHeight: 0 }}>
        <div className="log">
          {lines.length === 0 ? (
            <div className="empty" style={{ minHeight: 120 }}>
              <div className="empty-hint">
                {tab === 'launcher'
                  ? '暂无启动器日志。切换页面或触发桥接事件后会记录在这里。'
                  : '暂无游戏日志。启动游戏后这里会显示输出。'}
              </div>
            </div>
          ) : (
            lines.map((l) => (
              <div key={l.id} style={{ display: 'flex', gap: 8 }}>
                <span style={{ opacity: 0.55, flexShrink: 0 }}>[{l.at}]</span>
                <span style={{ opacity: 0.7, flexShrink: 0, width: 54 }}>[{l.channel}]</span>
                <span style={{ minWidth: 0, wordBreak: 'break-word' }}>{l.text}</span>
              </div>
            ))
          )}
        </div>
      </div>

      {host}
    </div>
  )
}
