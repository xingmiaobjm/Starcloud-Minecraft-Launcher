/// 插件页：对齐 Win 端 PluginsPage ——
/// 顶部「安装插件…」；卡 1 内核插件（内置能力与耗时）；卡 2 已安装（开关 + 卸载）；
/// 底部提示 .mclplug 的放置方式。
import { useCallback, useEffect, useState } from 'react'

import { request } from '../bridge'
import type { ExternalPlugin, PluginList } from '../bridge'
import { Modal, SkeletonList, Toggle, useToasts } from '../ui'
import { PluginPanelView } from '../pluginView'
import { IconWarning } from '../icons'

/// 内核内置插件（与 Win 端内核注册表一致）。
/// 这些是编译进内核的固定能力，不来自 plugins.list，所以在这里静态展示。
const CORE_PLUGINS = [
  {
    kind: '引擎',
    name: '图标缓存',
    version: '1.0.0',
    desc: '把远程图标/封面落到本地磁盘并复用（走下载引擎）',
    provides: 'IIconCache'
  },
  {
    kind: '引擎',
    name: '多线程下载引擎',
    version: '1.0.0',
    desc: '多文件批量下载 + 多源分片竞速 + 重试 + 断点续传；可在局域网内复用同账号设备的已下载文件',
    provides: 'IDownloadEngine'
  },
  {
    kind: '引擎',
    name: '搜索词翻译',
    version: '1.0.0',
    desc: '中文搜索词 → 英文项目名（内置词典 → MC 百科 → 机器翻译，带缓存）',
    provides: 'ITranslationEngine'
  },
  {
    kind: '功能',
    name: '投影管理',
    version: '1.0.0',
    desc: '扫描全局与各版本实例的投影文件，可把投影复制/移动到指定版本',
    provides: 'IProjectionLibrary'
  },
  {
    kind: '功能',
    name: '版本管理',
    version: '1.0.0',
    desc: '官方版本清单拉取与缓存、本地已安装版本枚举、版本删除',
    provides: 'IVersionCatalog'
  }
]

