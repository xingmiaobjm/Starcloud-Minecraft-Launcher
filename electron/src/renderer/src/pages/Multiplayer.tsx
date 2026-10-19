/// 联机页：内置陶瓦联机 + 第三方联机 SDK。
/// 设计：docs/designs/2026-09-21-multiplayer-sdk-design.md
///
/// 这一页只做两件事：把快照画出来、把点击翻译成一次桥接调用。
/// 状态来源有两个：进页拉一次 `multiplayer.state`，之后靠 `multiplayer.changed` 事件实时刷新。
/// 核心安装是长任务：调用只回 accepted，进度与终态分别走 `multiplayer.progress`
/// 和 `multiplayer.install.done` / `multiplayer.install.failed`。
import { useCallback, useEffect, useMemo, useState } from 'react'
import { onBridgeEvent, request } from '../bridge'
import type {
  MultiplayerProviderInfo,
  MultiplayerSnapshot
} from '../bridge'
import { EmptyState, Modal, PageShell, ProgressBar, SkeletonList, useToasts } from '../ui'

/** 阶段 key（与桥接 PhaseKey 一一对应）→ 中文文案。 */
const PHASE_LABEL: Record<string, string> = {
  idle: '空闲',
  hostScanning: '创建房间中',
  hostStarting: '房间启动中',
  hostReady: '房间已创建',
  guestConnecting: '连接房间中',
  guestEntering: '已进入虚拟局域网',
  guestReady: '联机成功',
  failed: '联机异常'
}

/** 阶段 → 状态点颜色：就绪绿、进行中黄、异常红、其余灰。 */
function phaseColor(phase: string): string {
  if (phase === 'hostReady' || phase === 'guestReady') return 'var(--success)'
  if (phase === 'failed') return 'var(--danger)'
  if (phase === 'idle') return 'var(--text-tertiary)'
  return 'var(--accent)'
}

/** 状态下那行"下一步要做什么"；没事可做时返回空串（整行不占位）。 */
function phaseHint(phase: string): string {
  switch (phase) {
    case 'hostScanning':
      return '请先在游戏内开启「对局域网开放」，核心会自动接上那个世界。'
    case 'guestConnecting':
      return '打洞进行中，稍等片刻，状态会自动更新。'
    case 'guestEntering':
      return '已打洞成功，可先在游戏里打开「多人游戏」。'
    case 'guestReady':
      return '打开游戏里的「多人游戏」，在局域网世界列表中进入房主的世界。'
    case 'failed':
      return '可以「回到空闲重试」，或展开下方日志看核心报了什么。'
    default:
      return ''
  }
}

const DIFFICULTY_LABEL: Record<string, string> = {
  easiest: '最简单',
  simple: '简单',
  medium: '中等',
  tough: '困难'
}

/** 节点地区：与 Windows 端 MultiplayerPage 的下拉保持一致。 */
const NODE_REGIONS: Array<{ value: string; label: string }> = [
  { value: 'auto', label: '自动' },
  { value: 'cn', label: '优先大陆节点' },
  { value: 'global', label: '仅国际节点' }
]

interface InstallProgress {
  stage: string
  bytes: number
  total: number
}

function formatSize(bytes: number): string {
  if (bytes <= 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1073741824) return `${(bytes / 1048576).toFixed(1)} MB`
  return `${(bytes / 1073741824).toFixed(2)} GB`
}

