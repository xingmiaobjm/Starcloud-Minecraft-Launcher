# ARCHITECTURE.md — Starcloud 架构（.NET / WinUI 3）

> 2026-09-06 起项目全面转向 .NET：核心逻辑以纯 C# 重写（`dotnet/`），
> 界面使用 WinUI 3（Windows App SDK 1.8）。旧 Tauri/Rust + Preact 实现保留在
> `src/`、`src-tauri/`、`ui/` 目录仅作参考，不再维护。

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
    ├── Starcloud.Core/               # 纯 C# 类库（无 UI 依赖）
    │   ├── Models/
    │   │   └── Minecraft.cs          # Mojang JSON 数据模型（清单/版本/资产/参数）
    │   └── Services/                 # 26 个服务，按域分组
    │       ├── 路径/设置：LauncherPaths、InstanceSettings（版本隔离布局 + 生效启动配置）
    │       ├── 下载/清单：DownloadService、ManifestService、LoaderBuildCache
    │       ├── 安装：InstallerService、ForgeInstallerService、MrPackInstallerService
    │       ├── 启动：GameLauncher、LaunchAccountService、LaunchPreflightService
    │       ├── 账户：AccountService、StarcloudIdService
    │       ├── Java/ModLoader：JavaService、FabricService
    │       ├── 内容管理：ModrinthService、ModConflictService、SaveService、
    │       │             SaveLibraryService、PendingWorldStore、GameDiagnosticsService
    │       └── Nova：NovaService、NovaPermissions、NovaMoments、NovaInviteService、
    │                 NovaFpsWatcher
    └── Starcloud.App/                # WinUI 3 前台（WindowsPackageType=None）
        ├── App.xaml / App.xaml.cs
        ├── MainWindow.xaml(.cs)      # 亚克力 + NavigationView 壳
        ├── Services/
        │   └── AppServices.cs        # 服务单例容器（对应旧 Tauri AppState）
        └── Views/
            ├── HomePage.xaml(.cs)          # 首页：版本列表 + 账户/启动卡
            ├── VersionsPage.xaml(.cs)      # 版本管理列表
            ├── VersionDetailPage.xaml(.cs) # 版本详情（隔离设置/Mod/存档/导出）
            ├── SavesPage.xaml(.cs)         # 存档跨版本聚合 + 复制启动
            ├── DownloadPage.xaml(.cs)      # 版本列表 + 安装进度
            ├── AccountsPage.xaml(.cs)      # 离线/微软/Starcloud ID 账户
            ├── NovaPage.xaml(.cs)          # Nova 助手页
            ├── NovaChatView.xaml(.cs)      # Nova 对话视图（页面内嵌与浮窗共用）
            ├── NovaOverlayWindow.xaml(.cs) # Nova 浮窗窗口
            ├── SettingsPage.xaml(.cs)      # 设置（路径/内存/Java/并发/主题）
            ├── LogsPage.xaml(.cs)          # 启动器日志
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
| `GameLauncher` | 离线 UUID（MD5("OfflinePlayer:name") 按 UUID v3 打补丁）；启动命令行构建（新旧两代参数格式、`${var}` 替换、内置 G1GC 优化、classpath 拼接、引号转义）；`Process.Start` 启动 | InstallerService |
| `AccountStore` | 账户存储（`accounts.json`）：离线账户添加、微软账户增改、默认账户切换 | GameLauncher.OfflineUuid |
| `MicrosoftAuthService` | 微软设备码 OAuth：`StartDeviceCodeAsync` → `PollAsync`（pending/success/error）→ XBL → XSTS → `login_with_xbox` → profile。client_id 为官方启动器 `00000000402b5328` | — |
| `JavaService` | 系统 Java 检测（PATH/JAVA_HOME + `java -version` 解析主版本，兼容 1.8 旧格式）；Adoptium JDK 下载解压到 `java/<major>/`（含子目录上提） | DownloadService |
| `FabricService` | `meta.fabricmc.net/v2/versions/loader/<mc>` 查询可用 loader 版本 | DownloadService |

## 4. 应用层（Starcloud.App）

- **AppServices**：`static Instance` 单例容器，构造时按依赖顺序实例化全部服务；
  设置修改后调用 `SaveSettings()` / `ReloadSettings()`。
- **MainWindow**：自定义亚克力背景（`TransparentAcrylicBackdrop`）+ `ExtendsContentIntoTitleBar`；
  `Nav_SelectionChanged` 将 NavigationView tag 路由到对应页面类型。
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
LaunchOptions（版本/玩家名/内存/Java）→ GameLauncher.LaunchVersionAsync
  ├─ LoadVersionAsync（inheritsFrom 递归合并）
  ├─ BuildCommandLineAsync
  │   ├─ classpath = 规则过滤后的库 + 版本 jar（";" 连接）
  │   ├─ JVM：-Xms/-Xmx/-Djava.library.path + 内置 G1GC（可关）+ 用户附加
  │   ├─ 新代 arguments.jvm/game（规则过滤 + ${var} 替换）
  │   ├─ 旧代 minecraftArguments 拆词替换
  │   └─ 现代版本缺 --username 时补全必需 game 参数
  └─ Process.Start（工作目录 = 游戏目录，CreateNoWindow）
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
- 离线账户 auth_access_token 固定为占位值（离线模式约定）；微软令牌不做自动刷新，
  过期需重新设备码登录。
- Quilt / OptiFine 未接入（原版、Fabric、Forge、NeoForge 已支持）。
- 插件系统、Terracotta 联机为旧 Tauri 架构特性，.NET 版暂未实现。
