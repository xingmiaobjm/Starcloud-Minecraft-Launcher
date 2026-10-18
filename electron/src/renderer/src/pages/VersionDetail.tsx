/// 版本详情：实例信息 + 模组/资源包/光影管理 + Modrinth 安装 + 实例参数 + 更新/依赖检查。
///
/// 交互对齐 Flutter 版（version_detail_page.dart），差异：
/// - 圆角/动效走 Win 端设计令牌（theme.ts）
/// - content.progress 走全局事件订阅（Electron 端没有回调参数）
import { useCallback, useEffect, useState } from 'react'
import {
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Dropdown,
  Input,
  Option,
  ProgressBar,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  ArrowDownloadRegular,
  ArrowSyncRegular,
  DeleteRegular,
  FolderOpenRegular,
  SettingsRegular
} from '@fluentui/react-icons'
import { onBridgeEvent, request } from '../bridge'
import type {
  ContentFile,
  ContentInstallResult,
  ContentKind,
  ContentSearchHit,
  ContentUpdate,
  DependencyReport,
  VersionDetail,
  VersionSettings
} from '../bridge'
import { brandYellow, motion, radius } from '../theme'
import { EmptyState, PageShell, PointerGlow, SkeletonList, useToasts } from '../ui'

const useStyles = makeStyles({
  info: { padding: '18px 20px', marginBottom: '12px' },
  bar: { display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap', marginBottom: '12px' },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '10px 14px',
    marginBottom: '8px',
    background: 'rgba(255,255,255,0.035)',
    animation: `rowIn ${motion.enter}ms ${motion.easeEnter} both`
  },
  chip: {
    fontSize: '11px',
    padding: '1px 7px',
    borderRadius: '9px',
    background: 'rgba(255,255,255,0.07)',
    color: 'rgba(255,255,255,0.75)'
  }
})

const KIND_LABEL: Record<ContentKind, string> = {
  mod: '模组',
  resourcepack: '资源包',
  shader: '光影'
}

