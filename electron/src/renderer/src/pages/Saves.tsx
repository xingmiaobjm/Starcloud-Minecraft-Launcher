/// 存档页：跨版本（主目录 + 各隔离实例）汇总展示，支持打开目录 / 备份 / 删除。
/// 删除是破坏性操作，必须走 Modal 二次确认。
/// 视觉全部走 winui.css 的纯 CSS 类，不依赖 Fluent 组件。
import { useCallback, useEffect, useState } from 'react'
import type { CSSProperties } from 'react'
import { request } from '../bridge'
import type { SaveInfo, SaveList } from '../bridge'
import { EmptyState, Modal, PageShell, SkeletonList, useToasts } from '../ui'

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

/** 相对时间比绝对日期更有信息量：刚玩过的存档一眼可辨。 */
function formatLastPlayed(iso?: string): string {
  if (!iso) return '从未游玩'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '从未游玩'
  const diff = Date.now() - d.getTime()
  const minute = 60_000
  const hour = 60 * minute
  const day = 24 * hour
  if (diff < minute) return '刚刚'
  if (diff < hour) return `${Math.floor(diff / minute)} 分钟前`
  if (diff < day) return `${Math.floor(diff / hour)} 小时前`
  if (diff < 30 * day) return `${Math.floor(diff / day)} 天前`
  return `${d.getFullYear()}-${(d.getMonth() + 1).toString().padStart(2, '0')}-${d
    .getDate()
    .toString()
    .padStart(2, '0')}`
}

export default function SavesPage() {
  const { push, host } = useToasts()
  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [loading, setLoading] = useState(true)
  // 待删除目标：非空即代表确认弹窗处于打开态。
  const [pendingDelete, setPendingDelete] = useState<SaveInfo | null>(null)
  const [deleting, setDeleting] = useState(false)

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const data = await request<SaveList>('saves.list')
      setSaves(data.saves)
    } catch (e) {
      push('error', `存档列表读取失败：${String(e)}`)
    }
    setLoading(false)
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  async function openFolder(save: SaveInfo): Promise<void> {
    try {
      await request<{ path: string }>('saves.openFolder', {
        gameRoot: save.gameRoot,
        folderName: save.folderName
      })
      push('success', `已打开 ${save.levelName} 的存档目录`)
    } catch (e) {
      push('error', `打开目录失败：${String(e)}`)
    }
  }

  async function backup(save: SaveInfo): Promise<void> {
    try {
      const result = await request<{ path: string; sizeBytes: number }>('saves.backup', {
        gameRoot: save.gameRoot,
        folderName: save.folderName
      })
      push('success', `已备份 ${save.levelName}（${formatBytes(result.sizeBytes)}）`)
    } catch (e) {
      push('error', `备份失败：${String(e)}`)
    }
  }

  async function confirmDelete(): Promise<void> {
    if (!pendingDelete) return
    const target = pendingDelete
    setDeleting(true)
    try {
      const result = await request<{ freedBytes: number }>('saves.delete', {
        gameRoot: target.gameRoot,
        folderName: target.folderName
      })
      push('success', `已删除 ${target.levelName}，释放 ${formatBytes(result.freedBytes)}`)
      setPendingDelete(null)
      await load()
    } catch (e) {
      push('error', `删除失败：${String(e)}`)
    } finally {
      setDeleting(false)
    }
  }

  return (
    <>
      <PageShell
        title="存档"
        subtitle={`共 ${saves.length} 个世界（包含各隔离实例）`}
        actions={
          <button className="btn btn-subtle" onClick={() => void load()} disabled={loading}>
            ↻
          </button>
        }
      >
        {loading && <SkeletonList rows={4} />}
        {!loading && saves.length === 0 && (
          <EmptyState
            title="还没有发现存档"
            hint="启动一次游戏后回到这里，或确认设置里的 .minecraft 目录是否正确。"
            action={
              <button className="btn" onClick={() => void load()}>
                ↻ 重新扫描
              </button>
            }
          />
        )}

        {!loading && saves.length > 0 && (
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))',
              gap: 12,
              paddingTop: 4
            }}
          >
            {saves.map((save, index) => (
              <div
                key={`${save.gameRoot}::${save.folderName}`}
                className="card glow"
                style={
                  {
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 10,
                    padding: 14,
                    animation: 'slideUp var(--t-enter) var(--e-enter) both',
                    animationDelay: `${index * 39}ms`,
                    '--glow':
                      'radial-gradient(360px circle at 50% 0%, color-mix(in srgb, var(--accent) 18%, transparent), transparent 65%)'
                  } as CSSProperties
                }
              >
                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                  <div
                    className="icon32"
                    style={{ background: 'color-mix(in srgb, var(--accent) 12%, transparent)', overflow: 'hidden' }}
                  >
                    {save.iconPath ? (
                      <img
                        src={`file://${save.iconPath}`}
                        alt=""
                        width={32}
                        height={32}
                        style={{ width: 32, height: 32, borderRadius: 'var(--r-control)', objectFit: 'cover' }}
                      />
                    ) : (
                      <span style={{ color: 'var(--accent)' }}>◫</span>
                    )}
                  </div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="t-strong truncate">{save.levelName}</div>
                    <div className="t-meta truncate">{save.folderName}</div>
                  </div>
                </div>

                <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                  <span className="chip">{save.versionId}</span>
                  {save.isolated ? (
                    <span className="chip">实例独立</span>
                  ) : (
                    <span className="chip chip-accent">主目录共享</span>
                  )}
                </div>

                <div style={{ display: 'flex', gap: 14 }}>
                  <span className="t-meta">{formatBytes(save.sizeBytes)}</span>
                  <span className="t-meta">{formatLastPlayed(save.lastPlayedAt)}</span>
                </div>

                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 2 }}>
                  <button
                    className="btn btn-primary"
                    onClick={() => void openFolder(save)}
                  >
                    ▤ 打开目录
                  </button>
                  <button className="btn" onClick={() => void backup(save)}>
                    ⛁ 备份
                  </button>
                  <div style={{ flex: 1 }} />
                  <button
                    className="btn btn-subtle btn-danger"
                    onClick={() => setPendingDelete(save)}
                    aria-label={`删除 ${save.levelName}`}
                  >
                    ✕
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </PageShell>

      {/* 删除确认：不可撤销，必须二次确认。 */}
      <Modal
        open={pendingDelete !== null}
        title={`删除存档「${pendingDelete?.levelName ?? ''}」？`}
        onClose={() => setPendingDelete(null)}
        width={480}
        footer={
          <>
            <button className="btn" onClick={() => setPendingDelete(null)} disabled={deleting}>
              取消
            </button>
            <button
              className="btn btn-primary"
              style={{ background: 'var(--danger)', color: '#fff' }}
              onClick={() => void confirmDelete()}
              disabled={deleting}
            >
              ✕ {deleting ? '删除中…' : '确认删除'}
            </button>
          </>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div className="t-body">
            这会永久删除目录 {pendingDelete?.folderName}
            {pendingDelete !== null && ` （约 ${formatBytes(pendingDelete.sizeBytes)}）`}
            ，且无法撤销。建议先备份。
          </div>
          {deleting && (
            <div className="t-cap" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <div className="spinner" />
              正在删除，请勿关闭窗口…
            </div>
          )}
        </div>
      </Modal>
      {host}
    </>
  )
}
