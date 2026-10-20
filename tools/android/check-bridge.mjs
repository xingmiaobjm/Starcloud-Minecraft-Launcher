// 桥覆盖检查：渲染层实际调用哪些方法 vs 安卓原生实现了哪些。
//
// 用途：electron 渲染产物一更新，就能立刻看出哪些请求会落到 "unsupported"，
// 不用等真机上点出错误。也可作为提交前的自检。
//
//   node tools/check-bridge.mjs
//
// 退出码：0 = 核心方法全覆盖；1 = 有核心方法缺失（其余缺失只提示）。

import { readFileSync, existsSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const bundleDir = join(root, 'Starcloud.Launcher.Compose/app/src/main/assets/web/app/assets')
const bridgeFile = join(root, 'Starcloud.Launcher.Compose/app/src/main/java/cn/starcloud/launcher/web/StarcloudBridge.kt')

// 页面能正常打开所需的最低方法集：缺了这些，壳基本不可用
const CORE = [
  'app.info',
  'catalog.installed',
  'catalog.versions',
  'versions.install',
  'install.vanilla',
  'launch.start',
  'settings.get',
  'settings.apply'
]

function fail(msg) {
  console.error(msg)
  process.exit(1)
}

if (!existsSync(bundleDir)) fail(`找不到渲染产物目录：${bundleDir}`)
if (!existsSync(bridgeFile)) fail(`找不到桥实现：${bridgeFile}`)

// 1) 渲染层调了什么
const bundle = (await import('node:fs')).readdirSync(bundleDir).filter((f) => f.endsWith('.js'))
if (bundle.length === 0) fail('渲染产物里没有 .js')

const called = new Set()
for (const f of bundle) {
  const text = readFileSync(join(bundleDir, f), 'utf8')
  for (const m of text.matchAll(/request\("([a-z]+\.[a-zA-Z.]+)"/g)) called.add(m[1])
}

// 2) Kotlin 里接了哪些（方法名在 when 分支里以字符串出现）
const kt = readFileSync(bridgeFile, 'utf8')
const handled = new Set()
for (const m of kt.matchAll(/"([a-z]+\.[a-zA-Z.]+)"/g)) handled.add(m[1])

const covered = [...called].filter((m) => handled.has(m)).sort()
const missing = [...called].filter((m) => !handled.has(m)).sort()
const missingCore = CORE.filter((m) => !handled.has(m))

console.log(`渲染层调用：${called.size} 个`)
console.log(`原生已实现：${covered.length} 个`)
console.log(`未实现：${missing.length} 个`)
if (missing.length > 0) console.log('  ' + missing.join('\n  '))

if (missingCore.length > 0) {
  console.error(`\n核心方法缺失：${missingCore.join(', ')}`)
  process.exit(1)
}
console.log('\n核心方法已全覆盖')
