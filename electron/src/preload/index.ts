import { contextBridge, ipcRenderer } from 'electron'

const api = {
  request: (method: string, params: unknown): Promise<unknown> =>
    ipcRenderer.invoke('bridge:request', method, params),
  appVersion: (): Promise<string> => ipcRenderer.invoke('app:version')
}

contextBridge.exposeInMainWorld('bridge', api)
contextBridge.exposeInMainWorld('bridgeEvents', {
  subscribe: (handler: (event: string, params: Record<string, unknown>) => void) => {
    ipcRenderer.on('bridge:event', (_e, event, params) => handler(event, params))
  }
})

export type BridgeApi = typeof api
