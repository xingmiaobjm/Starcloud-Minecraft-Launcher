/// 下载页：拉取官方版本清单、搜索过滤、异步安装（含进度事件流）。
/// 安装走「先受理 + 事件推进」的模型：request 立即返回 accepted，进度由
/// install.progress / install.done / install.failed 三个事件驱动。
/// 视觉全部走 winui.css 的纯 CSS 类，不依赖 Fluent 组件。
import { useCallback, useEffect, useMemo, useState } from 'react'
import { onBridgeEvent, request } from '../bridge'
import type { RemoteVersion, VersionCatalog } from '../bridge'
import { EmptyState, PageShell, ProgressBar, SkeletonList, useToasts } from '../ui'

/** 单个安装任务的进度快照。 */
interface InstallProgress {
  versionId: string
  stage: string
  detail: string
  current: number
  total: number
}

function isRelease(version: RemoteVersion): boolean {
  return version.type.toLowerCase() === 'release'
}

function typeLabel(version: RemoteVersion): string {
  return version.type.toLowerCase() === 'release' ? '正式版' : '快照版'
}

function typeColor(version: RemoteVersion): string {
  return isRelease(version) ? '#6FA852' : '#8A6FD8'
}

/** 只取日期部分，发布时间过长且带时区后缀，展示反而噪声。 */
function releaseDate(iso?: string): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '—'
  const two = (n: number) => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`
}

export default function DownloadPage() {
  const { push, host } = useToasts()
  const [catalog, setCatalog] = useState<VersionCatalog>({ remote: [], installed: [] })
  const [loading, setLoading] = useState(true)
  const [keyword, setKeyword] = useState('')
  const [releaseOnly, setReleaseOnly] = useState(true)
  const [progress, setProgress] = useState<InstallProgress | null>(null)

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

  // 安装进度全部走事件流：request 已返回 accepted，这里只负责渲染与终态收敛。
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
        const versionId = String(params.versionId ?? '')
        setProgress(null)
        push('success', `${versionId} 安装完成`)
        void load()
      } else if (event === 'install.failed') {
        const versionId = String(params.versionId ?? '')
        const error = String(params.error ?? '未知错误')
        setProgress(null)
        push('error', `${versionId} 安装失败：${error}`)
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
    // 先乐观标记，让提示条立刻有内容，随后由事件流覆盖真实进度。
    setProgress({ versionId, stage: '准备中', detail: '', current: 0, total: 0 })
    try {
      await request<{ accepted: boolean }>('install.vanilla', { versionId })
    } catch (e) {
      setProgress(null)
      push('error', `安装请求被拒绝：${String(e)}`)
    }
  }

  const filtered = catalog.remote.filter((v) => {
    if (releaseOnly && !isRelease(v)) return false
    if (keyword.trim() !== '' && !v.id.toLowerCase().includes(keyword.trim().toLowerCase())) {
      return false
    }
    return true
  })

  return (
    <>
      <PageShell
        title="下载版本"
        subtitle={`官方清单 ${catalog.remote.length} 个版本，已安装 ${catalog.installed.length} 个`}
        actions={
          <button className="btn btn-subtle" onClick={() => void load()} disabled={loading}>
            ↻
          </button>
        }
      >
        <div style={{ display: 'flex', alignItems: 'flex-end', gap: 16, paddingBottom: 12 }}>
          <div style={{ width: 320 }}>
            <div className="t-cap" style={{ marginBottom: 4 }}>
              搜索版本号
            </div>
            <input
              className="input"
              style={{ width: '100%' }}
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
              placeholder="例如 1.21"
            />
          </div>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8, paddingBottom: 6 }}>
            <input
              type="checkbox"
              checked={releaseOnly}
              onChange={(e) => setReleaseOnly(e.target.checked)}
              style={{ accentColor: 'var(--accent)', width: 15, height: 15 }}
            />
            <span className="t-body">只看正式版</span>
          </label>
          <div style={{ flex: 1 }} />
          <span className="t-cap" style={{ paddingBottom: 7 }}>
            命中 {filtered.length} 条
          </span>
        </div>

        {progress && (
          <div className="info info-info" style={{ marginBottom: 12 }}>
            <span className="spinner" aria-hidden />
            <div className="info-body">
              <div className="t-body">
                正在安装 {progress.versionId} · {progress.stage}
                {progress.total > 0 && (
                  <span style={{ opacity: 0.75 }}>
                    {' '}
                    {progress.current}/{progress.total}（{progress.detail}）
                  </span>
                )}
              </div>
              {progress.total > 0 && (
                <div style={{ marginTop: 8 }}>
                  <ProgressBar
                    value={(progress.current / progress.total) * 100}
                  />
                </div>
              )}
            </div>
          </div>
        )}

        {loading && <SkeletonList rows={7} />}
        {!loading && filtered.length === 0 && (
          <EmptyState
            title={catalog.remote.length === 0 ? '版本清单为空' : '没有匹配的版本'}
            hint={
              catalog.remote.length === 0
                ? '点右上角刷新按钮重新拉取官方清单。'
                : '换个关键词，或关掉「只看正式版」再试试。'
            }
            action={
              <button className="btn" onClick={() => void load()}>
                ↻ 重新拉取
              </button>
            }
          />
        )}

        {!loading &&
          filtered.map((version, index) => {
            const installed = installedIds.has(version.id)
            const installing = installingId === version.id
            const color = typeColor(version)
            const dimmed = busy && !installing
            return (
              <div
                key={version.id}
                className={`row row-static${installing ? ' row-active' : ''}`}
                style={{ animationDelay: `${index * 39}ms`, opacity: dimmed ? 0.5 : 1 }}
              >
                <div
                  className="icon32"
                  style={{ background: `${color}2b`, border: `1px solid ${color}`, color }}
                >
                  {installed ? '✓' : version.id[0]?.toUpperCase()}
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <span className="t-strong truncate" style={{ display: 'block' }}>
                    {version.id}
                  </span>
                </div>
                <span className="t-meta" style={{ color, width: 52 }}>
                  {typeLabel(version)}
                </span>
                <span className="t-meta" style={{ width: 104, textAlign: 'right' }}>
                  {releaseDate(version.releaseTime)}
                </span>
                {installing ? (
                  <div style={{ width: 150, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8 }}>
                    <ProgressBar
                      value={
                        progress && progress.total > 0
                          ? Math.min(100, (progress.current / progress.total) * 100)
                          : undefined
                      }
                    />
                  </div>
                ) : installed ? (
                  <span className="chip chip-accent" style={{ minWidth: 92, justifyContent: 'center' }}>
                    已安装
                  </span>
                ) : (
                  <button
                    className="btn btn-primary"
                    style={{ minWidth: 92 }}
                    disabled={dimmed}
                    onClick={() => void install(version.id)}
                  >
                    ↓ {installing ? '安装中' : '安装'}
                  </button>
                )}
              </div>
            )
          })}
      </PageShell>
      {host}
    </>
  )
}
