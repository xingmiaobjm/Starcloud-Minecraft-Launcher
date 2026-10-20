/// 账户页：对齐 Win 端 AccountsPage + AccountAddPage ——
/// 顶部云账号卡（Starcloud ID，未接内核时显示未登录）+ 游戏账户区（皮肤头像、类型、首选芯片）。
///
/// 「添加账户」对齐 Win 端 AccountAddPage 的三条互不替代的身份路径：
/// · 微软账户 = **设备码流程**（拿一次性验证码 → 用系统浏览器打开验证站点 → 轮询授权），
///   不是密码框。轮询节奏由内核返回的 interval 决定，组件卸载 / 弹窗关闭即停。
/// · 外置账户 = Yggdrasil 账密（认证服务器地址 + 用户名 + 密码），
///   失败时**原样显示后端错误**（内核已经把 Yggdrasil 的错误原文带回来了）。
/// · 离线账户 = 只要玩家名，不参与任何在线验证。
import { useCallback, useEffect, useRef, useState } from 'react'

import { nativeApi, request } from '../bridge'
import type {
  AccountList,
  DeviceCodeSession,
  ExternalLoginResult,
  GameAccount,
  MicrosoftPollResult
} from '../bridge'
import { EmptyState, Modal, Segmented, SkeletonList, useToasts } from '../ui'

/** 账户头像（与 Win 端 AvatarHelper 同规则：微软用 UUID，其它用用户名）。 */
function avatarUrl(a: GameAccount): string {
  const id = a.type === 'microsoft' ? a.uuid : a.name
  return `https://minotar.net/helm/${encodeURIComponent(id)}/128`
}

const TYPE_LABEL: Record<string, string> = {
  microsoft: '微软账户',
  external: '外置账户',
  offline: '离线账户'
}

type AddKind = 'microsoft' | 'external' | 'offline'

const ADD_KIND_LABEL: Record<AddKind, string> = {
  microsoft: '微软账户',
  external: '外置账户',
  offline: '离线账户'
}

/** 设备码轮询的最短间隔（内核契约的 interval 是秒；Win 端同样按它 sleep）。 */
const MIN_POLL_INTERVAL_SEC = 3

