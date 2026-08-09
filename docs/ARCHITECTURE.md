# Starcloud Minecraft Launcher — 架构说明

> 版本 0.2.0 | 2026-08-09  
> 本文档描述启动器整体架构：分层、模块职责、关键流程与插件机制。

---

## 1. 总体架构

启动器采用 **Rust 核心库 + Tauri 2 桌面壳 + Preact 前端** 的分层架构：

```
┌─────────────────────────────────────────────────────────┐
│  ui/  Preact 前端（6 页面 + 插件 SDK）                    │
│  · 首页 / 版本管理 / 账户 / 插件 / 设置 / 日志              │
└──────────────────────────┬──────────────────────────────┘
                           │ invoke / event（Tauri IPC）
┌──────────────────────────┴──────────────────────────────┐
│  src-tauri/ Tauri 2 桌面壳                               │
│  · commands.rs  后端命令（版本/安装/启动/账户/插件）       │
│  · plugin_host.rs GUI 插件宿主（furry:// 协议）           │
│  · events.rs    事件负载定义                              │
│  · state.rs     AppState（config / game_pid）            │
└──────────────────────────┬──────────────────────────────┘
                           │ 依赖（无反向依赖）
┌──────────────────────────┴──────────────────────────────┐
│  src/  starcloud-minecraft 核心库（无 Tauri 依赖）         │
│  · core/   服务层：InstallService / LaunchService /       │
│            AccountService（GUI 与 CLI 共用）              │
│  · download/  下载引擎（并发/重试/进度/SHA1）              │
│  · launch/    启动引擎（Java 检测/参数构建/进程管理）       │
│  · version_manager/ 版本安装器/校验/比较/缓存              │
│  · account/   微软设备码登录 / 离线账户                    │
│  · modloader/ Fabric/Forge/Quilt 版本查询                 │
│  · plugin/    核心插件系统（事件钩子）                     │
└─────────────────────────────────────────────────────────┘
```

设计原则：

- **核心库零 Tauri 依赖**：`src/` 可独立作为库或 CLI 使用（`src/main.rs`），GUI 壳只做桥接。
- **服务层隔离**：`core/` 将下载/启动/账户组织为高层异步服务，CLI 与 GUI 复用同一套逻辑。
- **事件驱动 GUI**：长耗时操作（安装/启动）在后台任务执行，通过 `AppHandle::emit` 推送进度/日志/结果，前端订阅事件，避免阻塞与轮询。

## 2. 模块职责

### 2.1 核心库（src/）

| 模块 | 职责 | 关键类型 |
|------|------|----------|
| `core/install.rs` | 版本/ModLoader 安装服务 | `InstallService`：`install_version` / `install_fabric` / `ensure_client_jar` |
| `core/launch.rs` | 启动服务（确保安装→校验→启动→日志→退出判定） | `LaunchService` / `LaunchOutcome` |
| `core/account.rs` | 账户服务（持久化 + 微软登录轮询） | `AccountService` / `MicrosoftLoginPoll` |
| `download/` | 下载引擎：版本清单/详情/库/资源，并发限流、指数退避重试、SHA1 校验、进度节流 | `DownloadEngine` / `ProgressTracker` / `ProgressInfo` |
| `launch/` | 启动参数构建、Java 自动检测 | `LaunchEngine` / `JavaDetector` |
| `version_manager/` | 版本目录安装/删除/校验、版本号比较、缓存 | `VersionInstaller` / `VersionComparator` |
| `account/` | 微软 OAuth 2.0 设备码流程、离线账户 | `MicrosoftAuth` / `OfflineAccount` |
| `modloader/` | 加载器版本查询（meta 接口） | `fabric::get_latest_loader` 等 |
| `plugin/` | 核心插件系统（动态库/脚本插件，事件钩子） | `PluginManager` / `PluginStage` |
| `config/` | 配置加载/保存/迁移 | `LauncherConfig` |

### 2.2 桌面壳（src-tauri/）

| 文件 | 职责 |
|------|------|
| `lib.rs` | 应用入口：注册 `furry://` 协议、命令表、配置加载 |
| `commands.rs` | 全部 `#[tauri::command]`：版本/安装/启动/账户/设置/插件/日志/Java |
| `plugin_host.rs` | GUI 插件宿主：`plugin.json` 扫描、插件包安装/删除/启停、`furry://` 静态资源服务 |
| `events.rs` | 事件名常量与负载结构 |
| `state.rs` | `AppState`：`Mutex<LauncherConfig>` + `Mutex<Option<u32>>`(game_pid) + `Mutex<bool>`(game_running) |

### 2.3 前端（ui/）

| 页面 | 说明 |
|------|------|
| 首页 (Home) | 版本选择、账户选择（正版/离线）、内存设置、启动/停止 |
| 版本管理 (Versions) | 已安装列表（删除 / 安装 Fabric）、远程安装（搜索/筛选） |
| 账户 (Accounts) | 微软设备码登录、离线账户增删、默认账户切换 |
| 插件 (Plugins) | GUI 插件安装（URL/本地 zip）/删除/启停、核心插件列表 |
| 设置 (Settings) | .minecraft 路径、Java、内存、窗口、下载参数 |
| 日志 (Logs) | 游戏实时日志（订阅 game-log 事件）+ 历史日志 |

`ui/src/plugin-sdk.ts` 提供插件前端可复用的 `invokeSafe` / `listenEvent` / `getContext`。

## 3. 关键流程

### 3.1 版本安装流程（InstallService::install_version）

