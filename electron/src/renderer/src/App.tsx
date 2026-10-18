import { useEffect, useState } from 'react'
import {
  Avatar,
  Button,
  Card,
  Dropdown,
  MessageBar,
  Option,
  Text,
  makeStyles,
  mergeClasses
} from '@fluentui/react-components'
import {
  ArrowSyncRegular,
  DeleteRegular,
  PlayRegular
} from '@fluentui/react-icons'
import { request, onBridgeEvent } from './bridge'
import type {
  AccountList,
  GameAccount,
  InstalledVersion,
  LaunchSession
} from './bridge'

interface SessionState {
  pid?: number
  logs: string[]
  status: string
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    padding: '8px 24px 20px',
    gap: '12px',
    boxSizing: 'border-box'
  },
  columns: {
    display: 'flex',
    flex: 1,
    minHeight: 0,
    gap: '16px'
  },
  versionsCard: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '12px 16px 10px'
  },
  versionList: {
    flex: 1,
    overflowY: 'auto',
    minHeight: 0
  },
  versionRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '8px 12px',
    borderRadius: '6px',
    cursor: 'pointer',
    border: '1.2px solid transparent'
  },
  versionRowSelected: {
    backgroundColor: 'rgba(255,194,31,0.14)',
    border: '1.2px solid #FFC21F'
  },
  versionIcon: {
    width: '36px',
    height: '36px',
    borderRadius: '6px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontWeight: 'bold',
    fontSize: '15px',
    flexShrink: 0
  },
  accountCard: {
    width: '360px',
    display: 'flex',
    flexDirection: 'column',
    padding: '16px',
    gap: '12px'
  },
  avatarBlock: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '10px'
  },
  launchButton: {
    height: '56px',
    width: '100%',
    backgroundColor: '#FFC21F',
    color: '#1b1b1b',
    borderRadius: '8px',
    ':hover': { backgroundColor: '#ffd35c' }
  },
  logCard: {
    height: '220px',
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
    fontFamily: 'monospace',
    fontSize: '11px',
    lineHeight: '1.45'
  }
})

function tagFor(id: string): { label: string; color: string } {
  const lower = id.toLowerCase()
  if (lower.includes('neoforge')) return { label: 'NeoForge', color: '#E8905A' }
  if (lower.includes('fabric')) return { label: 'Fabric', color: '#B08A4F' }
  if (lower.includes('quilt')) return { label: 'Quilt', color: '#9A8ABF' }
  if (lower.includes('forge')) return { label: 'Forge', color: '#5A5A66' }
  if (/^\d+w\d+[a-z]/.test(lower) || lower.includes('-rc') || lower.includes('-pre'))
    return { label: '快照版', color: '#8A6FD8' }
  return { label: '正式版', color: '#6FA852' }
}

