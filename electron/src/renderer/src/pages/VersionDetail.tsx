/// 版本详情：实例信息 + 模组/资源包/光影管理 + Modrinth 安装 + 实例参数 + 更新/依赖检查。
///
/// 交互对齐 Flutter 版（version_detail_page.dart），差异：
/// - 圆角/动效走 winui.css 的 CSS 变量与现成动效类
/// - content.progress 走全局事件订阅（Electron 端没有回调参数）
import { useCallback, useEffect, useState } from 'react'
import type { CSSProperties } from 'react'
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
import {EmptyState,
  Modal,
  PageShell,
  Segmented,
  SkeletonList,
  useToasts, Select } from '../ui'

const KIND_LABEL: Record<ContentKind, string> = {
  mod: '模组',
  resourcepack: '资源包',
  shader: '光影'
}

/** 分段控件的固定档位顺序（指标识符跑出来的顺序，别依赖 object 的键序）。 */
const KIND_ORDER: ContentKind[] = ['mod', 'resourcepack', 'shader']

const KIND_OPTIONS = KIND_ORDER.map((k) => ({ value: k, label: KIND_LABEL[k] }))

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
    <>
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
            <button
              className="btn btn-subtle"
              disabled={detail === null}
              onClick={() => setShowSettings(true)}
            >
              ⚙ 实例设置
            </button>
            <button
              className="btn btn-subtle"
              disabled={detail === null}
              onClick={() => void openFolder()}
            >
              ▤ 打开目录
            </button>
            <button className="btn btn-subtle" onClick={() => void load()}>
              ↻ 刷新
            </button>
          </>
        }
      >
        {loading ? (
          <SkeletonList rows={6} />
        ) : (
          <>
            {detail !== null && (
              <div
                className="card glow"
                style={
                  {
                    padding: '18px 20px',
                    marginBottom: 12,
                    '--glow':
                      'radial-gradient(520px circle at 20% 0%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 68%)'
                  } as CSSProperties
                }
              >
                <div style={{ display: 'flex', gap: 26, flexWrap: 'wrap' }}>
                  <Info label="游戏版本" value={detail.mcVersion} />
                  <Info
                    label="加载器"
                    value={detail.loaderVersion != null ? `${detail.loader} ${detail.loaderVersion}` : detail.loader}
                  />
                  <Info label="隔离" value={detail.isolated ? '独立版本目录' : '共享 .minecraft'} />
                  <Info label="最近游玩" value={fmtTime(detail.lastPlayedAt)} />
                  <div style={{ minWidth: 260 }}>
                    <div className="t-meta">实例目录</div>
                    <div className="t-body truncate">{detail.gameRoot}</div>
                  </div>
                </div>
              </div>
            )}

            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 10,
                flexWrap: 'wrap',
                marginBottom: 12
              }}
            >
              <Segmented
                value={kind}
                options={KIND_OPTIONS}
                onChange={setKind}
              />
              <div style={{ flex: 1 }} />
              {kind === 'mod' && (
                <>
                  <button
                    className="btn"
                    disabled={globalBusy}
                    onClick={() => void checkUpdates()}
                  >
                    ↻ 检查更新
                  </button>
                  <button
                    className="btn"
                    disabled={globalBusy}
                    onClick={() => void checkDependencies()}
                  >
                    ⚖ 检查依赖
                  </button>
                </>
              )}
              <button
                className="btn btn-primary"
                disabled={globalBusy}
                onClick={() => setInstallOpen(true)}
              >
                ↓ 安装内容
              </button>
            </div>

            {progress !== '' && (
              <div className="info info-info" style={{ marginBottom: 12 }}>
                <span className="spinner" aria-hidden />
                <div className="info-body t-body">{progress}</div>
              </div>
            )}

            {files.length === 0 ? (
              <EmptyState
                title={`还没有${KIND_LABEL[kind]}`}
                hint="点「安装内容」从 Modrinth 搜索安装。"
              />
            ) : (
              files.map((file, index) => (
                <div
                  key={file.fileName}
                  className="card glow"
                  style={
                    {
                      display: 'flex',
                      alignItems: 'center',
                      gap: 12,
                      padding: '10px 14px',
                      marginBottom: 8,
                      animation: `rowIn var(--t-enter) var(--e-enter) both`,
                      animationDelay: `${index * 39}ms`,
                      '--glow':
                        'radial-gradient(420px circle at 12% 50%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 66%)'
                    } as CSSProperties
                  }
                >
                  <span style={{ fontSize: 15, opacity: file.enabled ? 1 : 0.45 }}>
                    {file.enabled ? '☑' : '☐'}
                  </span>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="t-strong truncate">{file.displayName}</div>
                    <div className="t-cap">
                      {fmtSize(file.sizeBytes)}
                      {file.enabled ? '' : ' · 已停用'}
                    </div>
                  </div>
                  <button className="btn" disabled={globalBusy} onClick={() => void toggle(file)}>
                    {file.enabled ? '停用' : '启用'}
                  </button>
                  <button
                    className="btn btn-subtle btn-danger"
                    disabled={globalBusy}
                    onClick={() => setConfirmDelete(file)}
                  >
                    ✕ 删除
                  </button>
                </div>
              ))
            )}
          </>
        )}
        {host}
      </PageShell>

      <Modal
        open={confirmDelete !== null}
        title={`删除 ${confirmDelete?.displayName}？`}
        width={460}
        onClose={() => setConfirmDelete(null)}
        footer={
          <>
            <button className="btn" onClick={() => setConfirmDelete(null)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: '#fff' }}
              onClick={() => {
                const t = confirmDelete
                setConfirmDelete(null)
                if (t !== null) void remove(t)
              }}
            >
              删除
            </button>
          </>
        }
      >
        <div className="t-body">文件会从版本目录永久删除，不可恢复。</div>
      </Modal>

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
    </>
  )
}

