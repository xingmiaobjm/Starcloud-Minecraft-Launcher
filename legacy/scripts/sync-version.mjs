// 版本同步脚本：以 src-tauri/Cargo.toml 的 `version` 为唯一来源，
// 打包/构建前把版本号同步到 tauri.conf.json 与 ui/package.json（含 lock）。
//
// 用法：node scripts/sync-version.mjs（由根 package.json 的 build 脚本自动调用）
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

// 1. 读取唯一来源：src-tauri/Cargo.toml 的 version
const cargoToml = readFileSync(join(root, "src-tauri", "Cargo.toml"), "utf8");
const m = cargoToml.match(/^version\s*=\s*"([^"]+)"/m);
if (!m) {
  console.error("[sync-version] 未找到 src-tauri/Cargo.toml 的 version 字段");
  process.exit(1);
}
const version = m[1];
if (!/^\d+\.\d+\.\d+/.test(version)) {
  console.error(`[sync-version] version 格式异常: ${version}`);
  process.exit(1);
}

// 2. tauri.conf.json（打包产物命名/窗口版本标识）
const confPath = join(root, "src-tauri", "tauri.conf.json");
const conf = JSON.parse(readFileSync(confPath, "utf8"));
conf.version = version;
writeFileSync(confPath, JSON.stringify(conf, null, 2) + "\n", "utf8");

// 3. ui/package.json
const pkgPath = join(root, "ui", "package.json");
const pkg = JSON.parse(readFileSync(pkgPath, "utf8"));
pkg.version = version;
writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + "\n", "utf8");

// 4. ui/package-lock.json（顶层与根包两处 version）
const lockPath = join(root, "ui", "package-lock.json");
if (existsSync(lockPath)) {
  const lock = JSON.parse(readFileSync(lockPath, "utf8"));
  lock.version = version;
  if (lock.packages?.[""]) lock.packages[""].version = version;
  writeFileSync(lockPath, JSON.stringify(lock, null, 2) + "\n", "utf8");
}

// 5. 根 Cargo.toml（核心库 version，与应用版本保持一致）
const rootCargoPath = join(root, "Cargo.toml");
const rootCargo = readFileSync(rootCargoPath, "utf8").replace(
  /^(version\s*=\s*")[^"]+(")/m,
  `$1${version}$2`,
);
if (rootCargo !== readFileSync(rootCargoPath, "utf8")) {
  writeFileSync(rootCargoPath, rootCargo, "utf8");
}

console.log(`[sync-version] 版本已同步为 ${version}`);
