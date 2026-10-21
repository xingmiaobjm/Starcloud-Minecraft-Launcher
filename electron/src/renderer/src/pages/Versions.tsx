/// 版本页：对齐 Win 端 VersionsPage —— 顶部工具栏（游戏目录 / 导入 / 搜索 / 类别 / 刷新）
/// + 满高卡片列表（方块图标、类型与隔离芯片、两行数据列、更多菜单、删除）。
import { useEffect, useMemo, useState } from 'react'

import { request } from '../bridge'
import type { InstalledVersion } from '../bridge'
import { EmptyState, Modal, SkeletonList, useToasts, Select } from '../ui'
import VersionDetailPage from './VersionDetail'

import grassIcon from '../assets/mc-icons/natural_grass_block.png'
import amethystIcon from '../assets/mc-icons/block_of_amethyst.png'
import cobbleIcon from '../assets/mc-icons/masonry_cobblestone.png'
import dirtIcon from '../assets/mc-icons/natural_dirt.png'
import neoforgeIcon from '../assets/mc-icons/neoforge.png'
import forgeIcon from '../assets/mc-icons/forge.png'
import fabricIcon from '../assets/mc-icons/fabric.png'
import quiltIcon from '../assets/mc-icons/quilt.png'

const LOADER_ICONS: Record<string, string> = {
  neoforge: neoforgeIcon,
  forge: forgeIcon,
  fabric: fabricIcon,
  quilt: quiltIcon
}
const TYPE_ICONS: Record<string, string> = {
  release: grassIcon,
  snapshot: amethystIcon,
  old_beta: cobbleIcon,
  old_alpha: dirtIcon
}
const LOADER_LABEL: Record<string, string> = {
  neoforge: 'NeoForge',
  forge: 'Forge',
  fabric: 'Fabric',
  quilt: 'Quilt'
}
const TYPE_LABEL: Record<string, string> = {
  release: '正式版',
  snapshot: '快照版',
  old_beta: '远古 Beta',
  old_alpha: '远古 Alpha'
}

/** 版本行图标（加载器优先，否则按版本类型）。 */
function iconFor(v: InstalledVersion): string {
  const loader = v.loader ?? ''
  if (LOADER_ICONS[loader] !== undefined) return LOADER_ICONS[loader]
  const lower = v.id.toLowerCase()
  if (lower.includes('neoforge')) return neoforgeIcon
  if (lower.includes('forge')) return forgeIcon
  if (lower.includes('fabric')) return fabricIcon
  if (lower.includes('quilt')) return quiltIcon
  return TYPE_ICONS[v.type ?? 'release'] ?? grassIcon
}

/** 类型标签（加载器优先，否则版本类型）。 */
function tagFor(v: InstalledVersion): string {
  const loader = v.loader ?? ''
  if (LOADER_LABEL[loader] !== undefined) return LOADER_LABEL[loader]
  const lower = v.id.toLowerCase()
  if (lower.includes('neoforge')) return 'NeoForge'
  if (lower.includes('forge')) return 'Forge'
  if (lower.includes('fabric')) return 'Fabric'
  if (lower.includes('quilt')) return 'Quilt'
  return TYPE_LABEL[v.type ?? 'release'] ?? '正式版'
}

