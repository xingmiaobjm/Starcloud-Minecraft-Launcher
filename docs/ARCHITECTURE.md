# ARCHITECTURE.md — Starcloud 架构（.NET / WinUI 3）

> 2026-09-06 起项目全面转向 .NET：核心逻辑以纯 C# 重写（`dotnet/`），
> 界面使用 WinUI 3（Windows App SDK 1.8）。旧 Tauri/Rust + Preact 实现保留在
> `legacy/` 目录仅作参考，不再维护。

## 1. 总览

```
┌─────────────────────────────────────────────┐
│  Starcloud.App（WinUI 3 前台，net8.0-windows） │
│  MainWindow + Views/*（9 页面 + 子视图）      │
│  AppServices 单例容器                         │
└──────────────────┬──────────────────────────┘
                   │ 直接方法调用（无 IPC）
┌──────────────────▼──────────────────────────┐
│  Starcloud.Core（纯 C# 类库，net8.0）         │
│  清单 / 下载 / 安装 / 启动 / 账户 / Java / Fabric │
└─────────────────────────────────────────────┘
```

- **无进程间通信**：UI 与核心逻辑在同一进程，通过 C# 接口（`IProgress<T>`、
  `async/await`、`CancellationToken`）交互——这是相对旧 Tauri 架构最大的简化。
- **解决方案**：`dotnet/Starcloud.slnx`（.NET 8 SDK 生成的 XML 解决方案格式）。

## 2. 项目结构

