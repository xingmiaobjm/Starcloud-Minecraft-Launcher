/// 插件页：外部插件（v2 沙箱）安装/启停/卸载 + 面板入口。
import { useEffect, useState } from 'react'
import { request } from '../bridge'
import type { ExternalPlugin, PluginList } from '../bridge'
import { EmptyState, Modal, PageShell, SkeletonList, useToasts } from '../ui'

export default function PluginsPage() {
  const { push, host } = useToasts()
  const [plugins, setPlugins] = useState<ExternalPlugin[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)
  const [installPath, setInstallPath] = useState('')
  const [confirm, setConfirm] = useState<ExternalPlugin | null>(null)
  const [disclaimer, setDisclaimer] = useState<ExternalPlugin | null>(null)

  async function reload(): Promise<void> {
    setLoading(true)
    try {
      const r = await request<PluginList>('plugins.list')
      setPlugins(r.plugins)
    } catch (e) {
      push('error', `读取插件列表失败：${String(e)}`)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function install(): Promise<void> {
    if (installPath.trim() === '') return
    setBusy('__install__')
    try {
      const p = await request<ExternalPlugin>('plugins.install', { path: installPath.trim() })
      push('success', `已安装 ${p.name} v${p.version}`)
      setInstallPath('')
      await reload()
    } catch (e) {
      push('error', `安装失败：${String(e)}`)
    } finally {
      setBusy(null)
    }
  }

  async function toggle(p: ExternalPlugin, enabled: boolean): Promise<void> {
    if (enabled && !p.official && !p.disclaimerAccepted) {
      setDisclaimer(p)
      return
    }
    setBusy(p.id)
    try {
      await request('plugins.setEnabled', { id: p.id, enabled })
      push('success', enabled ? `已启用 ${p.name}` : `已停用 ${p.name}`)
      await reload()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function confirmEnable(): Promise<void> {
    const p = disclaimer
    setDisclaimer(null)
    if (!p) return
    setBusy(p.id)
    try {
      await request('plugins.setEnabled', { id: p.id, enabled: true, acceptDisclaimer: true })
      push('success', `已启用 ${p.name}`)
      await reload()
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
      await reload()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function openFolder(p: ExternalPlugin): Promise<void> {
    try {
      await request('plugins.openFolder', { id: p.id })
    } catch (e) {
      push('error', String(e))
    }
  }

  return (
    <>
      <PageShell
        title="插件"
        subtitle="外部插件（v2 沙箱）：.mclplug 安装、JS / Python / 声明式运行时"
        actions={
          <button className="btn btn-subtle" onClick={() => void reload()}>
            ↻ 刷新
          </button>
        }
      >
        <div className="card" style={{ padding: 16, marginBottom: 12 }}>
          <div className="t-strong" style={{ display: 'block', marginBottom: 10 }}>
            安装插件
          </div>
          <div style={{ display: 'flex', gap: 10 }}>
            <input
              className="input"
              style={{ flex: 1 }}
              value={installPath}
              placeholder=".mclplug 安装包绝对路径"
              onChange={(e) => setInstallPath(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void install()
              }}
            />
            <button
              className="btn btn-primary"
              disabled={busy !== null}
              onClick={() => void install()}
            >
              {busy === '__install__' ? '安装中…' : '安装'}
            </button>
          </div>
        </div>

        {loading ? (
          <SkeletonList rows={3} />
        ) : plugins.length === 0 ? (
          <EmptyState title="还没有安装外部插件" hint="从 .mclplug 安装包安装第三方插件。" />
        ) : (
          plugins.map((p, index) => (
            <div
              key={p.id}
              className="card"
              style={{
                padding: 16,
                marginBottom: 8,
                animation: `slideUp var(--t-enter) var(--e-enter) both`,
                animationDelay: `${index * 39}ms`
              }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <div
                  className="icon32"
                  style={{
                    fontSize: 22,
                    color: p.enabled ? 'var(--accent)' : 'var(--text-tertiary)',
                    background: p.enabled
                      ? 'color-mix(in srgb, var(--accent) 14%, transparent)'
                      : 'var(--surface-subtle)'
                  }}
                >
                  ◫
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div
                    style={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: 8,
                      flexWrap: 'wrap'
                    }}
                  >
                    <span className="t-strong truncate">
                      {p.name} {p.version}
                    </span>
                    <span className="chip">{p.id}</span>
                    {p.official && <span className="chip">官方</span>}
                    {p.enabled && <span className="chip chip-accent">已启用</span>}
                    {p.loaded && <span className="chip chip-accent">运行中</span>}
                  </div>
                  <div className="t-cap">{p.description}</div>
                  <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 4 }}>
                    {p.permissions.slice(0, 6).map((perm) => (
                      <span key={perm} className="chip">
                        {perm}
                      </span>
                    ))}
                  </div>
                </div>
                <button
                  className={`btn${p.enabled ? '' : ' btn-primary'}`}
                  disabled={busy !== null}
                  onClick={() => void toggle(p, !p.enabled)}
                >
                  {p.enabled ? '停用' : '启用'}
                </button>
                <button
                  className="btn btn-subtle"
                  disabled={busy !== null}
                  onClick={() => void openFolder(p)}
                >
                  ▤ 目录
                </button>
                <button
                  className="btn btn-subtle btn-danger"
                  disabled={busy !== null}
                  onClick={() => setConfirm(p)}
                >
                  ✕ 卸载
                </button>
              </div>
            </div>
          ))
        )}
        {host}
      </PageShell>

      <Modal
        open={confirm !== null}
        title={`卸载 ${confirm?.name}？`}
        width={460}
        onClose={() => setConfirm(null)}
        footer={
          <>
            <button className="btn" onClick={() => setConfirm(null)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: '#fff' }}
              onClick={() => {
                const t = confirm
                setConfirm(null)
                if (t) void uninstall(t)
              }}
            >
              卸载
            </button>
          </>
        }
      >
        <div className="t-body">插件目录与启用状态会被删除，不可恢复。</div>
      </Modal>

      <Modal
        open={disclaimer !== null}
        title="启用第三方插件？"
        width={460}
        onClose={() => setDisclaimer(null)}
        footer={
          <>
            <button className="btn" onClick={() => setDisclaimer(null)}>
              取消
            </button>
            <button className="btn btn-primary" onClick={() => void confirmEnable()}>
              确认并启用
            </button>
          </>
        }
      >
        <div className="t-body">插件由第三方独立开发，启动器不对其内容与合法性负责。</div>
      </Modal>
    </>
  )
}
