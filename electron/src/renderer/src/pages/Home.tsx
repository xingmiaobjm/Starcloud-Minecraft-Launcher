/// 首页：左「已安装版本」列表 + 右「账户 / 启动」卡 + 底「运行日志」。
import { useEffect, useState } from 'react'
import {
  Avatar,
  Button,
  Card,
  Dropdown,
  Option,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  ArrowSyncRegular,
  DeleteRegular,
  PlayRegular
} from '@fluentui/react-icons'
import { request } from '../bridge'
import type { AccountList, GameAccount, InstalledVersion } from '../bridge'
import { brandYellow, motion, radius } from '../theme'
import { EmptyState, PointerGlow } from '../ui'
import type { GameSessionApi } from '../state/session'

const useStyles = makeStyles({
  columns: { display: 'flex', flex: 1, minHeight: 0, gap: '16px' },
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
  versionList: { flex: 1, overflowY: 'auto', minHeight: 0 },
  versionRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '8px 12px',
    margin: '0 8px',
    borderRadius: `${radius.row}px`,
    cursor: 'pointer',
    border: '1.2px solid transparent',
    transition: `background ${motion.fast}ms ${motion.ease}, border-color ${motion.fast}ms ${motion.ease}, transform ${motion.fast}ms ${motion.ease}`,
    animation: `rowIn ${motion.normal}ms ${motion.easeOut} both`
  },
  versionRowSelected: {
    backgroundColor: 'rgba(255,194,31,0.14)',
    border: `1.2px solid ${brandYellow}`
  },
  versionRowHover: { backgroundColor: 'rgba(255,255,255,0.055)' },
  versionIcon: {
    width: '36px',
    height: '36px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontWeight: 'bold',
    fontSize: '15px',
    flexShrink: 0,
    borderRadius: `${radius.control}px`
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
    height: '58px',
    width: '100%',
    borderRadius: `${radius.overlay}px`,
    border: 'none',
    background: brandYellow,
    color: '#1b1b1b',
    fontSize: '15px',
    fontWeight: 700,
    cursor: 'pointer',
    transition: `background ${motion.fast}ms ${motion.ease}, transform ${motion.fast}ms ${motion.ease}, box-shadow ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: '#FFD35C' },
    ':active': { transform: 'scale(0.985)' }
  },
  launchButtonBusy: { opacity: 0.6, cursor: 'default' },
  launchButtonGlow: { boxShadow: `0 0 0 0 rgba(255,194,31,0.45)` },
  logCard: { height: '230px', display: 'flex', flexDirection: 'column' },
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
  dot: { width: '8px', height: '8px', borderRadius: '50%', flexShrink: 0 }
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

export default function HomePage(props: { session: GameSessionApi }) {
  const styles = useStyles()
  const session = props.session
  const [installed, setInstalled] = useState<InstalledVersion[]>([])
  const [accounts, setAccounts] = useState<GameAccount[]>([])
  const [selected, setSelected] = useState<string | undefined>(undefined)
  const [selectedAccount, setSelectedAccount] = useState<string | undefined>()
  const [hovered, setHovered] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let alive = true
    async function reload(): Promise<void> {
      try {
        const catalog = await request<{ installed: InstalledVersion[] }>(
          'catalog.installed'
        )
        if (!alive) return
        setInstalled(catalog.installed)
        setSelected((prev) => {
          if (prev && catalog.installed.some((v) => v.id === prev)) return prev
          return (
            catalog.installed.find((v) => v.isDefault)?.id ??
            catalog.installed[0]?.id
          )
        })
      } catch (e) {
        if (alive) setError(String(e))
      }
      try {
        const list = await request<AccountList>('accounts.list')
        if (!alive) return
        setAccounts(list.accounts)
        setSelectedAccount((prev) => {
          if (prev && list.accounts.some((a) => a.uuid === prev)) return prev
          return list.current ?? list.accounts[0]?.uuid
        })
      } catch (e) {
        if (alive) setError(String(e))
      }
      if (alive) setLoading(false)
    }
    void reload()
    return () => {
      alive = false
    }
  }, [])

  useEffect(() => {
    if (selected) session.setVersion(selected)
  }, [selected, session])

  const account =
    accounts.find((a) => a.uuid === selectedAccount) ?? accounts[0]
  const running = session.pid !== undefined && session.pid > 0
  const canLaunch =
    !session.launching && !running && selected !== undefined && account != null

  const statusColor = running
    ? '#3FBF6F'
    : session.launching
      ? '#E8B31A'
      : session.status.startsWith('启动失败')
        ? '#E04B4B'
        : '#8A8A8A'

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {error !== '' && (
        <Card style={{ marginBottom: '12px', border: '1px solid rgba(224,75,75,0.4)' }}>
          <Text size={200} style={{ color: '#FF9A9A' }}>
            {error}
          </Text>
        </Card>
      )}
      <div className={styles.columns}>
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
            {loading && <div style={{ padding: '20px', textAlign: 'center' }}>加载中…</div>}
            {!loading && installed.length === 0 && (
              <EmptyState
                title="还没有已安装的版本"
                hint="去「下载」页选择一个版本安装；或把已有的 .minecraft 放到设置里指定的目录。"
              />
            )}
            {installed.map((version, index) => {
              const isSelected = version.id === selected
              const info = tagFor(version.id)
              return (
                <div
                  key={version.id}
                  className={[
                    styles.versionRow,
                    isSelected ? styles.versionRowSelected : '',
                    hovered === version.id && !isSelected ? styles.versionRowHover : ''
                  ]
                    .filter(Boolean)
                    .join(' ')}
                  style={{ animationDelay: `${index * 24}ms` }}
                  onMouseEnter={() => setHovered(version.id)}
                  onMouseLeave={() => setHovered(null)}
                  onClick={() => setSelected(version.id)}
                >
                  <div
                    className={styles.versionIcon}
                    style={{
                      background: `${info.color}2b`,
                      border: `1px solid ${info.color}`
                    }}
                  >
                    {version.id[0]?.toUpperCase()}
                  </div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text size={300} weight="semibold" truncate>
                      {version.id}
                    </Text>
                  </div>
                  <Text size={200} style={{ opacity: 0.6 }}>
                    {info.label}
                  </Text>
                  <div style={{ width: '96px', textAlign: 'right' }}>
                    <Text size={200} style={{ opacity: 0.6 }}>
                      {formatTime(version.lastPlayedAt)}
                    </Text>
                  </div>
                </div>
              )
            })}
          </div>
        </Card>

        <Card className={styles.accountCard}>
          <Text size={400} weight="semibold">
            游戏账户
          </Text>
          <Dropdown
            value={account?.name ?? '未选择账户'}
            disabled={accounts.length === 0}
            onOptionSelect={(_, data) => setSelectedAccount(String(data.optionValue))}
          >
            {accounts.map((a) => (
              <Option key={a.uuid} value={a.uuid} text={a.name}>
                {a.name}（{a.type}）
              </Option>
            ))}
          </Dropdown>
          <div className={styles.avatarBlock}>
            <Avatar
              size={96}
              name={account?.name ?? '未选择账户'}
              color="brand"
              style={{ animation: session.launching ? 'pulse 1.4s infinite' : undefined }}
            />
            <Text size={400} weight="semibold">
              {account?.name ?? '未选择账户'}
            </Text>
            <Text size={200} style={{ opacity: 0.6 }}>
              {account == null
                ? '去「账户」页添加离线账户'
                : account.type === 'offline'
                  ? '离线账户'
                  : account.serverName ?? account.type}
            </Text>
          </div>
          {session.status !== '' && (
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <div
                className={styles.dot}
                style={{
                  background: statusColor,
                  animation: session.launching || running ? 'pulse 1.6s infinite' : undefined
                }}
              />
              <Text size={200} style={{ opacity: 0.85 }} truncate>
                {session.status}
              </Text>
            </div>
          )}
          <button
            className={[
              styles.launchButton,
              canLaunch ? '' : styles.launchButtonBusy,
              canLaunch ? styles.launchButtonGlow : ''
            ]
              .filter(Boolean)
              .join(' ')}
            disabled={!canLaunch}
            onClick={() => {
              if (!selected || !account) return
              void session.launch({
                versionId: selected,
                playerName: account.name,
                accountUuid: account.uuid
              })
            }}
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
              {session.launching ? '启动中…' : running ? '游戏运行中' : '启动游戏'}
            </div>
            <div style={{ fontSize: '11px', opacity: 0.75, marginTop: '2px' }}>
              {selected ?? '未选择版本'}
            </div>
          </button>
        </Card>
      </div>

      {session.logs.length > 0 && (
        <Card className={styles.logCard} style={{ marginTop: '14px' }}>
          <div className={styles.logHeader}>
            <div
              className={styles.dot}
              style={{ background: running ? '#3FBF6F' : '#8A8A8A' }}
            />
            <Text size={300} weight="semibold">
              运行日志
            </Text>
            <div style={{ flex: 1 }} />
            {running && (
              <Text size={200} style={{ opacity: 0.6 }}>
                pid {session.pid}
              </Text>
            )}
            <Button
              appearance="subtle"
              icon={<DeleteRegular />}
              onClick={session.clearLogs}
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