```
dotnet/
├── Starcloud.slnx
└── src/
    ├── Starcloud.Core/               # 纯 C# 类库（无 UI 依赖，跨平台版共用）
    │   ├── Models/
    │   │   └── Minecraft.cs          # Mojang JSON 数据模型（清单/版本/资产/参数）
    │   ├── Plugins/                  # 插件宿主：清单/权限校验 + JS 沙箱与 Python 子进程运行时
    │   └── Services/                 # 48 个服务，按域分组
    │       ├── 路径/平台：LauncherPaths、InstanceSettings（版本隔离布局 + 生效启动配置）、
    │       │             Platform（OS 能力抽象，跨平台分支的单一入口）
    │       ├── 下载/清单：DownloadService、ChunkedFileDownloader、ManifestService、
    │       │             DownloadSource、LoaderBuildCache、ResourceCacheService
    │       ├── 安装：InstallerService、ForgeInstallerService、MrPackInstallerService、
    │       │         ModPackExportService、VersionBackupService
    │       ├── 启动：GameLauncher、LaunchAccountService、LaunchPreflightService、
    │       │         GameSession、GameSessionGuard
    │       ├── 账户：AccountService（含 AccountStore / MicrosoftAuthService）、
    │       │         YggdrasilService、StarcloudIdService
    │       ├── Java/ModLoader：JavaService、FabricService、SystemMemory、MemoryAdvisor
    │       ├── 内容管理：ModrinthService、ContentLibraryService、ModConflictService、
    │       │             SaveService、SaveLibraryService、PendingWorldStore、IconCacheService
    │       ├── 诊断：EnvironmentCheckService、GameDiagnosticsService、CrashAnalysisService、
    │       │         CrashLogTrimmer、LauncherLog
    │       ├── 任务：TaskCenterService（全局任务总线：下载/启动/Java 搜索统一登记）
    │       └── Nova：NovaService、NovaTools、NovaAgentLoop、OpenAiSseClient、
    │                 NovaPermissions、NovaMoments、NovaInviteService、NovaFpsWatcher
    └── Starcloud.App/                # WinUI 3 前台（WindowsPackageType=None）
        ├── App.xaml / App.xaml.cs
        ├── MainWindow.xaml(.cs)      # 亚克力 + 自定义背景层 + NavigationView 壳 + 调度中心抽屉
        ├── Assets/                   # 内置壁纸 / 3D 皮肤模型 / authlib-injector / 插件 SDK
        ├── Interop/                  # SwapChainPanel 原生互操作（3D 皮肤视图）
        ├── Services/
        │   ├── AppServices.cs        # 服务单例容器（对应旧 Tauri AppState）
        │   ├── AppearanceService.cs  # 主题应用 + 内置壁纸清单 + 毛玻璃遮罩解析
        │   ├── MotionService.cs      # 界面动效总开关（跟随系统 / 始终开启 / 关闭）
        │   ├── PluginService.cs      # 插件加载/启用/权限与免责声明确认
        │   ├── NovaRuntime.cs        # Nova 会话运行时（流式增量 + Agent 事件消费）
        │   └── DxgiNative.cs         # DXGI 适配器查询（显存与适配器信息）
        ├── Localization/
        │   └── Loc.cs                # 本地化服务：Loc.T / Loc.TCore + 11 个 XAML 附加属性
        ├── Strings/
        │   └── en-US.json            # 英文词典（键 = 简体中文原文；源语言即键）
        └── Views/
            ├── CardHoverBehavior.cs        # 卡片悬浮/按压反馈附加属性（Composition Scale）
            ├── EmptyStateView.xaml(.cs)    # 统一空状态（图标 + 标题 + 副标题 + 可选按钮）
            ├── HomePage.xaml(.cs)          # 首页：版本列表 + 账户/启动卡
            ├── VersionsPage.xaml(.cs)      # 版本管理列表
            ├── VersionDetailPage.xaml(.cs) # 版本详情（隔离设置/Mod/存档/导出）
            ├── SavesPage.xaml(.cs)         # 存档跨版本聚合 + 复制启动
            ├── DownloadPage.xaml(.cs)      # 版本列表 + 安装进度
            ├── AccountsPage.xaml(.cs)      # 微软 / 第三方 / Starcloud ID 账户
            ├── AuthDialogs.cs              # 就地登录对话框（微软设备码 / 第三方密码）
            ├── PluginsPage.xaml(.cs)       # 插件管理（安装/启停/权限与免责声明确认）
            ├── PluginPanelPage.xaml(.cs)   # 插件面板宿主（WebView 承载插件 panel.html）
            ├── ContentDownloadDialog.xaml(.cs) # 内容库安装对话框（Mod/整合包/资源包等）
            ├── Skin3DView.cs               # 3D 皮肤预览（SwapChainPanel + 模型渲染）
            ├── ThemedDialog.cs             # 主题一致的对话框基类
            ├── NovaPage.xaml(.cs)          # Nova 助手页
            ├── NovaChatView.xaml(.cs)      # Nova 对话视图（页面内嵌与浮窗共用）
            ├── NovaOverlayWindow.xaml(.cs) # Nova 浮窗窗口
            ├── SettingsPage.xaml(.cs)      # 设置（路径/内存/Java/并发/主题/背景）
            ├── LogsPage.xaml(.cs)          # 启动器日志
            ├── WindowBackgroundHost.cs     # 背景图 / 模糊层 / 毛玻璃遮罩三层
            ├── PageRows.cs                 # 列表行共享 record
            ├── ComboItem.cs                # ComboBox 项共享 record
            ├── InstanceActions.cs          # 版本操作共享逻辑
            └── LaunchTips.cs               # 启动提示共享逻辑
```

## 3. 核心服务（Starcloud.Core）

