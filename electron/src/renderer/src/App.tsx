/// 应用壳：窗口骨架 + 自绘标题栏 + 侧边导航 + 内容区。
/// 主题跟随系统（Win 端 nativeTheme → 这里 data-theme）。
import { useEffect, useState } from 'react'

import { appVersion as readAppVersion } from './bridge'

import Nav, { PAGE_LABEL, type PageKey } from './nav'
import { useTheme } from './theme'
import TitleBar from './titlebar'
import { ErrorBoundary } from './ui'
import { useGameSession } from './state/session'
import HomePage from './pages/Home'
import VersionsPage from './pages/Versions'
import DownloadPage from './pages/Download'
import SavesPage from './pages/Saves'
import MultiplayerPage from './pages/Multiplayer'
import LabPage from './pages/Lab'
import ProjectionsPage from './pages/Projections'
import RunningPage from './pages/Running'
import AccountsPage from './pages/Accounts'
import PluginsPage from './pages/Plugins'
import NovaPage from './pages/Nova'
import SettingsPage from './pages/Settings'
import LogsPage from './pages/Logs'

import brandMark from './assets/brand/starcloud-mark-small.svg'

export default function App(props: { initialTheme: import("./theme").ResolvedTheme }): JSX.Element {
  const theme = useTheme(props.initialTheme)
  const [page, setPage] = useState<PageKey>('home')
  const session = useGameSession()
  const [appVersion, setAppVersion] = useState('')

  useEffect(() => {
    void readAppVersion()
      .then(setAppVersion)
      .catch(() => undefined)
  }, [])
  const running = session.pid !== undefined && session.pid > 0

  useEffect(() => {
    const onKey = (e: KeyboardEvent): void => {
      if (!e.ctrlKey && !e.metaKey) return
      const order: PageKey[] = [
        'home',
        'versions',
        'download',
        'saves',
        'multiplayer',
        'lab',
        'projections',
        'running',
        'accounts',
        'plugins'
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

  const render = (): React.ReactNode => {
    switch (page) {
      case 'home':
        return (
          <HomePage
            session={session}
            onNavigate={(target) => setPage(target)}
          />
        )
      case 'versions':
        return (
          <VersionsPage
            onOpenDetail={(id) => setPage('versions')}
            onOpenSettings={() => setPage('settings')}
          />
        )
      case 'download':
        return <DownloadPage />
      case 'saves':
        return <SavesPage />
      case 'multiplayer':
        return <MultiplayerPage />
      case 'lab':
        return <LabPage />
      case 'projections':
        return <ProjectionsPage />
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
        return <HomePage session={session} onNavigate={(t) => setPage(t)} />
    }
  }

  return (
    <div className="win mica">
      <TitleBar
        title={PAGE_LABEL[page]}
        themeMode={theme.mode}
        onCycleTheme={theme.cycle}
      >
        <span className="tb-brand">
          <img src={brandMark} alt="" width={18} height={18} />
        </span>
        <span className="tb-appname">Starcloud</span>
        <span className="tb-ver">{appVersion !== '' ? `开发版 V${appVersion}` : 'Linux'}</span>
      </TitleBar>
      <div className="win-body">
        <Nav page={page} dark={theme.resolved === 'dark'} running={running} onNavigate={setPage} />
        <main className="content">
          <ErrorBoundary key={page} label={PAGE_LABEL[page]}>
            <div className="page" key={page}>
              {render()}
            </div>
          </ErrorBoundary>
        </main>
      </div>
    </div>
  )
}