export default function AccountsPage(): JSX.Element {
  const { push, host } = useToasts()
  const [accounts, setAccounts] = useState<GameAccount[]>([])
  const [current, setCurrent] = useState<string | undefined>()
  const [loading, setLoading] = useState(true)
  const [failedAvatars, setFailedAvatars] = useState<Set<string>>(new Set())
  const [pendingRemove, setPendingRemove] = useState<GameAccount | null>(null)

  // ── 添加账户弹窗 ──
  const [addOpen, setAddOpen] = useState(false)
  const [addKind, setAddKind] = useState<AddKind>('microsoft')
  const [addBusy, setAddBusy] = useState(false)
  const [addError, setAddError] = useState('')

  // 微软设备码
  const [device, setDevice] = useState<DeviceCodeSession | null>(null)
  const [msStatus, setMsStatus] = useState('')
  const [codeCopied, setCodeCopied] = useState(false)

  // setInterval 的回调每 interval 秒才跑一次，它闭包里的 setAddError /
  // closeAdd 都是**第一次渲染时**的引用；closeAdd 又会去 setDevice(null)……
  // 直接用就会写到早就卸载/已重置的状态上（React 18 只是警告，行为不可预期）。
  // 所以：轮询定时器只碰 ref 指向的最新函数与状态。
  const addErrorRef = useRef('')
  const closeAddRef = useRef<() => void>(() => undefined)
  const loadRef = useRef<() => Promise<void>>(async () => undefined)
  const pushToastRef = useRef<(kind: 'success' | 'error' | 'info' | 'warning', text: string) => void>(
    () => undefined
  )
  /** 设备码代数：换会话后旧定时器的回调靠它自我丢弃（设备码一次性的，不能混着用）。 */
  const generationRef = useRef(0)
  /** 当前轮询定时器句柄（null = 没有在轮询）。 */
  const pollRef = useRef<number | null>(null)

  // 外置
  const [extUrl, setExtUrl] = useState('')
  const [extName, setExtName] = useState('')
  const [extPwd, setExtPwd] = useState('')

  // 离线
  const [draftName, setDraftName] = useState('')

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const data = await request<AccountList>('accounts.list')
      setAccounts(data.accounts)
      setCurrent(data.current)
    } catch (e) {
      push('error', `读取账户失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  // 组件卸载时停掉轮询：设备码在微软侧有有效期，但更重要的是别在页面
  // 已经不在时继续往桥接灌请求。
  useEffect(
    () => () => {
      if (pollRef.current !== null) window.clearInterval(pollRef.current)
      pollRef.current = null
    },
    []
  )

  const stopPolling = useCallback((): void => {
    if (pollRef.current !== null) window.clearInterval(pollRef.current)
    pollRef.current = null
  }, [])

  const nameValid = /^[A-Za-z0-9_]{3,16}$/.test(draftName.trim())
  const extReady = extUrl.trim() !== '' && extName.trim() !== '' && extPwd !== ''

  function closeAdd(): void {
    stopPolling()
    generationRef.current += 1 // 让在途的那一拍轮询也作废
    setAddOpen(false)
    setAddBusy(false)
    setAddError('')
    addErrorRef.current = ''
    setDevice(null)
    setMsStatus('')
    setCodeCopied(false)
    setDraftName('')
    setExtUrl('')
    setExtName('')
    setExtPwd('')
  }

  /** 轮询所需的最新引用；closeAdd 定义在后面，这里随每次渲染同步。 */
  function syncRefs(): void {
    closeAddRef.current = closeAdd
    loadRef.current = load
    pushToastRef.current = push
  }
  syncRefs()

  /** 设备码轮询：按 interval 秒调 auth.microsoft.poll，pending 继续，success 收尾。 */
  const startPolling = useCallback((session: DeviceCodeSession, generation: number): void => {
    const intervalMs = Math.max(session.interval, MIN_POLL_INTERVAL_SEC) * 1000
    setMsStatus('等待授权…')
    const timer = window.setInterval(() => {
      void (async (): Promise<void> => {
        // 会话已被换掉 / 弹窗关掉：这一拍直接收工
        if (generation !== generationRef.current) return
        try {
          const r = await request<MicrosoftPollResult>('auth.microsoft.poll', {
            deviceCode: session.deviceCode
          })
          if (generation !== generationRef.current) return
          if (r.state === 'success') {
            stopPolling()
            pushToastRef.current(
              'success',
              r.account !== undefined
                ? `Microsoft 账户已登录：${r.account.name}`
                : 'Microsoft 账户已登录'
            )
            closeAddRef.current()
            await loadRef.current()
            return
          }
          // pending：内核给了原因（等待用户授权 / 降速轮询），原样透出
          setMsStatus(r.message !== undefined && r.message !== '' ? r.message : '等待授权…')
        } catch (e) {
          // 桥接把终态错误作为请求失败抛出（deviceCode 失效 / 用户拒绝授权 /
          // 认证链失败），这里直接终止并把原因给用户 —— 不做静默重试：
          // 设备码一次性的，重试拿到的只会是「设备码已失效」这种二次错误。
          if (generation !== generationRef.current) return
          stopPolling()
          setMsStatus('')
          const message = `登录失败：${String(e)}`
          addErrorRef.current = message
          setAddError(message)
        }
      })()
    }, intervalMs)
    pollRef.current = timer
  }, [stopPolling])

  async function startMicrosoft(): Promise<void> {
    setAddBusy(true)
    setAddError('')
    try {
      const session = await request<DeviceCodeSession>('auth.microsoft.start')
      const generation = generationRef.current + 1
      generationRef.current = generation
      setDevice(session)
      startPolling(session, generation)
    } catch (e) {
      setAddError(`获取设备码失败：${String(e)}`)
    } finally {
      setAddBusy(false)
    }
  }

  /** 打开验证站点（Win 端：复制验证码并打开验证网站）。 */
  async function openVerification(): Promise<void> {
    if (device === null) return
    try {
      // 验证码先落到剪贴板，用户切到浏览器就能直接粘贴
      await navigator.clipboard?.writeText(device.userCode)
      setCodeCopied(true)
    } catch {
      // 剪贴板不可用（权限/无头）不阻断：下面照样能打开网页手动输入
    }
    try {
      const api = nativeApi()
      if (api === null) {
        push('info', `请手动打开 ${device.verificationUri} 并输入代码 ${device.userCode}`)
        return
      }
      await api.openExternal(device.verificationUri)
    } catch (e) {
      setAddError(`打开验证网站失败：${String(e)}`)
    }
  }

  async function loginExternal(): Promise<void> {
    if (!extReady) return
    setAddBusy(true)
    setAddError('')
    try {
      const r = await request<ExternalLoginResult>('auth.yggdrasil.login', {
        url: extUrl.trim(),
        username: extName.trim(),
        password: extPwd
      })
      // 后端原文直出：Yggdrasil 的失败原因（密码错误 / 服务器不存在 / 封禁）
      // 已经由内核 message 带回来，这里不做改写，用户要看到的就是真实原因。
      push('success', `第三方登录 ${r.account.name} 已添加`)
      setExtPwd('')
      closeAdd()
      await load()
    } catch (e) {
      setAddError(`登录失败：${String(e)}`)
    } finally {
      setAddBusy(false)
    }
  }

  async function addOffline(): Promise<void> {
    if (!nameValid) return
    setAddBusy(true)
    setAddError('')
    try {
      await request('accounts.addOffline', { name: draftName.trim() })
      push('success', `离线账户 ${draftName.trim()} 已添加`)
      closeAdd()
      await load()
    } catch (e) {
      setAddError(`添加失败：${String(e)}`)
    } finally {
      setAddBusy(false)
    }
  }

  async function setAsCurrent(a: GameAccount): Promise<void> {
    try {
      await request('accounts.setCurrent', { uuid: a.uuid })
      push('success', `已将 ${a.name} 设为当前账户`)
      await load()
    } catch (e) {
      push('error', String(e))
    }
  }

  async function remove(a: GameAccount): Promise<void> {
    try {
      await request('accounts.remove', { uuid: a.uuid })
      push('success', `已移除账户 ${a.name}`)
      setPendingRemove(null)
      await load()
    } catch (e) {
      push('error', `移除失败：${String(e)}`)
    }
  }

  // 同步给设备码轮询用的 ref（渲染期纯赋值，无副作用）
  syncRefs()

  return (
    <div className="page">
      <div className="page-body" style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        {/* 云账号卡（Win 端：Starcloud ID） */}
        <div className="card" style={{ padding: '18px 22px' }}>
          <div style={{ display: 'flex', gap: 14, alignItems: 'center' }}>
            <div
              style={{
                width: 44,
                height: 44,
                borderRadius: '50%',
                background: 'var(--accent)',
                color: 'var(--text-on-accent)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                fontWeight: 700,
                fontSize: 18,
                flexShrink: 0
              }}
            >
              ?
            </div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div className="t-strong">未登录 Starcloud ID</div>
              <div className="t-cap">登录后可使用云端模型、邀请与时刻等在线能力</div>
            </div>
            <button
              className="btn btn-primary"
              onClick={() => push('info', 'Starcloud ID 登录：内核能力接入后可用')}
            >
              登录
            </button>
          </div>
        </div>

        {/* 游戏账户区 */}
        <div>
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              marginBottom: 12
            }}
          >
            <span
              style={{
                width: 4,
                height: 18,
                borderRadius: 2,
                background: 'var(--accent)',
                flexShrink: 0
              }}
            />
            <span className="t-strong" style={{ fontSize: 16 }}>
              游戏账户
            </span>
            <span className="t-cap">{accounts.length} 个账户</span>
            <div style={{ flex: 1 }} />
            <button
              className="btn btn-primary"
              onClick={() => {
                setAddOpen(true)
                setAddError('')
              }}
            >
              添加账户
            </button>
          </div>

          {loading && <SkeletonList rows={2} avatar />}
          {!loading && accounts.length === 0 && (
            <EmptyState
              title="还没有游戏账户"
              hint="点右上角「添加账户」用微软账户登录，或建一个离线账户。"
              action={
                <button className="btn btn-primary" onClick={() => setAddOpen(true)}>
                  添加账户
                </button>
              }
            />
          )}

          {accounts.map((a) => {
            const isCurrent = a.uuid === current || a.isDefault === true
            const failed = failedAvatars.has(a.uuid)
            return (
              <div
                key={a.uuid}
                className="row"
                style={{ minHeight: 72 }}
                onClick={() => void setAsCurrent(a)}
              >
                <div
                  className="icon32"
                  style={{ width: 48, height: 48, borderRadius: 8, overflow: 'hidden' }}
                >
                  {failed ? (
                    <span style={{ fontSize: 18 }}>{a.name[0]?.toUpperCase()}</span>
                  ) : (
                    <img
                      src={avatarUrl(a)}
                      alt=""
                      width={48}
                      height={48}
                      style={{ objectFit: 'cover' }}
                      onError={() =>
                        setFailedAvatars((prev) => new Set(prev).add(a.uuid))
                      }
                    />
                  )}
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <span className="t-strong truncate">{a.name}</span>
                    {isCurrent && <span className="chip chip-accent">首选</span>}
                    {a.hasSession === false && <span className="chip chip-danger">需重新登录</span>}
                  </div>
                  <div className="t-cap">
                    {TYPE_LABEL[a.type] ?? a.type}
                    {a.serverName !== undefined && a.serverName !== ''
                      ? ` · ${a.serverName}`
                      : ''}
                  </div>
                </div>
                {a.skinId !== undefined && a.skinId !== '' && (
                  <div className="t-cap" style={{ textAlign: 'right' }}>
                    皮肤
                    <br />
                    {a.skinId}
                  </div>
                )}
                <button
                  className="btn btn-subtle btn-sm btn-danger"
                  title="移除账户"
                  onClick={(e) => {
                    e.stopPropagation()
                    setPendingRemove(a)
                  }}
                >
                  移除
                </button>
                <span style={{ opacity: 0.4 }}>›</span>
              </div>
            )
          })}
        </div>
      </div>

      {/* 添加账户：左选身份，右就地填表（对齐 Win 端 AccountAddPage） */}
      <Modal
        open={addOpen}
        title="添加账户"
        onClose={closeAdd}
        width={560}
        footer={
          addKind === 'microsoft' ? (
            <>
              <button className="btn" onClick={closeAdd}>
                取消
              </button>
              {device === null ? (
                <button
                  className="btn btn-primary"
                  disabled={addBusy}
                  onClick={() => void startMicrosoft()}
                >
                  {addBusy ? '获取中…' : '开始登录'}
                </button>
              ) : (
                <button className="btn" onClick={closeAdd}>
                  取消登录
                </button>
              )}
            </>
          ) : (
            <>
              <button className="btn" onClick={closeAdd}>
                取消
              </button>
              <button
                className="btn btn-primary"
                disabled={addKind === 'offline' ? !nameValid || addBusy : !extReady || addBusy}
                onClick={() => {
                  if (addKind === 'offline') void addOffline()
                  else void loginExternal()
                }}
              >
                {addBusy ? '处理中…' : addKind === 'offline' ? '添加' : '登录'}
              </button>
            </>
          )
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Segmented<AddKind>
            value={addKind}
            options={[
              { value: 'microsoft', label: ADD_KIND_LABEL.microsoft },
              { value: 'external', label: ADD_KIND_LABEL.external },
              { value: 'offline', label: ADD_KIND_LABEL.offline }
            ]}
            onChange={(v) => {
              setAddKind(v)
              setAddError('')
            }}
          />

          {addError !== '' && (
            <div className="info info-danger">
              <span aria-hidden>✕</span>
              <div className="info-body">{addError}</div>
            </div>
          )}

          {/* ── 微软：设备码（不是密码框） ── */}
          {addKind === 'microsoft' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {device === null ? (
                <div className="t-body">
                  使用一次性验证代码登录 Microsoft 账户：点「开始登录」拿到代码后，
                  用浏览器打开验证网站输入代码完成授权，启动器会自动把账户加进来。
                </div>
              ) : (
                <div
                  style={{
                    display: 'flex',
                    flexDirection: 'column',
                    alignItems: 'center',
                    gap: 10,
                    padding: '6px 0'
                  }}
                >
                  <div className="t-body" style={{ textAlign: 'center' }}>
                    使用一次性验证代码登录到你的 Microsoft 账户
                  </div>
                  <div
                    className="t-mono"
                    style={{
                      fontSize: 30,
                      fontWeight: 600,
                      letterSpacing: 3,
                      color: 'var(--text-primary)',
                      userSelect: 'text'
                    }}
                  >
                    {device.userCode}
                  </div>
                  <div className="t-cap" style={{ textAlign: 'center' }}>
                    使用浏览器打开 {device.verificationUri}，输入以上代码并按照网页提示完成授权。
                    完成后启动器将自动把账户添加至启动器内。
                  </div>
                  <button
                    className="btn btn-primary"
                    style={{ width: '100%' }}
                    onClick={() => void openVerification()}
                  >
                    {codeCopied ? '已复制并打开验证网站' : '复制并打开验证网站'}
                  </button>
                  <div
                    style={{ display: 'flex', alignItems: 'center', gap: 8, color: 'var(--text-secondary)' }}
                  >
                    <span className="spinner" />
                    <span className="t-cap">{msStatus === '' ? '等待授权…' : msStatus}</span>
                  </div>
                </div>
              )}
            </div>
          )}

          {/* ── 外置：Yggdrasil 账密 ── */}
          {addKind === 'external' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                <span className="t-cap">认证服务器地址</span>
                <input
                  className="input"
                  value={extUrl}
                  placeholder="例如 https://littleskin.cn/api/yggdrasil"
                  onChange={(e) => setExtUrl(e.target.value)}
                />
              </label>
              <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                <span className="t-cap">邮箱或用户名</span>
                <input
                  className="input"
                  autoFocus
                  value={extName}
                  onChange={(e) => setExtName(e.target.value)}
                />
              </label>
              <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                <span className="t-cap">密码</span>
                <input
                  className="input"
                  type="password"
                  value={extPwd}
                  onChange={(e) => setExtPwd(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' && extReady && !addBusy) void loginExternal()
                  }}
                />
              </label>
            </div>
          )}

          {/* ── 离线：只要玩家名 ── */}
          {addKind === 'offline' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <span className="t-cap">玩家名（3–16 位字母、数字或下划线）</span>
              <input
                className="input"
                autoFocus
                value={draftName}
                placeholder="例如 Steve"
                onChange={(e) => setDraftName(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && nameValid) void addOffline()
                }}
              />
              {draftName.trim() !== '' && !nameValid ? (
                <span className="t-cap" style={{ color: 'var(--danger)' }}>
                  名字不合法：3–16 位字母/数字/下划线
                </span>
              ) : (
                <span className="t-cap">
                  离线账户无需登录，可用于单人游戏、局域网联机与允许离线登录的服务器。
                </span>
              )}
            </div>
          )}
        </div>
      </Modal>

      {/* 移除确认 */}
      <Modal
        open={pendingRemove !== null}
        title="移除账户？"
        onClose={() => setPendingRemove(null)}
        footer={
          <>
            <button className="btn" onClick={() => setPendingRemove(null)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: 'var(--text-on-accent)' }}
              onClick={() => {
                const a = pendingRemove
                if (a !== null) void remove(a)
              }}
            >
              移除
            </button>
          </>
        }
      >
        <div className="dialog-body">
          将从启动器移除账户 <b>{pendingRemove?.name}</b>（不会删除游戏内的玩家数据）。
        </div>
      </Modal>

      {host}
    </div>
  )
}