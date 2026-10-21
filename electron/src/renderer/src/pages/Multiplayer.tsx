/// 联机页：对齐 Win 端 MultiplayerPage ——
/// 右上「联机方案」下拉 → 状态卡（核心/会话/状态 + 高级选项）→ 操作卡（创建/加入/日志）
/// → 可展开行（核心日志/使用步骤/第三方 SDK）→ 底部署名。
import { useCallback, useEffect, useRef, useState } from 'react'

import { onBridgeEvent, request } from '../bridge'
import type { MultiplayerSnapshot } from '../bridge'
import { Modal, ProgressBar, Select, useToasts } from '../ui'

/** 可展开行（Win 端 Expander 样式：一行标题 + 右侧箭头）。 */
function Expander(props: {
  title: string
  open: boolean
  onToggle: () => void
  children: React.ReactNode
}): JSX.Element {
  return (
    <div className="card" style={{ padding: 0, marginBottom: 10, overflow: 'hidden' }}>
      <button
        className="nav-item"
        style={{ height: 52, width: '100%', borderRadius: 0, justifyContent: 'space-between' }}
        onClick={props.onToggle}
      >
        <span className="t-body" style={{ color: 'var(--text-primary)', fontSize: 14 }}>
          {props.title}
        </span>
        <span style={{ opacity: 0.55, transform: props.open ? 'rotate(90deg)' : 'none' }}>
          ›
        </span>
      </button>
      {props.open && (
        <div style={{ padding: '4px 16px 14px' }}>{props.children}</div>
      )}
    </div>
  )
}

const PHASE_LABEL: Record<string, string> = {
  idle: '空闲',
  hosting: '房间已创建',
  joining: '正在加入',
  connected: '已连接',
  failed: '出错',
  stopping: '正在停止'
}

