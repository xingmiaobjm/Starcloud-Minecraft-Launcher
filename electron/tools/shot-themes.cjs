// 主题截图取证：分别以亮/暗渲染同一页面并落盘，用于人工比对。
// 用法：electron tools/shot-themes.cjs（输出到 STARCLOUD_SHOT_DIR，默认 /root/dev/shots）
const { app, BrowserWindow, ipcMain, nativeTheme } = require('electron')
const fs = require('fs')
const path = require('path')

const OUT = process.env.STARCLOUD_SHOT_DIR || '/root/dev/shots'

// 稳定的示例数据（不依赖 C# 桥接，保证截图可复现）
const MOCK = {
  'catalog.installed': () => ({
    installed: [
      {
        id: 'neoforge-26.2.0.88',
        sizeBytes: 26843545,
        lastPlayedAt: new Date(Date.now() - 4 * 36e5).toISOString(),
        isDefault: true
      },
      { id: 'fabric-loader-0.16.9-26.3', sizeBytes: 41210000, lastPlayedAt: new Date(Date.now() - 50 * 36e5).toISOString() },
      { id: '26.3-rc-3', sizeBytes: 39690000 },
      { id: '1.21.11', sizeBytes: 38040000, lastPlayedAt: new Date(Date.now() - 120 * 36e5).toISOString() },
      { id: '1.20.4', sizeBytes: 24445539, lastPlayedAt: new Date(Date.now() - 26 * 36e5).toISOString() }
    ]
  }),
  'accounts.list': () => ({
    accounts: [
      { uuid: 'a1', name: 'Notch', type: 'microsoft', hasSession: true },
      { uuid: 'c3', name: 'Steve', type: 'offline', hasSession: true }
    ],
    current: 'c3'
  }),
  'settings.get': () => ({ gameDir: '/root/.minecraft', maxMemoryMb: 6144, downloadSource: 'auto' }),
  'java.list': () => ({ runtimes: [{ versionString: '17.0.20.1', majorVersion: 17 }] }),
  'projections.scan': () => ({
    files: [
      { filePath: '/root/.minecraft/schematics/tower.litematic', fileName: 'tower.litematic', sizeBytes: 564, modifiedAt: new Date().toISOString() },
      { filePath: '/root/.minecraft/versions/1.20.4/schematics/vault.schem', fileName: 'vault.schem', sizeBytes: 184320, modifiedAt: new Date(Date.now() - 864e5).toISOString(), versionId: '1.20.4' }
    ],
    versions: ['1.20.4']
  })
}

async function shoot(win, dark, name) {
  nativeTheme.themeSource = dark ? 'dark' : 'light'
  await win.webContents.executeJavaScript(
    `localStorage.setItem('starcloud.theme', '${dark ? 'dark' : 'light'}'); location.reload()`
  )
  await win.webContents.once('did-finish-load', () => undefined)
  // 等字体 swap + 主题生效后再抓（font-display:block 期间字形是空的）
  await new Promise((r) => setTimeout(r, 4500))
  const info = await win.webContents.executeJavaScript(`
    JSON.stringify({
      theme: document.documentElement.getAttribute('data-theme'),
      fontReady: document.fonts.check('20px "HarmonyOS Icons"'),
      icons: document.querySelectorAll('.nav-icon').length
    })
  `)
  fs.writeFileSync(path.join(OUT, name), (await win.webContents.capturePage()).toPNG())
  console.log(`[shot] ${name} ${info}`)
}

app.whenReady().then(async () => {
  fs.mkdirSync(OUT, { recursive: true })
  const win = new BrowserWindow({
    width: 1360,
    height: 880,
    show: false,
    frame: false,
    webPreferences: {
      preload: path.join(__dirname, '..', 'out/preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  })

  ipcMain.handle('bridge:request', async (_e, method) => {
    const fn = MOCK[method]
    return fn !== undefined ? fn() : {}
  })
  ipcMain.handle('app:version', () => '0.3.4')
  const dark = process.env.PROBE_DARK === '1'
  ipcMain.handle('app:theme', () => ({ dark, source: dark ? 'dark' : 'light' }))
  ipcMain.on('theme:set-background', (_e, d) => {
    win.setBackgroundColor(d ? '#1c1c1e' : '#f3f3f3')
  })
  ipcMain.on('win:action', () => undefined)

  await win.loadFile(path.join(__dirname, '..', 'out/renderer/index.html'))
  await shoot(win, process.env.PROBE_DARK === '1', process.env.SHOT_NAME || 'theme.png')
  app.quit()
})
