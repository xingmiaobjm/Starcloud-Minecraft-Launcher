# 项目地图（Project Map）

> 更新：2026-10-21　适用版本：桌面端 0.3.5
> 用途：第一次接手这个仓库时，先看这一份，知道每一块在哪、负责什么、彼此怎么连。

---

## 1. 一张图看全貌

```
                    ┌──────────────────────────────────────────────┐
                    │                Starcloud.Core                 │  ← 内核：全部业务逻辑
                    │  下载 / 版本 / 启动 / 账户 / 内容 / Nova / 联机  │     （无 UI 依赖）
                    └───────┬───────────────┬───────────────┬──────┘
                            │               │               │
              ┌─────────────▼──┐   ┌────────▼───────┐  ┌────▼─────────────┐
              │ Starcloud.App  │   │ Starcloud.Bridge│  │  Android 端      │
              │  WinUI 3 桌面端 │   │  CLI / JSON-RPC │  │  Kotlin + Compose │
              └────────────────┘   └────────┬───────┘  └──────────────────┘
                                            │
                              ┌─────────────▼──────────────┐
                              │ electron/  Linux 桌面壳     │
                              │ React + Fluent UI（Web UI） │
                              └────────────────────────────┘
```

一句话：**业务在内核，界面是外壳**。内核 `Starcloud.Core` 是与平台无关的 .NET 类库；
桌面端用 WinUI 3，Linux 端用 Electron + React，安卓端用 Kotlin/Compose（早期还有 Uno 版本）。

---

## 2. 目录地图

```
starcloud-minecraft-launcher/
├─ dotnet/                     ← Windows / Linux 共用的 .NET 侧
│  ├─ Starcloud.slnx
│  ├─ src/
│  │  ├─ Starcloud.Core/       ← 内核：所有业务逻辑与数据模型（无 UI）
│  │  ├─ Starcloud.App/        ← WinUI 3 桌面端（0.3.5 的实际交付物）
│  │  └─ Starcloud.Bridge/     ← 无界面 CLI，把内核能力以 JSON 暴露给其它外壳
│  └─ tests/Starcloud.Core.Tests/   ← 804 项单测（xunit）
│
├─ android/                    ← 安卓端（本次起纳入版本库）
│  ├─ Starcloud.Launcher.Compose/   ← 当前在用的 Kotlin + Jetpack Compose 前端
│  ├─ src/Starcloud.Android.Core/   ← 安卓侧重写的内核（下载 / 启动 / 版本）
│  ├─ src/Starcloud.Android.Host/   ← 宿主层：JVM 启动、SDL、输入、系统集成
│  ├─ src/Starcloud.Launcher.Uno/   ← 早期 Uno 前端（已停更，保留参考）
│  ├─ src/Starcloud.Launcher.Phone/ ← 更早的移动壳
│  ├─ src/Starcloud.Android.Maui/   ← MAUI 试验壳
│  ├─ components/                  ← 组件获取脚本（JRE / LWJGL / Mesa 运行时包）
│  ├─ spikes/                      ← 技术验证：s1-jvm（JVM 嵌入）、fcl-src（参考）
│  └─ tests/                       ← 安卓内核单测
│
├─ electron/                   ← Linux 桌面端：Electron + React + Fluent UI
│  ├─ src/main/                ← 主进程（窗口、菜单、原生能力）
│  ├─ src/preload/             ← 预加载脚本（IPC 桥）
│  └─ src/renderer/            ← 渲染进程（React 界面）
│
├─ flutter/                    ← Flutter 端（Linux 先行，另一条 UI 路线）
├─ legacy/                     ← 归档：旧 Tauri 桌面端、Uno 跨平台壳（不再演进）
│
├─ plugins/example-plugin/     ← 插件模板（plugin-spec-v3）
├─ starcloud-multiplayer/      ← 联机模块（独立解决方案：房间/协议/中继）
│
├─ docs/                       ← 文档
│  ├─ designs/                 ← 设计稿（按日期，先读再改）
│  ├─ plans/                   ← 实施计划（与设计稿成对）
│  ├─ API.md / ARCHITECTURE.md ← 接口与架构说明
│  ├─ plugin-spec*.md          ← 插件规范三代
│  └─ PROJECT-MAP.md           ← 本文件
│
├─ tools/                      ← 构建与资源生成
│  ├─ build-installer.ps1      ← 一键打包（publish → zip → NSIS）
│  ├─ installer.nsi            ← 安装器脚本（单窗口自定义 UI）
│  ├─ gen-installer-ui.py      ← 生成安装器位图（三档 DPI）
│  ├─ gen-installer-assets.py  ← 生成品牌图
│  ├─ gen-app-icon.ps1         ← exe 图标
│  └─ export-icon-png.ps1      ← 品牌 PNG 导出
│
├─ releases/                   ← 打包产物（git 忽略）
├─ secrets/                    ← 本地密钥（git 忽略，只保留 README）
├─ 加载器/                      ← 加载器图标素材
└─ README.md / PRODUCT.md / DESIGN.md
```

