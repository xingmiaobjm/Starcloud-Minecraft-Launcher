/// 存档页：跨版本（主目录 + 各隔离实例）汇总展示，支持打开目录 / 备份 / 删除。
/// 删除是破坏性操作，必须走 Dialog 二次确认。
import { useCallback, useEffect, useState } from 'react'
import {
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogSurface,
  DialogTitle,
  Text,
  makeStyles
} from '@fluentui/react-components'
import {
  ArchiveRegular,
  ArrowSyncRegular,
  DeleteRegular,
  FolderOpenRegular,
  HardDriveRegular
} from '@fluentui/react-icons'
import { request } from '../bridge'
import type { SaveInfo, SaveList } from '../bridge'
import { brandYellow, motion } from '../theme'
import { EmptyState, PageShell, SkeletonList, useToasts } from '../ui'

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))',
    gap: '12px',
    paddingTop: '4px'
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '14px',
    borderRadius: '10px',
    border: '1.2px solid transparent',
    transition: `background ${motion.fast}ms ${motion.ease}, border-color ${motion.fast}ms ${motion.ease}, transform ${motion.fast}ms ${motion.ease}`,
    animation: `rowIn ${motion.normal}ms ${motion.easeOut} both`,
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.055)',
      transform: 'translateY(-1px)'
    }
  },
  head: { display: 'flex', alignItems: 'center', gap: '10px' },
  icon: {
    width: '32px',
    height: '32px',
    borderRadius: '6px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    overflow: 'hidden',
    flexShrink: 0
  },
  meta: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap'
  },
  tag: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '999px',
    border: '1px solid rgba(255,255,255,0.16)',
    background: 'rgba(255,255,255,0.05)',
    whiteSpace: 'nowrap'
  },
  sharedTag: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '999px',
    border: `1px solid ${brandYellow}`,
    background: 'rgba(255,194,31,0.14)',
    color: brandYellow,
    whiteSpace: 'nowrap'
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginTop: '2px'
  },
  primaryButton: {
    height: '30px',
    borderRadius: '6px',
    border: 'none',
    background: brandYellow,
    color: '#1b1b1b',
    fontWeight: 600,
    cursor: 'pointer',
    padding: '0 12px',
    transition: `background ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: '#FFD35C' }
  },
  linkButton: {
    height: '30px',
    borderRadius: '6px',
    border: '1px solid rgba(255,255,255,0.14)',
    background: 'transparent',
    color: 'inherit',
    cursor: 'pointer',
    padding: '0 10px',
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    transition: `background ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: 'rgba(255,255,255,0.07)' }
  },
  dangerButton: {
    height: '30px',
    borderRadius: '6px',
    border: '1px solid rgba(224,75,75,0.45)',
    background: 'transparent',
    color: '#FF9A9A',
    cursor: 'pointer',
    padding: '0 10px',
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    transition: `background ${motion.fast}ms ${motion.ease}`,
    ':hover': { background: 'rgba(224,75,75,0.16)' }
  }
})

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
  const styles = useStyles()
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
          <Button
            appearance="subtle"
            icon={<ArrowSyncRegular />}
            onClick={() => void load()}
            disabled={loading}
          />
        }
      >
        {loading && <SkeletonList rows={4} />}
        {!loading && saves.length === 0 && (
          <EmptyState
            title="还没有发现存档"
            hint="启动一次游戏后回到这里，或确认设置里的 .minecraft 目录是否正确。"
            action={
              <Button appearance="secondary" icon={<ArrowSyncRegular />} onClick={() => void load()}>
                重新扫描
              </Button>
            }
          />
        )}

        {!loading && saves.length > 0 && (
          <div className={styles.grid}>
            {saves.map((save, index) => (
              <Card
                key={`${save.gameRoot}::${save.folderName}`}
                className={styles.card}
                style={{ animationDelay: `${index * 24}ms` }}
              >
                <div className={styles.head}>
                  <div className={styles.icon} style={{ background: 'rgba(255,194,31,0.12)' }}>
                    {save.iconPath ? (
                      <img
                        src={`file://${save.iconPath}`}
                        alt=""
                        width={32}
                        height={32}
                        style={{ width: '32px', height: '32px', borderRadius: '6px', objectFit: 'cover' }}
                      />
                    ) : (
                      <HardDriveRegular />
                    )}
                  </div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text size={300} weight="semibold" truncate>
                      {save.levelName}
                    </Text>
                    <Text size={100} style={{ opacity: 0.55 }} truncate>
                      {save.folderName}
                    </Text>
                  </div>
                </div>

                <div className={styles.meta}>
                  <span className={styles.tag}>{save.versionId}</span>
                  <span className={save.isolated ? styles.tag : styles.sharedTag}>
                    {save.isolated ? '实例独立' : '主目录共享'}
                  </span>
                </div>

                <div style={{ display: 'flex', gap: '14px' }}>
                  <Text size={200} style={{ opacity: 0.65 }}>
                    {formatBytes(save.sizeBytes)}
                  </Text>
                  <Text size={200} style={{ opacity: 0.65 }}>
                    {formatLastPlayed(save.lastPlayedAt)}
                  </Text>
                </div>

                <div className={styles.actions}>
                  <button
                    className={styles.primaryButton}
                    onClick={() => void openFolder(save)}
                    style={{ display: 'flex', alignItems: 'center', gap: '6px' }}
                  >
                    <FolderOpenRegular />
                    打开目录
                  </button>
                  <button className={styles.linkButton} onClick={() => void backup(save)}>
                    <ArchiveRegular />
                    备份
                  </button>
                  <div style={{ flex: 1 }} />
                  <button
                    className={styles.dangerButton}
                    onClick={() => setPendingDelete(save)}
                    aria-label={`删除 ${save.levelName}`}
                  >
                    <DeleteRegular />
                  </button>
                </div>
              </Card>
            ))}
          </div>
        )}
      </PageShell>

      {/* 删除确认：不可撤销，必须二次确认。 */}
      <Dialog
        open={pendingDelete !== null}
        onOpenChange={(_e, data) => {
          if (!data.open) setPendingDelete(null)
        }}
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>删除存档「{pendingDelete?.levelName}」？</DialogTitle>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
              <Text size={200} style={{ opacity: 0.75 }}>
                这会永久删除目录 {pendingDelete?.folderName}
                {pendingDelete !== null && ` （约 ${formatBytes(pendingDelete.sizeBytes)}）`}
                ，且无法撤销。建议先备份。
              </Text>
              {deleting && (
                <Text size={200} style={{ opacity: 0.7 }}>
                  正在删除，请勿关闭窗口…
                </Text>
              )}
            </div>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setPendingDelete(null)} disabled={deleting}>
                取消
              </Button>
              <Button
                appearance="primary"
                onClick={() => void confirmDelete()}
                disabled={deleting}
                icon={<DeleteRegular />}
                style={{ background: '#E04B4B', color: '#ffffff' }}
              >
                {deleting ? '删除中…' : '确认删除'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {host}
    </>
  )
}
