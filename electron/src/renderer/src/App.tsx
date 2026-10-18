/// 应用壳：左侧导航 + 右侧内容区 + 页面过渡动画。
/// 导航结构与 Windows 端一致（首页/版本/下载/存档/联机/实验室/运行状态/账户/插件/Nova）。
import { useEffect, useMemo, useState } from 'react'
import {
  Avatar,
  Badge,
  Card,
  Text,
  Tooltip,
  makeStyles
} from '@fluentui/react-components'
import {
  ArrowDownloadRegular,
  ArrowSyncRegular,
  BeakerRegular,
  ChatRegular,
  ClipboardTaskListLtrRegular,
  GamesRegular,
  HomeRegular,
  PeopleTeamRegular,
  PersonRegular,
  PlayRegular,
  PuzzleCubeRegular,
  SettingsRegular
} from '@fluentui/react-icons'
import { brandYellow, motion, radius } from './theme'
import { ErrorBoundary, PageFade, injectGlobalStyle, injectKeyframes } from './ui'
import HomePage from './pages/Home'
import VersionsPage from './pages/Versions'
import DownloadPage from './pages/Download'
import SavesPage from './pages/Saves'
import MultiplayerPage from './pages/Multiplayer'
import LabPage from './pages/Lab'
import RunningPage from './pages/Running'
import AccountsPage from './pages/Accounts'
import PluginsPage from './pages/Plugins'
import NovaPage from './pages/Nova'
import SettingsPage from './pages/Settings'
import LogsPage from './pages/Logs'
import { useGameSession } from './state/session'

const useStyles = makeStyles({
  shell: {
    display: 'flex',
    height: '100vh',
    overflow: 'hidden'
  },
  nav: {
    width: '224px',
    flexShrink: 0,
    display: 'flex',
    flexDirection: 'column',
    background: 'rgba(255,255,255,0.025)',
    borderRight: '1px solid rgba(255,255,255,0.07)',
    padding: '10px 8px'
  },
  brand: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '10px 10px 14px'
  },
  brandMark: {
    width: '30px',
    height: '30px',
    borderRadius: `${radius.control}px`,
    background: brandYellow,
    color: '#1b1b1b',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontWeight: 800,
    fontSize: '15px',
    boxShadow: `0 0 14px rgba(255,194,31,0.25)`
  },
  navList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    flex: 1
  },
  navItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '9px 10px',
    borderRadius: `${radius.row}px`,
    cursor: 'pointer',
    color: 'rgba(255,255,255,0.82)',
    transition: `background ${motion.hoverIn}ms ${motion.easeFade}, transform ${motion.hoverIn}ms ${motion.easeFade}`,
    border: 'none',
    background: 'transparent',
    textAlign: 'left',
    width: '100%',
    fontSize: '13px'
  },
  navItemActive: {
    background: 'rgba(255,194,31,0.14)',
    color: brandYellow,
    fontWeight: 600
  },
  navItemHover: {
    background: 'rgba(255,255,255,0.06)'
  },
  navItemActiveHover: {
    background: 'rgba(255,194,31,0.2)'
  },
  content: {
    flex: 1,
    minWidth: 0,
    padding: '18px 22px 0',
    display: 'flex',
    flexDirection: 'column'
  }
})

export type PageKey =
  | 'home'
  | 'versions'
  | 'download'
  | 'saves'
  | 'multiplayer'
  | 'lab'
  | 'running'
  | 'accounts'
  | 'plugins'
  | 'nova'
  | 'logs'
  | 'settings'

interface NavEntry {
  key: PageKey
  label: string
  icon: React.ReactElement
  footer?: boolean
}

const NAV: NavEntry[] = [
  { key: 'home', label: '首页', icon: <HomeRegular /> },
  { key: 'versions', label: '版本', icon: <ClipboardTaskListLtrRegular /> },
  { key: 'download', label: '下载', icon: <ArrowDownloadRegular /> },
  { key: 'saves', label: '存档', icon: <GamesRegular /> },
  { key: 'multiplayer', label: '联机', icon: <PeopleTeamRegular /> },
  { key: 'lab', label: '实验室', icon: <BeakerRegular /> },
  { key: 'running', label: '运行状态', icon: <PlayRegular /> },
  { key: 'accounts', label: '账户', icon: <PersonRegular /> },
  { key: 'plugins', label: '插件', icon: <PuzzleCubeRegular /> },
  { key: 'nova', label: 'Nova', icon: <ChatRegular /> },
  { key: 'logs', label: '启动器日志', icon: <ArrowSyncRegular />, footer: true },
  { key: 'settings', label: '设置', icon: <SettingsRegular />, footer: true }
]