function formatTime(iso?: string): string {
  if (!iso) return '从未启动'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '从未启动'
  const two = (n: number) => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`
}

export default function App() {
  const styles = useStyles()
  const [installed, setInstalled] = useState<InstalledVersion[]>([])
  const [accounts, setAccounts] = useState<GameAccount[]>([])
  const [currentUuid, setCurrentUuid] = useState<string | undefined>()
  const [selectedVersion, setSelectedVersion] = useState<string | undefined>()
  const [selectedAccount, setSelectedAccount] = useState<string | undefined>()
  const [error, setError] = useState('')
  const [session, setSession] = useState<SessionState>({ logs: [], status: '' })

  useEffect(() => {
    async function reload(): Promise<void> {
      try {
        const catalog = await request<{
          installed: Array<{
            id: string
            sizeBytes: number
            lastPlayedAt?: string
            isDefault?: boolean
          }>
        }>('catalog.installed')
        setInstalled(
          catalog.installed.map((entry) => ({
            id: entry.id,
            sizeBytes: entry.sizeBytes,
            lastPlayedAt: entry.lastPlayedAt,
            isDefault: entry.isDefault
          }))
        )
        setSelectedVersion((prev) =>
          prev && catalog.installed.some((v) => v.id === prev)
            ? prev
            : (catalog.installed.find((v) => v.isDefault)?.id ??
              catalog.installed[0]?.id)
        )
      } catch (e) {
        setError(String(e))
      }
      try {
        const list = await request<AccountList>('accounts.list')
        setAccounts(list.accounts)
        setCurrentUuid(list.current)
        setSelectedAccount((prev) =>
          prev && list.accounts.some((a) => a.uuid === prev)
            ? prev
            : (list.current ?? list.accounts[0]?.uuid)
        )
      } catch (e) {
        setError(String(e))
      }
    }
    void reload()
    onBridgeEvent((event, params) => {
      if (event === 'game.output') {
        const line = String(params.line ?? '')
        setSession((s) => ({ ...s, logs: [...s.logs, line].slice(-500) }))
      } else if (event === 'game.exited') {
        const code = Number(params.code ?? -1)
        setSession((s) => ({
          ...s,
          pid: undefined,
          status: `游戏退出（exit=${code}）`
        }))
      }
    })
  }, [])

  const currentAccount =
    accounts.find((a) => a.uuid === selectedAccount) ?? accounts[0]
  const launchingNow = session.status === '启动中…'
  const runningNow = session.pid !== undefined && session.pid > 0

  async function launch(): Promise<void> {
    if (!selectedVersion || !currentAccount) return
    setSession({ logs: [], status: '启动中…' })
    try {
      const launched = await request<LaunchSession>('launch.start', {
        versionId: selectedVersion,
        playerName: currentAccount.name,
        accountUuid: currentAccount.uuid
      })
      setSession({
        pid: launched.pid,
        logs: [],
        status: `游戏运行中（pid ${launched.pid}）`
      })
    } catch (e) {
      setSession({ logs: [], status: `启动失败：${String(e)}` })
    }
  }

  return (
    <div className={styles.root}>
      {error !== '' && (
        <Card>
          <MessageBar intent="error">
            <Text>出错了：{error}</Text>
          </MessageBar>
        </Card>
      )}
      <div className={styles.columns}>
        {/* 左：已安装版本 */}
        <Card className={styles.versionsCard}>
          <div className={styles.cardHeader}>
            <Text size={400} weight="semibold">
              已安装版本
            </Text>
            <Text size={200} style={{ opacity: 0.6 }}>
              ({installed.length})
            </Text>
            <div style={{ flex: 1 }} />
            <Button
              appearance="subtle"
              icon={<ArrowSyncRegular />}
              onClick={() => window.location.reload()}
            />
          </div>
          <div className={styles.versionList}>
            {installed.length === 0 && (
              <div style={{ padding: '20px', textAlign: 'center', opacity: 0.7 }}>
                还没有已安装的版本 —— 去「下载」页安装
              </div>
            )}
            {installed.map((version) => {
              const selected = version.id === selectedVersion
              const info = tagFor(version.id)
              return (
                <div
                  key={version.id}
                  className={mergeClasses(
                    styles.versionRow,
                    selected && styles.versionRowSelected
                  )}
                  onClick={() => setSelectedVersion(version.id)}
                >
                  <div
                    className={styles.versionIcon}
                    style={{
                      backgroundColor: `${info.color}33`,
                      border: `1px solid ${info.color}`
                    }}
                  >
                    {version.id[0]?.toUpperCase()}
                  </div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text size={300} weight="semibold">
                      {version.id}
                    </Text>
                  </div>
                  <Text size={200} style={{ opacity: 0.6 }}>
                    {info.label}
                  </Text>
                  <div style={{ width: '92px', textAlign: 'right' }}>
                    <Text size={200} style={{ opacity: 0.6 }}>
                      {formatTime(version.lastPlayedAt)}
                    </Text>
                  </div>
                </div>
              )
            })}
          </div>
        </Card>

        {/* 右：账户 / 启动 */}
        <Card className={styles.accountCard}>
          <Text size={400} weight="semibold">
            游戏账户
          </Text>
          <Dropdown
            value={
              accounts.find((a) => a.uuid === selectedAccount)?.name ??
              '未选择账户'
            }
            onOptionSelect={(_, data) =>
              setSelectedAccount(String(data.optionValue))
            }
          >
            {accounts.map((account) => (
              <Option key={account.uuid} value={account.uuid} text={account.name}>
                {account.name}（{account.type}）
              </Option>
            ))}
          </Dropdown>
          {accounts.length === 0 && (
            <Text size={200} style={{ opacity: 0.6 }}>
              还没有账户 —— 去「账户」页添加离线账户
            </Text>
          )}
          <div className={styles.avatarBlock}>
            <Avatar
              size={96}
              name={currentAccount?.name ?? '未选择账户'}
              color="brand"
            />
            <Text size={400} weight="semibold">
              {currentAccount?.name ?? '未选择账户'}
            </Text>
            <Text size={200} style={{ opacity: 0.6 }}>
              {currentAccount == null
                ? '去「账户」页添加离线账户'
                : currentAccount.type === 'offline'
                  ? '离线账户'
                  : currentAccount.type}
            </Text>
          </div>
          {session.status !== '' && (
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <div
                style={{
                  width: 8,
                  height: 8,
                  borderRadius: '50%',
                  backgroundColor: runningNow
                    ? '#3FBF6F'
                    : launchingNow
                      ? '#E8B31A'
                      : '#8A8A8A',
                  flexShrink: 0
                }}
              />
              <Text size={200} style={{ opacity: 0.8 }}>
                {session.status}
              </Text>
            </div>
          )}
          <button
            className={styles.launchButton}
            disabled={launchingNow || runningNow}
            onClick={() => void launch()}
            style={{
              opacity: launchingNow || runningNow ? 0.5 : 1,
              cursor: launchingNow || runningNow ? 'default' : 'pointer'
            }}
          >
            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: '8px',
                fontSize: '15px',
                fontWeight: 700
              }}
            >
              <PlayRegular />{' '}
              {launchingNow ? '启动中…' : runningNow ? '游戏运行中' : '启动游戏'}
            </div>
            {!launchingNow && selectedVersion && (
              <div style={{ fontSize: '11px', opacity: 0.75 }}>
                {selectedVersion}
              </div>
            )}
          </button>
        </Card>
      </div>

      {session.logs.length > 0 && (
        <Card className={styles.logCard}>
          <div className={styles.logHeader}>
            <div
              style={{
                width: 8,
                height: 8,
                borderRadius: '50%',
                backgroundColor: runningNow ? '#3FBF6F' : '#8A8A8A'
              }}
            />
            <Text size={300} weight="semibold">
              运行日志
            </Text>
            <div style={{ flex: 1 }} />
            {runningNow && (
              <Text size={200} style={{ opacity: 0.6 }}>
                pid {session.pid}
              </Text>
            )}
            <Button
              appearance="subtle"
              icon={<DeleteRegular />}
              onClick={() => setSession((s) => ({ ...s, logs: [] }))}
            />
          </div>
          <div className={styles.logBody}>
            {session.logs.map((line, index) => (
              <div key={index}>{line}</div>
            ))}
          </div>
        </Card>
      )}
    </div>
  )
}