export default function PluginsPage(): JSX.Element {
  const { push, host } = useToasts()
  const [plugins, setPlugins] = useState<ExternalPlugin[]>([])
  const [loading, setLoading] = useState(true)
  const [installOpen, setInstallOpen] = useState(false)
  const [installPath, setInstallPath] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [pendingUninstall, setPendingUninstall] = useState<ExternalPlugin | null>(null)
  const [pendingDisclaimer, setPendingDisclaimer] = useState<ExternalPlugin | null>(null)
  /** 面板：当前打开的插件与其面板列表 */
  const [panelFor, setPanelFor] = useState<ExternalPlugin | null>(null)
  const [panels, setPanels] = useState<Array<{ id: string; title: string; declarative: boolean; entry: string }>>([])
  const [activePanelId, setActivePanelId] = useState('')
  const [panelBusy, setPanelBusy] = useState(false)

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const r = await request<PluginList>('plugins.list')
      setPlugins(r.plugins)
    } catch (e) {
      push('error', `读取插件失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  async function install(): Promise<void> {
    if (installPath.trim() === '') return
    setBusy('__install__')
    try {
      const p = await request<ExternalPlugin>('plugins.install', {
        path: installPath.trim()
      })
      push('success', `已安装插件 ${p.name} ${p.version}`)
      setInstallOpen(false)
      setInstallPath('')
      await load()
    } catch (e) {
      push('error', `安装失败：${String(e)}`)
    } finally {
      setBusy(null)
    }
  }

  async function togglePlugin(p: ExternalPlugin, enabled: boolean): Promise<void> {
    // 第三方插件首次启用需要确认免责声明
    if (enabled && !p.official && !p.disclaimerAccepted) {
      setPendingDisclaimer(p)
      return
    }
    setBusy(p.id)
    try {
      await request('plugins.setEnabled', { id: p.id, enabled })
      await load()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function uninstall(p: ExternalPlugin): Promise<void> {
    setBusy(p.id)
    try {
      await request('plugins.uninstall', { id: p.id })
      push('success', `已卸载 ${p.name}`)
      setPendingUninstall(null)
      await load()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function openPanels(p: ExternalPlugin): Promise<void> {
    setPanelFor(p)
    setPanelBusy(true)
    try {
      const r = await request<{ panels: Array<{ id: string; title: string; declarative: boolean; entry: string }> }>(
        'plugins.panels',
        { id: p.id }
      )
      setPanels(r.panels)
      setActivePanelId(r.panels[0]?.id ?? '')
    } catch (e) {
      push('error', String(e))
      setPanelFor(null)
    } finally {
      setPanelBusy(false)
    }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div style={{ flex: 1 }} />
        <button className="btn btn-primary" onClick={() => setInstallOpen(true)}>
          安装插件…
        </button>
      </div>

      <div className="page-body" style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {/* 内核插件 */}
        <div className="card">
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, marginBottom: 6 }}>
            <span className="t-strong" style={{ fontSize: 15 }}>
              内核插件
            </span>
            <span className="t-cap">共 {CORE_PLUGINS.length} 个 · 内核 1.0.0</span>
          </div>
          {CORE_PLUGINS.map((p) => (
            <div
              key={p.name}
              className="row row-static"
              style={{ alignItems: 'flex-start', minHeight: 0, paddingTop: 10, paddingBottom: 10 }}
            >
              <span className="chip" style={{ marginTop: 2 }}>
                {p.kind}
              </span>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
                  <span className="t-strong">{p.name}</span>
                  <span className="t-meta">{p.version}</span>
                </div>
                <div className="t-cap">
                  {p.desc}　提供：{p.provides}
                </div>
              </div>
              <span className="t-cap" style={{ flexShrink: 0 }}>
                运行中
              </span>
            </div>
          ))}
        </div>

        {/* 已安装（第三方 / 用户插件） */}
        <div className="card">
          <div className="t-strong" style={{ fontSize: 15, marginBottom: 6 }}>
            已安装
          </div>
          {loading && <SkeletonList rows={2} />}
          {!loading && plugins.length === 0 && (
            <div className="t-cap" style={{ padding: '12px 0' }}>
              还没有安装第三方插件。
            </div>
          )}
          {plugins.map((p) => (
            <div
              key={p.id}
              className="row row-static"
              style={{ alignItems: 'flex-start', minHeight: 0, paddingTop: 12, paddingBottom: 12 }}
            >
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                  <span className="t-strong">{p.name}</span>
                  <span className="t-meta">{p.version}</span>
                  {!p.official && <span className="chip chip-accent">第三方未验证</span>}
                  <span className="chip">API {p.apiVersion}</span>
                </div>
                <div className="t-cap">{p.description}</div>
                <div className="t-meta" style={{ opacity: 0.8 }}>
                  作者 {p.author === '' ? '未知' : p.author}
                  {p.permissions.length > 0 && ` · 能力：${p.permissions.join(' · ')}`}
                </div>
              </div>
              {p.panelCount > 0 && (
                <button
                  className="btn btn-sm"
                  disabled={busy !== null || panelBusy}
                  onClick={() => void openPanels(p)}
                >
                  打开面板
                </button>
              )}
              <button
                className="btn btn-sm"
                disabled={busy !== null}
                onClick={() => setPendingUninstall(p)}
              >
                卸载
              </button>
              <Toggle
                checked={p.enabled}
                disabled={busy !== null}
                label={`启用 ${p.name}`}
                onChange={(v) => void togglePlugin(p, v)}
              />
            </div>
          ))}
        </div>

        <div className="t-cap">
          把 .mclplug 文件放到这里：点「安装插件…」选择插件包，安装后打开开关启用。
        </div>
      </div>

      {/* 安装插件 */}
      <Modal
        open={installOpen}
        title="安装插件"
        onClose={() => setInstallOpen(false)}
        footer={
          <>
            <button className="btn" onClick={() => setInstallOpen(false)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              disabled={installPath.trim() === '' || busy !== null}
              onClick={() => void install()}
            >
              安装
            </button>
          </>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <span className="t-cap">插件包路径（.mclplug 或已解压目录）</span>
          <input
            className="input"
            autoFocus
            value={installPath}
            placeholder="例如 /home/user/Downloads/demo.mclplug"
            onChange={(e) => setInstallPath(e.target.value)}
          />
        </div>
      </Modal>

      {/* 第三方免责声明 */}
      <Modal
        open={pendingDisclaimer !== null}
        title="启用第三方插件？"
        onClose={() => setPendingDisclaimer(null)}
        footer={
          <>
            <button className="btn" onClick={() => setPendingDisclaimer(null)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              onClick={() => {
                const p = pendingDisclaimer
                setPendingDisclaimer(null)
                if (p === null) return
                void request('plugins.setEnabled', {
                  id: p.id,
                  enabled: true,
                  acceptDisclaimer: true
                })
                  .then(() => load())
                  .catch((e) => push('error', String(e)))
              }}
            >
              我了解风险，启用
            </button>
          </>
        }
      >
        <div className="dialog-body">
          该插件不是 Starcloud 官方发布，未经审核。插件可以访问它声明过的能力，
          请确认来源可信后再启用。
        </div>
      </Modal>

      {/* 卸载确认 */}
      <Modal
        open={pendingUninstall !== null}
        title="卸载插件？"
        onClose={() => setPendingUninstall(null)}
        footer={
          <>
            <button className="btn" onClick={() => setPendingUninstall(null)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: '#fff' }}
              onClick={() => {
                const p = pendingUninstall
                if (p !== null) void uninstall(p)
              }}
            >
              卸载
            </button>
          </>
        }
      >
        <div className="dialog-body">
          将删除插件 <b>{pendingUninstall?.name}</b> 的目录与配置。
        </div>
      </Modal>

      {/* 插件面板 */}
      <Modal
        open={panelFor !== null}
        title={panelFor === null ? '' : `${panelFor.name} · 面板`}
        width={720}
        onClose={() => setPanelFor(null)}
        footer={
          <button className="btn" onClick={() => setPanelFor(null)}>
            关闭
          </button>
        }
      >
        {panelBusy ? (
          <div className="t-cap">加载面板列表…</div>
        ) : panels.length === 0 ? (
          <div className="t-cap">该插件没有声明面板。</div>
        ) : (
          <div style={{ display: 'flex', gap: 12, minHeight: 240 }}>
            {panels.length > 1 && (
              <div style={{ width: 160, flexShrink: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
                {panels.map((panel) => (
                  <button
                    key={panel.id}
                    className={`nav-item${activePanelId === panel.id ? ' nav-active' : ''}`}
                    style={{ height: 36, width: '100%' }}
                    onClick={() => setActivePanelId(panel.id)}
                  >
                    {panel.title}
                  </button>
                ))}
              </div>
            )}
            <div style={{ flex: 1, minWidth: 0, overflowY: 'auto' }}>
              {panels.find((x) => x.id === activePanelId)?.declarative === false ? (
                <div className="info info-warning">
                  <IconWarning size={14} />
                  <div className="info-body">
                    HTML 面板需要主进程文件 IPC，当前版本暂以声明式面板为准。
                  </div>
                </div>
              ) : panelFor !== null && activePanelId !== '' ? (
                <PluginPanelView
                  pluginId={panelFor.id}
                  panelId={activePanelId}
                  onError={(msg) => push('error', msg)}
                />
              ) : null}
            </div>
          </div>
        )}
      </Modal>

      {host}
    </div>
  )
}