```
版本清单 fetch_version_manifest
  → 定位版本条目
  → 版本详情 fetch_version_details
  → 下载客户端 jar（SHA1 校验 + 原子写入）
  → 下载库文件（并发 + native 解压）
  → 下载资源文件（asset_index → 逐对象下载）
  → 写出资源索引 indexes/{id}.json
  → 写出 version.json
  → 写出 log4j2 日志配置
  → 广播 VersionInstalled 插件事件
```

### 3.2 Fabric 安装流程（InstallService::install_fabric）

Fabric 官方 profile JSON 使用 `inheritsFrom` 继承原版；启动器将其**内联合并**为自包含版本：

1. 查询最新稳定 Fabric Loader，获取 profile JSON
2. 获取父版本（原版）详情
3. `merge_inherited_details`：
   - 库：父 ∪ 子（按 name 去重，子在前）
   - 参数：子无 `arguments`/`minecraftArguments` 时继承父级
   - 客户端 jar / 资源索引 / assets / javaVersion / logging：子缺失时从父级补齐
   - `main_class` 保留子版本（Fabric 的 KnotClient）
4. 走常规安装流程（jar → 库 → 资源 → 索引 → version.json）

产物版本 ID 形如 `fabric-loader-{loader}-{mc}`。

### 3.3 微软设备码登录（GUI）

1. 前端调用 `login_microsoft_device` → 后端获取 `device_code` + `user_code` + `verification_uri`
2. 前端展示授权码与链接，用户前往微软页面授权
3. 前端按 `interval` 秒轮询 `poll_microsoft_login`（单次轮询命令）
4. 后端 `poll_token` → 成功后 `complete_auth_chain`（access_token → Xbox → XSTS → Minecraft → Profile）
5. 账户持久化到 accounts.json，返回 `success` 终止轮询

### 3.4 启动流程（LaunchService::launch）

```
读取版本详情 → 确保已安装（缺失则下载）
  → 账户凭据解析（微软 refresh 续期，否则回退离线）
  → Java 检测（配置路径优先，否则系统检测）
  → 构建 JVM/游戏参数（库 classpath、natives、资源、日志配置替换）
  → 启动子进程 → 转发输出流 → 记录 PID
  → 等待退出 → 崩溃判定（退出码）→ 广播 game-exited
```

## 4. GUI 插件机制（双通道）

- **view（视图插件）**：以 iframe 挂载到侧边栏导航，URL 为 `furry://views/{plugin_id}/{path}`。
- **replacement（替换插件）**：整体接管主窗口 shell（默认 GUI 兜底），URL 为 `furry://replacement/{plugin_id}/{path}`。

目录规范：

```
plugins/{id}/plugin.json        # 清单（必填）
plugins/{id}/frontend/**        # 前端静态资源（furry:// 服务根目录）
plugins/{id}/native/**          # 原生模块（预留，本版本不加载）
plugins/{id}/.disabled          # 禁用标记文件（存在即禁用）
```

`furry://` 协议处理器做**双重路径穿越防护**（`clean_relative` + `safe_join`），MIME 按扩展名映射。

### plugin.json 清单

```json
{
  "id": "com.example.myplugin",
  "name": "我的插件",
  "version": "1.0.0",
  "api_version": 1,
  "min_launcher_version": "0.2.0",
  "gui": {
    "type": "view",
    "title": "插件标题",
    "icon": null,
    "entry": "index.html"
  }
}
```

版本兼容校验：`api_version` 必须等于当前支持版本（1）；`min_launcher_version` 不高于启动器版本（`LAUNCHER_VERSION`）。不兼容插件标记为 `compatible=false` 并在插件页提示原因。

### 插件包安装

插件包为 zip（含根级 `plugin.json`）。来源支持 URL（HttpClient 下载）或本地路径。流程：

1. 下载/读取 zip 字节
2. 解压到 `.tmp-install-{pid}` 临时目录（`enclosed_name()` 拒绝路径穿越条目）
3. 校验 `plugin.json` 合法、`id` 白名单（字母数字/`._-`，拒绝穿越）、`gui.type` ∈ {view, replacement}
4. 移入 `plugins/{id}/`（覆盖旧版本）

插件 ID 白名单校验同样用于删除/启停命令，防止路径注入。

## 5. 配置与数据位置

| 项目 | 位置 |
|------|------|
| 启动器配置 | 系统配置目录 `{config_dir}/starcloud-launcher/config.toml` |
| 游戏目录 | 配置 `dot_minecraft`（默认 `{data_dir}/.minecraft`） |
| 账户 | `{dot_minecraft}/accounts.json` |
| 核心插件 | 配置 `plugins_dir`（可被环境变量 `STARCLOUD_PLUGINS_DIR` 覆盖） |

## 6. 安全设计

- **zip 路径穿越**：安装解压使用 `enclosed_name()`；`furry://` 服务使用组件级过滤。
- **插件 ID 白名单**：拒绝空、`.`/`..`、超过 128 字符、含路径分隔符。
- **下载校验**：客户端 jar 与资源对象均做 SHA1 校验，不匹配则报 `ValidationError` 或重试。
- **配置迁移**：`LauncherConfig::migrate()` 兼容旧版本配置字段。
- **锁粒度**：`AppState` 内 Mutex 不跨 `await` 持锁（快照 clone 后异步）。

## 7. 测试策略

- **单元测试**：核心库各模块（下载校验、版本比较、合并继承、插件宿主安全）。
- **集成测试**：`tests/integration_tests.rs`（wiremock 模拟下载链路，11 个用例）。
- **CI**：`.github/workflows/ci.yml` 执行 fmt/clippy/test。
- 质量门槛：`cargo fmt --check`、`cargo clippy --all-targets`、`cargo test --workspace` 全绿后合入。
