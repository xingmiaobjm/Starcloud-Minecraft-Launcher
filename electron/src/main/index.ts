/// 主进程窗口配置（与渲染层 UI 完全分离）。
///
/// ── Linux 平台坑点与规避 ────────────────────────────────────────────────
/// 1. **透明窗口圆角被 WM 覆盖**：X11 下窗口形状由 WM 决定，`transparent:true`
///    只影响窗口内部合成，WM 仍会画自己的装饰/背景。规避：窗口本身不透明，
///    圆角交给渲染层 CSS（`--win-radius` + `overflow:hidden`），
///    并把背景色设成与界面一致，避免边缘出现异色缝。
/// 2. **透明窗口首帧闪烁**：Electron 透明窗口在 Wayland/X11 都会先闪一帧
///    未合成的黑底。规避：`show:false` + `ready-to-show` 再显示，
///    同时 body 背景与 `backgroundColor` 一致。
/// 3. **Wayland 下 `-webkit-app-region: drag` 失效**：Electron 的
///    app-region 拖拽依赖 WM 的 _NET_WM_MOVERESIZE，在 Wayland 下不生效。
///    规避：优先用 app-region；渲染层另有一套 JS 兜底（见 titlebar.tsx，
///    Wayland 检测命中时用 IPC 让主进程 setPosition 手动移动窗口）。
/// 4. **backdrop-filter 开销**：大��围模糊在软件渲染（WSLg/无 GPU）下会掉帧。
///    规避：材质模糊只铺在窗口装饰层（标题栏 + 侧边轨道），
///    内容区用纯色分层；并提供 `--no-blur` 降级开关（见 renderer 的 blur 开关）。
/// 5. **root 用户 / 容器无沙箱**：Chromium 沙箱不可用，需显式 `--no-sandbox`
///    （由 ELECTRON_DISABLE_SANDBOX=1 环境变量控制）。
import { app, BrowserWindow, dialog, ipcMain, nativeTheme, screen, shell } from 'electron'
import { readFileSync, existsSync, statSync, writeFileSync } from 'node:fs'
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import * as readline from 'node:readline'
import * as path from 'node:path'

const LAUNCHER_VERSION = '0.3.4'
const isDev = !app.isPackaged

// ── C# 桥接进程 ───────────────────────────────────────────────────────────

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void }

let bridgeProc: ChildProcessWithoutNullStreams | null = null
let nextId = 1
const pending = new Map<number, Pending>()
let mainWindow: BrowserWindow | null = null
/** 桥接自动重启次数（用于诊断持续崩溃）。 */
let restartCount = 0
/** 应用正在退出，桥接退出属于预期，不再重启。 */
let shuttingDown = false

function bridgeCandidates(): string[] {
  const env = process.env.STARCLOUD_BRIDGE_DLL
  const candidates: string[] = []
  if (env) candidates.push(env)
  const appPath = app.getAppPath()
  candidates.push(path.join(appPath, '..', 'bridge-linux-x64', 'Starcloud.Bridge.dll'))
  candidates.push(path.join(appPath, 'bridge-linux-x64', 'Starcloud.Bridge.dll'))
  // 打包态：extraResources 放到 resources/bridge-linux-x64
  candidates.push(path.join(process.resourcesPath ?? '', 'bridge-linux-x64', 'Starcloud.Bridge.dll'))
  return candidates.map((p) => path.normalize(p))
}

function startBridge(): ChildProcessWithoutNullStreams {
  const dll = bridgeCandidates().find((p) => existsSync(p))
  if (!dll) {
    throw new Error(
      '找不到 C# 桥接宿主（Starcloud.Bridge.dll）。\n' +
        '开发：dotnet publish dotnet/src/Starcloud.Bridge -c Release -r linux-x64 ' +
        '--self-contained false -o bridge-linux-x64\n' +
        '或用 STARCLOUD_BRIDGE_DLL 指定路径。'
    )
  }
  const proc = spawn('dotnet', [dll], { stdio: ['pipe', 'pipe', 'pipe'] })
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
      mainWindow?.webContents.send('bridge:event', msg.event, msg.params ?? {})
      return
    }
    if (msg.id === undefined) return
    const call = pending.get(msg.id)
    if (!call) return
    pending.delete(msg.id)
    if (msg.ok) call.resolve(msg.result)
    else call.reject(new Error(msg.error ?? '未知桥接错误'))
  })
  proc.stderr.on('data', (d: Buffer) => console.error('[bridge/stderr]', d.toString().trimEnd()))
  proc.on('exit', (code) => {
    console.error(`[bridge] 进程退出（${code}）`)
    bridgeProc = null
    for (const [, p] of pending) p.reject(new Error(`桥接进程退出（${code}）`))
    pending.clear()
  })
  console.log('[bridge] started:', dll)
  return proc
}

