/// 应用级游戏会话状态（跨页面存活，防双开、保留日志）。
import { useEffect, useRef, useState } from 'react'
import { onBridgeEvent } from '../bridge'

export interface SessionStore {
  /** 桥接侧会话 id（game.kill 需要它；pid 是系统进程号）。 */
  sessionId?: number
  pid?: number
  launching: boolean
  status: string
  logs: string[]
  exitCode?: number
  /** 启动覆盖层：当前步骤文案（Win 端「当前步骤」）。 */
  stage?: string
  /** 启动覆盖层：登录方式（正版 / 外置 / 离线）。 */
  loginMode?: string
  /** 启动覆盖层进度（0–100，未知时不设）。 */
  progress?: number
  /** 本次启动是否失败（覆盖层据此显示错误而不是转圈）。 */
  launchFailed?: boolean
  /** 正在启动的版本 id（覆盖层标题下方显示）。 */
  launchVersion?: string
}

export interface GameSessionApi extends SessionStore {
  launch: (opts: {
    versionId: string
    playerName: string
    accountUuid: string
    loginMode?: string
  }) => Promise<void>
  clearLogs: () => void
  /** 关闭启动失败覆盖层（不清日志）。 */
  dismissLaunchError: () => void
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
          sessionId: undefined,
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
    dismissLaunchError: () =>
      setStore((s) => ({ ...s, launchFailed: false, status: '', stage: undefined, progress: undefined })),
    launch: async ({ versionId, playerName, accountUuid, loginMode }) => {
      if (startedRef.current === false) return
      const step = (stage: string, progress: number): void =>
        setStore((s) => ({ ...s, stage, progress, launching: true }))

      setStore((s) => ({
        ...s,
        launching: true,
        launchFailed: false,
        logs: [],
        status: '启动中…',
        launchVersion: versionId,
        stage: '检查运行环境…',
        progress: 8,
        loginMode: loginMode ?? ''
      }))
      const { request } = await import('../bridge')
      try {
        step('准备游戏文件…', 35)
        const session = await request<{ sessionId: number; pid: number; logFile?: string }>('launch.start', {
          versionId,
          playerName,
          accountUuid
        })
        step('游戏进程已启动', 100)
        setStore((s) => ({
          ...s,
          launching: false,
          sessionId: session.sessionId,
          pid: session.pid,
          exitCode: undefined,
          launchFailed: false,
          stage: undefined,
          progress: undefined,
          status: `游戏运行中（pid ${session.pid}）`
        }))
      } catch (e) {
        const message = String(e)
        setStore((s) => ({
          ...s,
          launching: false,
          launchFailed: true,
          stage: undefined,
          progress: undefined,
          status: `启动失败：${message}`
        }))
      }
    }
  }
}
