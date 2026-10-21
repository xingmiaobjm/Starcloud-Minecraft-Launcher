/// 投影管理页：对齐 Win 端 ProjectionManagerPage ——
/// 左侧投影列表（文件名 + 大小·日期·位置 + 完整路径 + 所在版本标签），
/// 右侧「目标版本」面板（下拉 + 复制/移动/在文件夹中显示/删除）。
import { useCallback, useEffect, useMemo, useState } from 'react'

import { request } from '../bridge'
import { EmptyState, Modal, SkeletonList, useToasts, Select } from '../ui'

export interface ProjectionFile {
  filePath: string
  fileName: string
  sizeBytes: number
  modifiedAt: string
  /** 空 = 全局 .minecraft/schematics */
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
  const [busy, setBusy] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  /** 目标版本：'' = 全局（共享目录） */
  const [dest, setDest] = useState('')
  const [confirmDelete, setConfirmDelete] = useState<ProjectionFile | null>(null)

  const load = useCallback(async (): Promise<void> => {
    setLoading(true)
    try {
      const r = await request<ScanResult>('projections.scan', {})
      setFiles(r.files)
      setVersions(r.versions)
      if (selected !== null && !r.files.some((f) => f.filePath === selected)) {
        setSelected(null)
      }
    } catch (e) {
      push('error', `扫描投影失败：${String(e)}`)
    }
    setLoading(false)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [push])

  useEffect(() => {
    void load()
  }, [load])

  const current = useMemo(
    () => files.find((f) => f.filePath === selected) ?? null,
    [files, selected]
  )

  async function transfer(mode: 'copy' | 'move'): Promise<void> {
    if (current === null) return
    setBusy(true)
    try {
      const r = await request<{ path: string }>(
        mode === 'copy' ? 'projections.copy' : 'projections.move',
        { filePath: current.filePath, versionId: dest === '' ? null : dest }
      )
      push('success', `${mode === 'copy' ? '已复制' : '已移动'}到 ${r.path}`)
      await load()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(false)
    }
  }

  async function remove(file: ProjectionFile): Promise<void> {
    setBusy(true)
    try {
      await request('projections.delete', { filePath: file.filePath })
      push('success', `已删除 ${file.fileName}`)
      setSelected(null)
      await load()
    } catch (e) {
      push('error', String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div style={{ flex: 1, minWidth: 0 }}>
          <h1 className="page-title">投影管理</h1>
          <div className="page-sub">扫描全局与各版本实例的投影，可直接复制到需要使用的版本。</div>
        </div>
        <button className="btn btn-primary" onClick={() => void load()} disabled={loading}>
          刷新
        </button>
      </div>

      <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: 16 }}>
        {/* 左：投影列表 */}
        <div className="card card-flush" style={{ flex: 1, minWidth: 0 }}>
          <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '8px 12px' }}>
            {loading && <SkeletonList rows={6} />}
            {!loading && files.length === 0 && (
              <EmptyState
                title="还没有投影文件"
                hint="投影放在游戏目录的 schematics 下（.litematic / .nbt / .schematic / .schem）。"
              />
            )}
            {files.map((f) => {
              const active = f.filePath === selected
              return (
                <div
                  key={f.filePath}
                  className={`row${active ? ' row-active' : ''}`}
                  style={{ alignItems: 'flex-start', paddingTop: 12, paddingBottom: 12 }}
                  onClick={() => setSelected(active ? null : f.filePath)}
                >
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="t-strong truncate">{f.fileName}</div>
                    <div className="t-meta">
                      {fmtSize(f.sizeBytes)} · {fmtTime(f.modifiedAt)} ·{' '}
                      {f.versionId === undefined ? '全局目录' : `版本 ${f.versionId}`}
                    </div>
                    <div
                      className="t-meta truncate"
                      style={{ opacity: 0.65 }}
                      title={f.filePath}
                    >
                      {f.filePath}
                    </div>
                  </div>
                  <span className="t-body" style={{ flexShrink: 0 }}>
                    {f.versionId === undefined ? '全局' : f.versionId}
                  </span>
                </div>
              )
            })}
          </div>
        </div>

        {/* 右：目标版本面板 */}
        <div className="card" style={{ width: 370, flexShrink: 0, alignSelf: 'flex-start' }}>
          <div className="t-strong" style={{ marginBottom: 12 }}>
            目标版本
          </div>
          <Select
            width={340}
            value={dest}
            options={[
              { value: '', label: '全局（共享目录）' },
              ...versions.map((v) => ({ value: v, label: v }))
            ]}
            onChange={setDest}
          />

          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 14 }}>
            <button
              className="btn"
              disabled={current === null || busy}
              onClick={() => void transfer('copy')}
            >
              复制到该版本
            </button>
            <button
              className="btn"
              disabled={current === null || busy}
              onClick={() => void transfer('move')}
            >
              移动到该版本
            </button>
            <button
              className="btn"
              disabled={current === null || busy}
              onClick={() =>
                push('info', '在文件夹中显示：需要主进程 openPath 能力，接入后可用')
              }
            >
              在文件夹中显示
            </button>
            <button
              className="btn btn-danger"
              disabled={current === null || busy}
              onClick={() => {
                if (current !== null) setConfirmDelete(current)
              }}
            >
              删除
            </button>
          </div>

          {current !== null && (
            <div className="t-cap" style={{ marginTop: 12 }}>
              已选：{current.fileName}
              <br />
              当前位于：{current.versionId === undefined ? '全局目录' : current.versionId}
            </div>
          )}
        </div>
      </div>

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
                const f = confirmDelete
                setConfirmDelete(null)
                if (f !== null) void remove(f)
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
    </div>
  )
}
