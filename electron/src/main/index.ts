import { app, BrowserWindow, ipcMain } from 'electron'
import { spawn, ChildProcessWithoutNullStreams } from 'child_process'
import * as readline from 'readline'
import * as path from 'path'
import * as fs from 'fs'

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void }

let bridgeProc: ChildProcessWithoutNullStreams | null = null
let nextId = 1
const pending = new Map<number, Pending>()
let mainWindow: BrowserWindow | null = null
let eventBuffer: Array<{ event: string; params: Record<string, unknown> }> = []

const LAUNCHER_VERSION = '0.3.4'

function bridgeCandidates(): string[] {
  const env = process.env.STARCLOUD_BRIDGE_DLL
  const candidates: string[] = []
  if (env) candidates.push(env)
  const appPath = app.getAppPath()
  candidates.push(path.join(appPath, '..', 'bridge-linux-x64', 'Starcloud.Bridge.dll'))
  candidates.push(path.join(appPath, 'bridge-linux-x64', 'Starcloud.Bridge.dll'))
  return candidates.map((p) => path.normalize(p))
}

function startBridge(): ChildProcessWithoutNullStreams {
  const dll = bridgeCandidates().find((p) => fs.existsSync(p))
  if (!dll) {
    throw new Error(
      `找不到 C# 桥接宿主（Starcloud.Bridge.dll）。\n` +
        `请先在仓库根执行：dotnet publish dotnet/src/Starcloud.Bridge -c Release -r linux-x64 --self-contained false -o bridge-linux-x64`
    )
  }
  const proc = spawn('dotnet', [dll], {
    env: { ...process.env },
    stdio: ['pipe', 'pipe', 'pipe']
  })
  const rl = readline.createInterface({ input: proc.stdout })
  rl.on('line', (line) => {
    let msg: {
      id?: number
      ok?: boolean
      result?: unknown
      error?: string
      event?: string
      params?: Record<string, unknown>
    }
    try {
      msg = JSON.parse(line)
    } catch {
      return
    }
    if (msg.event !== undefined) {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send('bridge:event', msg.event, msg.params ?? {})
      }
      return
    }
    if (msg.id === undefined) return
    const pendingCall = pending.get(msg.id)
    if (!pendingCall) return
    pending.delete(msg.id)
    if (msg.ok) pendingCall.resolve(msg.result)
    else pendingCall.reject(new Error(msg.error ?? '未知桥接错误'))
  })
  proc.stderr.on('data', (data: Buffer) => {
    console.error('[bridge/stderr]', data.toString().trimEnd())
  })
  proc.on('exit', (code) => {
    console.error(`[bridge] 进程退出（${code}）`)
    bridgeProc = null
    for (const [, p] of pending) p.reject(new Error(`桥接进程退出（${code}）`))
    pending.clear()
  })
  console.log('[bridge] started:', dll)
  return proc
}

function bridgeRequest(method: string, params: unknown = {}): Promise<unknown> {
  if (!bridgeProc) throw new Error('桥接进程未运行')
  const id = nextId++
  const payload = JSON.stringify({ id, method, params })
  return new Promise<unknown>((resolve, reject) => {
    pending.set(id, { resolve, reject })
    try {
      bridgeProc!.stdin.write(payload + '\n')
    } catch (e) {
      pending.delete(id)
      reject(e)
    }
  })
}

function createWindow(): void {
  mainWindow = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1100,
    minHeight: 720,
    backgroundColor: '#1b1b1b',
    title: 'Starcloud Launcher',
    webPreferences: {
      preload: path.join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  })
  const devUrl = process.env.ELECTRON_RENDERER_URL
  if (devUrl) mainWindow.loadURL(devUrl)
  else mainWindow.loadFile(path.join(__dirname, '../renderer/index.html'))
}

app.whenReady().then(() => {
  try {
    bridgeProc = startBridge()
  } catch (e) {
    console.error(String(e))
  }

  ipcMain.handle('bridge:request', (_event, method: string, params: unknown) =>
    bridgeRequest(method, params)
  )
  ipcMain.handle('app:version', () => LAUNCHER_VERSION)

  createWindow()

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('window-all-closed', () => {
  bridgeProc?.kill()
  app.quit()
})