function bridgeRequest(method: string, params: unknown): Promise<unknown> {
  // 桥接意外退出后自动拉起：否则一次崩溃会让界面此后所有请求都失败
  // （表现是「用一会儿之后什么都加载不出来」）。
  if (!bridgeProc && !shuttingDown) {
    bridgeProc = startBridge()
    restartCount += 1
    if (restartCount > 5) {
      console.error('[bridge] 重启过于频繁，可能存在持续崩溃')
    }
  }
  const id = nextId++
  const payload = JSON.stringify({ id, method, params })
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    try {
      bridgeProc!.stdin.write(payload + '\n')
    } catch (e) {
      pending.delete(id)
      reject(e as Error)
    }
  })
}

// ── 窗口骨架 ──────────────────────────────────────────────────────────────

function createWindow(): BrowserWindow {
  // 让内容区背景与窗口背景色一致：透明窗口在部分 WM 下会漏出黑边
  const background = nativeTheme.shouldUseDarkColors ? '#1c1c1e' : '#f3f3f3'

  const win = new BrowserWindow({
    width: 1360,
    height: 880,
    minWidth: 1000,
    minHeight: 640,
    show: false, // 防首帧闪烁（坑点 2）
    backgroundColor: background, // 不用 transparent：坑点 1（圆角/边缘异色）
    autoHideMenuBar: true,
    title: 'Starcloud Launcher',
    icon: path.join(__dirname, '../../build/icon.png'),
    // frame:false → 自绘标题栏（约束 2/3）；titleBarStyle 在 Linux 上无意义
    frame: false,
    webPreferences: {
      preload: path.join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false
    }
  })

  const devUrl = process.env.ELECTRON_RENDERER_URL
  if (devUrl) win.loadURL(devUrl)
  else win.loadFile(path.join(__dirname, '../renderer/index.html'))

  // 坑点 2：内容渲染完再显示，避免透明/未布局时闪一帧
  win.once('ready-to-show', () => win.show())

  // 截图取证用（本地验收，非产品功能）：渲染完成后抓一帧存盘就退出。
  // WSLg 下 GPU 进程可能崩溃、ready-to-show 不触发，所以用 did-finish-load 兜底。
  if (process.env.STARCLOUD_SCREENSHOT) {
    let done = false
    // 渲染层报错时打出来：白屏排查靠它（rootChildren=0 就是挂载失败）
    win.webContents.on('console-message', (_e, level, message, line, sourceId) => {
      console.log(`[renderer:${level}] ${message} (${sourceId}:${line})`)
    })
    win.webContents.on('render-process-gone', (_e, details) => {
      console.error('[renderer] gone', JSON.stringify(details))
    })
    const shoot = async (): Promise<void> => {
      if (done) return
      done = true
      const target = process.env.STARCLOUD_SCREENSHOT
      if (target === undefined) {
        app.quit()
        return
      }
      try {
        await new Promise((r) => setTimeout(r, 2500))
        // 指定页面：按导航项文案点一下再截（逐页取证用）
        const pageLabel = process.env.STARCLOUD_SCREENSHOT_PAGE
        if (pageLabel !== undefined && pageLabel !== '') {
          await win.webContents.executeJavaScript(`
            (() => {
              const btn = [...document.querySelectorAll('.nav-item')]
                .find((b) => (b.getAttribute('data-key') ?? b.getAttribute('data-label')) === ${JSON.stringify(pageLabel)})
              if (btn) btn.click()
              return btn !== undefined
            })()
          `)
          await new Promise((r) => setTimeout(r, 2200))
        }
        // 取证用：按 title 点一个按钮再截图（验证收起态这类交互）
        const clickTitle = process.env.STARCLOUD_SCREENSHOT_CLICK
        if (clickTitle !== undefined && clickTitle !== '') {
          await win.webContents.executeJavaScript(`
            (() => {
              const el = [...document.querySelectorAll('[title]')]
                .find((n) => n.getAttribute('title') === ${JSON.stringify(clickTitle)})
              if (el) el.click()
              return el !== undefined
            })()
          `)
          await new Promise((r) => setTimeout(r, 1200))
        }
        // 取证用：按可见文本点第一个匹配的按钮（列表行没有 title）
        const clickText = process.env.STARCLOUD_SCREENSHOT_CLICK_TEXT
        if (clickText !== undefined && clickText !== '') {
          await win.webContents.executeJavaScript(`
            (() => {
              const el = [...document.querySelectorAll('button, .row')]
                .find((n) => (n.textContent ?? '').includes(${JSON.stringify(clickText)}))
              if (el) el.click()
              return el !== undefined
            })()
          `)
          // 投影解析 + 资源抽取（读客户端 jar）可能要十几秒
          await new Promise((r) => setTimeout(r, 22000))
        }
        const diag = await win.webContents.executeJavaScript(`
          JSON.stringify({
            htmlLen: document.documentElement.outerHTML.length,
            rootChildren: document.getElementById('root')?.childElementCount ?? -1,
            bodyBg: getComputedStyle(document.body).backgroundColor,
            text: (document.getElementById('root')?.innerText ?? '').slice(0, 160),
            rows: document.querySelectorAll('.row').length,
            navIcons: document.querySelectorAll('.nav-icon').length,
            rects: (() => {
              const pick = (sel) => {
                const el = document.querySelector(sel)
                if (!el) return null
                const r = el.getBoundingClientRect()
                return { left: Math.round(r.left), right: Math.round(r.right), w: Math.round(r.width) }
              }
              return {
                content: pick('.content'),
                page: pick('.page'),
                head: pick('.page-head'),
                body: pick('.page-body'),
                card: pick('.card')
              }
            })()
          })
        `)
        console.log('[diag] ' + diag)
        const img = await win.webContents.capturePage()
        writeFileSync(target, img.toPNG())
        console.log('[screenshot] saved ' + target)
      } catch (e) {
        console.error('[screenshot] failed', e)
      }
      app.quit()
    }
    win.webContents.once('did-finish-load', () => void shoot())
  }

  // 主题跟随系统（约束 2）：nativeTheme 会随系统亮暗切换
  nativeTheme.on('updated', () => {
    win.webContents.send('theme:changed', {
      dark: nativeTheme.shouldUseDarkColors,
      source: nativeTheme.themeSource
    })
  })

  return win
}