---

## 3. 内核 `Starcloud.Core`

平台无关（`net8.0`），被桌面端、Bridge、以及未来的任何外壳直接引用。

| 子目录 / 文件 | 职责 |
| --- | --- |
| `Services/ManifestService.cs` | 版本清单拉取、本地已装版本枚举 |
| `Services/InstallerService.cs` | 原版 / Fabric / Quilt 安装（含同名多份副本） |
| `Services/ForgeInstallerService.cs` | Forge / NeoForge 官方安装器调用 |
| `Services/GameLauncher.cs` | 启动参数构建、加载器合并、进程启动 |
| `Services/DownloadService.cs` + `ChunkedFileDownloader.cs` | 多源并行分片下载 |
| `Services/ContentLibraryService.cs` | mod / 资源包 / 光影的安装与管理 |
| `Services/CurseForge*` / `Modrinth` 相关 | 两个内容平台客户端与整合包安装 |
| `Services/AccountService.cs` / `StarcloudIdService.cs` | 微软账户 / Starcloud ID 登录 |
| `Services/NovaService.cs` / `NovaGatewayClient.cs` | Nova 助手：Agent 循环、云端网关 |
| `Services/Projection/*` | 投影（litematica / schem）解析与导出 |
| `Services/Lab/*` | 实验室：彩色文字、指令助手、投影社区 |
| `Services/Kernel/*` | 内核与插件模型（`KernelHost`、`ServiceRegistry`、贡献点） |
| `Services/InstanceSettings.cs` / `InstanceLayout.cs` | 实例档案、版本隔离、改名/复制 |

约定：**内核不出现任何 UI 类型**，所有跨层能力都通过接口暴露，方便其它外壳复用。

---

## 4. 桌面端 `Starcloud.App`（WinUI 3）

| 位置 | 说明 |
| --- | --- |
| `MainWindow.xaml(.cs)` | 外壳：标题栏、左侧导航、页面宿主、Nova 面板 |
| `Views/*.xaml` | 12 个页面（主页/版本/下载/存档/账户/工坊/日志/设置/实验室/投影管理…） |
| `Views/Controls/` | 复用控件：`GlowButton`、`BrandMark`、`NovaChatView`、`EmptyStateView` |
| `Views/Behaviors/` | 跟手光效：`HoverShadowBehavior`、`PointerLightBehavior`、`RowHoverBehavior` |
| `Views/Rows/RowModels.cs` | 列表行视图模型（`LocalVersionRow`、`ModRow`、`ContentFileRow`…） |
| `Services/MotionService.cs` | 动效四档（极少 / 较少 / 均衡 / 全部） |
| `App.xaml` | **设计令牌的单一来源**：颜色、圆角、字体、按钮模板 |

视觉规范（给其它平台对齐用）：`docs/designs/2026-10-20-windows-visual-language-spec.md`。

---

## 5. 外壳与跨平台现状

