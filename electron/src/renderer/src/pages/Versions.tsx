/// 版本页：已安装版本列表（打开目录 / 设为默认 / 删除）。
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
  Text,
  makeStyles
} from '@fluentui/react-components'
import { FolderOpenRegular, PlayRegular, DeleteRegular } from '@fluentui/react-icons'
import { request } from '../bridge'
import type { InstalledVersion } from '../bridge'
import { brandYellow } from '../theme'
import { EmptyState, PageShell, PointerGlow, SkeletonList, useToasts } from '../ui'
import { motion, radius } from '../theme'
import VersionDetailPage from './VersionDetail'

const useStyles = makeStyles({
  card: { padding: '0', marginBottom: '8px', animation: `slideUp ${motion.normal}ms ${motion.easeOut} both` },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
    padding: '12px 16px'
  },
  icon: {
    width: '38px',
    height: '38px',
    borderRadius: '6px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontWeight: 'bold'
  },
  chip: {
    fontSize: '11px',
    padding: '1px 7px',
    borderRadius: '9px',
    background: 'rgba(255,255,255,0.07)',
    color: 'rgba(255,255,255,0.75)'
  }
})

function tagFor(id: string): { label: string; color: string } {
  const lower = id.toLowerCase()
  if (lower.includes('neoforge')) return { label: 'NeoForge', color: '#E8905A' }
  if (lower.includes('fabric')) return { label: 'Fabric', color: '#B08A4F' }
  if (lower.includes('quilt')) return { label: 'Quilt', color: '#9A8ABF' }
  if (lower.includes('forge')) return { label: 'Forge', color: '#5A5A66' }
  if (/^\d+w\d+[a-z]/.test(lower) || lower.includes('-rc')) return { label: '快照', color: '#8A6FD8' }
  return { label: '正式版', color: '#6FA852' }
}

function fmtSize(bytes: number): string {
  if (!bytes) return '大小未知'
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

export default function VersionsPage() {
  const styles = useStyles()
  const { push, host } = useToasts()
  const [items, setItems] = useState<InstalledVersion[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<InstalledVersion | null>(null)
  const [openId, setOpenId] = useState<string | null>(null)

  async function reload(): Promise<void> {
    setLoading(true)
    try {
      const r = await request<{ installed: InstalledVersion[] }>('catalog.installed')
      setItems(r.installed)
    } catch (e) {
      push('error', `读取版本列表失败：${String(e)}`)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function openFolder(v: InstalledVersion): Promise<void> {
    setBusy(v.id)
    try {
      await request('versions.openFolder', { versionId: v.id })
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function setDefault(v: InstalledVersion): Promise<void> {
    setBusy(v.id)
    try {
      await request('versions.setDefault', { versionId: v.id })
      push('success', `已将 ${v.id} 设为默认`)
      await reload()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function remove(v: InstalledVersion): Promise<void> {
    setBusy(v.id)
    try {
      const r = await request<{ freedBytes: number }>('versions.delete', { versionId: v.id })
      push('success', `已删除 ${v.id}（释放 ${fmtSize(r.freedBytes)}）`)
      await reload()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  if (openId !== null) {
    return <VersionDetailPage versionId={openId} onBack={() => setOpenId(null)} />
  }

  return (
    <PageShell title="版本" subtitle="管理本机已安装的 Minecraft 版本">
      {loading ? (
        <SkeletonList rows={6} />
      ) : items.length === 0 ? (
        <EmptyState title="还没有已安装的版本" hint="去「下载」页安装一个版本。" />
      ) : (
        items.map((v, index) => {
          const info = tagFor(v.id)
          const busyThis = busy === v.id
          return (
            <PointerGlow
              key={v.id}
              radius={radius.row}
              style={{ animation: `slideUp ${motion.enter}ms ${motion.easeEnter} both`, animationDelay: `${index * motion.stagger}ms` }}
              onClick={() => setOpenId(v.id)}
            >
              <div className={styles.row} style={{ cursor: 'pointer' }}>
                <div
                  className={styles.icon}
                  style={{ background: `${info.color}2b`, border: `1px solid ${info.color}`, color: info.color }}
                >
                  {v.id[0]?.toUpperCase()}
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                    <Text size={300} weight="semibold" truncate>
                      {v.id}
                    </Text>
                    <span className={styles.chip}>{info.label}</span>
                    {v.isDefault && <span className={styles.chip} style={{ color: brandYellow }}>默认</span>}
                    {v.isolated && <span className={styles.chip}>隔离</span>}
                    {v.inheritsFrom && <span className={styles.chip}>继承 {v.inheritsFrom}</span>}
                    {v.jarExists === false && (
                      <span className={styles.chip} style={{ color: '#FF9A9A' }}>缺少 jar</span>
                    )}
                  </div>
                  <Text size={200} style={{ opacity: 0.6 }}>
                    {fmtSize(v.sizeBytes)} · {v.directory ?? ''}
                  </Text>
                </div>
                <Button
                  appearance="subtle"
                  icon={<FolderOpenRegular />}
                  disabled={busy !== null}
                  onClick={(e) => {
                    e.stopPropagation()
                    void openFolder(v)
                  }}
                >
                  打开目录
                </Button>
                <Button
                  appearance="subtle"
                  disabled={busy !== null || v.isDefault === true}
                  onClick={(e) => {
                    e.stopPropagation()
                    void setDefault(v)
                  }}
                >
                  设为默认
                </Button>
                <Button
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  disabled={busy !== null}
                  style={{ color: '#FF9A9A' }}
                  onClick={(e) => {
                    e.stopPropagation()
                    setConfirm(v)
                  }}
                >
                  删除
                </Button>
              </div>
              {busyThis && (
                <div style={{ padding: '0 16px 10px' }}>
                  <div style={{ height: '2px', background: brandYellow, animation: 'pulse 1s infinite' }} />
                </div>
              )}
            </PointerGlow>
          )
        })
      )}
      <Dialog
        open={confirm !== null}
        onOpenChange={(_e, data) => !data.open && setConfirm(null)}
        modalType="alert"
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>删除 {confirm?.id}？</DialogTitle>
            <DialogContent>
              {confirm?.isolated
                ? '该版本启用了隔离：版本目录里的存档、模组、配置会一起删除，且不可恢复。'
                : '只删除版本目录；共享的 libraries/assets 与 .minecraft 根下的存档不受影响。'}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setConfirm(null)}>
                取消
              </Button>
              <Button
                appearance="primary"
                style={{ background: '#B42318' }}
                onClick={() => {
                  const target = confirm
                  setConfirm(null)
                  if (target) void remove(target)
                }}
              >
                删除
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {host}
    </PageShell>
  )
}
