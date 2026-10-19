/// 版本页：已安装版本列表（打开目录 / 设为默认 / 删除）。
/// 视觉全部走 winui.css 的纯 CSS 类，不依赖 Fluent 组件。
import { useEffect, useState } from 'react'
import type { CSSProperties } from 'react'
import { request } from '../bridge'
import type { InstalledVersion } from '../bridge'
import { EmptyState, Modal, PageShell, SkeletonList, useToasts } from '../ui'
import VersionDetailPage from './VersionDetail'

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
    <>
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
              <div
                key={v.id}
                className="row glow"
                style={
                  {
                    animationDelay: `${index * 39}ms`,
                    '--glow': `radial-gradient(420px circle at 50% 0%, color-mix(in srgb, ${info.color} 20%, transparent), transparent 65%)`
                  } as CSSProperties
                }
                onClick={() => setOpenId(v.id)}
              >
                <div
                  className="icon32"
                  style={{ background: `${info.color}2b`, border: `1px solid ${info.color}`, color: info.color }}
                >
                  {v.id[0]?.toUpperCase()}
                </div>
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 3 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
                    <span className="t-strong truncate">{v.id}</span>
                    <span className="chip">{info.label}</span>
                    {v.isDefault && <span className="chip chip-accent">默认</span>}
                    {v.isolated && <span className="chip">隔离</span>}
                    {v.inheritsFrom && <span className="chip">继承 {v.inheritsFrom}</span>}
                    {v.jarExists === false && <span className="chip chip-danger">缺少 jar</span>}
                  </div>
                  <span className="t-meta truncate">
                    {fmtSize(v.sizeBytes)} · {v.directory ?? ''}
                  </span>
                </div>
                <button
                  className="btn btn-subtle"
                  disabled={busy !== null}
                  onClick={(e) => {
                    e.stopPropagation()
                    void openFolder(v)
                  }}
                >
                  ▤ 打开目录
                </button>
                <button
                  className="btn btn-subtle"
                  disabled={busy !== null || v.isDefault === true}
                  onClick={(e) => {
                    e.stopPropagation()
                    void setDefault(v)
                  }}
                >
                  ★ 设为默认
                </button>
                <button
                  className="btn btn-subtle btn-danger"
                  disabled={busy !== null}
                  onClick={(e) => {
                    e.stopPropagation()
                    setConfirm(v)
                  }}
                >
                  ✕ 删除
                </button>
              </div>
            )
          })
        )}
        {items.some((v) => busy === v.id) && (
          <div
            style={{
              height: 2,
              marginTop: 10,
              borderRadius: 2,
              background: 'var(--accent)',
              animation: 'pulse 1s infinite'
            }}
          />
        )}
      </PageShell>

      {/* 删除是破坏性操作，必须二次确认。 */}
      <Modal
        open={confirm !== null}
        title={`删除 ${confirm?.id ?? ''}？`}
        onClose={() => setConfirm(null)}
        width={480}
        footer={
          <>
            <button className="btn" onClick={() => setConfirm(null)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: '#fff' }}
              onClick={() => {
                const target = confirm
                setConfirm(null)
                if (target) void remove(target)
              }}
            >
              删除
            </button>
          </>
        }
      >
        <div className="t-body">
          {confirm?.isolated
            ? '该版本启用了隔离：版本目录里的存档、模组、配置会一起删除，且不可恢复。'
            : '只删除版本目录；共享的 libraries/assets 与 .minecraft 根下的存档不受影响。'}
        </div>
      </Modal>
      {host}
    </>
  )
}
