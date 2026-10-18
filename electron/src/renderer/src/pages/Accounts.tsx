/// 账户页：列出本地账户、切换当前账户、添加离线账户、移除账户。
/// 移除账户是破坏性操作，必须走 Dialog 二次确认。
import { useCallback, useEffect, useState } from 'react'
import {
  Avatar,
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  PersonAddRegular,
  PersonDeleteRegular
} from '@fluentui/react-icons'
import { request } from '../bridge'
import type { AccountList, GameAccount } from '../bridge'
import { brandYellow, motion } from '../theme'
import { EmptyState, PageShell, SkeletonList, useToasts } from '../ui'

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px', paddingTop: '4px' },
  card: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
    padding: '14px 16px',
    borderRadius: '10px',
    border: '1.2px solid transparent',
    transition: `background ${motion.fast}ms ${motion.ease}, border-color ${motion.fast}ms ${motion.ease}`,
    animation: `rowIn ${motion.normal}ms ${motion.easeOut} both`,
    ':hover': { backgroundColor: 'rgba(255,255,255,0.055)' }
  },
  cardCurrent: {
    backgroundColor: 'rgba(255,194,31,0.1)',
    border: `1.2px solid ${brandYellow}`
  },
  nameBlock: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: '4px' },
  tags: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  tag: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '999px',
    border: '1px solid rgba(255,255,255,0.16)',
    background: 'rgba(255,255,255,0.05)',
    whiteSpace: 'nowrap'
  },
  currentTag: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '999px',
    border: `1px solid ${brandYellow}`,
    background: 'rgba(255,194,31,0.14)',
    color: brandYellow,
    whiteSpace: 'nowrap'
  },
  primaryButton: {
    height: '32px',
    borderRadius: '6px',
    border: 'none',
    background: brandYellow,
    color: '#1b1b1b',
    fontWeight: 600,
    cursor: 'pointer',
    padding: '0 14px',
    transition: `background ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: '#FFD35C' }
  },
  primaryButtonBusy: { opacity: 0.55, cursor: 'default' },
  dangerButton: {
    height: '32px',
    borderRadius: '6px',
    border: '1px solid rgba(224,75,75,0.45)',
    background: 'transparent',
    color: '#FF9A9A',
    cursor: 'pointer',
    padding: '0 12px',
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    transition: `background ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: 'rgba(224,75,75,0.16)' }
  },
  sessionState: { display: 'flex', alignItems: 'center', gap: '6px' }
})

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
  const styles = useStyles()
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
          <button className={styles.primaryButton} onClick={() => setAddOpen(true)}>
            <span style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
              <PersonAddRegular />
              添加离线账户
            </span>
          </button>
        }
      >
        {loading && <SkeletonList rows={3} />}
        {!loading && accounts.length === 0 && (
          <EmptyState
            title="还没有账户"
            hint="点右上角「添加离线账户」创建一个 3-16 位字母数字玩家名即可开始游戏。"
            action={
              <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setAddOpen(true)}>
                添加离线账户
              </Button>
            }
          />
        )}

        {!loading && accounts.length > 0 && (
          <div className={styles.list}>
            {accounts.map((account, index) => {
              const isCurrent = account.uuid === current || account.isDefault
              const online = account.type !== 'offline'
              // 正版/联机账户没有有效会话即视为凭证过期，需要重新登录。
              const expired = online && account.hasSession !== true
              const busy = switching !== null
              return (
                <Card
                  key={account.uuid}
                  className={[styles.card, isCurrent ? styles.cardCurrent : ''].filter(Boolean).join(' ')}
                  style={{ animationDelay: `${index * 24}ms` }}
                >
                  <Avatar
                    size={48}
                    name={account.name}
                    color="brand"
                    style={expired ? { animation: 'pulse 2.4s infinite' } : undefined}
                  />
                  <div className={styles.nameBlock}>
                    <Text size={300} weight="semibold" truncate>
                      {account.name}
                    </Text>
                    <div className={styles.tags}>
                      <span
                        className={styles.tag}
                        style={{ color: typeColor(account), borderColor: `${typeColor(account)}66` }}
                      >
                        {typeLabel(account)}
                      </span>
                      {isCurrent && <span className={styles.currentTag}>当前账户</span>}
                      <span className={styles.sessionState}>
                        {online ? (
                          expired ? (
                            <ErrorCircleRegular style={{ color: '#E8B31A', fontSize: '13px' }} />
                          ) : (
                            <CheckmarkCircleRegular style={{ color: '#3FBF6F', fontSize: '13px' }} />
                          )
                        ) : (
                          <CheckmarkCircleRegular style={{ color: '#8A8A8A', fontSize: '13px' }} />
                        )}
                        <Text size={100} style={{ opacity: 0.6 }}>
                          {!online ? '无需凭证' : expired ? '凭证已过期' : '凭证有效'}
                        </Text>
                      </span>
                    </div>
                  </div>
                  {!isCurrent && (
                    <button
                      className={[
                        styles.primaryButton,
                        busy ? styles.primaryButtonBusy : ''
                      ]
                        .filter(Boolean)
                        .join(' ')}
                      disabled={busy}
                      onClick={() => void setCurrentAccount(account)}
                    >
                      {switching === account.uuid ? '切换中…' : '设为当前'}
                    </button>
                  )}
                  <button
                    className={styles.dangerButton}
                    onClick={() => setPendingRemove(account)}
                    aria-label={`移除 ${account.name}`}
                  >
                    <PersonDeleteRegular />
                    移除
                  </button>
                </Card>
              )
            })}
          </div>
        )}
      </PageShell>

      {/* 添加离线账户 */}
      <Dialog open={addOpen} onOpenChange={(_e, data) => setAddOpen(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>添加离线账户</DialogTitle>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
              <Field
                label="玩家名"
                hint="3-16 位字母、数字或下划线"
                validationState={draftName === '' ? undefined : nameValid ? 'success' : 'error'}
                validationMessage={draftName !== '' && !nameValid ? '玩家名不合法：需 3-16 位字母、数字或下划线' : undefined}
              >
                <Input
                  value={draftName}
                  onChange={(_e, data) => setDraftName(data.value)}
                  placeholder="Steve"
                  autoFocus
                  disabled={adding}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' && nameValid) void addOffline()
                  }}
                />
              </Field>
              <Text size={100} style={{ opacity: 0.6 }}>
                离线账户不需要登录微软账号，凭证永不失效；存档与皮肤由本地玩家名决定。
              </Text>
            </div>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setAddOpen(false)} disabled={adding}>
                取消
              </Button>
              <Button
                appearance="primary"
                icon={<PersonAddRegular />}
                onClick={() => void addOffline()}
                disabled={!nameValid || adding}
              >
                {adding ? '添加中…' : '添加'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {/* 移除确认：账户数据不可恢复，必须二次确认。 */}
      <Dialog
        open={pendingRemove !== null}
        onOpenChange={(_e, data) => {
          if (!data.open) setPendingRemove(null)
        }}
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>移除账户「{pendingRemove?.name}」？</DialogTitle>
            <Text size={200} style={{ opacity: 0.75 }}>
              只会移除本地的账户记录，不会删除任何存档或版本文件。移除后需要重新添加才能使用该玩家名。
            </Text>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setPendingRemove(null)} disabled={removing}>
                取消
              </Button>
              <Button
                appearance="primary"
                icon={<PersonDeleteRegular />}
                onClick={() => void confirmRemove()}
                disabled={removing}
                style={{ background: '#E04B4B', color: '#ffffff' }}
              >
                {removing ? '移除中…' : '确认移除'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {host}
    </>
  )
}