| 服务 | 职责 | 关键依赖 |
| --- | --- | --- |
| `LauncherPaths` | 数据根 `%APPDATA%/starcloud-launcher`（与历史版本共用）；设置/账户/清单缓存/Java/日志路径 | — |
| `LauncherSettings` | 启动器设置持久化（`launcher.settings.json`，蛇形命名兼容旧配置） | System.Text.Json |
| `DownloadService` | HttpClient（MaxConnectionsPerServer=64）；单文件下载（SHA1 校验 + 指数退避重试 + `.sc-tmp` 临时文件）；批量并发下载（`SemaphoreSlim` + URL 去重 + 同大小跳过 + 进度/速度/ETA 回调）；Adoptium v3 API 解析 `binary.package.link` | — |
| `ManifestService` | 官方清单 `piston-meta.mojang.com/mc/game/version_manifest_v2.json` 获取 + 本地缓存（缓存优先，损坏回源）；扫描 `versions/<id>/<id>.json` 列出已安装版本 | DownloadService |
| `InstallerService` | 原版安装（版本 JSON + client jar + 库 + natives 解压 + 资源索引与对象）；Fabric 安装（拉取 meta.fabricmc.net profile 合并保存）；`inheritsFrom` 递归加载与 `MergeVersions`；`RulesAllow`（os:windows 规则判定） | DownloadService |
| `GameLauncher` | 启动命令行构建（新旧两代参数格式、`${var}` 替换、内置 G1GC 优化、classpath 拼接、引号转义）；第三方账户追加 `-javaagent:authlib-injector.jar=<base>` 与 `--userType`；`Process.Start` 启动 | InstallerService |
| `AccountStore` | 账户存储（`accounts.json`，仅存身份信息：uuid/username/type/默认标记/第三方服务器地址，**不落盘任何令牌**）；微软与第三方账户增改、默认账户切换 | — |
| `YggdrasilService` | 第三方认证（authlib-injector / Yggdrasil）：认证服务器地址解析（`serverLocation` 别名跳转，最多 3 跳）、`authenticate` 换票、皮肤 textures 解析；地址由用户填写，仓库不硬编码第三方认证服务 | — |
| `LaunchAccountService` | 启动前账户解析：命中内存会话则产出 `LaunchCredentials`，未命中抛 `AccountLoginRequiredException` 触发就地登录 | AccountStore |
| `TaskCenterService` | 全局任务总线：长任务登记（标题/进度/速度/分源贡献），UI 定时快照刷新；保留最近 60 条 | — |
| `Platform` | OS 能力抽象（路径/内存/终端/包管理器等差异集中在此，供跨平台版复用） | — |
| `MicrosoftAuthService` | 微软设备码 OAuth：`StartDeviceCodeAsync` → `PollAsync`（pending/success/error）→ XBL → XSTS → `login_with_xbox` → profile。client_id 为官方启动器 `00000000402b5328` | — |
| `JavaService` | 系统 Java 检测（PATH/JAVA_HOME + `java -version` 解析主版本，兼容 1.8 旧格式）；Adoptium JDK 下载解压到 `java/<major>/`（含子目录上提） | DownloadService |
| `FabricService` | `meta.fabricmc.net/v2/versions/loader/<mc>` 查询可用 loader 版本 | DownloadService |

## 4. 应用层（Starcloud.App）

- **AppServices**：`static Instance` 单例容器，构造时按依赖顺序实例化全部服务；
  设置修改后调用 `SaveSettings()` / `ReloadSettings()`。
- **MainWindow**：自定义亚克力背景（`TransparentAcrylicBackdrop`）+ `ExtendsContentIntoTitleBar`；
  `Nav_SelectionChanged` 将 NavigationView tag 路由到对应页面类型。
- **外观层（D 组）**：`AppearanceService` 负责把设置翻译成行为（主题应用、内置壁纸清单与
  遮罩不透明度解析），`WindowBackgroundHost` 负责窗口级背景渲染——在亚克力之上叠
  「清晰图 / 模糊层 / 毛玻璃遮罩」三层。模糊用 composition 的 `GaussianBlurEffect`
  作用于图片表面（`Microsoft.Graphics.Win2D` 1.4.0，`Microsoft.Graphics.Canvas.Effects`
  命名空间），组合器不可用时静默退化为清晰图。设置页改动通过
  `MainWindow.RefreshAppearance()` 即时预览。
- **动效层（D 组）**：`MotionService` 是唯一开关来源，`Enabled` 由设置 `ui_animations`
  （`system` 读 `UISettings.AnimationsEnabled` / `on` / `off`）解析；任意探测失败按「有动效」处理。
  页面转场通过 `MotionService.TransitionFor(enabled, drillIn)` 显式传给 `Frame.Navigate`
  （关闭时 `SuppressNavigationTransitionInfo`）。卡片反馈由附加属性 `CardHoverBehavior.IsEnabled`
  挂载（全局样式 `ScHoverCard`），用 Composition **显式**动画驱动 `Scale`（`Visual.Translation`
  在 WinAppSDK 1.8 投影中不可用，故不做上浮）；动效关闭时不接线、零开销。
  空状态统一由 `EmptyStateView` 承载（`Glyph` / `Title` / `Subtitle` / `ActionText` + `ActionClick`）。
