// 一键打包 + 安装包归档
//
// 用法：node scripts/package-release.mjs（或 npm run package）
//   1. 执行 `cargo tauri build`（内部会自动同步版本号 + 构建前端 + 生成 NSIS 安装包）
//   2. 把生成的安装包归档到 releases/，文件名带时间戳，所有版本/所有构建均保留，互不覆盖
//
// 注意：直接使用 `cargo tauri build` 不会归档，请用本脚本或 npm run package。
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

// 1. 打包
console.log("[package] 开始打包（cargo tauri build）...");
const build = spawnSync("cargo", ["tauri", "build"], {
  cwd: join(root, "src-tauri"),
  stdio: "inherit",
  shell: process.platform === "win32",
});
if (build.status !== 0) {
  console.error(`[package] cargo tauri build 失败（退出码 ${build.status}）`);
  process.exit(build.status ?? 1);
}

// 2. 归档所有生成的安装包（带时间戳，保留全部历史）
const nsisDir = join(root, "target", "release", "bundle", "nsis");
const outDir = join(root, "releases");
mkdirSync(outDir, { recursive: true });

const d = new Date();
const pad = (n) => String(n).padStart(2, "0");
const ts = `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(
  d.getHours(),
)}${pad(d.getMinutes())}${pad(d.getSeconds())}`;

let count = 0;
for (const f of readdirSync(nsisDir)) {
  if (!f.endsWith(".exe")) continue;
  const ext = f.slice(f.lastIndexOf("."));
  const base = f.slice(0, f.lastIndexOf("."));
  const dest = join(outDir, `${base}-${ts}${ext}`);
  copyFileSync(join(nsisDir, f), dest);
  console.log(`[package] 已归档: ${base}-${ts}${ext}`);
  count++;
}
console.log(`[package] 完成，共归档 ${count} 个安装包 → ${outDir}`);
