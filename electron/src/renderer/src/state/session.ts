/// 应用级游戏会话状态（跨页面存活，防双开、保留日志）。
import { useEffect, useRef, useState } from 'react'
import { onBridgeEvent } from '../bridge'

export interface SessionStore {
  pid?: number
  launching: boolean
  status: string
  logs: string[]
  exitCode?: number
}

export interface GameSessionApi extends SessionStore {
  launch: (opts: {
    versionId: string
    playerName: string
    accountUuid: string
  }) => Promise<void>
  clearLogs: () => void
  setVersion: (id: string) => void
  selectedVersion?: string
}

const MAX_LOGS = 600

/** 全局单例式会话状态：所有页面共享同一份，避免切页丢日志/重复启动。 */
export function useGameSession(): GameSessionApi {
  const [store, setStore] = useState<SessionStore>({
    launching: false,
    status: '',
    logs: []
  })
  const [selectedVersion, setVersion] = useState<string | undefined>()
  const startedRef = useRef(false)

  useEffect(() => {
    if (startedRef.current) return
    startedRef.current = true
    onBridgeEvent((event, params) => {
      if (event === 'game.output') {
        const line = String(params.line ?? '')
        setStore((s) => ({ ...s, logs: [...s.logs, line].slice(-MAX_LOGS) }))
      } else if (event === 'game.exited') {
        const code = Number(params.code ?? -1)
        setStore((s) => ({
          ...s,
          pid: undefined,
          launching: false,
          exitCode: code,
          status: `游戏退出（exit=${code}）`
        }))
      }
    })
  }, [])

  return {
    ...store,
    selectedVersion,
    setVersion,
    clearLogs: () => setStore((s) => ({ ...s, logs: [] })),
    launch: async ({ versionId, playerName, accountUuid }) => {
      if (startedRef.current === false) return
      setStore((s) => ({ ...s, launching: true, status: '启动中…', logs: [] }))
      const { request } = await import('../bridge')
      try {
        const session = await request<{ pid: number }>('launch.start', {
          versionId,
          playerName,
          accountUuid
        })
        setStore((s) => ({
          ...s,
          launching: false,
          pid: session.pid,
          exitCode: undefined,
          status: `游戏运行中（pid ${session.pid}）`
        }))
      } catch (e) {
        setStore((s) => ({
          ...s,
          launching: false,
          status: `启动失败：${String(e)}`
        }))
      }
    }
  }
}