- **页面 ↔ 服务交互**：页面直接调用 `AppServices.Instance.*`；
  长任务通过 `IProgress<BatchProgress>` 更新进度条，`CancellationToken` 支持取消。

## 5. 关键流程

### 5.1 安装原版（DownloadPage）

```
选择版本 → InstallerService.InstallVanillaAsync
  ├─ 下载版本 JSON（存 versions/<id>/<id>.json）
  ├─ 收集下载清单：client jar + 规则过滤后的库 + natives-windows
  ├─ 下载资产索引 → 展开为 objects/<hash[0:2]>/<hash> 全量资源
  ├─ DownloadBatchAsync 并发下载（去重 + 断点跳过已存在同大小文件）
  └─ natives zip 解压 .dll（尊重 extract.exclude）到 versions/<id>/natives/
```

### 5.2 安装 Fabric（DownloadPage）

```
选择 MC 版本 → FabricService.ListLoadersAsync 查 loader
→ InstallerService.InstallFabricAsync
  ├─ 下载 profile/json → FabricProfile.ToVersion() 转 MinecraftVersion
  ├─ MergeVersions(原版, Fabric)：mainClass 覆盖 / 库追加 / arguments 合并
  ├─ 保存合并结果为 fabric-loader-<mc>-<loader>.json
  └─ InstallVersionContentAsync（与原版相同的内容安装）
```

### 5.3 微软登录（AccountsPage）

```
StartDeviceCodeAsync → 展示 user_code + verification_uri
→ 循环 PollAsync（interval 秒）
  ├─ authorization_pending → 继续等待
  ├─ slow_down → 降速
  └─ 成功 → MS access_token
      → XBL (user.auth.xboxlive.com, RpsTicket=d=<msToken>)
      → XSTS (xsts.auth.xboxlive.com, Sandbox=RETAIL, RP=rp://api.minecraftservices.com/)
      → login_with_xbox (XBL3.0 x=<uhs>;<xstsToken>)
      → GET /minecraft/profile → name + id
→ AccountStore.AddOrUpdateMicrosoft（按 uuid 去重，设为默认）
```

### 5.4 启动游戏（HomePage）

```
环境体检（EnvironmentCheckService，阻断项可一键修复）
→ GameSessionGuard.TryAcquire（防双开：<.minecraft>/starcloud/game-session.json）
→ 文件预检（PreflightService，缺失文件可批量补全）
→ LaunchOptions（版本/玩家名/内存/Java）→ GameLauncher.LaunchVersionAsync
  ├─ LoadVersionAsync（inheritsFrom 递归合并）
  ├─ BuildCommandLineAsync
  │   ├─ classpath = 规则过滤后的库 + 版本 jar（";" 连接）
  │   ├─ JVM：-Xms/-Xmx/-Djava.library.path + 内置 G1GC（可关）+ 用户附加
  │   ├─ 新代 arguments.jvm/game（规则过滤 + ${var} 替换）
  │   ├─ 旧代 minecraftArguments 拆词替换
  │   └─ 现代版本缺 --username 时补全必需 game 参数
  └─ Process.Start（工作目录 = 游戏目录，CreateNoWindow）
       → GameSession：stdout/stderr 重定向并落盘（持续读流防阻塞）
                      → event OutputLine（首页实时日志面板，100ms 批量刷新）
                      → event Exited(int)（退出收尾 / 崩溃诊断 / 8s 内自动重试）
```

### 5.5 Java 管理（SettingsPage）

```
JavaService.ListRuntimesAsync（启动器下载的 + 系统 PATH/JAVA_HOME）
→ 不足时 DownloadRuntimeAsync(major)
  ├─ GetAdoptiumDownloadUrlAsync（Adoptium v3，解析 binary.package.link，.zip）
  ├─ 下载 → 解压到 java/<major>/ → 子目录上提
  └─ 返回 java.exe 路径
```

## 6. 数据目录

