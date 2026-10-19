/// 投影管理：把散落在全局目录和各版本实例里的投影收在一张表里，
/// 直接复制 / 移动到需要的版本（对齐 Win 端 ProjectionManagerPage）。
import { useEffect, useState } from 'react'

import { request } from '../bridge'
import { EmptyState, Modal, PageShell, SkeletonList, useToasts } from '../ui'

export interface ProjectionFile {
  filePath: string
  fileName: string
  sizeBytes: number
  modifiedAt: string
  /** null = 全局 .minecraft/schematics */
  versionId?: string
}

interface ScanResult {
  files: ProjectionFile[]
  versions: string[]
}

function fmtSize(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}

function fmtTime(iso: string): string {
  const d = new Date(iso)
  if (isNaN(d.getTime())) return iso
  const p = (n: number): string => n.toString().padStart(2, '0')
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}

export default function ProjectionsPage(): JSX.Element {
  const { push, host } = useToasts()
  const [files, setFiles] = useState<ProjectionFile[]>([])
  const [versions, setVersions] = useState<string[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)
  const [target, setTarget] = useState<ProjectionFile | null>(null)
  const [mode, setMode] = useState<'copy' | 'move'>('copy')
  const [destVersion, setDestVersion] = useState<string>('')
  const [confirmDelete, setConfirmDelete] = useState<ProjectionFile | null>(null)

  async function reload(): Promise<void> {
    setLoading(true)
    try {
      const r = await request<ScanResult>('projections.scan', {})
      setFiles(r.files)
      setVersions(r.versions)
    } catch (e) {
      push('error', `扫描投影失败：${String(e)}`)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function transfer(): Promise<void> {
    if (target === null) return
    setBusy(target.filePath)
    try {
      const r = await request<{ path: string }>(
        mode === 'copy' ? 'projections.copy' : 'projections.move',
        { filePath: target.filePath, versionId: destVersion === '' ? null : destVersion }
      )
      push('success', `${mode === 'copy' ? '已复制' : '已移动'}到 ${r.path}`)
      setTarget(null)
      await reload()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  async function remove(file: ProjectionFile): Promise<void> {
    setBusy(file.filePath)
    try {
      await request('projections.delete', { filePath: file.filePath })
      push('success', `已删除 ${file.fileName}`)
      await reload()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(null)
    }
  }

  return (
    <PageShell
      title="投影管理"
      subtitle="全局与各版本实例的投影文件，跨版本复制或移动"
      actions={
        <button className="btn" onClick={() => void reload()} disabled={loading}>
          ↻ 刷新
        </button>
      }
    >
      {loading ? (
        <SkeletonList rows={6} avatar />
      ) : files.length === 0 ? (
        <EmptyState
          title="还没有投影文件"
          hint="投影放在游戏目录的 schematics 下（.litematic / .nbt / .schematic / .schem）。"
        />
      ) : (
        <div>
          {files.map((file, index) => (
            <div
              key={file.filePath}
              className="row row-static"
              style={{ animationDelay: `${index * 39}ms` }}
            >
              <div className="icon32">◈</div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div className="t-strong truncate">{file.fileName}</div>
                <div className="t-cap truncate">
                  {fmtSize(file.sizeBytes)} · {fmtTime(file.modifiedAt)} ·{' '}
                  {file.versionId ?? '全局目录'}
                </div>
              </div>
              <div className="meta">
                <span className="meta-k">位置</span>
                <span className="meta-v">{file.versionId ?? '全局'}</span>
                <span className="meta-k">大小</span>
                <span className="meta-v">{fmtSize(file.sizeBytes)}</span>
              </div>
              <button
                className="btn"
                disabled={busy !== null}
                onClick={() => {
                  setTarget(file)
                  setMode('copy')
                  setDestVersion('')
                }}
              >
                复制到…
              </button>
              <button
                className="btn"
                disabled={busy !== null}
                onClick={() => {
                  setTarget(file)
                  setMode('move')
                  setDestVersion('')
                }}
              >
                移动到…
              </button>
              <button
                className="btn btn-subtle btn-danger"
                disabled={busy !== null}
                onClick={() => setConfirmDelete(file)}
              >
                删除
              </button>
            </div>
          ))}
        </div>
      )}

      {/* 复制/移动目标选择 */}
      <Modal
        open={target !== null}
        title={mode === 'copy' ? '复制投影' : '移动投影'}
        onClose={() => setTarget(null)}
        footer={
          <>
            <button className="btn" onClick={() => setTarget(null)}>
              取消
            </button>
            <button
              className="btn btn-primary"
              disabled={busy !== null}
              onClick={() => void transfer()}
            >
              {busy !== null ? '处理中…' : mode === 'copy' ? '复制' : '移动'}
            </button>
          </>
        }
      >
        <div className="t-body" style={{ marginBottom: 12 }}>
          将「{target?.fileName}」{mode === 'copy' ? '复制' : '移动'}到：
        </div>
        <select
          className="input"
          style={{ width: '100%' }}
          value={destVersion}
          onChange={(e) => setDestVersion(e.target.value)}
        >
          <option value="">全局 .minecraft/schematics</option>
          {versions.map((v) => (
            <option key={v} value={v}>
              {v}
            </option>
          ))}
        </select>
      </Modal>

      {/* 删除确认 */}
      <Modal
        open={confirmDelete !== null}
        title="删除投影？"
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
        <div className="dialog-body">
          投影文件会从磁盘永久删除：{confirmDelete?.fileName}
        </div>
      </Modal>

      {host}
    </PageShell>
  )
}
