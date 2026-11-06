// 把 Electron 的 Web 前端构建产物同步到安卓 Web 壳的 assets。
//
// 为什么要自动同步：安卓 Web 壳跑的是 Electron 的同一份 React 页面，
// 但产物是两份 —— electron/out/renderer 与 android/.../assets/web/app。
// 以前靠手工拷贝，产物与源码会漂移（安卓上看到的还是旧界面，还查不出来）。
//
// 用法：
//   npm run build            （在 electron/ 下构建）
//   gradle :app:syncWebAssets（在本目录，把产物拷进 assets 并改写 index.html 引用）
// 或直接：
//   node tools/sync-web-assets.mjs

import { cpSync, existsSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join, dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(here, '..')

const srcDir = join(repoRoot, 'electron', 'out', 'renderer')
const dstDir = join(
  repoRoot,
  'android/Starcloud.Launcher.Compose/app/src/main/assets/web/app'
)

if (!existsSync(srcDir)) {
  console.error(`[sync-web] 找不到构建产物：${srcDir}`)
  console.error('[sync-web] 先在 electron/ 下执行 npm run build')
  process.exit(1)
}

const assetsSrc = join(srcDir, 'assets')
const assetsDst = join(dstDir, 'assets')

if (!existsSync(assetsSrc)) {
  console.error(`[sync-web] 找不到 assets 目录：${assetsSrc}`)
  process.exit(1)
}

// 整目录替换：先清空再拷，避免旧哈希文件残留（index-<hash>.js 这种）
if (existsSync(assetsDst)) {
  for (const name of readdirSync(assetsDst)) {
    rmSync(join(assetsDst, name), { recursive: true, force: true })
  }
} else {
  console.warn('[sync-web] 目标 assets 目录不存在，新建')
}
cpSync(assetsSrc, assetsDst, { recursive: true })

// 改写 index.html 里的引用（文件名带内容哈希，必须跟着换）
const indexPath = join(dstDir, 'index.html')
if (existsSync(indexPath)) {
  let html = readFileSync(indexPath, 'utf8')
  const files = readdirSync(assetsSrc)
  const js = files.find((f) => f.endsWith('.js') && f.startsWith('index-'))
  const css = files.find((f) => f.endsWith('.css') && f.startsWith('index-'))
  if (js) {
    html = html.replace(/src="\.\/assets\/index-[^"]+\.js"/, `src="./assets/${js}"`)
  }
  if (css) {
    html = html.replace(/href="\.\/assets\/index-[^"]+\.css"/, `href="./assets/${css}"`)
  }
  writeFileSync(indexPath, html)
  console.log(`[sync-web] index.html 已指向 ${js} / ${css}`)
} else {
  console.warn('[sync-web] 找不到 index.html，引用未更新')
}

console.log(`[sync-web] 已同步到 ${assetsDst}`)
