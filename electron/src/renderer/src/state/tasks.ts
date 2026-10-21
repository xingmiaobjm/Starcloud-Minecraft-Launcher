/// 调度中心任务列表（Win 端 MainWindow 右侧任务抽屉的等价物）。
/// 任务只保留本次会话（与 Win 端一致：纯内存，不跨启动持久化）。
import { useEffect, useState } from 'react'

import { onBridgeEvent } from '../bridge'

export interface TaskItem {
  id: string
  kind: 'download' | 'game' | 'other'
  title: string
  status: string
  progress?: number
  startedAt: number
  finished?: boolean
  failed?: boolean
}

let seq = 1

/** 全局任务列表：下载/安装类从事件流汇总；游戏会话由调用方并入。 */
export function useInstallTasks(): TaskItem[] {
  const [tasks, setTasks] = useState<TaskItem[]>([])

  useEffect(() => {
    let alive = true

    const upsert = (id: string, patch: Partial<TaskItem>): void => {
      setTasks((prev) => {
        const idx = prev.findIndex((t) => t.id === id)
        if (idx < 0) return prev
        const next = [...prev]
        next[idx] = { ...next[idx], ...patch }
        return next
      })
    }

    onBridgeEvent((event, params) => {
      if (!alive) return
      if (event === 'install.progress') {
        const id = String(params.versionId ?? params.projectId ?? '')
        if (id === '') return
        setTasks((prev) => {
          const exists = prev.some((t) => t.id === id)
          const base: TaskItem = {
            id,
            kind: 'download',
            title: id,
            status: String(params.stage ?? ''),
            progress:
              Number(params.total ?? 0) > 0
                ? (Number(params.current ?? 0) / Number(params.total)) * 100
                : undefined,
            startedAt: Date.now()
          }
          return exists
            ? prev.map((t) => (t.id === id ? { ...t, status: base.status, progress: base.progress } : t))
            : [base, ...prev]
        })
      } else if (event === 'install.done') {
        const id = String(params.versionId ?? params.projectId ?? '')
        upsert(id, { finished: true, status: '已完成', progress: 100 })
      } else if (event === 'install.failed') {
        const id = String(params.versionId ?? params.projectId ?? '')
        upsert(id, { finished: true, failed: true, status: `失败：${String(params.error ?? '')}` })
      } else if (event === 'game.exited') {
        const id = `game-${String(params.sessionId ?? '')}`
        upsert(id, { finished: true, status: `游戏退出（exit=${Number(params.code ?? -1)}）` })
      }
    })

    return () => {
      alive = false
    }
  }, [])

  return tasks
}