// ── IPC ───────────────────────────────────────────────────────────────────

/**
 * 皮肤缩略图大小上限。
 * 皮肤纹理最大 4MB（内核 ImportBytes 的限制），HD 图也有 512×256；
 * 列表里只画 64px 方块，超过 4MB 的直接拒绝读，避免把大图塞进 renderer 内存。
 */
const SKIN_ICON_MAX_BYTES = 4 * 1024 * 1024

const IMAGE_MIME: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.webp': 'image/webp'
}

function registerIpc(): void {
  ipcMain.handle('bridge:request', (_e, method: string, params: unknown) =>
    bridgeRequest(method, params)
  )
  ipcMain.handle('app:version', () => LAUNCHER_VERSION)

  // ── 渲染层没有文件系统：本地图片读成 data URL 回传 ──
  // 不放开 file:// CSP（那等于把整个磁盘挂到页面上），只在受控的 IPC 上做。
  ipcMain.handle('skin:icon', (_e, filePath: unknown) => {
    if (typeof filePath !== 'string' || filePath.trim() === '') {
      throw new Error('皮肤路径为空')
    }
    const full = path.resolve(filePath)
    if (!existsSync(full) || !statSync(full).isFile()) {
      throw new Error(`皮肤文件不存在：${full}`)
    }
    const size = statSync(full).size
    if (size > SKIN_ICON_MAX_BYTES) {
      throw new Error(`皮肤文件过大（${Math.round(size / 1024)} KB），无法生成缩略图`)
    }
    const mime = IMAGE_MIME[path.extname(full).toLowerCase()]
    if (mime === undefined) {
      throw new Error(`不支持的图片格式：${path.extname(full) || '（无扩展名）'}`)
    }
    return `data:${mime};base64,${readFileSync(full).toString('base64')}`
  })

  // 设备码登录要跳系统浏览器打开验证站点。
  // 只放行 http/https：file:// 及其它 scheme 不给渲染层用，避免变成任意本地文件读取器。
  ipcMain.handle('shell:openExternal', async (_e, url: unknown) => {
    if (typeof url !== 'string' || url === '') throw new Error('链接为空')
    let parsed: URL
    try {
      parsed = new URL(url)
    } catch {
      throw new Error(`不是有效的链接：${url}`)
    }
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      throw new Error('只允许打开 http / https 链接')
    }
    await shell.openExternal(parsed.toString())
  })

  // 导入本地皮肤：系统文件选择框（比让用户手敲绝对路径可靠）
  ipcMain.handle('dialog:pickFile', async (_e, title: unknown) => {
    const win = mainWindow
    const options: Electron.OpenDialogOptions = {
      title: typeof title === 'string' && title !== '' ? title : '选择文件',
      properties: ['openFile'],
      filters: [{ name: 'PNG 图片', extensions: ['png'] }]
    }
    const result =
      win !== null
        ? await dialog.showOpenDialog(win, options)
        : await dialog.showOpenDialog(options)
    if (result.canceled || result.filePaths.length === 0) return null
    return result.filePaths[0]
  })

  // 渲染层定好主题后回传窗口底色，避免缩放时露出另一套主题的底
  ipcMain.on('theme:set-background', (_e, dark: boolean) => {
    mainWindow?.setBackgroundColor(dark ? '#1c1c1e' : '#f3f3f3')
  })
  ipcMain.handle('app:theme', () => ({
    dark: nativeTheme.shouldUseDarkColors,
    source: nativeTheme.themeSource
  }))

  // 标题栏按钮（坑点 3 的 Wayland 兜底也走这里）
  ipcMain.on('win:action', (_e, action: string) => {
    const win = mainWindow
    if (!win) return
    switch (action) {
      case 'minimize':
        win.minimize()
        break
      case 'maximize':
        if (win.isMaximized()) win.unmaximize()
        else win.maximize()
        break
      case 'close':
        win.close()
        break
    }
  })

  // Wayland 兜底：app-region 拖拽失效时，渲染层用鼠标位移驱动这里手动移动
  ipcMain.on('win:moveBy', (_e, dx: number, dy: number) => {
    const win = mainWindow
    if (!win || win.isMaximized()) return
    const [x, y] = win.getPosition()
    win.setPosition(Math.round(x + dx), Math.round(y + dy))
  })

  // 窗口状态（标题栏最大化/还原图标需要）
  ipcMain.on('win:state', (e) => {
    const win = mainWindow
    e.returnValue = {
      maximized: win?.isMaximized() ?? false,
      fullscreen: win?.isFullScreen() ?? false,
      display: win ? screen.getDisplayNearestPoint(win.getBounds()).id : 0
    }
  })
}

// ── 启动 ──────────────────────────────────────────────────────────────────

if (process.env.ELECTRON_DISABLE_SANDBOX === '1') {
  // 坑点 5：root / 容器 / WSL 下 Chromium 沙箱不可用
  app.commandLine.appendSwitch('no-sandbox')
  app.commandLine.appendSwitch('disable-gpu-sandbox')
  app.commandLine.appendSwitch('disable-dev-shm-usage')
}

app.whenReady().then(() => {
  // 主题跟随系统（约束 2）：显式置 system，避免 GTK/portal 推断差异
  nativeTheme.themeSource = 'system'
  try {
    bridgeProc = startBridge()
  } catch (e) {
    console.error(String(e))
  }

  registerIpc()
  mainWindow = createWindow()

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) mainWindow = createWindow()
  })
})

app.on('window-all-closed', () => {
  shuttingDown = true
  bridgeProc?.kill()
  app.quit()
})
