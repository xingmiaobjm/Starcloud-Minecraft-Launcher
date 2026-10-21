/// 存档页：对齐 Win 端 SavesPage —— 顶部工具栏（批量操作 + 搜索 + 筛选 + 导入 + 打开目录）
/// + 满高列表（64px 世界缩略图、名称双行、版本/隔离芯片、两行数据列）。
///
/// 缩略图：世界目录里的 icon.png 由桥接 `saves.icon` 读成 data URL 返回
/// （渲染层读不到文件系统，也不该为图片放开 file:// CSP）。
import { useCallback, useEffect, useMemo, useState } from 'react'

import { request } from '../bridge'
import type { SaveInfo, SaveList } from '../bridge'
import { EmptyState, SkeletonList, useToasts, Select } from '../ui'

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`
}

/** 上次游玩文案（对齐 Win 端：无记录 → 无游玩记录）。 */
function lastPlayedText(iso?: string): string {
  if (iso === undefined || iso === null || iso === '') return '无游玩记录'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '无游玩记录'
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

export default function SavesPage(): JSX.Element {
  const { push, host } = useToasts()
  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [icons, setIcons] = useState<Record<string, string>>({})
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [query, setQuery] = useState('')
  const [versionFilter, setVersionFilter] = useState('全部')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const data = await request<SaveList>('saves.list')
      setSaves(data.saves)
      // 缩略图逐张取（数量有限；失败就保持占位块）
      for (const s of data.saves) {
        if (s.iconPath === undefined || s.iconPath === '') continue
        void request<{ dataUrl: string }>('saves.icon', { path: s.iconPath })
          .then((r) => setIcons((prev) => ({ ...prev, [s.folderName]: r.dataUrl })))
          .catch(() => undefined)
      }
    } catch (e) {
      push('error', `存档列表读取失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  const versions = useMemo(() => {
    const set = new Set<string>()
    for (const s of saves) set.add(s.versionId === '' ? '全局' : s.versionId)
    return ['全部', ...set]
  }, [saves])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return saves.filter((s) => {
      if (q !== '' && !`${s.levelName} ${s.folderName} ${s.versionId}`.toLowerCase().includes(q)) {
        return false
      }
      if (versionFilter !== '全部') {
        const v = s.versionId === '' ? '全局' : s.versionId
        if (v !== versionFilter) return false
      }
      return true
    })
  }, [saves, query, versionFilter])

  const selectedSaves = filtered.filter((s) => selected.has(s.folderName))

  async function openFolder(save?: SaveInfo): Promise<void> {
    setBusy(true)
    try {
      await request('saves.openFolder', { gameRoot: save?.gameRoot, folderName: save?.folderName })
      push('success', save === undefined ? '已打开存档目录' : `已打开 ${save.levelName} 的目录`)
    } catch (e) {
      push('error', `打开目录失败：${String(e)}`)
    } finally {
      setBusy(false)
    }
  }

  async function backup(targets: SaveInfo[]): Promise<void> {
    if (targets.length === 0) {
      push('info', '先勾选要备份的世界')
      return
    }
    setBusy(true)
    try {
      let total = 0
      for (const t of targets) {
        const r = await request<{ sizeBytes: number }>('saves.backup', {
          gameRoot: t.gameRoot,
          folderName: t.folderName
        })
        total += r.sizeBytes
      }
      push('success', `已备份 ${targets.length} 个世界（${formatBytes(total)}）`)
    } catch (e) {
      push('error', `备份失败：${String(e)}`)
    } finally {
      setBusy(false)
    }
  }

  function toggle(folder: string): void {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(folder)) next.delete(folder)
      else next.add(folder)
      return next
    })
  }

  const tplBtn = (
    label: string,
    onClick: () => void,
    opts?: { disabled?: boolean; title?: string }
  ): JSX.Element => (
    <button
      className="btn"
      disabled={opts?.disabled === true || busy}
      title={opts?.title ?? label}
      onClick={onClick}
    >
      {label}
    </button>
  )

  return (
    <div className="page">
      {/* 工具栏（对齐 Win 端批量操作行） */}
      <div
        style={{
          display: 'flex',
          gap: 8,
          alignItems: 'center',
          marginBottom: 14,
          flexWrap: 'wrap',
          flexShrink: 0
        }}
      >
        {tplBtn('用其他版本启动', () => push('info', '跨版本启动：需选择版本后可用'), {
          disabled: true
        })}
        {tplBtn('复制到其他版本', () => push('info', '复制存档：内核能力接入后可用'), {
          disabled: true
        })}
        {tplBtn(
          '备份所选世界',
          () => void backup(selectedSaves),
          { disabled: selectedSaves.length === 0, title: '备份勾选的世界' }
        )}
        {tplBtn('同步到设备…', () => push('info', '设备同步：需登录 Starcloud ID'), {
          disabled: true
        })}
        {tplBtn('分享', () => push('info', '分享：内核能力接入后可用'), { disabled: true })}
        <div style={{ width: 1, height: 24, background: 'var(--stroke-divider)' }} />
        <input
          className="input"
          style={{ width: 220 }}
          placeholder="搜索世界 / 版本"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <Select
          width={150}
          value={versionFilter}
          options={versions.map((v) => ({ value: v, label: v }))}
          onChange={setVersionFilter}
        />
        <div style={{ flex: 1 }} />
        {tplBtn('导入分享包', () => push('info', '导入分享包：内核能力接入后可用'), {
          disabled: true
        })}
        {tplBtn('打开存档目录', () => void openFolder())}
        <button className="btn btn-subtle" onClick={() => void load()} disabled={loading}>
          ↻
        </button>
      </div>

      {/* 列表卡片：满高、内部滚动 */}
      <div className="card card-flush" style={{ flex: 1, minHeight: 0 }}>
        <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', overflowX: 'hidden', padding: '6px 12px 12px' }}>
          {loading && <SkeletonList rows={5} avatar tall />}
          {!loading && filtered.length === 0 && (
            <EmptyState
              title={saves.length === 0 ? '还没有发现存档' : '没有匹配的世界'}
              hint={
                saves.length === 0
                  ? '启动一次游戏后回到这里，或确认设置里的 .minecraft 目录是否正确。'
                  : '换个关键词或版本筛选试试。'
              }
              action={
                <button className="btn" onClick={() => void load()}>
                  ↻ 重新扫描
                </button>
              }
            />
          )}
          {filtered.map((s) => {
            const icon = icons[s.folderName]
            const checked = selected.has(s.folderName)
            return (
              <div
                key={s.gameRoot + '/' + s.folderName}
                className={`row${checked ? ' row-active' : ''}`}
                onClick={() => toggle(s.folderName)}
              >
                {/* 64px 世界缩略图（Win 端同尺寸） */}
                <div
                  className="icon32"
                  style={{ width: 64, height: 64, borderRadius: 8, overflow: 'hidden' }}
                >
                  {icon !== undefined ? (
                    <img src={icon} alt="" width={64} height={64} style={{ objectFit: 'cover' }} />
                  ) : (
                    <span style={{ fontSize: 22, opacity: 0.45 }}>▧</span>
                  )}
                </div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div className="t-strong truncate">{s.levelName}</div>
                  <div className="t-meta truncate">{s.folderName}</div>
                </div>
                <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                  <span className="chip">{s.versionId === '' ? '全局' : s.versionId}</span>
                  {s.isolated && <span className="chip">隔离</span>}
                </div>
                <div className="meta">
                  <span className="meta-k">上次游玩</span>
                  <span className="meta-v">{lastPlayedText(s.lastPlayed)}</span>
                  <span className="meta-k">大小</span>
                  <span className="meta-v">{formatBytes(s.sizeBytes)}</span>
                </div>
              </div>
            )
          })}
        </div>
      </div>

      {host}
    </div>
  )
}
