# Furry Minecraft Launcher 生产级优化开发计划

> 版本：1.0 | 2026-08-01
> 项目：furry-minecraft（Rust 核心库 + CLI + Tauri 2 GUI）
> 依据用户需求：代码质量提升、核心功能生产化、内置 GUI、GUI 插件系统、文档更新，分阶段实施并在每阶段末测试与审查。

---

## 1. 摘要

对现有 Rust 启动器核心库进行生产级重构：修复关键缺陷（客户端 jar 缺失、classpath/工作目录错误、native 解压路径不一致、HttpClient 重试未接入下载链路、进度追踪失效、日志缺失等），完善账户认证与插件系统，随后基于 **Tauri 2** 构建跨平台内置 GUI，并实现**双通道 GUI 插件机制**（视图扩展 + 整体替换）。全程同步更新 docs 文档，分 5 个阶段推进，每阶段末执行测试、静态审查与代码评审。

## 2. 现状分析（Phase 1 探索结论）

### 2.1 架构现状

- **纯 Rust 2021**，单包：核心库（`src/lib.rs`）+ CLI 二进制（`src/main.rs`，clap）+ 示例 + criterion bench + 少量集成测试。
- 模块：`download`（DownloadEngine/AssetDownloader/LibraryDownloader/ProgressTracker/HttpClient）、`launch`（LaunchConfig/LaunchEngine/ArgumentsBuilder/JavaDetector）、`version`（Mojang JSON 数据结构）、`version_manager`（VersionComparator/Cache/Installer/Validator）、`config`（LauncherConfig/LaunchProfile/ProfileManager）、`account`（Offline/Microsoft/Mojang）、`modloader`（Forge/Fabric/Quilt 版本查询）、`plugin`（Plugin trait/PluginManager/PluginLoader/PluginInstaller）。
- 基线：`cargo check --all-targets` 通过（仅 1 处 unused 警告）。**git 仓库无任何提交**。

### 2.2 已识别的关键问题

