/// 下载页：对齐 Win 端 DownloadPage —— 左侧子导航（原版游戏 / 社区资源）
/// + 右侧分段（正式版/快照版/远古版本）+ 搜索 + 缓存 + 两张推荐卡 + 版本列表。
/// 安装走「先受理 + 事件推进」：request 立即返回，进度由 install.* 事件驱动。
import { useCallback, useEffect, useMemo, useState } from 'react'

import { onBridgeEvent, request } from '../bridge'
import type { RemoteVersion, VersionCatalog } from '../bridge'
import { EmptyState, ProgressBar, SkeletonList, useToasts } from '../ui'

import grassIcon from '../assets/mc-icons/natural_grass_block.png'
import amethystIcon from '../assets/mc-icons/block_of_amethyst.png'
import cobbleIcon from '../assets/mc-icons/masonry_cobblestone.png'
import dirtIcon from '../assets/mc-icons/natural_dirt.png'
import { IconHome, IconCube, IconList, IconFolder, IconSparkle, IconRefresh, IconChevronRight } from '../icons'

interface InstallProgress {
  versionId: string
  stage: string
  detail: string
  current: number
  total: number
}

type Channel = 'release' | 'snapshot' | 'ancient'

const CHANNEL_LABEL: Record<Channel, string> = {
  release: '正式版',
  snapshot: '快照版',
  ancient: '远古版本'
}

const TYPE_ICON: Record<string, string> = {
  release: grassIcon,
  snapshot: amethystIcon,
  old_beta: cobbleIcon,
  old_alpha: dirtIcon
}

const TYPE_LABEL: Record<string, string> = {
  release: '正式版',
  snapshot: '快照版',
  old_beta: '远古 Beta',
  old_alpha: '远古 Alpha'
}

/** 版本 id → 图标（远古版本按 id 前缀区分 beta/alpha）。 */
function iconFor(v: RemoteVersion): string {
  const t = v.type.toLowerCase()
  if (t === 'old_beta' || t === 'old_alpha') return TYPE_ICON[t]
  return TYPE_ICON[t] ?? grassIcon
}

function typeLabel(v: RemoteVersion): string {
  const t = v.type.toLowerCase()
  return TYPE_LABEL[t] ?? t
}