function fmtSize(bytes: number): string {
  if (!bytes) return '大小未知'
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function fmtTime(iso?: string): string {
  if (iso === undefined || iso === null || iso === '') return '未记录'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '未记录'
  const p = (n: number): string => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}

export default function VersionDetailPage(props: {
  versionId: string
  onBack: () => void
}) {
  const styles = useStyles()
  const { push, host } = useToasts()
  const [detail, setDetail] = useState<VersionDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)
  const [kind, setKind] = useState<ContentKind>('mod')
  const [confirmDelete, setConfirmDelete] = useState<ContentFile | null>(null)
  const [showSettings, setShowSettings] = useState(false)
  const [updates, setUpdates] = useState<ContentUpdate[] | null>(null)
  const [deps, setDeps] = useState<DependencyReport | null>(null)
  const [installOpen, setInstallOpen] = useState(false)
  const [progress, setProgress] = useState('')

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      setDetail(await request<VersionDetail>('version.detail', { versionId: props.versionId }))
    } catch (e) {
      push('error', `读取版本详情失败：${String(e)}`)
    } finally {
      setLoading(false)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.versionId])

  useEffect(() => {
    void load()
  }, [load])

  // 内容安装进度（content.install / version.updateMod 都会发）
  useEffect(() => {
    let alive = true
    onBridgeEvent((event, params) => {
      if (!alive || event !== 'content.progress') return
      const stage = String(params.stage ?? '')
      if (stage !== '') setProgress(stage)
    })
    return () => {
      alive = false
    }
  }, [])

  const filesFor = (d: VersionDetail | null, k: ContentKind): ContentFile[] => {
    if (d === null) return []
    return k === 'mod' ? d.mods : k === 'resourcepack' ? d.resourcepacks : d.shaderpacks
  }

  const replaceFiles = (files: ContentFile[]): void => {
    setDetail((prev) => {
      if (prev === null) return prev
      const k = kind
      if (k === 'mod') return { ...prev, mods: files }
      if (k === 'resourcepack') return { ...prev, resourcepacks: files }
      return { ...prev, shaderpacks: files }
    })
  }

  async function toggle(file: ContentFile): Promise<void> {
    setBusy(file.fileName)
    try {
      const r = await request<{ files: ContentFile[] }>('version.toggleContent', {
        versionId: props.versionId,
        kind,
        fileName: file.fileName,
        enabled: !file.enabled
      })
      replaceFiles(r.files)
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function remove(file: ContentFile): Promise<void> {
    setBusy(file.fileName)
    try {
      const r = await request<{ files: ContentFile[] }>('version.deleteContent', {
        versionId: props.versionId,
        kind,
        fileName: file.fileName
      })
      replaceFiles(r.files)
      push('success', `已删除 ${file.displayName}`)
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function openFolder(): Promise<void> {
    try {
      await request('version.openContentFolder', { versionId: props.versionId, kind })
    } catch (e) {
      push('error', String(e))
    }
  }

  async function checkUpdates(): Promise<void> {
    setBusy('__updates__')
    try {
      const r = await request<{ updates: ContentUpdate[] }>('version.checkUpdates', {
        versionId: props.versionId
      })
      setUpdates(r.updates)
    } catch (e) {
      push('error', `检查更新失败：${String(e)}`)
    } finally {
      setBusy(null)
    }
  }

  async function checkDependencies(): Promise<void> {
    setBusy('__deps__')
    try {
      setDeps(await request<DependencyReport>('version.checkDependencies', { versionId: props.versionId }))
    } catch (e) {
      push('error', `依赖检查失败：${String(e)}`)
    } finally {
      setBusy(null)
    }
  }

  async function updateOne(u: ContentUpdate): Promise<void> {
    setBusy(u.fileName)
    try {
      await request('version.updateMod', { versionId: props.versionId, fileName: u.fileName })
      push('success', `${u.fileName} → ${u.latestVersion}`)
      await load()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  const files = filesFor(detail, kind)
  const globalBusy = busy !== null

  return (
    <PageShell
      title={`版本详情 · ${props.versionId}`}
      subtitle={
        detail === null
          ? '实例内容与参数'
          : `MC ${detail.mcVersion} · ${detail.loader}` +
            (detail.isolated ? ' · 独立目录' : ' · 共享目录')
      }
      actions={
        <>
          <Button
            appearance="subtle"
            icon={<SettingsRegular />}
            disabled={detail === null}
            onClick={() => setShowSettings(true)}
          >
            实例设置
          </Button>
          <Button
            appearance="subtle"
            icon={<FolderOpenRegular />}
            disabled={detail === null}
            onClick={() => void openFolder()}
          >
            打开目录
          </Button>
          <Button appearance="subtle" icon={<ArrowSyncRegular />} onClick={() => void load()}>
            刷新
          </Button>
        </>
      }
    >
      {loading ? (
        <SkeletonList rows={6} />
      ) : (
        <>
          {detail !== null && (
            <Card className={styles.info}>
              <div style={{ display: 'flex', gap: '26px', flexWrap: 'wrap' }}>
                <Info label="游戏版本" value={detail.mcVersion} />
                <Info
                  label="加载器"
                  value={detail.loaderVersion != null ? `${detail.loader} ${detail.loaderVersion}` : detail.loader}
                />
                <Info label="隔离" value={detail.isolated ? '独立版本目录' : '共享 .minecraft'} />
                <Info label="最近游玩" value={fmtTime(detail.lastPlayedAt)} />
                <div style={{ minWidth: '260px' }}>
                  <Text size={200} style={{ opacity: 0.6 }}>
                    实例目录
                  </Text>
                  <Text size={300} truncate>
                    {detail.gameRoot}
                  </Text>
                </div>
              </div>
            </Card>
          )}

          <div className={styles.bar}>
            {(Object.keys(KIND_LABEL) as ContentKind[]).map((k) => (
              <Button
                key={k}
                appearance={kind === k ? 'primary' : 'secondary'}
                style={kind === k ? { background: brandYellow, color: '#1b1b1b' } : undefined}
                onClick={() => setKind(k)}
              >
                {KIND_LABEL[k]}
              </Button>
            ))}
            <div style={{ flex: 1 }} />
            {kind === 'mod' && (
              <>
                <Button disabled={globalBusy} onClick={() => void checkUpdates()}>
                  检查更新
                </Button>
                <Button disabled={globalBusy} onClick={() => void checkDependencies()}>
                  检查依赖
                </Button>
              </>
            )}
            <Button
              appearance="primary"
              icon={<ArrowDownloadRegular />}
              style={{ background: brandYellow, color: '#1b1b1b' }}
              disabled={globalBusy}
              onClick={() => setInstallOpen(true)}
            >
              安装内容
            </Button>
          </div>

          {progress !== '' && (
            <Card style={{ padding: '10px 14px', marginBottom: '12px' }}>
              <Text size={200}>{progress}</Text>
            </Card>
          )}

          {files.length === 0 ? (
            <EmptyState
              title={`还没有${KIND_LABEL[kind]}`}
              hint="点「安装内容」从 Modrinth 搜索安装。"
            />
          ) : (
            files.map((file, index) => (
              <PointerGlow
                key={file.fileName}
                radius={radius.row}
                style={{ animation: `rowIn ${motion.enter}ms ${motion.easeEnter} both`, animationDelay: `${index * motion.stagger}ms` }}
              >
                <div className={styles.row}>
                  <span style={{ fontSize: '15px', opacity: file.enabled ? 1 : 0.45 }}>
                    {file.enabled ? '☑' : '☐'}
                  </span>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text size={300} weight="semibold" truncate>
                      {file.displayName}
                    </Text>
                    <Text size={200} style={{ opacity: 0.6 }}>
                      {fmtSize(file.sizeBytes)}
                      {file.enabled ? '' : ' · 已停用'}
                    </Text>
                  </div>
                  <Button disabled={globalBusy} onClick={() => void toggle(file)}>
                    {file.enabled ? '停用' : '启用'}
                  </Button>
                  <Button
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    disabled={globalBusy}
                    style={{ color: '#FF9A9A' }}
                    onClick={() => setConfirmDelete(file)}
                  >
                    删除
                  </Button>
                </div>
              </PointerGlow>
            ))
          )}
        </>
      )}

      <Dialog
        open={confirmDelete !== null}
        onOpenChange={(_e, d) => !d.open && setConfirmDelete(null)}
        modalType="alert"
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>删除 {confirmDelete?.displayName}？</DialogTitle>
            <DialogContent>文件会从版本目录永久删除，不可恢复。</DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setConfirmDelete(null)}>
                取消
              </Button>
              <Button
                appearance="primary"
                style={{ background: '#B42318' }}
                onClick={() => {
                  const t = confirmDelete
                  setConfirmDelete(null)
                  if (t !== null) void remove(t)
                }}
              >
                删除
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {updates !== null && (
        <UpdatesDialog
          updates={updates}
          busy={busy}
          onUpdate={updateOne}
          onClose={() => {
            setUpdates(null)
            void load()
          }}
        />
      )}
      {deps !== null && <DepsDialog deps={deps} versionId={props.versionId} onClose={() => setDeps(null)} />}
      {showSettings && detail !== null && (
        <InstanceSettingsDialog
          versionId={props.versionId}
          onClose={() => setShowSettings(false)}
          onSaved={() => {
            setShowSettings(false)
            void load()
          }}
        />
      )}
      {installOpen && detail !== null && (
        <SearchInstallDialog
          kind={kind}
          mcVersion={detail.mcVersion}
          loader={detail.loader}
          versionId={props.versionId}
          onInstalled={() => {
            void load()
          }}
          onClose={() => setInstallOpen(false)}
        />
      )}
      {host}
    </PageShell>
  )
}

function Info(props: { label: string; value: string }): JSX.Element {
  return (
    <div style={{ minWidth: '150px' }}>
      <Text size={200} style={{ opacity: 0.6 }}>
        {props.label}
      </Text>
      <Text size={300}>{props.value}</Text>
    </div>
  )
}

function UpdatesDialog(props: {
  updates: ContentUpdate[]
  busy: string | null
  onUpdate: (u: ContentUpdate) => Promise<void>
  onClose: () => void
}): JSX.Element {
  const [done, setDone] = useState<string[]>([])
  const updateAll = async (): Promise<void> => {
    for (const u of props.updates) {
      if (done.includes(u.fileName)) continue
      await props.onUpdate(u)
      setDone((prev) => [...prev, u.fileName])
    }
  }
  return (
    <Dialog open onOpenChange={(_e, d) => !d.open && props.onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>模组更新（{props.updates.length}）</DialogTitle>
          <DialogContent>
            <div style={{ maxHeight: '380px', overflowY: 'auto', minWidth: '520px' }}>
              {props.updates.map((u) => {
                const isDone = done.includes(u.fileName)
                return (
                  <div
                    key={u.fileName}
                    style={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: '12px',
                      padding: '8px 0'
                    }}
                  >
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <Text size={300} weight="semibold" truncate>
                        {u.fileName}
                      </Text>
                      <Text size={200} style={{ opacity: 0.65 }}>
                        {u.currentVersion} → {u.latestVersion}
                      </Text>
                    </div>
                    <Button
                      disabled={isDone || props.busy !== null}
                      onClick={() => {
                        setDone((prev) => [...prev, u.fileName])
                        void props.onUpdate(u)
                      }}
                    >
                      {isDone ? '已更新' : '更新'}
                    </Button>
                  </div>
                )
              })}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={props.onClose}>
              关闭
            </Button>
            <Button
              appearance="primary"
              style={{ background: brandYellow, color: '#1b1b1b' }}
              disabled={props.busy !== null}
              onClick={() => void updateAll()}
            >
              全部更新
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function DepsDialog(props: {
  deps: DependencyReport
  versionId: string
  onClose: () => void
}): JSX.Element {
  const [stage, setStage] = useState('')
  const [busy, setBusy] = useState(false)
  const [installed, setInstalled] = useState<number | null>(null)
  const clean = props.deps.missing.length === 0 && props.deps.incompatible.length === 0

  const installMissing = async (): Promise<void> => {
    setBusy(true)
    try {
      for (const d of props.deps.missing) {
        setStage(`安装 ${d.title}…`)
        await request<ContentInstallResult>('content.install', {
          kind: 'mod',
          projectId: d.projectId,
          title: d.title,
          targetVersionId: props.versionId
        })
      }
      setInstalled(props.deps.missing.length)
      setStage('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open onOpenChange={(_e, d) => !d.open && props.onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>依赖检查</DialogTitle>
          <DialogContent>
            <div style={{ maxHeight: '380px', overflowY: 'auto', minWidth: '520px' }}>
              {clean && (
                <Card style={{ padding: '12px', border: '1px solid rgba(63,191,111,0.4)' }}>
                  <Text size={300}>依赖完整</Text>
                  <Text size={200} style={{ opacity: 0.75 }}>
                    没有缺失的必需依赖，也没有已安装的不兼容模组。
                  </Text>
                </Card>
              )}
              {props.deps.missing.length > 0 && (
                <>
                  <Text size={300} weight="semibold" style={{ display: 'block', marginTop: '8px' }}>
                    缺失的必需依赖（{props.deps.missing.length}）
                  </Text>
                  {props.deps.missing.map((d) => (
                    <Card key={d.projectId} style={{ padding: '8px 12px', marginTop: '6px' }}>
                      <Text size={300}>{d.title}</Text>
                    </Card>
                  ))}
                </>
              )}
              {props.deps.incompatible.length > 0 && (
                <Card style={{ padding: '12px', marginTop: '10px', border: '1px solid rgba(224,75,75,0.45)' }}>
                  <Text size={300}>已安装但不兼容</Text>
                  <Text size={200} style={{ opacity: 0.8 }}>
                    {props.deps.incompatible.join('、')}
                  </Text>
                </Card>
              )}
              {props.deps.optional.length > 0 && (
                <Text size={200} style={{ opacity: 0.6, marginTop: '8px' }}>
                  可选依赖 {props.deps.optional.length} 个（未安装不影响启动）
                </Text>
              )}
              {props.deps.truncated && (
                <Text size={200} style={{ opacity: 0.6, marginTop: '6px' }}>
                  模组数量超过 200，只检查了前 200 个。
                </Text>
              )}
              {stage !== '' && (
                <Text size={200} style={{ marginTop: '8px', color: brandYellow }}>
                  {stage}
                </Text>
              )}
              {installed !== null && (
                <Text size={200} style={{ marginTop: '8px', color: '#3FBF6F' }}>
                  已安装 {installed} 个依赖
                </Text>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={props.onClose}>
              关闭
            </Button>
            {props.deps.missing.length > 0 && (
              <Button
                appearance="primary"
                style={{ background: brandYellow, color: '#1b1b1b' }}
                disabled={busy}
                onClick={() => void installMissing()}
              >
                {busy ? '安装中…' : '安装缺失依赖'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function InstanceSettingsDialog(props: {
  versionId: string
  onClose: () => void
  onSaved: () => void
}): JSX.Element {
  const [initial, setInitial] = useState<VersionSettings | null>(null)
  const [isolation, setIsolation] = useState('global')
  const [memoryMode, setMemoryMode] = useState('global')
  const [minMem, setMinMem] = useState('512')
  const [maxMem, setMaxMem] = useState('4096')
  const [javaMode, setJavaMode] = useState('global')
  const [javaPath, setJavaPath] = useState('')
  const [windowMode, setWindowMode] = useState('global')
  const [width, setWidth] = useState('854')
  const [height, setHeight] = useState('480')
  const [fullscreen, setFullscreen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    void (async () => {
      try {
        const s = await request<VersionSettings>('version.settings', { versionId: props.versionId })
        setInitial(s)
        setIsolation(s.isolation ?? 'global')
        setMemoryMode(s.memoryMode)
        setMinMem(`${s.memoryMinMb ?? s.effective.minMem}`)
        setMaxMem(`${s.memoryMaxMb ?? s.effective.maxMem}`)
        setJavaMode(s.javaMode)
        setJavaPath(s.javaPath ?? '')
        setWindowMode(s.windowMode)
        setWidth(`${s.windowWidth ?? s.effective.windowW ?? 854}`)
        setHeight(`${s.windowHeight ?? s.effective.windowH ?? 480}`)
        setFullscreen(s.fullscreen ?? s.effective.fullscreen ?? false)
      } catch (e) {
        setError(String(e))
      }
    })()
  }, [props.versionId])

  const save = async (): Promise<void> => {
    setBusy(true)
    setError('')
    try {
      await request('version.saveSettings', {
        versionId: props.versionId,
        isolation: isolation === 'global' ? null : isolation,
        memoryMode,
        memoryMinMb: memoryMode === 'manual' ? Number(minMem) : null,
        memoryMaxMb: memoryMode === 'manual' ? Number(maxMem) : null,
        javaMode,
        javaPath: javaMode === 'manual' ? javaPath : null,
        windowMode,
        windowWidth: windowMode === 'manual' ? Number(width) : null,
        windowHeight: windowMode === 'manual' ? Number(height) : null,
        fullscreen: windowMode === 'manual' ? fullscreen : null
      })
      props.onSaved()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const eff = initial?.effective
  return (
    <Dialog open onOpenChange={(_e, d) => !d.open && props.onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>实例设置 · {props.versionId}</DialogTitle>
          <DialogContent>
            <div style={{ maxHeight: '460px', overflowY: 'auto', minWidth: '520px' }}>
              <LabeledSelect
                label="版本隔离"
                value={isolation}
                options={[
                  ['global', '跟随全局'],
                  ['shared', '共享 .minecraft'],
                  ['isolated', '独立版本目录']
                ]}
                onChange={setIsolation}
              />
              <LabeledSelect
                label="内存档位"
                value={memoryMode}
                options={[
                  ['global', '跟随全局'],
                  ['manual', '自定义'],
                  ['auto', '自动推荐']
                ]}
                onChange={setMemoryMode}
              />
              {memoryMode === 'manual' && (
                <div style={{ display: 'flex', gap: '10px', marginTop: '8px' }}>
                  <LabeledInput label="最小内存（MB）" value={minMem} onChange={setMinMem} />
                  <LabeledInput label="最大内存（MB）" value={maxMem} onChange={setMaxMem} />
                </div>
              )}
              <LabeledSelect
                label="Java 档位"
                value={javaMode}
                options={[
                  ['global', '跟随全局'],
                  ['manual', '自定义路径'],
                  ['auto', '按版本 JSON 推荐']
                ]}
                onChange={setJavaMode}
              />
              {javaMode === 'manual' && (
                <div style={{ marginTop: '8px' }}>
                  <LabeledInput label="java 可执行文件路径" value={javaPath} onChange={setJavaPath} />
                </div>
              )}
              <LabeledSelect
                label="窗口档位"
                value={windowMode}
                options={[
                  ['global', '跟随全局'],
                  ['manual', '自定义'],
                  ['auto', '854×480 窗口化']
                ]}
                onChange={setWindowMode}
              />
              {windowMode === 'manual' && (
                <div style={{ display: 'flex', gap: '10px', marginTop: '8px', alignItems: 'flex-end' }}>
                  <LabeledInput label="宽" value={width} onChange={setWidth} />
                  <LabeledInput label="高" value={height} onChange={setHeight} />
                  <label style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '12px' }}>
                    <input
                      type="checkbox"
                      checked={fullscreen}
                      onChange={(e) => setFullscreen(e.target.checked)}
                    />
                    全屏
                  </label>
                </div>
              )}
              {eff !== undefined && (
                <Text size={200} style={{ opacity: 0.7, marginTop: '14px' }}>
                  当前生效：内存 {eff.minMem}–{eff.maxMem} MB · Java {eff.java ?? '自动探测'} · 窗口{' '}
                  {eff.windowW ?? 854}×{eff.windowH ?? 480}
                  {eff.fullscreen === true ? '（全屏）' : ''}
                </Text>
              )}
              {error !== '' && (
                <Text size={200} style={{ color: '#FF9A9A', marginTop: '10px' }}>
                  {error}
                </Text>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={props.onClose}>
              取消
            </Button>
            <Button
              appearance="primary"
              style={{ background: brandYellow, color: '#1b1b1b' }}
              disabled={busy}
              onClick={() => void save()}
            >
              {busy ? '保存中…' : '保存'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function LabeledSelect(props: {
  label: string
  value: string
  options: Array<[string, string]>
  onChange: (v: string) => void
}): JSX.Element {
  return (
    <div style={{ marginTop: '12px' }}>
      <Text size={200} style={{ opacity: 0.65, display: 'block', marginBottom: '4px' }}>
        {props.label}
      </Text>
      <Dropdown value={props.value} selectedOptions={[props.value]} onOptionSelect={(_e, d) => props.onChange(String(d.optionValue))}>
        {props.options.map(([v, label]) => (
          <Option key={v} value={v}>
            {label}
          </Option>
        ))}
      </Dropdown>
    </div>
  )
}

function LabeledInput(props: {
  label: string
  value: string
  onChange: (v: string) => void
}): JSX.Element {
  return (
    <div style={{ flex: 1 }}>
      <Text size={200} style={{ opacity: 0.65, display: 'block', marginBottom: '4px' }}>
        {props.label}
      </Text>
      <Input value={props.value} onChange={(_e, d) => props.onChange(d.value)} />
    </div>
  )
}

function SearchInstallDialog(props: {
  kind: ContentKind
  mcVersion: string
  loader: string
  versionId: string
  onClose: () => void
  onInstalled: () => void
}): JSX.Element {
  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<ContentSearchHit[]>([])
  const [searching, setSearching] = useState(false)
  const [installing, setInstalling] = useState<string | null>(null)
  const [stage, setStage] = useState('')
  const [installed, setInstalled] = useState<ContentInstallResult | null>(null)

  useEffect(() => {
    let alive = true
    onBridgeEvent((event, params) => {
      if (!alive || event !== 'content.progress') return
      const s = String(params.stage ?? '')
      if (s !== '') setStage(s)
    })
    return () => {
      alive = false
    }
  }, [])

  const search = async (): Promise<void> => {
    setSearching(true)
    try {
      const r = await request<{ hits: ContentSearchHit[] }>('content.search', {
        kind: props.kind,
        query,
        gameVersion: props.mcVersion,
        loader: props.kind === 'mod' ? props.loader : undefined
      })
      setHits(r.hits)
    } finally {
      setSearching(false)
    }
  }

  const install = async (hit: ContentSearchHit): Promise<void> => {
    setInstalling(hit.projectId)
    setStage('')
    try {
      const r = await request<ContentInstallResult>('content.install', {
        kind: props.kind,
        projectId: hit.projectId,
        title: hit.title,
        targetVersionId: props.versionId
      })
      setInstalled(r)
      props.onInstalled()
    } finally {
      setInstalling(null)
      setStage('')
    }
  }

  return (
    <Dialog open onOpenChange={(_e, d) => !d.open && props.onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>安装{KIND_LABEL[props.kind]}（Modrinth）</DialogTitle>
          <DialogContent>
            <div style={{ minWidth: '640px' }}>
              <div style={{ display: 'flex', gap: '10px', marginBottom: '12px' }}>
                <Input
                  style={{ flex: 1 }}
                  value={query}
                  placeholder={`搜索${KIND_LABEL[props.kind]}（留空 = 热门）`}
                  onChange={(_e, d) => setQuery(d.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') void search()
                  }}
                />
                <Button
                  appearance="primary"
                  style={{ background: brandYellow, color: '#1b1b1b' }}
                  disabled={searching}
                  onClick={() => void search()}
                >
                  {searching ? '搜索中…' : '搜索'}
                </Button>
              </div>
              {stage !== '' && (
                <Text size={200} style={{ display: 'block', marginBottom: '8px', color: brandYellow }}>
                  {stage}
                </Text>
              )}
              {installed !== null && (
                <Card style={{ padding: '10px', marginBottom: '10px', border: '1px solid rgba(63,191,111,0.4)' }}>
                  <Text size={200}>
                    已安装 {installed.installed.length} 个文件
                    {installed.missing.length > 0 ? `；${installed.missing.length} 个依赖未找到` : ''}
                  </Text>
                </Card>
              )}
              <div style={{ maxHeight: '360px', overflowY: 'auto' }}>
                {hits.length === 0 ? (
                  <Text size={200} style={{ opacity: 0.6, textAlign: 'center', padding: '30px' }}>
                    {searching ? '正在搜索…' : '输入关键词后点「搜索」'}
                  </Text>
                ) : (
                  hits.map((hit, index) => (
                    <div
                      key={hit.projectId}
                      style={{
                        display: 'flex',
                        gap: '12px',
                        alignItems: 'center',
                        padding: '10px 0',
                        borderBottom: '1px solid rgba(255,255,255,0.06)',
                        animation: `rowIn ${motion.enter}ms ${motion.easeEnter} both`,
                        animationDelay: `${index * motion.stagger}ms`
                      }}
                    >
                      <div
                        style={{
                          width: '40px',
                          height: '40px',
                          borderRadius: `${radius.control}px`,
                          overflow: 'hidden',
                          background: 'rgba(255,255,255,0.06)',
                          display: 'flex',
                          alignItems: 'center',
                          justifyContent: 'center',
                          flexShrink: 0
                        }}
                      >
                        {hit.iconUrl != null && hit.iconUrl !== '' ? (
                          <img
                            src={hit.iconUrl}
                            alt=""
                            style={{ width: '100%', height: '100%', objectFit: 'cover' }}
                          />
                        ) : (
                          <span style={{ opacity: 0.6 }}>◧</span>
                        )}
                      </div>
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <Text size={300} weight="semibold" truncate>
                          {hit.title}
                        </Text>
                        <Text size={200} style={{ opacity: 0.6 }}>
                          {hit.author} · {hit.downloads.toLocaleString()} 次下载
                        </Text>
                        <Text size={200} style={{ opacity: 0.5 }} truncate>
                          {hit.description}
                        </Text>
                      </div>
                      <Button disabled={installing !== null} onClick={() => void install(hit)}>
                        {installing === hit.projectId ? '安装中…' : '安装'}
                      </Button>
                    </div>
                  ))
                )}
              </div>
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={props.onClose}>
              {installed !== null ? '完成' : '关闭'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
