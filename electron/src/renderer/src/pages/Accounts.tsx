/// 账户页：对齐 Win 端 AccountsPage ——
/// 顶部云账号卡（Starcloud ID，未接内核时显示未登录）+ 游戏账户区（皮肤头像、类型、首选芯片）。
import { useCallback, useEffect, useState } from 'react'

import { request } from '../bridge'
import type { AccountList, GameAccount } from '../bridge'
import { EmptyState, Modal, SkeletonList, useToasts } from '../ui'

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

export default function AccountsPage(): JSX.Element {
  const { push, host } = useToasts()
  const [accounts, setAccounts] = useState<GameAccount[]>([])
  const [current, setCurrent] = useState<string | undefined>()
  const [loading, setLoading] = useState(true)
  const [addOpen, setAddOpen] = useState(false)
  const [draftName, setDraftName] = useState('')
  const [addBusy, setAddBusy] = useState(false)
  const [pendingRemove, setPendingRemove] = useState<GameAccount | null>(null)
  const [failedAvatars, setFailedAvatars] = useState<Set<string>>(new Set())

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

  const nameValid = /^[A-Za-z0-9_]{3,16}$/.test(draftName.trim())

  async function addOffline(): Promise<void> {
    if (!nameValid) return
    setAddBusy(true)
    try {
      await request('accounts.addOffline', { name: draftName.trim() })
      push('success', `已添加离线账户 ${draftName.trim()}`)
      setAddOpen(false)
      setDraftName('')
      await load()
    } catch (e) {
      push('error', `添加失败：${String(e)}`)
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
            <button className="btn btn-primary" onClick={() => setAddOpen(true)}>
              添加账户
            </button>
          </div>

          {loading && <SkeletonList rows={2} avatar />}
          {!loading && accounts.length === 0 && (
            <EmptyState
              title="还没有游戏账户"
              hint="点右上角「添加账户」建一个离线账户，或用微软账户登录。"
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

      {/* 添加账户 */}
      <Modal
        open={addOpen}
        title="添加离线账户"
        onClose={() => setAddOpen(false)}
        footer={
          <>
            <button className="btn" onClick={() => setAddOpen(false)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              disabled={!nameValid || addBusy}
              onClick={() => void addOffline()}
            >
              {addBusy ? '添加中…' : '添加'}
            </button>
          </>
        }
      >
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
          {draftName.trim() !== '' && !nameValid && (
            <span className="t-cap" style={{ color: 'var(--danger)' }}>
              名字不合法：3–16 位字母/数字/下划线
            </span>
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
              style={{ background: 'var(--danger)', color: '#fff' }}
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
