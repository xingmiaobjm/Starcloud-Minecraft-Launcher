/// 联机页：内置陶瓦联机 + 第三方联机 SDK。
/// 设计：docs/designs/2026-09-21-multiplayer-sdk-design.md
///
/// 这一页只做两件事：把快照画出来、把点击翻译成一次桥接调用。
/// 状态来源有两个：进页拉一次 `multiplayer.state`，之后靠 `multiplayer.changed` 事件实时刷新。
/// 核心安装是长任务：调用只回 accepted，进度与终态分别走 `multiplayer.progress`
/// 和 `multiplayer.install.done` / `multiplayer.install.failed`。
import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  ProgressBar,
  Select,
  Spinner,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  ArrowSyncRegular,
  ErrorCircleRegular,
  PeopleTeamRegular,
  PlayRegular,
  StopRegular
} from '@fluentui/react-icons'
import { onBridgeEvent, request } from '../bridge'
import type {
  MultiplayerProviderInfo,
  MultiplayerSnapshot
} from '../bridge'
import { brandYellow, brandYellowHover, motion, radius } from '../theme'
import { EmptyState, PageShell, SkeletonList, useToasts } from '../ui'

const useStyles = makeStyles({
  cards: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.25fr) minmax(320px, 0.85fr)',
    gap: '14px',
    alignItems: 'start',
    '@media (max-width: 1080px)': { gridTemplateColumns: 'minmax(0, 1fr)' }
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px'
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px'
  },
  // 阶段徽标：一点状态色 + 一行字，占位小但一眼可辨
  phaseRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '12px 14px',
    borderRadius: `${radius.card}px`,
    background: 'rgba(255,255,255,0.04)',
    border: '1px solid rgba(255,255,255,0.07)',
    animation: `fadeIn ${motion.normal}ms ${motion.ease} both`
  },
  phaseDot: {
    width: '10px',
    height: '10px',
    borderRadius: '50%',
    flexShrink: 0
  },
  metaGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
    gap: '8px'
  },
  metaCell: {
    padding: '9px 11px',
    borderRadius: `${radius.card}px`,
    background: 'rgba(255,255,255,0.035)',
    border: '1px solid rgba(255,255,255,0.06)'
  },
  // 邀请码：等宽 + 可选中，比普通文本更像"给人念的码"
  roomBox: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '12px 14px',
    borderRadius: `${radius.card}px`,
    border: `1.2px dashed ${brandYellow}`,
    background: 'rgba(255,194,31,0.08)'
  },
  roomCode: {
    fontFamily: 'Consolas, Menlo, monospace',
    fontSize: '20px',
    fontWeight: 700,
    letterSpacing: '3px',
    color: brandYellow,
    userSelect: 'all' as const,
    flex: 1,
    minWidth: 0
  },
  playerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '7px 10px',
    margin: '0 4px',
    borderRadius: `${radius.row}px`,
    background: 'rgba(255,255,255,0.035)',
    animation: `rowIn ${motion.normal}ms ${motion.easeOut} both`,
    transition: `background ${motion.fast}ms ${motion.ease}`
  },
  playerDot: {
    width: '7px',
    height: '7px',
    borderRadius: '50%',
    background: brandYellow,
    flexShrink: 0
  },
  providerRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '10px',
    padding: '9px 10px',
    margin: '0 4px',
    borderRadius: `${radius.row}px`,
    cursor: 'pointer',
    border: '1.2px solid transparent',
    transition: `background ${motion.fast}ms ${motion.ease}, border-color ${motion.fast}ms ${motion.ease}`,
    animation: `rowIn ${motion.normal}ms ${motion.easeOut} both`
  },
  providerActive: {
    background: 'rgba(255,194,31,0.14)',
    border: `1.2px solid ${brandYellow}`
  },
  providerRadio: {
    width: '15px',
    height: '15px',
    borderRadius: '50%',
    border: '1.6px solid rgba(255,255,255,0.35)',
    marginTop: '2px',
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center'
  },
  providerRadioOn: {
    borderTopColor: brandYellow,
    borderRightColor: brandYellow,
    borderBottomColor: brandYellow,
    borderLeftColor: brandYellow
  },
  providerRadioDot: {
    width: '7px',
    height: '7px',
    borderRadius: '50%',
    background: brandYellow
  },
  logCard: {
    height: '200px',
    display: 'flex',
    flexDirection: 'column'
  },
  logHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '10px 16px'
  },
  logBody: {
    flex: 1,
    overflowY: 'auto',
    padding: '8px 14px',
    fontFamily: 'Consolas, Menlo, monospace',
    fontSize: '11.5px',
    lineHeight: '1.5',
    whiteSpace: 'pre-wrap'
  },
  // 主操作：品牌黄实心，和其它次要按钮拉开层级
  primaryButton: {
    background: brandYellow,
    color: '#1b1b1b',
    fontWeight: 700,
    border: 'none',
    transition: `background ${motion.fast}ms ${motion.ease}, transform ${motion.fast}ms ${motion.ease}, box-shadow ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: brandYellowHover, color: '#1b1b1b' },
    ':active': { transform: 'scale(0.985)' }
  },
  primaryGlow: { boxShadow: `0 0 0 0 rgba(255,194,31,0.45)` },
  actions: { display: 'flex', flexDirection: 'column', gap: '8px' },
  dot: { width: '8px', height: '8px', borderRadius: '50%', flexShrink: 0 }
})

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
  if (phase === 'hostReady' || phase === 'guestReady') return '#3FBF6F'
  if (phase === 'failed') return '#E04B4B'
  if (phase === 'idle') return '#8A8A8A'
  return brandYellow
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
  const styles = useStyles()
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
      <PageShell
        title="联机"
        subtitle="内置陶瓦联机 + 第三方联机 SDK：先装核心，再建房或进房。"
        actions={
          <Button
            appearance="subtle"
            icon={<ArrowSyncRegular />}
            onClick={() => void reload()}
          />
        }
      >
        {loading && <SkeletonList rows={4} />}

        {!loading && error !== '' && (
          <MessageBar intent="error" style={{ marginBottom: '12px' }}>
            <ErrorCircleRegular />
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}

        {!loading && snapshot == null && (
          <Card>
            <EmptyState
              title="暂时拿不到联机状态"
              hint="桥接进程可能还没起来，稍等一下再刷新。"
              action={
                <Button icon={<ArrowSyncRegular />} onClick={() => void reload()}>
                  重新加载
                </Button>
              }
            />
          </Card>
        )}

        {!loading && snapshot != null && (
          <>
            <div className={styles.cards}>
              {/* ── 状态卡 ── */}
              <Card className={styles.card}>
                <div className={styles.cardHeader}>
                  <PeopleTeamRegular />
                  <Text size={400} weight="semibold">
                    联机状态
                  </Text>
                  <div style={{ flex: 1 }} />
                  {state?.isBusy === true && <Spinner size="tiny" />}
                </div>

                <div className={styles.phaseRow}>
                  <div
                    className={styles.phaseDot}
                    style={{
                      background: phaseColor(phase),
                      animation:
                        state?.isBusy === true ? 'pulse 1.4s infinite' : undefined
                    }}
                  />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text size={300} weight="semibold">
                      {phaseText}
                    </Text>
                    {hint !== '' && (
                      <Text size={200} style={{ opacity: 0.7, display: 'block' }}>
                        {hint}
                      </Text>
                    )}
                  </div>
                </div>

                <div className={styles.metaGrid}>
                  <div className={styles.metaCell}>
                    <Text size={100} style={{ opacity: 0.55, display: 'block' }}>
                      核心版本
                    </Text>
                    <Text size={300} weight="semibold">
                      {core?.installed ? `v${core.version ?? '未知'}` : '未安装'}
                    </Text>
                  </div>
                  <div className={styles.metaCell}>
                    <Text size={100} style={{ opacity: 0.55, display: 'block' }}>
                      核心会话
                    </Text>
                    <Text size={300} weight="semibold">
                      {core?.sessionRunning ? '运行中' : '已停止'}
                    </Text>
                  </div>
                  <div className={styles.metaCell}>
                    <Text size={100} style={{ opacity: 0.55, display: 'block' }}>
                      对应地址
                    </Text>
                    <Text size={300} weight="semibold" truncate>
                      {state?.address ?? '—'}
                    </Text>
                  </div>
                  <div className={styles.metaCell}>
                    <Text size={100} style={{ opacity: 0.55, display: 'block' }}>
                      难度
                    </Text>
                    <Text size={300} weight="semibold">
                      {state?.difficulty
                        ? (DIFFICULTY_LABEL[state.difficulty] ?? '未知')
                        : '—'}
                    </Text>
                  </div>
                </div>

                {phase === 'hostReady' && state?.room && (
                  <div className={styles.roomBox}>
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <Text size={100} style={{ opacity: 0.6, display: 'block' }}>
                        邀请码（发给好友）
                      </Text>
                      <div className={styles.roomCode}>{state.room}</div>
                    </div>
                    <Button
                      appearance="subtle"
                      size="small"
                      onClick={() => {
                        void navigator.clipboard
                          ?.writeText(state.room ?? '')
                          .then(() => push('success', '邀请码已复制。'))
                          .catch(() => push('error', '复制失败，请手动选中。'))
                      }}
                    >
                      复制
                    </Button>
                  </div>
                )}

                {state?.players != null && state.players.length > 0 && (
                  <div>
                    <Text size={200} style={{ opacity: 0.6, display: 'block', marginBottom: '6px' }}>
                      房间成员（{state.players.length}）
                    </Text>
                    {state.players.map((name, index) => (
                      <div
                        key={`${name}-${index}`}
                        className={styles.playerRow}
                        style={{ animationDelay: `${index * motion.stagger}ms` }}
                      >
                        <div className={styles.playerDot} />
                        <Text size={200} truncate>
                          {name}
                        </Text>
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
                  <MessageBar intent="error">
                    <ErrorCircleRegular />
                    <MessageBarBody>
                      联机异常：{state?.error ?? '未知错误'}
                    </MessageBarBody>
                  </MessageBar>
                )}

                {snapshot?.lastError != null && snapshot.lastError.length > 0 && (
                  <MessageBar intent="warning">
                    <MessageBarBody>状态查询失败：{snapshot.lastError}</MessageBarBody>
                  </MessageBar>
                )}
              </Card>

              {/* ── 操作卡 ── */}
              <Card className={styles.card}>
                <Text size={400} weight="semibold">
                  操作
                </Text>

                {core?.installed !== true && (
                  <>
                    <Text size={200} style={{ opacity: 0.7 }}>
                      首次使用需下载陶瓦联机核心（约 8 MB，国内镜像优先）；核心由启动器在首次使用时从
                      官方 Releases 下载，不随安装包分发。
                    </Text>
                    <button
                      className={[styles.primaryButton, styles.primaryGlow].join(' ')}
                      style={{
                        height: '40px',
                        borderRadius: `${radius.control}px`,
                        cursor: installing ? 'default' : 'pointer',
                        opacity: installing ? 0.6 : 1
                      }}
                      disabled={installing}
                      onClick={() => void installCore()}
                    >
                      <div
                        style={{
                          display: 'flex',
                          alignItems: 'center',
                          justifyContent: 'center',
                          gap: '8px'
                        }}
                      >
                        <ArrowSyncRegular />
                        {installing ? '安装中…' : '安装联机核心'}
                      </div>
                    </button>
                    {installing && (
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                        <ProgressBar
                          // value 传 undefined 即不定量进度（这版 Fluent 没有 indeterminate 属性）
                          value={percent}
                          thickness="large"
                          style={{ color: brandYellow }}
                        />
                        <Text size={100} style={{ opacity: 0.65 }}>
                          {progress
                            ? `${progress.stage} · ${formatSize(progress.bytes)}${
                                progress.total > 0
                                  ? ` / ${formatSize(progress.total)}`
                                  : ''
                              }`
                            : '正在准备安装…'}
                        </Text>
                      </div>
                    )}
                  </>
                )}

                <Field label="玩家名" size="small">
                  <Input
                    value={nickname}
                    maxLength={24}
                    placeholder="在房间里显示的名字"
                    onChange={(_, data) => setNickname(data.value)}
                    onBlur={() =>
                      nickname.trim().length > 0 &&
                      void run('multiplayer.setNickname', {
                        name: nickname.trim()
                      })
                    }
                  />
                </Field>

                <Field label="节点地区" size="small">
                  <Select
                    size="small"
                    value={snapshot?.nodeRegion ?? 'auto'}
                    onChange={(_, data) =>
                      void run('multiplayer.setNodeRegion', {
                        region: String(data.value)
                      })
                    }
                  >
                    {NODE_REGIONS.map((r) => (
                      <Option key={r.value} value={r.value} text={r.label}>
                        {r.label}
                      </Option>
                    ))}
                  </Select>
                </Field>

                <div className={styles.actions}>
                  <button
                    className={[
                      styles.primaryButton,
                      canPlay ? styles.primaryGlow : ''
                    ]
                      .filter(Boolean)
                      .join(' ')}
                    style={{
                      height: '40px',
                      borderRadius: `${radius.control}px`,
                      cursor: canPlay ? 'pointer' : 'default',
                      opacity: canPlay ? 1 : 0.45
                    }}
                    disabled={!canPlay}
                    onClick={() =>
                      void run(
                        'multiplayer.host',
                        {},
                        '已开始创建房间，等状态变成「房间已创建」后把邀请码发给好友。'
                      )
                    }
                  >
                    <div
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'center',
                        gap: '8px'
                      }}
                    >
                      <PlayRegular />
                      创建房间
                    </div>
                  </button>

                  <Button
                    appearance="outline"
                    icon={<PeopleTeamRegular />}
                    disabled={!canPlay}
                    onClick={() => setJoinOpen(true)}
                  >
                    加入房间
                  </Button>

                  <Button
                    appearance="outline"
                    icon={<ArrowSyncRegular />}
                    disabled={!canPlay || phase === 'idle'}
                    onClick={() => void run('multiplayer.idle', {}, '已回到空闲。')}
                  >
                    回到空闲重试
                  </Button>

                  {/* 破坏性操作：先走 Dialog 二次确认再停会话 */}
                  <Button
                    appearance="subtle"
                    icon={<StopRegular />}
                    style={{ color: '#FF9A9A' }}
                    disabled={!hasSession}
                    onClick={() => setConfirmStop(true)}
                  >
                    停止联机会话
                  </Button>
                </div>

                <div>
                  <Text size={200} style={{ opacity: 0.6, display: 'block', marginBottom: '4px' }}>
                    联机方案
                  </Text>
                  {providers.length === 0 ? (
                    <Text size={200} style={{ opacity: 0.55 }}>
                      没有可用的联机方案。
                    </Text>
                  ) : (
                    providers.map((p: MultiplayerProviderInfo, index: number) => {
                      const active = p.id === snapshot?.activeId
                      return (
                        <div
                          key={p.id}
                          className={[styles.providerRow, active ? styles.providerActive : '']
                            .filter(Boolean)
                            .join(' ')}
                          style={{ animationDelay: `${index * motion.stagger}ms` }}
                          onClick={() => {
                            if (!active) void run('multiplayer.selectProvider', { id: p.id })
                          }}
                        >
                          <div
                            className={[styles.providerRadio, active ? styles.providerRadioOn : '']
                              .filter(Boolean)
                              .join(' ')}
                          >
                            {active && <div className={styles.providerRadioDot} />}
                          </div>
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <Text size={200} weight="semibold" truncate>
                              {/* 第三方方案在名字后缀上署名，一眼看出这条是谁家的 */}
                              {p.displayName}
                              {!p.builtIn ? ` · ${p.vendor}` : ''}
                            </Text>
                            {p.description != null && p.description.length > 0 && (
                              <Text
                                size={100}
                                style={{ opacity: 0.6, display: 'block' }}
                              >
                                {p.description}
                              </Text>
                            )}
                          </div>
                        </div>
                      )
                    })
                  )}
                </div>
              </Card>
            </div>

            {/* ── 联机日志：需要时再看，不占首屏 ── */}
            <Card className={styles.logCard} style={{ marginTop: '14px' }}>
              <div className={styles.logHeader}>
                <div
                  className={styles.dot}
                  style={{ background: hasSession ? brandYellow : '#8A8A8A' }}
                />
                <Text size={300} weight="semibold">
                  联机日志
                </Text>
                <div style={{ flex: 1 }} />
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<ArrowSyncRegular />}
                  onClick={() => void loadLogs()}
                >
                  刷新
                </Button>
              </div>
              <div className={styles.logBody}>
                {logs.length === 0 ? (
                  <span style={{ opacity: 0.5 }}>暂无日志。</span>
                ) : (
                  logs.map((line, index) => <div key={index}>{line}</div>)
                )}
              </div>
            </Card>
          </>
        )}
      </PageShell>

      {/* ── 加入房间：邀请码收在弹窗里，点了才出现 ── */}
      <Dialog
        open={joinOpen}
        onOpenChange={(_, data) => setJoinOpen(data.open)}
        modalType="modal"
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>加入房间</DialogTitle>
            <DialogContent>
              <Field label="邀请码" size="small">
                <Input
                  value={joinCode}
                  placeholder="房主给你的那串邀请码"
                  onChange={(_, data) => setJoinCode(data.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') joinRoom()
                  }}
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setJoinOpen(false)}>
                取消
              </Button>
              <Button
                appearance="primary"
                className={styles.primaryButton}
                disabled={joinCode.trim().length === 0 || busy}
                onClick={joinRoom}
              >
                加入
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {/* 停止会话会断掉整个房间，属于不可逆操作：必须二次确认 */}
      <Dialog
        open={confirmStop}
        onOpenChange={(_, data) => setConfirmStop(data.open)}
        modalType="alert"
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>停止联机会话？</DialogTitle>
            <DialogContent>
              <Text size={200}>
                停止后房间会立刻关闭，正在游戏里的其他成员会被踢出。此操作不可撤销。
              </Text>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setConfirmStop(false)}>
                取消
              </Button>
              <Button
                appearance="primary"
                style={{ background: '#E04B4B', color: '#ffffff', fontWeight: 700 }}
                disabled={busy}
                onClick={() => {
                  setConfirmStop(false)
                  void run('multiplayer.stop', {}, '已停止联机会话。')
                }}
              >
                确认停止
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {host}
    </>
  )
}