/** 只取日期（发布时间带时区后缀，展示反而噪声）。 */
function releaseDate(iso?: string): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '—'
  const two = (n: number): string => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`
}

function releaseDateTime(iso?: string): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '—'
  const two = (n: number): string => n.toString().padStart(2, '0')
  return `${releaseDate(iso)} ${two(d.getHours())}:${two(d.getMinutes())}`
}

function channelOf(v: RemoteVersion): Channel {
  const t = v.type.toLowerCase()
  if (t === 'old_beta' || t === 'old_alpha') return 'ancient'
  return t === 'snapshot' ? 'snapshot' : 'release'
}

export default function DownloadPage(): JSX.Element {
  const { push, host } = useToasts()
  const [catalog, setCatalog] = useState<VersionCatalog>({ remote: [], installed: [] })
  const [loading, setLoading] = useState(true)
  const [keyword, setKeyword] = useState('')
  const [channel, setChannel] = useState<Channel>('release')
  const [progress, setProgress] = useState<InstallProgress | null>(null)
  /** 社区资源子页（Mod / 整合包 / 数据包 / 资源包 / 光影包）：Win 端左侧列表 */
  const [section, setSection] = useState<string>('vanilla')

  const installedIds = useMemo(
    () => new Set(catalog.installed.map((i) => i.id)),
    [catalog.installed]
  )

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const data = await request<VersionCatalog>('catalog.versions')
      setCatalog(data)
    } catch (e) {
      push('error', `版本清单拉取失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  // 安装进度事件流
  useEffect(() => {
    let alive = true
    onBridgeEvent((event, params) => {
      if (!alive) return
      if (event === 'install.progress') {
        setProgress({
          versionId: String(params.versionId ?? ''),
          stage: String(params.stage ?? ''),
          detail: String(params.detail ?? ''),
          current: Number(params.current ?? 0),
          total: Number(params.total ?? 0)
        })
      } else if (event === 'install.done') {
        const id = String(params.versionId ?? '')
        setProgress(null)
        push('success', `${id} 安装完成`)
        void load()
      } else if (event === 'install.failed') {
        const id = String(params.versionId ?? '')
        setProgress(null)
        push('error', `${id} 安装失败：${String(params.error ?? '未知错误')}`)
        void load()
      }
    })
    return () => {
      alive = false
    }
  }, [push, load])

  const installingId = progress?.versionId ?? ''
  const busy = progress !== null

  async function install(versionId: string): Promise<void> {
    if (busy) return
    setProgress({ versionId, stage: '准备中', detail: '', current: 0, total: 0 })
    try {
      await request<{ accepted: boolean }>('install.vanilla', { versionId })
    } catch (e) {
      setProgress(null)
      push('error', `安装请求被拒绝：${String(e)}`)
    }
  }

  // 最新正式版 / 最新预览版（推荐卡）
  const latestRelease = catalog.remote.find((v) => v.type.toLowerCase() === 'release')
  const latestSnapshot = catalog.remote.find((v) => v.type.toLowerCase() === 'snapshot')

  const filtered = useMemo(() => {
    const q = keyword.trim().toLowerCase()
    return catalog.remote.filter((v) => {
      if (channelOf(v) !== channel) return false
      if (q !== '' && !v.id.toLowerCase().includes(q)) return false
      return true
    })
  }, [catalog.remote, channel, keyword])

  const subNav: Array<{ id: string; label: string; icon: React.ReactNode; group?: string }> = [
    { id: 'vanilla', label: '原版游戏', icon: <IconHome size={15} /> },
    { id: 'mod', label: 'Mod', icon: <IconCube size={15} />, group: '社区资源' },
    { id: 'modpack', label: '整合包', icon: <IconList size={15} /> },
    { id: 'datapack', label: '数据包', icon: <IconList size={15} /> },
    { id: 'resourcepack', label: '资源包', icon: <IconFolder size={15} /> },
    { id: 'shader', label: '光影包', icon: <IconSparkle size={15} /> }
  ]

  return (
    <div className="page">
      <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: 16 }}>
        {/* 左侧子导航卡（Win 端 260 宽） */}
        <div className="card" style={{ width: 240, flexShrink: 0, padding: 8 }}>
          {subNav.map((item) => (
            <div key={item.id}>
              {item.group !== undefined && (
                <div className="t-cap" style={{ padding: '8px 10px 4px' }}>
                  {item.group}
                </div>
              )}
              <button
                className={`nav-item${section === item.id ? ' nav-active' : ''}`}
                style={{ height: 38, width: '100%' }}
                onClick={() => {
                  setSection(item.id)
                  if (item.id !== 'vanilla') {
                    push('info', `${item.label}：内容检索接入后可用`)
                  }
                }}
              >
                <span className="nav-icon" style={{ fontFamily: 'inherit', fontSize: 14 }}>
                  {item.icon}
                </span>
                <span>{item.label}</span>
              </button>
            </div>
          ))}
        </div>

        {/* 右侧内容 */}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
          {/* 顶部：分段 + 搜索 + 缓存 */}
          <div style={{ display: 'flex', gap: 10, alignItems: 'center', marginBottom: 12 }}>
            <div className="seg" style={{ width: 330, gridTemplateColumns: 'repeat(3, 1fr)' }}>
              <div
                className="seg-ind"
                style={{
                  width: 'calc(33.333% - 2px)',
                  // translateX 的百分比相对指示器自身宽度（= 一格）
                  transform: `translateX(calc(2px + ${
                    (channel === 'release' ? 0 : channel === 'snapshot' ? 1 : 2) * 100
                  }% + ${
                    (channel === 'release' ? 0 : channel === 'snapshot' ? 1 : 2) * 2
                  }px))`
                }}
              />
              {(['release', 'snapshot', 'ancient'] as Channel[]).map((c) => (
                <button
                  key={c}
                  className={`seg-item${channel === c ? ' seg-active' : ''}`}
                  onClick={() => setChannel(c)}
                >
                  {CHANNEL_LABEL[c]}
                </button>
              ))}
            </div>
            <input
              className="input"
              style={{ flex: 1, minWidth: 0 }}
              placeholder="搜索版本 ID…"
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
            />
            <button className="btn btn-primary btn-sm" onClick={() => void load()} disabled={loading}>
              缓存
            </button>
          </div>

          {/* 推荐卡：最新正式版 / 最新预览版 */}
          {!loading && (latestRelease !== undefined || latestSnapshot !== undefined) && (
            <div style={{ display: 'flex', gap: 12, marginBottom: 12 }}>
              {[latestRelease, latestSnapshot].map((v, i) =>
                v === undefined ? null : (
                  <div
                    key={v.id}
                    className="card glow"
                    style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 12, cursor: 'pointer' }}
                    onClick={() => setKeyword(v.id)}
                  >
                    <div className="icon32" style={{ width: 40, height: 40 }}>
                      <img src={iconFor(v)} alt="" width={40} height={40} />
                    </div>
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <div className="t-cap">{i === 0 ? '最新正式版' : '最新预览版'}</div>
                      <div className="t-strong truncate">{v.id}</div>
                      <div className="t-meta">发布于 {releaseDateTime(v.releaseTime)}</div>
                    </div>
                    <IconChevronRight size={15} style={{ opacity: 0.5 }} />
                  </div>
                )
              )}
            </div>
          )}

          {/* 安装进度条 */}
          {progress !== null && (
            <div className="info info-info" style={{ marginBottom: 12 }}>
              <span className="spinner" aria-hidden />
              <div className="info-body">
                <div className="t-body">
                  正在安装 {progress.versionId} · {progress.stage}
                  {progress.total > 0 && (
                    <span style={{ opacity: 0.75 }}>
                      {' '}
                      {progress.current}/{progress.total}
                    </span>
                  )}
                </div>
                {progress.total > 0 && (
                  <div style={{ marginTop: 8 }}>
                    <ProgressBar value={(progress.current / progress.total) * 100} />
                  </div>
                )}
              </div>
            </div>
          )}

          {/* 版本列表（满高滚动） */}
          <div className="card card-flush" style={{ flex: 1, minHeight: 0 }}>
            <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', overflowX: 'hidden', padding: '6px 10px 10px' }}>
              {loading && <SkeletonList rows={7} avatar />}
              {!loading && filtered.length === 0 && (
                <EmptyState
                  title={catalog.remote.length === 0 ? '版本清单为空' : '没有匹配的版本'}
                  hint={
                    catalog.remote.length === 0
                      ? '点「缓存」重新拉取官方清单（需要网络）。'
                      : '换个关键词或切换上面的版本类型。'
                  }
                  action={
                    <button className="btn" onClick={() => void load()}>
                      <IconRefresh size={14} /> 重新拉取
                    </button>
                  }
                />
              )}
              {!loading &&
                filtered.map((v) => {
                  const installed = installedIds.has(v.id)
                  const installing = installingId === v.id
                  const dimmed = busy && !installing
                  return (
                    <div
                      key={v.id}
                      className={`row row-static${installing ? ' row-active' : ''}`}
                      style={{ opacity: dimmed ? 0.5 : 1 }}
                    >
                      <div className="icon32">
                        <img src={iconFor(v)} alt="" width={32} height={32} />
                      </div>
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div className="t-strong truncate">{v.id}</div>
                        <div className="t-meta">
                          {releaseDate(v.releaseTime)} · {typeLabel(v)}
                        </div>
                      </div>
                      {installing ? (
                        <div style={{ width: 150, flexShrink: 0, display: 'flex', alignItems: 'center' }}>
                          <ProgressBar
                            value={
                              progress !== null && progress.total > 0
                                ? Math.min(100, (progress.current / progress.total) * 100)
                                : undefined
                            }
                            indeterminate={progress !== null && progress.total === 0}
                          />
                        </div>
                      ) : installed ? (
                        <span
                          className="chip"
                          style={{ color: 'var(--success)', background: 'transparent', border: 'none' }}
                        >
                          已安装
                        </span>
                      ) : (
                        <button
                          className="btn btn-sm"
                          disabled={dimmed}
                          onClick={() => void install(v.id)}
                        >
                          ↓ 安装
                        </button>
                      )}
                    </div>
                  )
                })}
            </div>
          </div>
        </div>
      </div>
      {host}
    </div>
  )
}
