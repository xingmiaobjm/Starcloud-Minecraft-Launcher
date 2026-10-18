/// 插件页：外部插件（v2 沙箱）安装/启停/卸载 + 面板入口。
import { useEffect, useState } from 'react'
import {
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  ArrowSyncRegular,
  DeleteRegular,
  FolderOpenRegular,
  PuzzleCubeRegular
} from '@fluentui/react-icons'
import { request } from '../bridge'
import type { ExternalPlugin, PluginList } from '../bridge'
import { brandYellow, motion } from '../theme'
import { EmptyState, PageShell, SkeletonList, useToasts } from '../ui'

const useStyles = makeStyles({
  card: { padding: '16px', marginBottom: '8px' },
  row: { display: 'flex', alignItems: 'center', gap: '12px' },
  chip: {
    fontSize: '11px',
    padding: '1px 7px',
    borderRadius: '9px',
    background: 'rgba(255,255,255,0.07)',
    color: 'rgba(255,255,255,0.75)'
  }
})

export default function PluginsPage() {
  const styles = useStyles()
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
    <PageShell
      title="插件"
      subtitle="外部插件（v2 沙箱）：.mclplug 安装、JS / Python / 声明式运行时"
      actions={<Button appearance="subtle" icon={<ArrowSyncRegular />} onClick={() => void reload()} />}
    >
      <Card style={{ padding: '16px', marginBottom: '12px' }}>
        <Text size={300} weight="semibold" style={{ display: 'block', marginBottom: '10px' }}>
          安装插件
        </Text>
        <div style={{ display: 'flex', gap: '10px' }}>
          <Input
            value={installPath}
            placeholder=".mclplug 安装包绝对路径"
            style={{ flex: 1 }}
            onChange={(_e, data) => setInstallPath(data.value)}
          />
          <Button
            appearance="primary"
            style={{ background: brandYellow, color: '#1b1b1b' }}
            disabled={busy !== null}
            onClick={() => void install()}
          >
            {busy === '__install__' ? '安装中…' : '安装'}
          </Button>
        </div>
      </Card>

      {loading ? (
        <SkeletonList rows={3} />
      ) : plugins.length === 0 ? (
        <EmptyState title="还没有安装外部插件" hint="从 .mclplug 安装包安装第三方插件。" />
      ) : (
        plugins.map((p, index) => (
          <Card
            key={p.id}
            className={styles.card}
            style={{ animation: `slideUp ${motion.normal}ms ${motion.easeOut} both`, animationDelay: `${index * 24}ms` }}
          >
            <div className={styles.row}>
              <PuzzleCubeRegular style={{ fontSize: '22px', color: p.enabled ? brandYellow : '#8A8A8A' }} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' }}>
                  <Text size={300} weight="semibold" truncate>
                    {p.name} {p.version}
                  </Text>
                  <span className={styles.chip}>{p.id}</span>
                  {p.official && <span className={styles.chip}>官方</span>}
                  {p.enabled && (
                    <span className={styles.chip} style={{ color: brandYellow }}>
                      已启用
                    </span>
                  )}
                  {p.loaded && <span className={styles.chip}>运行中</span>}
                </div>
                <Text size={200} style={{ opacity: 0.65 }}>
                  {p.description}
                </Text>
                <div style={{ display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '4px' }}>
                  {p.permissions.slice(0, 6).map((perm) => (
                    <span key={perm} className={styles.chip}>
                      {perm}
                    </span>
                  ))}
                </div>
              </div>
              <Button
                appearance={p.enabled ? 'secondary' : 'primary'}
                style={!p.enabled ? { background: brandYellow, color: '#1b1b1b' } : undefined}
                disabled={busy !== null}
                onClick={() => void toggle(p, !p.enabled)}
              >
                {p.enabled ? '停用' : '启用'}
              </Button>
              <Button
                appearance="subtle"
                icon={<FolderOpenRegular />}
                disabled={busy !== null}
                onClick={() => void openFolder(p)}
              >
                目录
              </Button>
              <Button
                appearance="subtle"
                icon={<DeleteRegular />}
                disabled={busy !== null}
                style={{ color: '#FF9A9A' }}
                onClick={() => setConfirm(p)}
              >
                卸载
              </Button>
            </div>
          </Card>
        ))
      )}

      <Dialog
        open={confirm !== null}
        onOpenChange={(_e, data) => !data.open && setConfirm(null)}
        modalType="alert"
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>卸载 {confirm?.name}？</DialogTitle>
            <DialogContent>插件目录与启用状态会被删除，不可恢复。</DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setConfirm(null)}>
                取消
              </Button>
              <Button
                appearance="primary"
                style={{ background: '#B42318' }}
                onClick={() => {
                  const t = confirm
                  setConfirm(null)
                  if (t) void uninstall(t)
                }}
              >
                卸载
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      <Dialog open={disclaimer !== null} onOpenChange={(_e, data) => !data.open && setDisclaimer(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>启用第三方插件？</DialogTitle>
            <DialogContent>
              插件由第三方独立开发，启动器不对其内容与合法性负责。
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setDisclaimer(null)}>
                取消
              </Button>
              <Button appearance="primary" onClick={() => void confirmEnable()}>
                确认并启用
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {host}
    </PageShell>
  )
}
