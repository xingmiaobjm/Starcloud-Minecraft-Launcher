/// preload：把 C# 桥接 + 窗口控制 + 主题通知安全地暴露给渲染层。
/// contextIsolation 保持开启，渲染层拿不到 Node/Electron 原始对象。
import { contextBridge, ipcRenderer } from 'electron'

/** C# 桥接（行式 JSON over stdio）。 */
const bridge = {
  request: (method: string, params: unknown): Promise<unknown> =>
    ipcRenderer.invoke('bridge:request', method, params),
  appVersion: (): Promise<string> => ipcRenderer.invoke('app:version')
}

/** 桥接事件（install.progress / game.output / …）。 */
const bridgeEvents = {
  subscribe: (handler: (event: string, params: Record<string, unknown>) => void): void => {
    ipcRenderer.on('bridge:event', (_e, event, params) => handler(event, params))
  }
}

/** 系统主题（亮/暗）与来源。 */
const theme = {
  get: (): Promise<{ dark: boolean; source: string }> => ipcRenderer.invoke('app:theme'),
  /** nativeTheme 变化时触发（跟随系统切换）。 */
  onChange: (handler: (state: { dark: boolean; source: string }) => void): void => {
    ipcRenderer.on('theme:changed', (_e, state) => handler(state))
  },
  /**
   * 把当前主题的窗口底色同步给主进程（坑点 3）：
   * 缩放 / 最大化时窗口会露出 backgroundColor，不跟着改会闪另一套底色。
   */
  setWindowBackground: (dark: boolean): void =>
    ipcRenderer.send('theme:set-background', dark)
}

/**
 * 窗口控制（自绘标题栏）。
 * `moveBy` 是 Wayland 兜底：`-webkit-app-region: drag` 在 Wayland 下不生效，
 * 标题栏改用 mousedown/mousemove 计算位移后调用它（见 renderer/titlebar.tsx）。
 */
const win = {
  minimize: (): void => ipcRenderer.send('win:action', 'minimize'),
  maximize: (): void => ipcRenderer.send('win:action', 'maximize'),
  close: (): void => ipcRenderer.send('win:action', 'close'),
  moveBy: (dx: number, dy: number): void => ipcRenderer.send('win:moveBy', dx, dy),
  state: (): { maximized: boolean; fullscreen: boolean; display: number } =>
    ipcRenderer.sendSync('win:state')
}

/**
 * 本机能力：渲染层（contextIsolation）拿不到 Node，文件系统与外部打开都得
 * 由主进程代办。三件事都只接受渲染层传来的字符串参数，不回传 Node 对象。
 */
const native = {
  /** 读本地图片并回传 data URL（皮肤库缩略图；渲染层没有 file:// 权限）。 */
  skinIcon: (path: string): Promise<string> => ipcRenderer.invoke('skin:icon', path),
  /** 用系统默认浏览器打开链接（微软设备码的验证站点）。 */
  openExternal: (url: string): Promise<void> => ipcRenderer.invoke('shell:openExternal', url),
  /** 系统文件选择框；用户取消返回 null。 */
  pickFile: (title: string): Promise<string | null> => ipcRenderer.invoke('dialog:pickFile', title)
}

contextBridge.exposeInMainWorld('bridge', bridge)
contextBridge.exposeInMainWorld('bridgeEvents', bridgeEvents)
contextBridge.exposeInMainWorld('theme', theme)
contextBridge.exposeInMainWorld('win', win)
contextBridge.exposeInMainWorld('native', native)

export type BridgeApi = typeof bridge
