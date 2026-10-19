/// 账户页：列出本地账户、切换当前账户、添加离线账户、移除账户。
/// 移除账户是破坏性操作，必须走 Modal 二次确认。
/// 视觉全部走 winui.css 的纯 CSS 类，不依赖 Fluent 组件。
import { useCallback, useEffect, useState } from 'react'
import type { CSSProperties } from 'react'
import { request } from '../bridge'
import type { AccountList, GameAccount } from '../bridge'
import { EmptyState, Modal, PageShell, SkeletonList, useToasts } from '../ui'

/** 离线账户的玩家名规则：3-16 位字母/数字/下划线，与正版一致。 */
const NAME_PATTERN = /^[A-Za-z0-9_]{3,16}$/

function typeLabel(account: GameAccount): string {
  if (account.type === 'offline') return '离线账户'
  return account.serverAlias ?? account.serverName ?? account.type
}

function typeColor(account: GameAccount): string {
  return account.type === 'offline' ? '#8A8A8A' : '#6FA852'
}

export default function AccountsPage() {
  const { push, host } = useToasts()
  const [accounts, setAccounts] = useState<GameAccount[]>([])
  const [current, setCurrent] = useState<string | undefined>(undefined)
  const [loading, setLoading] = useState(true)
  const [switching, setSwitching] = useState<string | null>(null)

  // 新增离线账户的弹窗状态
  const [addOpen, setAddOpen] = useState(false)
  const [draftName, setDraftName] = useState('')
  const [adding, setAdding] = useState(false)
  // 待移除账户：非空即代表确认弹窗处于打开态。
  const [pendingRemove, setPendingRemove] = useState<GameAccount | null>(null)
  const [removing, setRemoving] = useState(false)

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const data = await request<AccountList>('accounts.list')
      setAccounts(data.accounts)
      setCurrent(data.current)
    } catch (e) {
      push('error', `账户列表读取失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  // 弹窗关闭时清空草稿，避免下次打开残留上一次的输入。
  useEffect(() => {
    if (!addOpen) setDraftName('')
  }, [addOpen])

  const nameValid = NAME_PATTERN.test(draftName)

  async function addOffline(): Promise<void> {
    if (!nameValid) return
    setAdding(true)
    try {
      await request<{ uuid: string; name: string }>('accounts.addOffline', { name: draftName.trim() })
      push('success', `已添加离线账户 ${draftName.trim()}`)
      setAddOpen(false)
      await load()
    } catch (e) {
      push('error', `添加失败：${String(e)}`)
    } finally {
      setAdding(false)
    }
  }

  async function setCurrentAccount(account: GameAccount): Promise<void> {
    if (switching !== null) return
    setSwitching(account.uuid)
    try {
      await request<{ ok: boolean }>('accounts.setCurrent', { uuid: account.uuid })
      push('success', `已将 ${account.name} 设为当前账户`)
      await load()
    } catch (e) {
      push('error', `切换失败：${String(e)}`)
    } finally {
      setSwitching(null)
    }
  }

  async function confirmRemove(): Promise<void> {
    if (!pendingRemove) return
    const target = pendingRemove
    setRemoving(true)
    try {
      await request<{ ok: boolean }>('accounts.remove', { uuid: target.uuid })
      push('success', `已移除账户 ${target.name}`)
      setPendingRemove(null)
      await load()
    } catch (e) {
      push('error', `移除失败：${String(e)}`)
    } finally {
      setRemoving(false)
    }
  }

  return (
    <>
      <PageShell
        title="账户"
        subtitle={`共 ${accounts.length} 个账户${
          current !== undefined
            ? `，当前：${accounts.find((a) => a.uuid === current)?.name ?? '未知'}`
            : ''
        }`}
        actions={
          <button className="btn btn-primary" onClick={() => setAddOpen(true)}>
            ☺ 添加离线账户
          </button>
        }
      >
        {loading && <SkeletonList rows={3} avatar />}
        {!loading && accounts.length === 0 && (
          <EmptyState
            title="还没有账户"
            hint="点右上角「添加离线账户」创建一个 3-16 位字母数字玩家名即可开始游戏。"
            action={
              <button className="btn btn-primary" onClick={() => setAddOpen(true)}>
                ☺ 添加离线账户
              </button>
            }
          />
        )}

        {!loading && accounts.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10, paddingTop: 4 }}>
            {accounts.map((account, index) => {
              const isCurrent = account.uuid === current || account.isDefault
              const online = account.type !== 'offline'
              // 正版/联机账户没有有效会话即视为凭证过期，需要重新登录。
              const expired = online && account.hasSession !== true
              const busy = switching !== null
              const statusColor = online ? (expired ? 'var(--warning)' : 'var(--success)') : 'var(--text-tertiary)'
              return (
                <div
                  key={account.uuid}
                  className={`row glow${isCurrent ? ' row-active' : ''}`}
                  style={
                    {
                      animationDelay: `${index * 39}ms`,
                      '--glow':
                        'radial-gradient(420px circle at 50% 0%, color-mix(in srgb, var(--accent) 16%, transparent), transparent 65%)'
                    } as CSSProperties
                  }
                >
                  <div
                    className="icon32"
                    style={{
                      width: 48,
                      height: 48,
                      fontSize: 22,
                      fontWeight: 600,
                      background: 'linear-gradient(180deg,#3a2a1a,#241a10)',
                      color: '#e0a76a',
                      animation: expired ? 'pulse 2.4s infinite' : undefined
                    }}
                  >
                    {account.name[0]?.toUpperCase()}
                  </div>
                  <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
                    <div className="t-strong truncate">{account.name}</div>
                    <div
                      style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}
                    >
                      <span className="chip" style={{ color: typeColor(account) }}>
                        {typeLabel(account)}
                      </span>
                      {isCurrent && <span className="chip chip-accent">当前账户</span>}
                      <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                        <span
                          style={{ color: statusColor, fontSize: 13 }}
                          title={!online ? '无需凭证' : expired ? '凭证已过期' : '凭证有效'}
                        >
                          {expired ? '!' : '✓'}
                        </span>
                        <span className="t-meta">
                          {!online ? '无需凭证' : expired ? '凭证已过期' : '凭证有效'}
                        </span>
                      </span>
                    </div>
                  </div>
                  {!isCurrent && (
                    <button
                      className="btn btn-primary"
                      disabled={busy}
                      onClick={() => void setCurrentAccount(account)}
                    >
                      {switching === account.uuid ? '切换中…' : '设为当前'}
                    </button>
                  )}
                  <button
                    className="btn btn-danger"
                    onClick={() => setPendingRemove(account)}
                    aria-label={`移除 ${account.name}`}
                  >
                    ✕ 移除
                  </button>
                </div>
              )
            })}
          </div>
        )}
      </PageShell>

      {/* 添加离线账户 */}
      <Modal
        open={addOpen}
        title="添加离线账户"
        onClose={() => setAddOpen(false)}
        width={480}
        footer={
          <>
            <button className="btn" onClick={() => setAddOpen(false)} disabled={adding}>
              取消
            </button>
            <button
              className="btn btn-primary"
              onClick={() => void addOffline()}
              disabled={!nameValid || adding}
            >
              {adding ? '添加中…' : '添加'}
            </button>
          </>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div>
            <div className="t-cap" style={{ marginBottom: 4 }}>
              玩家名
            </div>
            <input
              className="input"
              style={{ width: '100%' }}
              value={draftName}
              onChange={(e) => setDraftName(e.target.value)}
              placeholder="Steve"
              autoFocus
              disabled={adding}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && nameValid) void addOffline()
              }}
            />
            <div
              className="t-meta"
              style={{ marginTop: 4, color: draftName !== '' && !nameValid ? 'var(--danger)' : undefined }}
            >
              {draftName !== '' && !nameValid
                ? '玩家名不合法：需 3-16 位字母、数字或下划线'
                : '3-16 位字母、数字或下划线'}
            </div>
          </div>
          <div className="t-meta">
            离线账户不需要登录微软账号，凭证永不失效；存档与皮肤由本地玩家名决定。
          </div>
        </div>
      </Modal>

      {/* 移除确认：账户数据不可恢复，必须二次确认。 */}
      <Modal
        open={pendingRemove !== null}
        title={`移除账户「${pendingRemove?.name ?? ''}」？`}
        onClose={() => setPendingRemove(null)}
        width={480}
        footer={
          <>
            <button className="btn" onClick={() => setPendingRemove(null)} disabled={removing}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: '#fff' }}
              onClick={() => void confirmRemove()}
              disabled={removing}
            >
              ✕ {removing ? '移除中…' : '确认移除'}
            </button>
          </>
        }
      >
        <div className="t-body">
          只会移除本地的账户记录，不会删除任何存档或版本文件。移除后需要重新添加才能使用该玩家名。
        </div>
      </Modal>
      {host}
    </>
  )
}