export default function App() {
  const styles = useStyles()
  const [page, setPage] = useState<PageKey>('home')
  const [hovered, setHovered] = useState<string | null>(null)
  const [version, setVersion] = useState('')
  const session = useGameSession()

  useEffect(() => {
    injectKeyframes()
    injectGlobalStyle()
    void window.bridge.appVersion().then(setVersion).catch(() => setVersion(''))
  }, [])

  // Ctrl+1..9 / Ctrl+0 快速切页；Ctrl+R 刷新当前页数据由页面自行处理
  useEffect(() => {
    const onKey = (e: KeyboardEvent): void => {
      if (!e.ctrlKey && !e.metaKey) return
      const order: PageKey[] = [
        'home', 'versions', 'download', 'saves', 'multiplayer',
        'lab', 'running', 'accounts', 'plugins', 'nova'
      ]
      if (e.key >= '1' && e.key <= '9') {
        const target = order[Number(e.key) - 1]
        if (target !== undefined) {
          e.preventDefault()
          setPage(target)
        }
      } else if (e.key === '0') {
        e.preventDefault()
        setPage('settings')
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const top = useMemo(() => NAV.filter((n) => !n.footer), [])
  const footer = useMemo(() => NAV.filter((n) => n.footer), [])

  const PAGE_LABEL: Record<PageKey, string> = {
    home: '首页',
    versions: '版本',
    download: '下载',
    saves: '存档',
    multiplayer: '联机',
    lab: '实验室',
    running: '运行状态',
    accounts: '账户',
    plugins: '插件',
    nova: 'Nova',
    logs: '启动器日志',
    settings: '设置'
  }

  const renderPage = () => {
    switch (page) {
      case 'home':
        return <HomePage session={session} />
      case 'versions':
        return <VersionsPage />
      case 'download':
        return <DownloadPage />
      case 'saves':
        return <SavesPage />
      case 'multiplayer':
        return <MultiplayerPage />
      case 'lab':
        return <LabPage />
      case 'running':
        return <RunningPage session={session} />
      case 'accounts':
        return <AccountsPage />
      case 'plugins':
        return <PluginsPage />
      case 'nova':
        return <NovaPage />
      case 'logs':
        return <LogsPage />
      case 'settings':
        return <SettingsPage />
      default:
        return <HomePage session={session} />
    }
  }

  const item = (entry: NavEntry) => {
    const active = page === entry.key
    const cls = [
      styles.navItem,
      active ? styles.navItemActive : '',
      hovered === entry.key && !active ? styles.navItemHover : '',
      active && hovered === entry.key ? styles.navItemActiveHover : ''
    ]
      .filter(Boolean)
      .join(' ')
    return (
      <button
        key={entry.key}
        className={cls}
        onClick={() => setPage(entry.key)}
        onMouseEnter={() => setHovered(entry.key)}
        onMouseLeave={() => setHovered(null)}
      >
        <span style={{ display: 'flex', fontSize: '16px' }}>{entry.icon}</span>
        <span style={{ flex: 1, textAlign: 'left' }}>{entry.label}</span>
        {entry.key === 'running' && session.pid !== undefined && (
          <Badge appearance="filled" color="success" size="tiny" />
        )}
      </button>
    )
  }

  return (
    <div className={styles.shell}>
      <nav className={styles.nav}>
        <div className={styles.brand}>
          <div className={styles.brandMark}>S</div>
          <div style={{ minWidth: 0 }}>
            <Text weight="bold" size={300} style={{ display: 'block' }}>
              Starcloud
            </Text>
            <Text size={100} style={{ opacity: 0.55 }}>
              {version ? `v${version}` : 'Linux'}
            </Text>
          </div>
        </div>
        <div className={styles.navList}>{top.map(item)}</div>
        <div className={styles.navList}>{footer.map(item)}</div>
      </nav>
      <main className={styles.content}>
        <ErrorBoundary key={page} label={PAGE_LABEL[page]}>
          <PageFade keyId={page}>{renderPage()}</PageFade>
        </ErrorBoundary>
      </main>
    </div>
  )
}