export default function MultiplayerPage() {
  const { push, host } = useToasts()

  const [snapshot, setSnapshot] = useState<MultiplayerSnapshot | undefined>()
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [nickname, setNickname] = useState('')
  const [room, setRoom] = useState('')
  const [joinOpen, setJoinOpen] = useState(false)
  const [joinCode, setJoinCode] = useState('')
  const [confirmStop, setConfirmStop] = useState(false)
  const [logs, setLogs] = useState<string[]>([])
  const [progress, setProgress] = useState<InstallProgress | undefined>()
  const [installing, setInstalling] = useState(false)

  // ── 数据：进页拉一次，之后全靠事件 ──────────────────────────────────

  const reload = useCallback(async () => {
    try {
      const snap = await request<MultiplayerSnapshot>('multiplayer.state')
      setSnapshot(snap)
      setNickname(snap.nickname)
      setError('')
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void reload()
  }, [reload])

  useEffect(() => {
    onBridgeEvent((event, params) => {
      if (event === 'multiplayer.changed') {
        // params 就是完整快照对象，直接换掉整份状态（无需重新拉）
        const snap = params as unknown as MultiplayerSnapshot
        setSnapshot(snap)
        setLoading(false)
      } else if (event === 'multiplayer.progress') {
        setProgress({
          stage: String(params.stage ?? '下载中…'),
          bytes: Number(params.bytes ?? 0),
          total: Number(params.total ?? 0)
        })
      } else if (event === 'multiplayer.install.done') {
        setInstalling(false)
        setProgress(undefined)
        push('success', '联机核心安装完成。')
        void reload()
      } else if (event === 'multiplayer.install.failed') {
        setInstalling(false)
        setProgress(undefined)
        push('error', `安装失败：${String(params.error ?? '未知错误')}`)
        void reload()
      }
    })
  }, [push, reload])

  const loadLogs = useCallback(async () => {
    try {
      const result = await request<{ logs: string[] }>('multiplayer.logs')
      setLogs(result.logs)
    } catch (e) {
      push('error', `日志读取失败：${String(e)}`)
    }
  }, [push])

  // ── 操作：把点击翻译成一次桥接调用 ──────────────────────────────────

  /** 统一的调用包装：忙 → 成功/失败提示 → 刷新快照。 */
  const run = useCallback(
    async (method: string, params: Record<string, unknown>, okText?: string) => {
      setBusy(true)
      setError('')
      try {
        const snap = await request<MultiplayerSnapshot>(method, params)
        if (snap?.providers) setSnapshot(snap)
        if (okText) push('success', okText)
        await loadLogs()
      } catch (e) {
        setError(String(e))
        push('error', String(e))
      } finally {
        setBusy(false)
      }
    },
    [push, loadLogs]
  )

  const core = snapshot?.core
  const state = snapshot?.state
  const phase = state?.phase ?? 'idle'
  const providers = useMemo(() => snapshot?.providers ?? [], [snapshot])

  const canPlay = core?.installed === true && !busy && !state?.isBusy
  const hasSession = core?.sessionRunning === true || phase !== 'idle'

  const installCore = async () => {
    setInstalling(true)
    setProgress({ stage: '正在准备安装…', bytes: 0, total: 0 })
    try {
      // 只回执 accepted，真正的进度/终态走事件
      await request<{ accepted: boolean }>('multiplayer.install')
      push('info', '已开始下载联机核心，进度会实时刷新。')
    } catch (e) {
      setInstalling(false)
      setProgress(undefined)
      push('error', String(e))
    }
  }

  const joinRoom = () => {
    const code = joinCode.trim()
    if (code.length === 0) return
    setJoinOpen(false)
    setJoinCode('')
    void run('multiplayer.join', { room: code }, '已提交进房请求。')
  }

  const phaseText = PHASE_LABEL[phase] ?? '未知状态'
  const hint = phaseHint(phase)
  const percent =
    progress && progress.total > 0
      ? Math.max(0, Math.min(100, (progress.bytes / progress.total) * 100))
      : undefined

  return (
    <>
      {/* 两栏布局：窄窗口降级成单栏（winui.css 里没有这一档，写在页面内） */}
      <style>{`.mp-cards{grid-template-columns:minmax(0,1fr)}
@media (min-width:1081px){.mp-cards{grid-template-columns:minmax(0,1.25fr) minmax(320px,0.85fr)}}`}</style>
      <PageShell
        title="联机"
        subtitle="内置陶瓦联机 + 第三方联机 SDK：先装核心，再建房或进房。"
        actions={
          <button
            className="btn btn-subtle"
            title="刷新"
            onClick={() => void reload()}
          >
            ↻
          </button>
        }
      >
        {loading && <SkeletonList rows={4} />}

        {!loading && error !== '' && (
          <div className="info info-danger" style={{ marginBottom: 12 }}>
            <span aria-hidden style={{ fontSize: 16 }}>✕</span>
            <div className="info-body" style={{ fontSize: 12 }}>
              {error}
            </div>
          </div>
        )}

        {!loading && snapshot == null && (
          <div className="card">
            <EmptyState
              title="暂时拿不到联机状态"
              hint="桥接进程可能还没起来，稍等一下再刷新。"
              action={
                <button className="btn" onClick={() => void reload()}>
                  ↻ 重新加载
                </button>
              }
            />
          </div>
        )}

        {!loading && snapshot != null && (
          <>
            <div
              style={{ display: 'grid', gap: 14, alignItems: 'start' }}
              className="mp-cards"
            >
              {/* ── 状态卡 ── */}              <div
                className="card"
                style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: 16 }}
              >
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span style={{ fontSize: 16 }}>⚯</span>
                  <span className="t-strong">联机状态</span>
                  <div style={{ flex: 1 }} />
                  {state?.isBusy === true && <div className="spinner" />}
                </div>

                {/* 阶段徽标：一点状态色 + 一行字，占位小但一眼可辨 */}
                <div
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: 10,
                    padding: '12px 14px',
                    borderRadius: 'var(--r-card)',
                    background: 'var(--surface-subtle)',
                    border: '1px solid var(--stroke-subtle)',
                    animation: 'fadeIn var(--t-enter) var(--e-enter) both'
                  }}
                >
                  <div
                    className="dot"
                    style={{
                      width: 10,
                      height: 10,
                      background: phaseColor(phase),
                      animation:
                        state?.isBusy === true ? 'pulse 1.4s infinite' : undefined
                    }}
                  />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="t-strong">{phaseText}</div>
                    {hint !== '' && <div className="t-cap">{hint}</div>}
                  </div>
                </div>

                <div
                  style={{
                    display: 'grid',
                    gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
                    gap: 8
                  }}
                >
                  {[
                    {
                      label: '核心版本',
                      value: core?.installed ? `v${core.version ?? '未知'}` : '未安装'
                    },
                    { label: '核心会话', value: core?.sessionRunning ? '运行中' : '已停止' },
                    { label: '对应地址', value: state?.address ?? '—' },
                    {
                      label: '难度',
                      value: state?.difficulty
                        ? (DIFFICULTY_LABEL[state.difficulty] ?? '未知')
                        : '—'
                    }
                  ].map((cell) => (
                    <div
                      key={cell.label}
                      style={{
                        padding: '9px 11px',
                        borderRadius: 'var(--r-card)',
                        background: 'var(--surface-subtle)',
                        border: '1px solid var(--stroke-subtle)',
                        minWidth: 0
                      }}
                    >
                      <div className="t-meta" style={{ display: 'block' }}>
                        {cell.label}
                      </div>
                      <div className="t-strong truncate">{cell.value}</div>
                    </div>
                  ))}
                </div>

                {phase === 'hostReady' && state?.room && (
                  <div
                    style={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: 10,
                      padding: '12px 14px',
                      borderRadius: 'var(--r-card)',
                      border: `1.2px dashed var(--accent)`,
                      background: 'color-mix(in srgb, var(--accent) 8%, transparent)'
                    }}
                  >
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <div className="t-meta" style={{ display: 'block' }}>
                        邀请码（发给好友）
                      </div>
                      <div
                        style={{
                          fontFamily: 'var(--font-mono)',
                          fontSize: 20,
                          fontWeight: 700,
                          letterSpacing: 3,
                          color: 'var(--accent)',
                          userSelect: 'all'
                        }}
                      >
                        {state.room}
                      </div>
                    </div>
                    <button
                      className="btn btn-subtle"
                      onClick={() => {
                        void navigator.clipboard
                          ?.writeText(state.room ?? '')
                          .then(() => push('success', '邀请码已复制。'))
                          .catch(() => push('error', '复制失败，请手动选中。'))
                      }}
                    >
                      复制
                    </button>
                  </div>
                )}

                {state?.players != null && state.players.length > 0 && (
                  <div>
                    <div className="t-cap" style={{ display: 'block', marginBottom: 6 }}>
                      房间成员（{state.players.length}）
                    </div>
                    {state.players.map((name, index) => (
                      <div
                        key={`${name}-${index}`}
                        className="row row-static"
                        style={{
                          padding: '7px 10px',
                          margin: '0 4px',
                          animationDelay: `${index * 39}ms`
                        }}
                      >
                        <div
                          className="dot"
                          style={{ width: 7, height: 7, background: 'var(--accent)' }}
                        />
                        <span className="t-body truncate">{name}</span>
                      </div>
                    ))}
                  </div>
                )}

                {(state?.players == null || state.players.length === 0) && phase === 'idle' && (
                  <EmptyState
                    title="还没有联机会话"
                    hint="先在下面填玩家名，然后创建房间或输入好友的邀请码。"
                  />
                )}

                {phase === 'failed' && (
                  <div className="info info-danger">
                    <span aria-hidden>✕</span>
                    <div className="info-body t-body">
                      联机异常：{state?.error ?? '未知错误'}
                    </div>
                  </div>
                )}

                {snapshot?.lastError != null && snapshot.lastError.length > 0 && (
                  <div className="info info-warning">
                    <span aria-hidden>⚠</span>
                    <div className="info-body t-body">
                      状态查询失败：{snapshot.lastError}
                    </div>
                  </div>
                )}
              </div>

              {/* ── 操作卡 ── */}
              <div
                className="card"
                style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: 16 }}
              >
                <span className="t-strong">操作</span>

                {core?.installed !== true && (
                  <>
                    <div className="t-body" style={{ opacity: 0.7 }}>
                      首次使用需下载陶瓦联机核心（约 8 MB，国内镜像优先）；核心由启动器在首次使用时从
                      官方 Releases 下载，不随安装包分发。
                    </div>
                    <button
                      className="btn btn-primary btn-lg"
                      disabled={installing}
                      onClick={() => void installCore()}
                    >
                      ↻ {installing ? '安装中…' : '安装联机核心'}
                    </button>
                    {installing && (
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                        <ProgressBar
                          value={percent}
                          indeterminate={percent === undefined}
                        />
                        <div className="t-meta">
                          {progress
                            ? `${progress.stage} · ${formatSize(progress.bytes)}${
                                progress.total > 0
                                  ? ` / ${formatSize(progress.total)}`
                                  : ''
                              }`
                            : '正在准备安装…'}
                        </div>
                      </div>
                    )}
                  </>
                )}

                <div>
                  <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
                    玩家名
                  </div>
                  <input
                    className="input"
                    style={{ width: '100%' }}
                    value={nickname}
                    maxLength={24}
                    placeholder="在房间里显示的名字"
                    onChange={(e) => setNickname(e.target.value)}
                    onBlur={() =>
                      nickname.trim().length > 0 &&
                      void run('multiplayer.setNickname', {
                        name: nickname.trim()
                      })
                    }
                  />
                </div>

                <div>
                  <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
                    节点地区
                  </div>
                  <select
                    className="input"
                    style={{ width: '100%' }}
                    value={snapshot?.nodeRegion ?? 'auto'}
                    onChange={(e) =>
                      void run('multiplayer.setNodeRegion', { region: e.target.value })
                    }
                  >
                    {NODE_REGIONS.map((r) => (
                      <option key={r.value} value={r.value}>
                        {r.label}
                      </option>
                    ))}
                  </select>
                </div>

                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <button
                    className="btn btn-primary btn-lg"
                    disabled={!canPlay}
                    onClick={() =>
                      void run(
                        'multiplayer.host',
                        {},
                        '已开始创建房间，等状态变成「房间已创建」后把邀请码发给好友。'
                      )
                    }
                  >
                    ▶ 创建房间
                  </button>

                  <button
                    className="btn"
                    disabled={!canPlay}
                    onClick={() => setJoinOpen(true)}
                  >
                    ⚯ 加入房间
                  </button>

                  <button
                    className="btn"
                    disabled={!canPlay || phase === 'idle'}
                    onClick={() => void run('multiplayer.idle', {}, '已回到空闲。')}
                  >
                    ↻ 回到空闲重试
                  </button>

                  {/* 破坏性操作：先走 Modal 二次确认再停会话 */}
                  <button
                    className="btn btn-subtle btn-danger"
                    disabled={!hasSession}
                    onClick={() => setConfirmStop(true)}
                  >
                    ✕ 停止联机会话
                  </button>
                </div>

                <div>
                  <div className="t-cap" style={{ display: 'block', marginBottom: 4 }}>
                    联机方案
                  </div>
                  {providers.length === 0 ? (
                    <div className="t-cap" style={{ opacity: 0.55 }}>
                      没有可用的联机方案。
                    </div>
                  ) : (
                    providers.map((p: MultiplayerProviderInfo, index: number) => {
                      const active = p.id === snapshot?.activeId
                      return (
                        <div
                          key={p.id}
                          className={`row${active ? ' row-active' : ''}`}
                          style={{
                            alignItems: 'flex-start',
                            padding: '9px 10px',
                            margin: '0 4px',
                            animationDelay: `${index * 39}ms`
                          }}
                          onClick={() => {
                            if (!active) void run('multiplayer.selectProvider', { id: p.id })
                          }}
                        >
                          <div
                            style={{
                              width: 15,
                              height: 15,
                              borderRadius: '50%',
                              border: `1.6px solid ${
                                active ? 'var(--accent)' : 'var(--stroke-strong)'
                              }`,
                              marginTop: 2,
                              flexShrink: 0,
                              display: 'flex',
                              alignItems: 'center',
                              justifyContent: 'center'
                            }}
                          >
                            {active && (
                              <div
                                style={{
                                  width: 7,
                                  height: 7,
                                  borderRadius: '50%',
                                  background: 'var(--accent)'
                                }}
                              />
                            )}
                          </div>
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <div className="t-body truncate" style={{ fontWeight: 600 }}>
                              {/* 第三方方案在名字后缀上署名，一眼看出这条是谁家的 */}
                              {p.displayName}
                              {!p.builtIn ? ` · ${p.vendor}` : ''}
                            </div>
                            {p.description != null && p.description.length > 0 && (
                              <div className="t-meta">{p.description}</div>
                            )}
                          </div>
                        </div>
                      )
                    })
                  )}
                </div>
              </div>
            </div>

            {/* ── 联机日志：需要时再看，不占首屏 ── */}
            <div
              className="card card-flush"
              style={{ height: 200, marginTop: 14, display: 'flex', flexDirection: 'column' }}
            >
              <div className="card-head">
                <div
                  className="dot"
                  style={{ background: hasSession ? 'var(--accent)' : 'var(--text-tertiary)' }}
                />
                <span className="card-title">联机日志</span>
                <div style={{ flex: 1 }} />
                <button className="btn btn-subtle" onClick={() => void loadLogs()}>
                  ↻ 刷新
                </button>
              </div>
              <div className="log" style={{ paddingTop: 0 }}>
                {logs.length === 0 ? (
                  <span style={{ opacity: 0.5 }}>暂无日志。</span>
                ) : (
                  logs.map((line, index) => <div key={index}>{line}</div>)
                )}
              </div>
            </div>
          </>
        )}
        {host}
      </PageShell>

      {/* ── 加入房间：邀请码收在弹窗里，点了才出现 ── */}
      <Modal
        open={joinOpen}
        title="加入房间"
        width={460}
        onClose={() => setJoinOpen(false)}
        footer={
          <>
            <button className="btn" onClick={() => setJoinOpen(false)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              disabled={joinCode.trim().length === 0 || busy}
              onClick={joinRoom}
            >
              加入
            </button>
          </>
        }
      >
        <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
          邀请码
        </div>
        <input
          className="input"
          style={{ width: '100%' }}
          value={joinCode}
          placeholder="房主给你的那串邀请码"
          onChange={(e) => setJoinCode(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') joinRoom()
          }}
        />
      </Modal>

      {/* 停止会话会断掉整个房间，属于不可逆操作：必须二次确认 */}
      <Modal
        open={confirmStop}
        title="停止联机会话？"
        width={460}
        onClose={() => setConfirmStop(false)}
        footer={
          <>
            <button className="btn" onClick={() => setConfirmStop(false)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: '#fff' }}
              disabled={busy}
              onClick={() => {
                setConfirmStop(false)
                void run('multiplayer.stop', {}, '已停止联机会话。')
              }}
            >
              确认停止
            </button>
          </>
        }
      >
        <div className="t-body">
          停止后房间会立刻关闭，正在游戏里的其他成员会被踢出。此操作不可撤销。
        </div>
      </Modal>
    </>
  )
}
