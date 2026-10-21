/// 调度中心数据（Win 端 MainWindow 右侧任务抽屉的等价物）。
/// 任务只保留本次会话（与 Win 端一致：纯内存，不跨启动持久化）。
/// 完成的任务会转成一条历史通知（时间 + 级别 + 消息）。
import { useCallback, useEffect, useRef, useState } from 'react'

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

export interface NotificationItem {
  id: number
  time: string
  level: '信息' | '错误'
  message: string
}

function now(): string {
  const d = new Date()
  const p = (n: number): string => n.toString().padStart(2, '0')
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

export interface TaskCenter {
  tasks: TaskItem[]
  notifications: NotificationItem[]
  clearNotifications: () => void
}

export function useTaskCenter(): TaskCenter {
  const [tasks, setTasks] = useState<TaskItem[]>([])
  const [notifications, setNotifications] = useState<NotificationItem[]>([])
  const seqRef = useRef(1)

  const notify = useCallback((level: '信息' | '错误', message: string): void => {
    setNotifications((prev) =>
      [{ id: seqRef.current++, time: now(), level, message }, ...prev].slice(0, 100)
    )
  }, [])

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
            ? prev.map((t) =>
                t.id === id ? { ...t, status: base.status, progress: base.progress } : t
              )
            : [base, ...prev]
        })
      } else if (event === 'install.done') {
        const id = String(params.versionId ?? params.projectId ?? '')
        upsert(id, { finished: true, status: '已完成', progress: 100 })
        notify('信息', `${id} 安装完成`)
      } else if (event === 'install.failed') {
        const id = String(params.versionId ?? params.projectId ?? '')
        upsert(id, { finished: true, failed: true, status: '失败' })
        notify('错误', `${id} 安装失败：${String(params.error ?? '')}`)
      } else if (event === 'game.exited') {
        const code = Number(params.code ?? -1)
        notify(
          code === 0 ? '信息' : '错误',
          `游戏退出（exit=${code}）`
        )
      }
    })

    return () => {
      alive = false
    }
  }, [notify])

  const clearNotifications = useCallback((): void => setNotifications([]), [])

  return { tasks, notifications, clearNotifications }
}