function fmtSize(bytes: number): string {
  if (!bytes) return '未完成'
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/** 上次启动文案（对齐 Win 端 FormatLastPlayed）。 */
function lastPlayed(iso?: string): string {
  if (iso === undefined || iso === null || iso === '') return '从未启动'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '从未启动'
  const pad = (n: number): string => n.toString().padStart(2, '0')
  const hm = `${pad(d.getHours())}:${pad(d.getMinutes())}`
  const today = new Date()
  const dayDiff = Math.floor(
    (new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime() -
      new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()) /
      86400000
  )
  if (dayDiff <= 0) return `今天 ${hm}`
  if (dayDiff === 1) return `昨天 ${hm}`
  if (dayDiff < 30) return `${dayDiff} 天前`
  if (dayDiff < 365) return `${Math.floor(dayDiff / 30)} 个月前`
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/** 排序对齐 Win：默认版本置顶，其余 id 倒序。 */
function sortVersions(list: InstalledVersion[]): InstalledVersion[] {
  return [...list].sort((a, b) => {
    const aLast = a.isDefault === true ? 1 : 0
    const bLast = b.isDefault === true ? 1 : 0
    if (aLast !== bLast) return bLast - aLast
    const ai = a.id.toLowerCase()
    const bi = b.id.toLowerCase()
    return ai < bi ? 1 : ai > bi ? -1 : 0
  })
}

export default function VersionsPage(props: {
  onOpenDetail: (id: string) => void
  onOpenSettings: () => void
}): JSX.Element {
  const { push, host } = useToasts()
  const [items, setItems] = useState<InstalledVersion[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<InstalledVersion | null>(null)
  const [gameDir, setGameDir] = useState('')
  const [query, setQuery] = useState('')
  const [kindFilter, setKindFilter] = useState('全部')
  const [menuFor, setMenuFor] = useState<InstalledVersion | null>(null)
  const [openId, setOpenId] = useState<string | null>(null)

  async function reload(): Promise<void> {
    setLoading(true)
    try {
      const r = await request<{ installed: InstalledVersion[] }>('catalog.installed')
      setItems(sortVersions(r.installed))
    } catch (e) {
      push('error', `读取版本列表失败：${String(e)}`)
    } finally {
      setLoading(false)
    }
    try {
      const s = await request<{ gameDir: string }>('settings.get')
      setGameDir(s.gameDir)
    } catch {
      /* 目录读取失败不阻塞列表 */
    }
  }

  useEffect(() => {
    void reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const kinds = ['全部', '正式版', '快照版', 'NeoForge', 'Forge', 'Fabric', 'Quilt']

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return items.filter((v) => {
      if (q !== '' && !v.id.toLowerCase().includes(q)) return false
      if (kindFilter !== '全部' && tagFor(v) !== kindFilter) return false
      return true
    })
  }, [items, query, kindFilter])

  async function openFolder(): Promise<void> {
    try {
      await request('versions.openFolder', {})
    } catch (e) {
      push('error', String(e))
    }
  }

  async function openVersionFolder(v: InstalledVersion): Promise<void> {
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
      push('success', `已将 ${v.id} 设为默认版本`)
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
    <div className="page">
      {/* 工具栏（对齐 Win 端：目录栏 + 导入 + 搜索 + 类别 + 刷新 / 打开目录） */}
      <div
        style={{
          display: 'flex',
          gap: 8,
          alignItems: 'center',
          marginBottom: 14,
          flexShrink: 0
        }}
      >
        <div
          className="input"
          style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 8 }}
          title={gameDir}
        >
          <span aria-hidden style={{ opacity: 0.55 }}>
            📁
          </span>
          <span className="truncate" style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
            {gameDir === '' ? '游戏目录' : gameDir}
          </span>
        </div>
        <button
          className="btn"
          onClick={() => push('info', '整合包导入：内核能力接入后可用')}
          title="导入整合包"
        >
          ⤓ 导入整合包
        </button>
        <button
          className="btn"
          onClick={() => push('info', '从链接导入：内核能力接入后可用')}
          title="从链接导入"
        >
          从链接导入
        </button>
        <input
          className="input"
          style={{ width: 210 }}
          placeholder="搜索版本 / 类别"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <Select
          width={110}
          value={kindFilter}
          options={kinds.map((k) => ({ value: k, label: k }))}
          onChange={setKindFilter}
        />
        <button className="btn" onClick={() => void reload()} disabled={loading}>
          ↻ 刷新
        </button>
        <button className="btn" onClick={() => void openFolder()}>
          📁 打开游戏目录
        </button>
      </div>

      {/* 列表卡片：满高、内部滚动 */}
      <div className="card card-flush" style={{ flex: 1, minHeight: 0 }}>
        <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', overflowX: 'hidden', padding: '6px 10px 10px' }}>
          {loading && <SkeletonList rows={6} avatar />}
          {!loading && filtered.length === 0 && (
            <EmptyState
              title={items.length === 0 ? '还没有已安装的版本' : '没有匹配的版本'}
              hint={
                items.length === 0
                  ? '去「下载」页选择一个版本安装。'
                  : '换个关键词或类别试试。'
              }
            />
          )}
          {filtered.map((v) => (
            <div
              key={v.id}
              className="row"
              style={{
                display: 'grid',
                gridTemplateColumns: 'auto minmax(120px, 260px) minmax(0, 1fr) auto auto auto',
                columnGap: 12
              }}
              onClick={() => setOpenId(v.id)}
            >
              <div className="icon32">
                <img src={iconFor(v)} alt="" width={32} height={32} />
              </div>
              <span className="t-strong truncate">{v.id}</span>
              <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                <span className="chip">{tagFor(v)}</span>
                {v.isolated === true && <span className="chip">版本隔离</span>}
                {v.isDefault === true && <span className="chip chip-accent">默认</span>}
                {v.jarExists === false && <span className="chip chip-danger">缺少 jar</span>}
              </div>
              <div className="meta">
                <span className="meta-k">上次启动</span>
                <span className="meta-v">{lastPlayed(v.lastPlayedAt)}</span>
                <span className="meta-k">大小</span>
                <span className="meta-v">{fmtSize(v.sizeBytes)}</span>
              </div>
              <button
                className="btn btn-subtle btn-sm"
                title="更多"
                disabled={busy !== null}
                onClick={(e) => {
                  e.stopPropagation()
                  setMenuFor(v)
                }}
              >
                ⋮
              </button>
              <button
                className="btn btn-subtle btn-sm btn-danger"
                title="删除"
                disabled={busy !== null}
                onClick={(e) => {
                  e.stopPropagation()
                  setConfirm(v)
                }}
              >
                🗑
              </button>
            </div>
          ))}
        </div>
      </div>

      {/* 更多菜单 */}
      <Modal
        open={menuFor !== null}
        title={menuFor?.id ?? ''}
        onClose={() => setMenuFor(null)}
        footer={
          <button className="btn" onClick={() => setMenuFor(null)}>
            关闭
          </button>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <button
            className="btn"
            onClick={() => {
              const v = menuFor
              setMenuFor(null)
              if (v !== null) void openVersionFolder(v)
            }}
          >
            打开版本目录
          </button>
          <button
            className="btn"
            disabled={menuFor?.isDefault === true}
            onClick={() => {
              const v = menuFor
              setMenuFor(null)
              if (v !== null) void setDefault(v)
            }}
          >
            设为默认版本
          </button>
          <button
            className="btn"
            onClick={() => {
              const v = menuFor
              setMenuFor(null)
              if (v !== null) setOpenId(v.id)
            }}
          >
            版本详情
          </button>
          <button className="btn" onClick={() => props.onOpenSettings()}>
            实例设置
          </button>
        </div>
      </Modal>

      {/* 删除确认 */}
      <Modal
        open={confirm !== null}
        title="删除版本？"
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
                const v = confirm
                setConfirm(null)
                if (v !== null) void remove(v)
              }}
            >
              删除
            </button>
          </>
        }
      >
        <div className="dialog-body">
          将删除版本 <b>{confirm?.id}</b> 的整个目录（含模组与配置），不可恢复。
        </div>
      </Modal>

      {host}
    </div>
  )
}