function Info(props: { label: string; value: string }): JSX.Element {
  return (
    <div style={{ minWidth: 150 }}>
      <div className="t-meta">{props.label}</div>
      <div className="t-body">{props.value}</div>
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
    <Modal
      open
      title={`模组更新（${props.updates.length}）`}
      width={600}
      onClose={props.onClose}
      footer={
        <>
          <button className="btn" onClick={props.onClose}>
            关闭
          </button>
          <button
            className="btn btn-primary"
            disabled={props.busy !== null}
            onClick={() => void updateAll()}
          >
            全部更新
          </button>
        </>
      }
    >
      <div style={{ maxHeight: 380, overflowY: 'auto', minWidth: 520 }}>
        {props.updates.map((u) => {
          const isDone = done.includes(u.fileName)
          return (
            <div
              key={u.fileName}
              className="row row-static"
              style={{ padding: '8px 0', borderRadius: 0 }}
            >
              <div style={{ flex: 1, minWidth: 0 }}>
                <div className="t-strong truncate">{u.fileName}</div>
                <div className="t-cap">
                  {u.currentVersion} → {u.latestVersion}
                </div>
              </div>
              <button
                className="btn"
                disabled={isDone || props.busy !== null}
                onClick={() => {
                  setDone((prev) => [...prev, u.fileName])
                  void props.onUpdate(u)
                }}
              >
                {isDone ? '已更新' : '更新'}
              </button>
            </div>
          )
        })}
      </div>
    </Modal>
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
    <Modal
      open
      title="依赖检查"
      width={600}
      onClose={props.onClose}
      footer={
        <>
          <button className="btn" onClick={props.onClose}>
            关闭
          </button>
          {props.deps.missing.length > 0 && (
            <button
              className="btn btn-primary"
              disabled={busy}
              onClick={() => void installMissing()}
            >
              {busy ? '安装中…' : '安装缺失依赖'}
            </button>
          )}
        </>
      }
    >
      <div style={{ maxHeight: 380, overflowY: 'auto', minWidth: 520 }}>
        {clean && (
          <div className="info info-success">
            <span aria-hidden>✓</span>
            <div className="info-body">
              <div className="info-title">依赖完整</div>
              <div className="t-cap">
                没有缺失的必需依赖，也没有已安装的不兼容模组。
              </div>
            </div>
          </div>
        )}
        {props.deps.missing.length > 0 && (
          <>
            <div className="t-strong" style={{ display: 'block', marginTop: 8 }}>
              缺失的必需依赖（{props.deps.missing.length}）
            </div>
            {props.deps.missing.map((d) => (
              <div key={d.projectId} className="card" style={{ padding: '8px 12px', marginTop: 6 }}>
                <div className="t-body">{d.title}</div>
              </div>
            ))}
          </>
        )}
        {props.deps.incompatible.length > 0 && (
          <div className="info info-danger" style={{ marginTop: 10 }}>
            <span aria-hidden>✕</span>
            <div className="info-body">
              <div className="info-title">已安装但不兼容</div>
              <div className="t-cap">{props.deps.incompatible.join('、')}</div>
            </div>
          </div>
        )}
        {props.deps.optional.length > 0 && (
          <div className="t-cap" style={{ marginTop: 8 }}>
            可选依赖 {props.deps.optional.length} 个（未安装不影响启动）
          </div>
        )}
        {props.deps.truncated && (
          <div className="t-cap" style={{ marginTop: 6 }}>
            模组数量超过 200，只检查了前 200 个。
          </div>
        )}
        {stage !== '' && (
          <div className="t-body" style={{ marginTop: 8, color: 'var(--accent)' }}>
            {stage}
          </div>
        )}
        {installed !== null && (
          <div className="t-body" style={{ marginTop: 8, color: 'var(--success)' }}>
            已安装 {installed} 个依赖
          </div>
        )}
      </div>
    </Modal>
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
    <Modal
      open
      title={`实例设置 · ${props.versionId}`}
      width={600}
      onClose={props.onClose}
      footer={
        <>
          <button className="btn" onClick={props.onClose}>
            取消
          </button>
          <button className="btn btn-primary" disabled={busy} onClick={() => void save()}>
            {busy ? '保存中…' : '保存'}
          </button>
        </>
      }
    >
      <div style={{ maxHeight: 460, overflowY: 'auto', minWidth: 520 }}>
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
          <div style={{ display: 'flex', gap: 10, marginTop: 8 }}>
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
          <div style={{ marginTop: 8 }}>
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
          <div style={{ display: 'flex', gap: 10, marginTop: 8, alignItems: 'flex-end' }}>
            <LabeledInput label="宽" value={width} onChange={setWidth} />
            <LabeledInput label="高" value={height} onChange={setHeight} />
            <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 12 }}>
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
          <div className="t-cap" style={{ opacity: 0.7, marginTop: 14 }}>
            当前生效：内存 {eff.minMem}–{eff.maxMem} MB · Java {eff.java ?? '自动探测'} · 窗口{' '}
            {eff.windowW ?? 854}×{eff.windowH ?? 480}
            {eff.fullscreen === true ? '（全屏）' : ''}
          </div>
        )}
        {error !== '' && (
          <div className="info info-danger" style={{ marginTop: 10 }}>
            <span aria-hidden>✕</span>
            <div className="info-body t-body">{error}</div>
          </div>
        )}
      </div>
    </Modal>
  )
}