#### P0 致命缺陷（导致无法实际启动游戏）
1. **客户端 jar 从不下载**：`cmd_install` 只下载库和资源，未下载 `details.downloads.client`，classpath 中引用的 `versions/{id}/{id}.jar` 永远不存在 → 启动必然失败。见 [main.rs](file:///d:/Git%20库/furry-minecraft/src/main.rs#L160-L209)。
2. **classpath 组装错误**：[engine.rs](file:///d:/Git%20库/furry-minecraft/src/launch/engine.rs#L146-L151) 将裸版本 ID（如 `1.21`）拼入 classpath，应为客户端 jar 的绝对路径。
3. **工作目录错误**：[engine.rs](file:///d:/Git%20库/furry-minecraft/src/launch/engine.rs#L217-L221) 将 `current_dir` 设为版本目录，应为游戏目录（`config.dot_minecraft`，对应 `game_directory` 参数）。
4. **native 库解压路径与启动参数不一致**：[library.rs](file:///d:/Git%20库/furry-minecraft/src/download/library.rs#L90-L98) 把 native 解压到库目录的父目录，而 `natives_directory` 参数指向 `versions/{id}/natives` → 游戏找不到 native 库。

#### P1 严重问题
5. **HttpClient/RetryConfig 未接入真实下载链路**：库/资源/清单下载、插件远程安装全部使用裸 `reqwest::Client::new()`，重试、超时、连接池优化形同虚设（与 API 文档宣称不符）。
6. **ProgressTracker 损坏**：[progress.rs](file:///d:/Git%20库/furry-minecraft/src/download/progress.rs#L111-L129) 的 `snapshot()` 硬编码 `status=Downloading`、`stage=""`、速度恒 0、ETA 计算错误；下载器未接入任何进度回调。
7. **无日志后端**：全库使用 `log` 宏但无任何初始化，日志被静默丢弃；无文件日志、无滚动。
8. **离线账户手写 MD5**：[offline.rs](file:///d:/Git%20库/furry-minecraft/src/account/offline.rs#L79-L159) 内嵌约 80 行自实现 MD5，应替换为 `md-5` crate。
9. **LauncherConfig 未生效**：CLI/GUI 流程忽略 `download_concurrency`、`download_max_retries`、`java_executable`、`extra_jvm_args` 等配置。
10. **资源下载无校验**：[assets.rs](file:///d:/Git%20库/furry-minecraft/src/download/assets.rs#L77-L117) 不校验 SHA1/大小，`&hash[..2]` 在 hash 过短时 panic。
11. **库下载无重试/超时/URL 回退**：旧版 Minecraft 库（仅有 `lib.url`，无 `downloads`）无法下载；已存在文件不做 SHA1 校验。
12. **Microsoft 认证缺陷**：[microsoft.rs](file:///d:/Git%20库/furry-minecraft/src/account/microsoft.rs) `full_login` 先 sleep 再轮询（浪费首个周期）；用字符串 contains 判断 `authorization_pending`（脆弱）；`xuid` 错误地用 `uhs` 冒充；无 refresh_token 续期方法。
13. **插件系统隐患**：[manager.rs](file:///d:/Git%20库/furry-minecraft/src/plugin/manager.rs) 持有锁期间调用插件回调（死锁风险）；不校验 `min_launcher_version` 与依赖；[loader.rs](file:///d:/Git%20库/furry-minecraft/src/plugin/loader.rs#L74-L77) 用 NopPlugin 占位造成语义混乱。
14. **zip 解压路径穿越**：[engine.rs](file:///d:/Git%20库/furry-minecraft/src/download/engine.rs#L58-L88) 的 `extract_native` 未防 `../` 穿越（安全加固）。

#### P2 改进项
15. `VersionComparator` 每次调用重编译正则（应 `OnceLock` 静态化）；快照判断 `contains('w')` 不可靠。
16. 模块测试覆盖不足：download 各下载器、config 保存/加载、plugin 管理器均无测试。
17. `main.rs` 命令流程与 config 未解耦，GUI 需要可复用的高层「服务层」。

### 2.3 技术决策（已与用户确认）

| 决策点 | 结论 |
|--------|------|
| GUI 框架 | **Tauri 2**（WebView 前端 + Rust 后端），前端 Vite + TypeScript + Preact（轻量、反应式、构建稳定），跨 Win/macOS/Linux（WebView2/WKWebView/webkit2gtk）。 |
| GUI 插件机制 | **双通道**：① 视图扩展（插件注册页面/选项卡，宿主 iframe 挂载）；② 整体替换（插件声明 replacement，接管主窗口 shell）。统一 `plugin.json` 清单 + `api_version` 版本兼容管理。 |
| 工程结构 | 根 Cargo.toml 升级为 workspace（`members = [".", "src-tauri"]`）；前端位于 `ui/`；Tauri 应用位于 `src-tauri/`。核心库不引入 tauri 依赖。 |
| 日志 | `tracing` + `tracing-subscriber` + `tracing-appender`（滚动文件）+ `tracing-log` 桥接现有 `log` 宏。 |
| 账户持久化 | 新增本地账户存储（accounts.json，含 refresh_token），支持离线/微软/（遗留）Mojang。 |

---

## 3. 目标架构

```
┌──────────────────────────────────────────────────────────────┐
│ ui/ (Tauri 前端: Vite+TS+Preact)                              │
│  默认 GUI shell（主题/路由/视图宿主）                           │
│  ├─ 视图宿主: iframe 挂载 furry://views/{plugin_id}/index.html│
│  └─ 替换模式: 加载 replacement 插件 index.html 作为主 shell     │
└──────────────────────────┬───────────────────────────────────┘
                           │ invoke / event
┌──────────────────────────▼───────────────────────────────────┐
│ src-tauri/ (GUI 应用: Rust)                                    │
│  ├─ commands: 版本/安装/启动/账户/设置/插件/日志                │
│  ├─ events: download-progress, launcher-status, game-log…     │
│  ├─ plugin_host: GUI 插件包加载、furry:// 静态资源协议、        │
│  │               api_version/min_launcher_version 校验          │
│  └─ 调用核心库服务层                                            │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│ furry-minecraft 核心库 (workspace 根包，保持无 tauri 依赖)      │
│  download / launch / version_manager / config / account /      │
│  modloader / plugin（加固后）                                   │
└───────────────────────────────────────────────────────────────┘
```

**服务层（新增 `core` 服务模块，CLI 与 GUI 共用）**：`InstallService`、`LaunchService`、`AccountService`、`VersionService`，封装异步流程 + 进度事件 + 插件事件广播，消除 `main.rs` 与 GUI 的逻辑重复。

---

## 4. 分阶段实施计划

> 每阶段完成条件：编译通过 + `cargo fmt --check` + `cargo clippy --all-targets -- -D warnings`（新代码无警告）+ 相关测试通过 + 代码审查 + git 提交。

### 阶段 0：基线建立与工程化（半天量级）

**目标**：建立可追踪的基线，配置质量工具与日志后端。

1. **git 基线**：对当前全部文件做首次提交 `chore: baseline import of furry-minecraft v0.1.0`（避免后续 diff 混乱）。
2. **质量工具**：根目录新增 `rustfmt.toml`（统一风格）、`clippy.toml`（按需）；`.gitignore` 补充 `ui/node_modules`、`ui/dist`、`src-tauri/target`、`*.log`、`launcher_data/` 等。
3. **日志后端**：Cargo.toml 增加 `tracing`、`tracing-subscriber`（features: env-filter, fmt）、`tracing-appender`、`tracing-log`；新建 `src/logging.rs`：`init_logging(log_dir, level)` 组装「stdout fmt + 滚动文件（`launcher.log`，按日滚动）」并桥接 `log` 宏；`main.rs` 入口调用。
4. **workspace 化**：根 Cargo.toml 加 `[workspace] members = [".", "src-tauri"]`（阶段 2 时补 src-tauri 成员，避免空成员报错——可将 workspace 声明放至阶段 2 一并完成，本阶段仅预留说明）。
5. **CI（可选但推荐）**：`.github/workflows/ci.yml`（fmt + clippy + test + build）。
6. **验证**：`cargo test`、`cargo clippy --all-targets` 全绿；运行任一 CLI 命令确认日志文件生成。

### 阶段 1：核心修复与加固（生产化）

**目标**：修复 P0/P1/P2，核心功能达到可部署标准；CLI 全流程可用。

#### 1.1 下载链路修复（download）
- [engine.rs](file:///d:/Git%20库/furry-minecraft/src/download/engine.rs)：`extract_native` 增加路径穿越防护（entry 名经 `Path::components` 校验，拒绝 `..` 与绝对路径）。
- [library.rs](file:///d:/Git%20库/furry-minecraft/src/download/library.rs)：
  - 新增 `natives_dir(impl Into<PathBuf>)` builder 字段，`DownloadEngine::library_downloader()` 调用处由调用方传入 `engine.natives_dir(version_id)`；native 解压目标改为该目录，并在解压前清空旧目录（避免残留）。
  - 下载统一走 `HttpClient`（带重试/超时/连接池）；单个文件失败重试。
  - 已存在文件做 SHA1 校验，不匹配则重下（原子写入保留）。
  - 支持旧版库 URL 回退：`downloads.artifact` 缺失但有 `lib.url` 时，按 `{lib.url}/{lib.name 转路径}` 构造下载地址。
- [assets.rs](file:///d:/Git%20库/furry-minecraft/src/download/assets.rs)：下载后 SHA1 + size 校验；`hash.len() < 2` 防御；统一走 `HttpClient`。
- [manifest.rs](file:///d:/Git%20库/furry-minecraft/src/download/manifest.rs)：清单/详情获取改走 `HttpClient`（带重试）。
- **客户端 jar 下载**：`VersionInstaller::install_client_jar(&VersionDetails)` 下载 `downloads.client` 到 `versions/{id}/{id}.jar`（SHA1 校验 + 原子写入）；`install` 流程与 `launch` 前置检查中调用。
- **进度接入**：`AssetDownloader`/`LibraryDownloader` 增加 `with_tracker(Arc<ProgressTracker>)`；每批完成后更新 total/current 并 `fire_callbacks()`。

#### 1.2 进度追踪修复（progress.rs）
- `snapshot()` 读取真实 `status`（原子化存储）与 `stage`；速度用时间窗（滑动窗口或 EMA）计算；ETA = 剩余字节/速度。
- `set_status`/`set_stage` 改为同步原子操作（去掉不必要的 async）；`add_progress`/`set_progress` 后自动触发回调（节流 ≥100ms）。
- 补单元测试：百分比、速度、ETA、状态一致性、回调触发。

#### 1.3 启动链路修复（launch）
- [engine.rs](file:///d:/Git%20库/furry-minecraft/src/launch/engine.rs)：
  - classpath 末尾改为客户端 jar 绝对路径 `{versions}/{id}/{id}.jar`（并校验存在）。
  - `current_dir` 改为 `config.dot_minecraft`（支持 `LaunchConfig.custom_game_dir` 时用自定义目录）。
  - 删除 `ArgumentsBuilder::build_classpath` 中未使用的裸 jar 名逻辑，统一由 engine 组装（或删函数，避免双实现）。
- 启动前校验升级：jar 存在 + natives 已解压 + 库 SHA1 有效；缺失时自动补下载（`EnsureInstalled` 步骤）。

#### 1.4 账户修复（account）
- [offline.rs](file:///d:/Git%20库/furry-minecraft/src/account/offline.rs)：删除手写 MD5，改用 `md-5` crate（`md5` 依赖改为 `md-5 = "0.10"`）。
- [microsoft.rs](file:///d:/Git%20库/furry-minecraft/src/account/microsoft.rs)：
  - 设备码轮询首轮立即请求（去掉先 sleep）；解析 token 响应的 `error` 字段（`authorization_pending` / `slow_down` / `expired_token` / `authorization_declined`）映射为明确的 `LauncherError::AccountError` 变体。
  - 新增 `refresh_login(&refresh_token)`：微软 OAuth refresh → Xbox/XSTS/MC 全链路续期，返回 `MicrosoftLoginResult`。
  - 修正 `xuid`：XSTS 不返回 xuid，不再用 `uhs` 冒充，置空（在线模式 `auth_xuid` 传空串，注释说明）。
- 新增 `AccountStore`（account 模块）：`accounts.json` 本地持久化（username/uuid/access_token/refresh_token/type/时间戳），支持增删改查；提供 `save_credentials`、`load_accounts`、`delete_account`、`default_account`。

#### 1.5 插件系统加固（plugin）
- [manager.rs](file:///d:/Git%20库/furry-minecraft/src/plugin/manager.rs)：
  - `fire_event` 改为先快照插件列表、释放读锁，再逐个调用回调（消除死锁）。
  - `load_plugin` 加载 + `on_enable` 期间不持有 loader 写锁。
  - 加载时校验 `min_launcher_version`（用 `VersionComparator::compare`，不足则报 `PluginLoadError` 并跳过）；`PluginMeta` 增加 `api_version: u32`（默认 1）。
  - 依赖解析：按 `dependencies` 做拓扑排序，缺失/循环依赖给出明确错误。
- [loader.rs](file:///d:/Git%20库/furry-minecraft/src/plugin/loader.rs)：`DynamicLib` 去掉 NopPlugin 占位，仅持有 `libloading::Library`；卸载顺序保证先 drop 插件实例再卸载动态库（manager 已满足，注释明确该约束）。
- [installer.rs](file:///d:/Git%20库/furry-minecraft/src/plugin/installer.rs)：远程安装改走 `HttpClient`；`uninstall_by_id` 支持精确匹配。

#### 1.6 配置生效与 CLI 完善（config + main）
- `LauncherConfig`：接入下载并发/重试/Java 路径/JVM 参数；`migrate()` 升级到 config_version 2（新增字段默认值）。
- 新增服务层 `src/core/`（`mod.rs`、`install.rs`、`launch.rs`、`account.rs`）：
  - `InstallService::install_version(config, version_id, progress_cb)`：完整安装（清单→详情→jar→库→资源→索引→version.json→插件事件）。
  - `LaunchService::launch(config, version_id, profile, progress_cb)`：确保安装→构建配置→校验→启动→输出流转发→退出码/崩溃判定（复用 `CrashInfo`）。
  - `AccountService`：登录/注销/切换 + 插件事件（`AccountChanged`）。
- [main.rs](file:///d:/Git%20库/furry-minecraft/src/main.rs)：命令全部改调服务层；初始化日志；`install` 增加客户端 jar 步骤；`launch` 增加 `EnsureInstalled`；修正 `cmd_plugins` 文案。新增 `--gui` 子命令（转发到 Tauri 应用，可选）。

#### 1.7 测试补充
- wiremock 模拟 Mojang API：库下载（SHA1 失败重下、native 筛选/解压、URL 回退）、资源下载、清单/详情获取、客户端 jar 下载。
- `ProgressTracker`、`VersionInstaller`（临时目录）、`AccountStore`（临时目录）、`PluginManager`（静态插件 + 版本校验 + 依赖排序）单元/集成测试。
- 保留并修正既有 bench。

**验证**：`cargo test`（全绿）、clippy 零警告、`cargo build --release`；有网环境下 `install 1.20.1 --dir <临时目录>` + `launch --dry-run` 核对 classpath/jar/natives 完整性。

### 阶段 2：GUI 骨架 + 插件协议（Tauri 2）

**目标**：Tauri 应用跑通，默认 GUI 外壳 + 后端命令/事件 + GUI 插件双通道机制落地。

#### 2.1 工程脚手架
- 根 Cargo.toml 声明 `[workspace] members = [".", "src-tauri"]`。
- `src-tauri/`：`Cargo.toml`（依赖 `tauri = "2"`、`tauri-build`、`serde`、`furry-minecraft = { path = ".." }`）、`build.rs`、`tauri.conf.json`（窗口 1200×800 最小 960×640，`beforeDevCommand: npm --prefix ui run dev`，`frontendDist: ../ui/dist`，`identifier: com.furry.launcher`）、`capabilities/default.json`（`core:default` 权限）、图标占位。
- `ui/`：`package.json`（preact、@tauri-apps/api、vite、@preact/preset-vite、typescript）、`vite.config.ts`（port 1420，strictPort）、`index.html`、`src/main.tsx`、`src/app.tsx`（shell：侧边导航 + 页面容器 + 视图宿主 iframe 区）。

#### 2.2 后端命令与事件（src-tauri/src/commands.rs、events.rs）
- 命令（`tauri::command`）：`list_versions`、`list_installed`、`install_version`、`remove_version`、`launch_game`、`stop_game`、`list_accounts`、`add_offline_account`、`login_microsoft_device`（返回 device code）、`poll_microsoft_login`、`get_settings`/`set_settings`、`list_plugins`、`install_plugin`/`remove_plugin`、`get_gui_plugins`、`get_game_logs`、`check_java`。
- 事件（`AppHandle::emit`）：`launcher-status`、`download-progress`、`install-done`、`game-log`、`game-exited`、`error-occurred`。
- 后台任务：安装/启动用 `tokio::spawn` + `app.handle()` 发事件，前端免轮询。

#### 2.3 GUI 插件协议（src-tauri/src/plugin_host.rs + ui/src/plugin-host.ts）
- **清单 `plugin.json`**（GUI 插件唯一入口）：
  ```json
  {
    "id": "com.example.views",
    "name": "示例视图",
    "version": "1.0.0",
    "api_version": 1,
    "min_launcher_version": "0.2.0",
    "gui": {
      "type": "view",              // "view" | "replacement"
      "title": "示例",
      "icon": "assets/icon.svg",
      "entry": "index.html",       // 相对 frontend/ 的入口
      "native_module": null        // 可选: native/xxx.dll 路径（需导出 _gui_plugin_create）
    }
  }
  ```
- **目录规范**：`plugins/{id}/plugin.json`、`plugins/{id}/frontend/**`（构建产物）、`plugins/{id}/native/**`（可选原生模块）。
- **加载流程**：扫描 plugins 目录 → 解析/校验 `api_version == 1` 且 `min_launcher_version ≤ 启动器版本` → 读取条目 → （可选）libloading 加载原生模块并调用 `GuiPlugin` trait 注册命令。
- **`GuiPlugin` trait（GUI crate 内定义，不污染核心库）**：
  ```rust
  pub trait GuiPlugin: Send + Sync {
      fn meta(&self) -> GuiPluginMeta;
      fn register_commands(&self, app: &tauri::AppHandle) -> Result<()>;
      fn on_event(&self, stage: PluginStage, ctx: &mut PluginContext) -> Result<()>;
  }
  ```
- **`furry://` 静态资源协议**：`Builder::register_uri_scheme_protocol("furry", …)`，`furry://views/{plugin_id}/{path}` → `plugins/{id}/frontend/{path}`（mime 映射，目录穿越防护）。
- **前端宿主**：
  - 视图模式：shell 向 `get_gui_plugins` 请求视图列表，渲染为导航选项卡；点击后 iframe `src = furry://views/{id}/index.html`；iframe 内经 `window.__TAURI__.core.invoke/event` 与后端通信。
  - 替换模式：启动时检测 replacement 插件，存在则顶层 iframe 加载 `furry://replacement/{id}/index.html` 作为主 shell；默认 GUI 作为内置 fallback。
  - `ui/src/plugin-sdk.ts`：导出 `invokeSafe`、`listenEvent`、`getContext`（封装 @tauri-apps/api，供插件前端使用）。

#### 2.4 默认 GUI 外壳（美观方向）
- 深色主题 + 品牌绿强调色（MC 风格），卡片式布局、平滑过渡、响应式（≥960px）；中文界面（`config.language` 预留 i18n 结构）。
- 页面骨架：首页（版本选择 + 启动按钮 + 状态/进度条）、版本管理、账户、设置、插件、日志。
- 本阶段先完成外壳 + 首页（状态显示）+ 日志页（订阅 game-log/日志文件）。

**验证**：`npm run build` 通过；`cargo tauri dev` 启动窗口；制作一个最小 view 示例插件（`examples/gui-plugin-demo/` 提供 plugin.json + 简单 index.html + 构建脚本），验证 iframe 挂载与 invoke 往返。

### 阶段 3：GUI 功能完善 + 跨平台适配

**目标**：GUI 覆盖全部核心功能，达可用标准。

1. **版本管理页**：远程版本列表（筛选正式/快照）、安装（进度条 + 阶段文案，订阅 download-progress）、已安装列表（删除/大小/类型）、ModLoader 入口（Fabric/Forge/Quilt 安装引导，复用 modloader 模块 + 服务层）。
2. **首页（启动）**：版本下拉 + 账户选择 + 内存/窗口设置 + 启动/停止；状态机（idle/downloading/launching/running/error）UI；game-log 实时滚动；游戏退出/崩溃提示（GameCrashed 弹窗）。
3. **账户页**：离线账户创建/删除；微软设备码登录（展示 code + 验证 URI + 复制按钮 + 轮询状态）；已存账户列表（默认账户切换，调用 AccountStore）。
4. **设置页**：.minecraft 路径、内存、窗口、Java 路径（自动检测展示 + 手动指定）、下载并发/重试、JVM 参数、主题；保存到 LauncherConfig。
5. **插件页**：已安装插件列表（类型 view/replacement 标记）、安装（本地选择/URL）、删除、启用/禁用；replacement 切换提示。
6. **跨平台适配**：路径统一走 `dirs`；macOS 菜单栏/窗口按钮、Linux WebKitGTK 依赖说明（README 标注 `webkit2gtk-4.1` 等系统依赖）；`cfg(target_os)` 差异化仅限窗口行为与路径。
7. **打包**：`tauri.conf.json` bundle 配置（nsis/msi Windows、dmg macOS、deb/AppImage Linux）；`cargo tauri build` 产出安装包（本机仅验证 Windows）。

**验证**：GUI 全流程走查（安装→启动 dry-run→账户→设置→插件）；`cargo tauri build` 成功产出 Windows 安装包；clippy/test 全绿。

### 阶段 4：文档更新 + 发布回归

**目标**：文档与实现一致，整体回归。

1. **docs/ARCHITECTURE.md（新增）**：分层架构图、事件流（安装/启动/账户/插件）、线程模型、GUI 架构、插件协议说明。
2. **docs/API.md（更新）**：新增服务层 API、GUI 命令/事件表、`AccountStore`、`ProgressTracker` 行为修正、`PluginMeta.api_version`、Microsoft `refresh_login`；标注废弃项。
3. **docs/PLUGIN_DEV_GUIDE.md（新增）**：GUI 插件开发指南（plugin.json 规范、view/replacement 两种模式完整示例、原生模块 GuiPlugin trait 示例、前端 plugin-sdk 用法、版本兼容与 api_version 说明）。
4. **docs/USER_MANUAL.md（新增）**：GUI 使用手册（安装/启动/账户/设置/插件/常见问题）。
5. **docs/PERFORMANCE_REPORT.md（更新）**：按新实现修正指标（重试接入后成功率、进度/日志开销）。
6. **发布回归**：`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、`cargo test --workspace`、`cargo build --release`、`cargo tauri build`（Windows）；最终代码审查（对照需求逐项核验）；打 tag `v0.2.0`。

---

## 5. 关键假设与决策记录

1. GUI 框架 **Tauri 2**（用户已确认）；前端 **Preact + Vite + TS**（轻量稳定，避免重型框架构建风险）。
2. GUI 插件机制 **双通道**（用户已确认）：view 用 iframe 隔离挂载；replacement 整体接管；统一 `plugin.json` + `api_version`。
3. 核心库保持**无 tauri 依赖**；`GuiPlugin` trait 与插件宿主逻辑放在 GUI crate。
4. 现有 `Plugin`（cdylib 核心插件）机制保留并加固，与 GUI 插件包解耦；GUI 插件的原生模块为可选扩展。
5. 微软正版登录采用设备码流；refresh_token 持久化于 `accounts.json`（明文本地文件，文档标注安全建议；不做加密以免引入平台密钥管理复杂度——如需加密可后续加 `keyring`）。
6. macOS/Linux 仅做代码级适配与文档说明，本机（Windows）无法实测，最终验证以 Windows 为准，其余平台给出 CI/构建指引。
7. 安装/启动需要网络与真实 Java 的端到端验证在有网环境进行；无网时以 wiremock 模拟 + dry-run 兜底。
8. 阶段 2 才引入 workspace 成员 `src-tauri`，阶段 0 不添加空成员避免编译告警。

## 6. 交付物清单

| 交付物 | 说明 |
|--------|------|
| 核心库 v0.2.0 | P0/P1 全部修复 + 服务层 + 测试 + 日志 |
| CLI 完整可用 | install/launch/list/remove/versions/account/config/plugins/check/gui |
| Tauri GUI 应用 | 默认 GUI（6 大页面）+ 命令/事件 + 打包配置 |
| GUI 插件系统 | plugin.json 规范 + 双通道宿主 + furry:// 协议 + 示例插件 |
| 文档 4+1 份 | ARCHITECTURE / API / PLUGIN_DEV_GUIDE / USER_MANUAL / PERFORMANCE_REPORT |

## 7. 验证矩阵（每阶段对照）

| 阶段 | 验证项 |
|------|--------|
| 0 | cargo test / clippy / fmt 全绿；日志文件生成 |
| 1 | 全部单元+集成测试；wiremock 下载测试；有网 dry-run 校验 classpath/jar/natives |
| 2 | tauri dev 窗口启动；示例 view 插件挂载 + invoke 往返 |
| 3 | GUI 全流程走查；Windows 打包产出 |
| 4 | workspace 全量回归；文档一致性核验；tag v0.2.0 |