export default function MultiplayerPage(): JSX.Element {
  const { push, host } = useToasts()
  const [snap, setSnap] = useState<MultiplayerSnapshot | null>(null)
  const [busy, setBusy] = useState(false)
  const [logs, setLogs] = useState<string[]>([])
  const [logsOpen, setLogsOpen] = useState(false)
  const [stepsOpen, setStepsOpen] = useState(false)
  const [sdkOpen, setSdkOpen] = useState(false)
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [joinOpen, setJoinOpen] = useState(false)
  const [joinCode, setJoinCode] = useState('')
  const [nickname, setNickname] = useState('')
  const [installProgress, setInstallProgress] = useState<number | null>(null)
  const pollRef = useRef<number | null>(null)

  const load = useCallback(async (): Promise<void> => {
    try {
      const s = await request<MultiplayerSnapshot>('multiplayer.state')
      setSnap(s)
      setNickname((prev) => (prev === '' ? s.nickname : prev))
    } catch (e) {
      push('error', `读取联机状态失败：${String(e)}`)
    }
  }, [push])

  useEffect(() => {
    void load()
    pollRef.current = window.setInterval(() => void load(), 2500)
    return () => {
      if (pollRef.current !== null) window.clearInterval(pollRef.current)
    }
  }, [load])

  useEffect(() => {
    onBridgeEvent((event, params) => {
      if (event === 'multiplayer.changed') {
        void load()
      } else if (event === 'multiplayer.install') {
        const pct = Number(params.percent ?? 0)
        setInstallProgress(pct > 0 ? pct : null)
      } else if (event === 'multiplayer.installed') {
        setInstallProgress(null)
        push('success', '联机核心已安装')
        void load()
      }
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function run(method: string, params: object, hint: string): Promise<void> {
    if (busy) return
    setBusy(true)
    try {
      await request(method, params)
      push('info', hint)
      await load()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(false)
    }
  }

  async function openLogs(): Promise<void> {
    try {
      const r = await request<{ logs: string[] }>('multiplayer.logs')
      setLogs(r.logs)
      setLogsOpen(true)
    } catch (e) {
      push('error', `日志读取失败：${String(e)}`)
    }
  }

  const providers = snap?.providers ?? []
  const activeId = snap?.activeId ?? ''
  const active = providers.find((p) => p.id === activeId)
  const coreVersion =
    snap?.core.installed === true && snap.core.version !== undefined ? snap.core.version : '未安装'
  const phase = snap?.state.phase ?? 'idle'

  return (
    <div className="page">
      <div className="page-head">
        <div style={{ flex: 1 }} />
        <span className="t-cap">联机方案</span>
        <Select
          width={220}
          value={activeId}
          disabled={busy || snap?.state.isBusy === true}
          placeholder="（无可用方案）"
          options={
            providers.length === 0
              ? [{ value: '', label: '（无可用方案）' }]
              : providers.map((p) => ({ value: p.id, label: p.displayName }))
          }
          onChange={(id) => void run('multiplayer.selectProvider', { id }, '已切换联机方案')}
        />
      </div>

      <div className="page-body">
        {/* 状态卡 */}
        <div className="card" style={{ marginBottom: 12 }}>
          <div style={{ display: 'flex', gap: 40, flexWrap: 'wrap' }}>
            <div>
              <div className="t-cap">联机核心</div>
              <div className="t-body" style={{ color: 'var(--text-primary)' }}>
                v{coreVersion}
              </div>
            </div>
            <div>
              <div className="t-cap">核心会话</div>
              <div className="t-body" style={{ color: 'var(--text-primary)' }}>
                {snap?.core.sessionRunning === true ? '运行中' : '已停止'}
              </div>
            </div>
            <div>
              <div className="t-cap">当前状态</div>
              <div className="t-body" style={{ color: 'var(--text-primary)' }}>
                {PHASE_LABEL[phase] ?? phase}
                {snap?.state.isBusy === true ? '（处理中）' : ''}
              </div>
            </div>
          </div>

          {snap?.core.installed === false && (
            <div className="info info-warning" style={{ marginTop: 12 }}>
              <span aria-hidden>⚠</span>
              <div className="info-body" style={{ flex: 1 }}>
                联机核心未安装。
                {installProgress !== null && (
                  <div style={{ marginTop: 6 }}>
                    <ProgressBar value={installProgress} />
                  </div>
                )}
              </div>
              <button
                className="btn btn-sm"
                disabled={installProgress !== null}
                onClick={() =>
                  void request('multiplayer.install')
                    .then(() => push('info', '已开始下载联机核心'))
                    .catch((e) => push('error', String(e)))
                }
              >
                安装
              </button>
            </div>
          )}

          <button
            className="nav-item"
            style={{
              width: '100%',
              marginTop: 12,
              background: 'var(--surface-subtle)',
              justifyContent: 'space-between',
              paddingLeft: 12
            }}
            onClick={() => setAdvancedOpen((v) => !v)}
          >
            <span className="t-body" style={{ color: 'var(--text-primary)' }}>
              高级选项
            </span>
            <span style={{ opacity: 0.55, transform: advancedOpen ? 'rotate(90deg)' : 'none' }}>
              ›
            </span>
          </button>
          {advancedOpen && (
            <div style={{ marginTop: 12, display: 'flex', gap: 12, flexWrap: 'wrap' }}>
              <label style={{ flex: 1, minWidth: 220 }}>
                <div className="t-cap" style={{ marginBottom: 4 }}>
                  昵称
                </div>
                <div style={{ display: 'flex', gap: 8 }}>
                  <input
                    className="input"
                    style={{ flex: 1 }}
                    value={nickname}
                    onChange={(e) => setNickname(e.target.value)}
                  />
                  <button
                    className="btn"
                    disabled={busy || nickname.trim() === ''}
                    onClick={() =>
                      void run('multiplayer.setNickname', { name: nickname.trim() }, '昵称已更新')
                    }
                  >
                    保存
                  </button>
                </div>
              </label>
              <label style={{ minWidth: 180 }}>
                <div className="t-cap" style={{ marginBottom: 4 }}>
                  节点地区
                </div>
                <Select
                  width={160}
                  value={snap?.nodeRegion ?? 'auto'}
                  options={[
                    { value: 'auto', label: '自动' },
                    { value: 'cn', label: '中国' },
                    { value: 'overseas', label: '海外' }
                  ]}
                  onChange={(region) =>
                    void run('multiplayer.setNodeRegion', { region }, '节点已切换')
                  }
                />
              </label>
            </div>
          )}
        </div>

        {/* 操作卡 */}
        <div className="card" style={{ display: 'flex', gap: 10, marginBottom: 12, flexWrap: 'wrap' }}>
          <button
            className="btn btn-primary"
            disabled={busy || snap?.core.installed === false}
            onClick={() =>
              void run('multiplayer.host', {}, '已开始创建房间，等状态变成「房间已创建」')
            }
          >
            创建房间
          </button>
          <button
            className="btn"
            disabled={busy || snap?.core.installed === false}
            onClick={() => setJoinOpen(true)}
          >
            加入好友的房间
          </button>
          <button className="btn" onClick={() => void openLogs()}>
            查看日志
          </button>
          {(phase === 'hosting' || phase === 'joining' || phase === 'connected') && (
            <>
              <button
                className="btn"
                disabled={busy}
                onClick={() => void run('multiplayer.stop', {}, '已停止联机会话')}
              >
                停止会话
              </button>
              <button
                className="btn btn-subtle"
                disabled={busy}
                onClick={() => void run('multiplayer.idle', {}, '已回到空闲')}
              >
                ↻ 回到空闲
              </button>
            </>
          )}
          <div style={{ flex: 1 }} />
          {snap?.state.room !== undefined && snap.state.room !== '' && (
            <span className="chip chip-accent">邀请码 {snap.state.room}</span>
          )}
        </div>

        {/* 可展开行 */}
        <Expander title="联机核心日志" open={logsOpen} onToggle={() => setLogsOpen((v) => !v)}>
          <div className="log" style={{ maxHeight: 240 }}>
            {logs.length === 0 ? '暂无日志' : logs.join('\n')}
          </div>
        </Expander>

        <Expander title="使用步骤" open={stepsOpen} onToggle={() => setStepsOpen((v) => !v)}>
          <ol className="t-body" style={{ margin: 0, paddingLeft: 18, lineHeight: 1.9 }}>
            <li>点「创建房间」，等状态变成「房间已创建」</li>
            <li>把邀请码发给好友</li>
            <li>好友点「加入好友的房间」填邀请码</li>
            <li>双方启动游戏，在多人游戏里连接本地房间</li>
          </ol>
        </Expander>

        <Expander title="第三方联机 SDK" open={sdkOpen} onToggle={() => setSdkOpen((v) => !v)}>
          <div className="t-body">
            {active === undefined
              ? '当前没有可用的联机方案。'
              : `${active.displayName}（${active.vendor}）—— ${active.description}`}
            {active?.vendorUrl !== undefined && active.vendorUrl !== '' && (
              <div className="t-meta" style={{ marginTop: 6 }}>
                {active.vendorUrl}
              </div>
            )}
          </div>
        </Expander>

        <div className="t-cap" style={{ textAlign: 'center', marginTop: 16 }}>
          联机由 Terracotta | 陶瓦联机 提供技术支持
        </div>
      </div>

      {/* 加入房间 */}
      <Modal
        open={joinOpen}
        title="加入好友的房间"
        onClose={() => setJoinOpen(false)}
        footer={
          <>
            <button className="btn" onClick={() => setJoinOpen(false)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              disabled={joinCode.trim() === '' || busy}
              onClick={() => {
                setJoinOpen(false)
                void run('multiplayer.join', { room: joinCode.trim() }, '已提交进房请求')
                setJoinCode('')
              }}
            >
              加入
            </button>
          </>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <span className="t-cap">邀请码</span>
          <input
            className="input"
            autoFocus
            value={joinCode}
            placeholder="粘贴好友发来的邀请码"
            onChange={(e) => setJoinCode(e.target.value)}
          />
        </div>
      </Modal>

      {/* 日志浮层 */}
      <Modal open={logsOpen && false} title="联机核心日志" onClose={() => setLogsOpen(false)}>
        <div className="log" style={{ maxHeight: '50vh' }}>
          {logs.length === 0 ? '暂无日志' : logs.join('\n')}
        </div>
      </Modal>

      {host}
    </div>
  )
}