```
%APPDATA%/starcloud-launcher/
├── launcher.settings.json      # LauncherSettings
├── accounts.json               # AccountStore
├── version_manifest_cache.json # 清单缓存
├── java/<major>/               # Adoptium JDK
└── logs/                       # 启动器日志
```

游戏数据目录由 `DotMinecraft` 设置决定，默认 `%APPDATA%/.minecraft`。

## 7. 已知限制

- 仅 Windows（WinUI 3 约束）；x64。
- 鉴权令牌（微软 access/refresh token、第三方 access token）只存内存不落盘：应用重启后
  首次启动游戏需要重新登录；密码只作请求参数，不落盘、不写日志。
- OptiFine 未接入（原版、Fabric、Quilt、Forge、NeoForge 已支持）。
- Terracotta 联机为旧 Tauri 架构特性，.NET 版暂未实现。

## 8. 本地化层（D3）

放弃 WinUI 的 `x:Uid` / `.resw` / `PrimaryLanguageOverride` 方案（非打包应用下 override 不跨会话持久化且无法清空，见 microsoft/WindowsAppSDK#5335），改为**源语言即键**的自建 JSON 词典：

- **源语言即键**：词条键 = 简体中文原文（`Loc.T("暂无已安装版本")`）。zh-CN 下不加载词典、一律回退原文，因此天然零回归；只需维护一份 `Strings/en-US.json`（作为 `EmbeddedResource` 嵌入程序集）。
- **`Loc`**（`Localization/Loc.cs`）：
  - `Loc.T(key)` / `Loc.T(key, args)`：普通取词（`string.Format` 回填）。
  - `Loc.TCore(text)`：本地化 **Core 层产出的**展示文案。Core 不引用 `Loc`，且多数文案在 Core 内已用运行时值格式化（如「退出码 1」），故除精确匹配外，还按词典里含 `{0}` / `{1}` 的条目做**占位符反解**（前缀 / 中缀 / 后缀切分 + `string.Format` 回填）。
  - **XAML 附加属性**（XAML 无自定义标记扩展，故用附加属性声明式取词）：`Text` / `Header` / `Placeholder` / `Tip` / `Name` / `Title` / `Message` / `OnContent` / `OffContent` / `Subtitle` / `ActionText`，共 11 个。控件经 `Track` 登记，语言切换时由 `ReplayAll()` 即时刷新。
  - `Initialize` / `SetLanguage`：读取设置 `ui_language`（默认 `zh-CN`），切片即换词典并重放全部已登记控件——**无需重启**。
  - 词典缺失 / 损坏 → 空词典 + 静默回退中文，绝不阻断启动。
- **边界规则**：品牌与专有名词（Starcloud / Java / Nova / Mod / `×` / `-Xss1M` / PBR）不本地化；内部标识符、落盘常量、XAML `Tag` 与 **Nova 意图路由关键词**不本地化（路由靠中文子串匹配，如 `lower.Contains("体检")`，送入 Nova 的请求文本必须保持原始中文）。
- **维护工具（`tools/`，PowerShell 5.1，注释必须纯 ASCII）**：
  - `extract-loc-keys.ps1`：从 `Loc.T("…")` 与 `loc:Loc.Xxx="…"` 静态抽取词条 → `loc-keys.txt`。
  - `loc-dict.ps1 -Mode split|merge`：`split` 把词条均分到 `tools/dict/keys-N.txt`（人工翻译写入 `trans-N.txt`）；`merge` 合并各分片并并入 `core-strings.json`（Core 文案）→ 生成 `Strings/en-US.json`。
  - `verify-loc.ps1`：期望集（静态键 + `extra-keys.txt` 运行时键 + `core-strings.json`）与词典比对，要求 `MISSING=0 UNUSED=0`。
- **无障碍**：仅图标交互控件一律 `loc:Loc.Name`（映射到 `AutomationProperties.Name`）并与 `loc:Loc.Tip` 同源；表单控件保证 `Header` / `PlaceholderText` 成对。
- **未覆盖**：Nova 会话文本（`NovaService.cs` 等 Core 内硬编码中文与意图路由关键词耦合）暂保持中文。