| 端 | 状态 | 入口 |
| --- | --- | --- |
| Windows 桌面 | **在维护**（0.3.5 交付物） | `dotnet/src/Starcloud.App` |
| Linux 桌面 | 进行中（Electron + React） | `electron/` |
| Android | 进行中（Compose，本次继续） | `android/Starcloud.Launcher.Compose` |
| Bridge | 稳定：无界面 CLI，JSON 进出 | `dotnet/src/Starcloud.Bridge` |
| Flutter | 实验分支 | `flutter/` |
| Tauri / Uno | 已归档 | `legacy/` |

**Bridge 是跨端的公共出口**：其它语言/进程的外壳不直接引用 .NET 程序集，
而是启动 `Starcloud.Bridge`，用 JSON 行协议调用内核能力（版本列表、安装、启动、Nova 对话等）。

---

## 6. 安卓端地图

```
android/
├─ Starcloud.Launcher.Compose/     ← 当前前端（Kotlin + Compose）
│  ├─ app/src/main/java/…          ← 页面、导航、主题
│  ├─ app/src/main/assets/         ← 随包资源（组件包由脚本下载，不入库）
│  └─ tools/                       ← 打包辅助脚本
├─ src/Starcloud.Android.Core/     ← 安卓内核：下载引擎、版本解析、启动参数
├─ src/Starcloud.Android.Host/     ← JVM 嵌入、SDL、输入法、系统调用
├─ src/Starcloud.Launcher.Uno/     ← 旧 Uno 前端（保留参考，不再演进）
├─ src/Starcloud.Launcher.Phone/   ← 更早的移动壳
├─ components/                     ← fetch-components.ps1：按需下载 JRE/LWJGL/Mesa
└─ tests/Starcloud.Android.Core.Tests/
```

分层与桌面端一致：**Core 管逻辑，Host 管与系统打交道，Compose 只管界面**。
因此换 UI（Compose → Web/Electron 风格页面）时，只替换最上面那一层，下面两层不动。

---

## 7. 构建与发布

| 目标 | 命令 |
| --- | --- |
| 桌面端编译 | `dotnet build dotnet/src/Starcloud.App/Starcloud.App.csproj -c Debug` |
| 单测 | `dotnet test dotnet/tests/Starcloud.Core.Tests/Starcloud.Core.Tests.csproj` |
| 打包（安装版 + 便携版） | `powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1` |
| 只重打安装器（复用上次 publish） | 同上，加 `-SkipPublish` |
| Linux 端 | `cd electron && npm run dev` / `npm run package` |
| 安卓端 | `cd android/Starcloud.Launcher.Compose && ./gradlew assembleDebug` |

产物落在 `releases/`：`Starcloud Minecraft Launcher_<版本>_x64-setup-<时间戳>.exe` 与 `…-portable-<时间戳>.zip`。

---

## 8. 约定（改代码前先看）

1. **版本号单一来源**：`dotnet/src/Starcloud.App/Starcloud.App.csproj` 的 `<Version>`；
   安装器位图、Bridge 都从它派生，不要另写死。
2. **设计令牌单一来源**：`Starcloud.App/App.xaml`；新颜色/圆角先加令牌再引用。
3. **内核不依赖 UI**，外壳不写业务逻辑。
4. 日志走 `LauncherLog`，不直接 `Console.Write`。
5. 大改动先写 `docs/designs/`（设计稿）+ `docs/plans/`（实施计划），再动代码。
6. 构建要求 **0 错误 0 警告**，单测全绿。

---

## 9. 常见入口

| 想做什么 | 从这里开始 |
| --- | --- |
| 加一个下载源 | `Core/Services/DownloadSource.cs` + `docs/designs/2026-09-12-multi-source-download-design.md` |
| 改启动参数 | `Core/Services/GameLauncher.cs` |
| 加一个页面 | `App/Views/` 新建 Page + `MainWindow.xaml` 导航项 + `App.xaml` 令牌 |
| 加一个插件 | `docs/plugin-spec-v3.md` + `plugins/example-plugin` |
| 改安装器外观 | `tools/gen-installer-ui.py`（位图）+ `tools/installer.nsi`（交互） |
| 接一个新的外壳 | 先读 `Starcloud.Bridge` 的 JSON 协议，再决定直连内核还是走 Bridge |