function LabeledSelect(props: {
  label: string
  value: string
  options: Array<[string, string]>
  onChange: (v: string) => void
}): JSX.Element {
  return (
    <div style={{ marginTop: 12 }}>
      <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
        {props.label}
      </div>
      <Select
        width={520}
        value={props.value}
        options={props.options.map(([v, label]) => ({ value: v, label }))}
        onChange={props.onChange}
      />
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
      <div className="t-meta" style={{ display: 'block', marginBottom: 4 }}>
        {props.label}
      </div>
      <input
        className="input"
        style={{ width: '100%' }}
        value={props.value}
        onChange={(e) => props.onChange(e.target.value)}
      />
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
    <Modal
      open
      title={`安装${KIND_LABEL[props.kind]}（Modrinth）`}
      width={720}
      onClose={props.onClose}
      footer={
        <button className="btn" onClick={props.onClose}>
          {installed !== null ? '完成' : '关闭'}
        </button>
      }
    >
      <div style={{ minWidth: 640 }}>
        <div style={{ display: 'flex', gap: 10, marginBottom: 12 }}>
          <input
            className="input"
            style={{ flex: 1 }}
            value={query}
            placeholder={`搜索${KIND_LABEL[props.kind]}（留空 = 热门）`}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void search()
            }}
          />
          <button className="btn btn-primary" disabled={searching} onClick={() => void search()}>
            {searching ? '搜索中…' : '搜索'}
          </button>
        </div>
        {stage !== '' && (
          <div className="t-body" style={{ display: 'block', marginBottom: 8, color: 'var(--accent)' }}>
            {stage}
          </div>
        )}
        {installed !== null && (
          <div className="info info-success" style={{ marginBottom: 10 }}>
            <span aria-hidden>✓</span>
            <div className="info-body t-body">
              已安装 {installed.installed.length} 个文件
              {installed.missing.length > 0 ? `；${installed.missing.length} 个依赖未找到` : ''}
            </div>
          </div>
        )}
        <div style={{ maxHeight: 360, overflowY: 'auto' }}>
          {hits.length === 0 ? (
            <div className="t-cap" style={{ textAlign: 'center', padding: 30 }}>
              {searching ? '正在搜索…' : '输入关键词后点「搜索」'}
            </div>
          ) : (
            hits.map((hit, index) => (
              <div
                key={hit.projectId}
                style={{
                  display: 'flex',
                  gap: 12,
                  alignItems: 'center',
                  padding: '10px 0',
                  borderBottom: '1px solid var(--stroke-divider)',
                  animation: `rowIn var(--t-enter) var(--e-enter) both`,
                  animationDelay: `${index * 39}ms`
                }}
              >
                <div
                  style={{
                    width: 40,
                    height: 40,
                    borderRadius: 'var(--r-control)',
                    overflow: 'hidden',
                    background: 'var(--surface-hover)',
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
                  <div className="t-strong truncate">{hit.title}</div>
                  <div className="t-cap">
                    {hit.author} · {hit.downloads.toLocaleString()} 次下载
                  </div>
                  <div className="t-meta truncate">{hit.description}</div>
                </div>
                <button
                  className="btn"
                  disabled={installing !== null}
                  onClick={() => void install(hit)}
                >
                  {installing === hit.projectId ? '安装中…' : '安装'}
                </button>
              </div>
            ))
          )}
        </div>
      </div>
    </Modal>
  )
}
