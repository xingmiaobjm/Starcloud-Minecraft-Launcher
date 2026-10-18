/// 下载页：拉取官方版本清单、搜索过滤、异步安装（含进度事件流）。
/// 安装走「先受理 + 事件推进」的模型：request 立即返回 accepted，进度由
/// install.progress / install.done / install.failed 三个事件驱动。
import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  ProgressBar,
  Switch,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  ArrowDownloadRegular,
  ArrowSyncRegular,
  CheckmarkCircleRegular,
  SearchRegular
} from '@fluentui/react-icons'
import { onBridgeEvent, request } from '../bridge'
import type { RemoteVersion, VersionCatalog } from '../bridge'
import { brandYellow, motion } from '../theme'
import { EmptyState, PageShell, SkeletonList, useToasts } from '../ui'

const useStyles = makeStyles({
  toolbar: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: '16px',
    padding: '0 0 12px'
  },
  searchBox: { width: '320px' },
  switchBox: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    paddingBottom: '6px'
  },
  summary: {
    fontSize: '12px',
    opacity: 0.6,
    paddingBottom: '7px'
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '9px 12px',
    margin: '0 4px',
    borderRadius: '6px',
    border: '1.2px solid transparent',
    transition: `background ${motion.fast}ms ${motion.ease}, border-color ${motion.fast}ms ${motion.ease}`,
    animation: `rowIn ${motion.normal}ms ${motion.easeOut} both`
  },
  rowHover: { backgroundColor: 'rgba(255,255,255,0.055)' },
  rowActive: {
    backgroundColor: 'rgba(255,194,31,0.1)',
    border: `1.2px solid ${brandYellow}`
  },
  rowDisabled: { opacity: 0.5 },
  versionIcon: {
    width: '34px',
    height: '34px',
    borderRadius: '6px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontWeight: 'bold',
    fontSize: '14px',
    flexShrink: 0
  },
  installButton: {
    height: '30px',
    minWidth: '92px',
    borderRadius: '6px',
    border: 'none',
    background: brandYellow,
    color: '#1b1b1b',
    fontWeight: 600,
    cursor: 'pointer',
    transition: `background ${motion.fast}ms ${motion.ease}, opacity ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: '#FFD35C' }
  },
  installButtonBusy: { opacity: 0.55, cursor: 'default' },
  installedButton: {
    height: '30px',
    minWidth: '92px',
    borderRadius: '6px',
    border: '1px solid rgba(63,191,111,0.5)',
    background: 'rgba(63,191,111,0.12)',
    color: '#7BD9A0',
    fontWeight: 600,
    cursor: 'default'
  },
  rowProgress: { width: '150px', flexShrink: 0 }
})

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
  const styles = useStyles()
  const { push, host } = useToasts()
  const [catalog, setCatalog] = useState<VersionCatalog>({ remote: [], installed: [] })
  const [loading, setLoading] = useState(true)
  const [keyword, setKeyword] = useState('')
  const [releaseOnly, setReleaseOnly] = useState(true)
  const [hovered, setHovered] = useState<string | null>(null)
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
    // 先乐观标记，让 InfoBar 立刻有内容，随后由事件流覆盖真实进度。
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
          <Button
            appearance="subtle"
            icon={<ArrowSyncRegular />}
            onClick={() => void load()}
            disabled={loading}
          />
        }
      >
        <div className={styles.toolbar}>
          <Field className={styles.searchBox} label="搜索版本号" size="small">
            <Input
              value={keyword}
              onChange={(_e, data) => setKeyword(data.value)}
              contentAfter={<SearchRegular />}
              placeholder="例如 1.21"
            />
          </Field>
          <div className={styles.switchBox}>
            <Switch
              checked={releaseOnly}
              label="只看正式版"
              onChange={(_e, data) => setReleaseOnly(data.checked)}
            />
          </div>
          <div style={{ flex: 1 }} />
          <Text className={styles.summary}>
            命中 {filtered.length} 条
          </Text>
        </div>

        {progress && (
          <MessageBar
            intent="info"
            style={{ marginBottom: '12px', animation: `fadeIn ${motion.normal}ms ${motion.ease} both` }}
          >
            <MessageBarBody>
              正在安装 {progress.versionId} · {progress.stage}
              {progress.total > 0 && (
                <span style={{ opacity: 0.75 }}>
                  {' '}
                  {progress.current}/{progress.total}（{progress.detail}）
                </span>
              )}
            </MessageBarBody>
          </MessageBar>
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
              <Button appearance="secondary" icon={<ArrowSyncRegular />} onClick={() => void load()}>
                重新拉取
              </Button>
            }
          />
        )}

        {!loading &&
          filtered.map((version, index) => {
            const installed = installedIds.has(version.id)
            const installing = installingId === version.id
            const color = typeColor(version)
            return (
              <div
                key={version.id}
                className={[
                  styles.row,
                  hovered === version.id && !installing ? styles.rowHover : '',
                  installing ? styles.rowActive : '',
                  busy && !installing ? styles.rowDisabled : ''
                ]
                  .filter(Boolean)
                  .join(' ')}
                style={{ animationDelay: `${index * 24}ms` }}
                onMouseEnter={() => setHovered(version.id)}
                onMouseLeave={() => setHovered(null)}
              >
                <div
                  className={styles.versionIcon}
                  style={{ background: `${color}2b`, border: `1px solid ${color}` }}
                >
                  {installed ? <CheckmarkCircleRegular /> : version.id[0]?.toUpperCase()}
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Text size={300} weight="semibold" truncate>
                    {version.id}
                  </Text>
                </div>
                <Text size={200} style={{ color, width: '52px' }}>
                  {typeLabel(version)}
                </Text>
                <div style={{ width: '104px', textAlign: 'right' }}>
                  <Text size={200} style={{ opacity: 0.6 }}>
                    {releaseDate(version.releaseTime)}
                  </Text>
                </div>
                {installing ? (
                  <div className={styles.rowProgress}>
                    <ProgressBar
                      value={progress && progress.total > 0 ? progress.current / progress.total : undefined}
                    />
                  </div>
                ) : null}
                {installed && !installing ? (
                  <div className={styles.installedButton} style={{ display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                    已安装
                  </div>
                ) : (
                  <button
                    className={[
                      styles.installButton,
                      busy && !installing ? styles.installButtonBusy : ''
                    ]
                      .filter(Boolean)
                      .join(' ')}
                    disabled={busy && !installing}
                    onClick={() => void install(version.id)}
                  >
                    <span
                      style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '6px' }}
                    >
                      <ArrowDownloadRegular />
                      {installing ? '安装中' : '安装'}
                    </span>
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
