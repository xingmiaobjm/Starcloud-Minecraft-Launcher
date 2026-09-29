# Starcloud Minecraft Launcher

现代化 Minecraft Java 版启动器，采用 **C# / .NET 8 核心库 + WinUI 3 原生界面**：自定义亚克力背景、NavigationView 侧栏导航、Fluent 控件体系，视觉与 Windows 11 系统应用一致。

> 2026-09 起，项目主实现已全面从 Rust + Tauri + Web 前端迁移至 .NET + WinUI 3（`dotnet/`）。
> 旧实现整体归档于 `legacy/`（见 [legacy/README.md](legacy/README.md)），仅作参考，不再随新版本演进。

## 功能特性

- **版本管理**：获取 Mojang 官方版本清单（本地缓存，秒开），一键安装原版——客户端 jar / 库文件 / 资源文件 / natives 解压，全程 SHA1 校验
- **ModLoader**：为已安装的原版一键安装 Fabric / Quilt（`meta.fabricmc.net` / `meta.quiltmc.org` profile 与原版 JSON 合并，`inheritsFrom` 继承链完整支持）、Forge / NeoForge（官方安装器）
- **版本隔离**：默认隔离 mods / 存档 / 配置 / 资源包 / 光影 5 类内容，库与资源文件共享；默认方式在设置里定，单个版本可在其详情页覆盖
- **内容管理**：版本详情页按 Mod / 资源包 / 光影包 / 存档 四类浏览，文件拖进去即安装；停用只是改名为 `xxx.disabled`（不删文件，随时还原），Mod 另支持「检查更新」与「冲突扫描」
- **整合包**：导入 Modrinth（`.mrpack` / 内含 `modrinth.index.json`）、CurseForge（内含 `manifest.json`）、MultiMC / Prism 实例包，或直接粘贴整合包页面链接；也可把当前版本导出为上述三种格式
- **资源下载**：Modrinth / CurseForge 双源搜索，项目详情 + 版本筛选 + 依赖提示，一键装到当前版本（整合包走新建版本流程）；资源列表与图标有本地缓存，命中后不再联网；模组简介在中文界面下显示 MCIM 的中文译文（原文先上屏、译文异步替换，失败保留原文）
- **CurseForge 接入**：接口自动分层——官方 API → MCIM 镜像 → curse.tools 代理，单层超过 6 秒无响应（或连不上 / 环境不允许访问）即换下一层并在 10 分钟内不再优先尝试；官方分发版本内置共用 API Key（构建期注入，**密钥不入库**，见 [secrets/README.md](secrets/README.md)），留空即用；官方 CDN 的下载直链会自动带上密钥（CurseForge 自 2026-07 起强制），整条直链失败时兜底到 MCIM 文件镜像
- **启动**：新旧两代参数格式兼容（`minecraftArguments` / `arguments.game|jvm`）、内置 G1GC 优化参数、启动前自动补齐缺失 Java、运行中会话识别（防双开）
- **运行状态**：当前这一局的 CPU / 内存 / GPU 实时曲线（60 点采样）、启动配置与 JVM 参数、日志尾自动吸底（上滚即暂停），可结束 / 重启 / 复制启动命令
- **账户**：微软官方设备码登录（OAuth 2.0 device code，XBL → XSTS → Minecraft API 完整链路）+ 第三方登录（authlib-injector / Yggdrasil，认证服务器地址由用户自行填写）；**鉴权令牌只存内存不落盘**，每次应用会话首次启动游戏时就地重新登录
- **联机**：内置 Terracotta 等联机方案，无需公网 IP 或端口映射即可与好友直连
- **存档**：世界备份 / 恢复 / 清理，跨版本运行态提示
- **Nova 助手**：顶栏入口 + 游戏内浮窗（共用同一条消息流）；接第三方 LLM（可配多套），能生成「计划」（内存、JVM 参数、版本隔离等）确认后一键执行，对话与操作日志留痕
- **插件系统**：`.mclplug` 插件包（`manifest.json` + `main.js` 进程内 Jint 沙箱 / `main.py` 子进程），权限白名单制，可扩展游戏启动参数、插件面板与**声明式 UI**（2.0 草案：`contributes` 声明面板、页面挂载点、首页卡片与左侧导航项、组件白名单，界面由宿主渲染为原生控件，动作与绑定仍走同一套权限校验）；规范见 [docs/plugin-spec.md](docs/plugin-spec.md)、[docs/plugin-spec-v2.md](docs/plugin-spec-v2.md)
- **调度中心**：下载、游戏启动、Java 搜索等长任务统一登记，顶栏入口实时查看聚合进度与分源贡献
- **Java 管理**：自动检测系统 Java（PATH / JAVA_HOME），一键下载 Adoptium Temurin JDK 8 / 21
- **外观**：主题跟随系统 / 浅色 / 深色即时生效；背景可选「关闭（纯毛玻璃）/ 内置壁纸 / 本地图片」，模糊强度、不透明度与毛玻璃强度实时可调（毛玻璃强度勾「跟随主题」即用深 0.45 / 浅 0.25 的配方）
- **界面**：WinUI 3 原生渲染——亚克力窗口 + `ExtendsContentIntoTitleBar` 自绘标题栏、NavigationView 侧栏导航（默认收拢为图标条）、PCL2 式首页双栏布局（左版本列表 / 右 360px 账户与启动卡）与版本详情页分组卡片（概览 / 个性化 / 快捷方式 / 高级管理）；页面抬头不写标题与说明，当前位置由左侧导航的激活项指示；卡片展开 / 收起状态、窗口尺寸等都会记住；跟手光斑、悬停抬升、展开收起等动效由设置里的「界面动效」统一开关

## 构建

### 前置要求

- **Windows 10 1809+（build 17763）/ Windows 11**，x64
- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)（8 / 9 / 10 均可构建）
- 无需其他依赖——Windows App SDK 以自包含（SelfContained）方式打包，目标机器无需单独安装运行时

> `crossplatform/` 是独立的跨平台版（Uno Platform，`net10.0-desktop`，目标 macOS / Linux / Windows），
> 需 .NET SDK 10；构建：`dotnet build crossplatform/Starcloud.Cross.slnx`。跨平台版与 Windows 版
> 共用 `dotnet/src/Starcloud.Core` 核心库。

### 构建与运行

```powershell
# 在仓库根目录执行

# 还原 + 编译（解决方案为 .slnx 格式）
dotnet build dotnet/Starcloud.slnx

# 直接运行应用
dotnet run --project dotnet/src/Starcloud.App

# 或运行已编译产物
dotnet/src/Starcloud.App/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Starcloud.App.exe

# 跑测试（Core 单元与集成测试，xUnit）
dotnet test dotnet/Starcloud.slnx

# 官方分发版构建：把共用的 CurseForge API Key 编进内核（密钥不入库，见 secrets/README.md）
dotnet build dotnet/Starcloud.slnx -c Release -p:CurseForgeApiKey=<key>
```

> **XAML 全量编译需要 MSVC 工具链**：`obj/` 被清掉后重新编译时，Windows App SDK 的
> XAML 编译器会去 `<VS 安装根>\VC\Tools\MSVC` 找 `vcmeta.dll`；若该 VS 没装 C++ 组件
> （报 `GetLatestMSVCVersion 任务意外失败`），显式指定一个装了 C++ 工具的目录即可：
> `dotnet build dotnet/src/Starcloud.App -p:VCToolsInstallDir="C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207\"`

## 目录结构

```
├── dotnet/                        # Windows 主实现（当前活跃）
│   ├── Starcloud.slnx             # 解决方案
│   ├── src/
│   │   ├── Starcloud.Core/        # 核心库（纯 C#，无 UI 依赖，跨平台版共用）
│   │   │   ├── Models/            # 版本清单 / 版本 JSON / 资源索引 等数据模型
│   │   │   ├── Plugins/           # 插件宿主（清单/权限/JS 与 Python 运行时）
│   │   │   └── Services/          # 下载/安装/启动/账户/Java/ModLoader/Nova 等服务
│   │   └── Starcloud.App/         # WinUI 3 桌面应用
│   │       ├── MainWindow.xaml(.cs)       # 亚克力窗口 + NavigationView 壳 + 调度中心抽屉
│   │       ├── Services/                  # AppServices 单例容器、插件服务、Nova 运行时
│   │       └── Views/                     # 页面（首页/版本/版本详情/运行状态/存档/下载/账户/插件/Nova/设置/日志…）
│   │           ├── Behaviors/             # 附加属性行为（悬停光斑/抬升/缩放/抽屉展开）
│   │           ├── Controls/              # 自绘控件（空状态、Nova 对话、3D 皮肤形象）
│   │           ├── Dialogs/               # 对话框（登录/整合包/崩溃/自更新/主题基类）
│   │           ├── Overlays/              # 独立浮窗（Nova 浮窗）
│   │           ├── Rows/                  # 列表行模型 + 版本类型/图标判定
│   │           └── Shared/                # 页面共用小工具（版本操作、远程图标、窗口背景）
│   └── tests/Starcloud.Core.Tests/        # Core 单元与集成测试（xUnit）
├── crossplatform/                 # 跨平台版（Uno Platform：macOS / Linux / Windows）
│   ├── Starcloud.Cross.slnx
│   ├── docs/POC-FINDINGS.md       # 选型与冒烟验证结论
│   └── src/Starcloud.Cross/
├── legacy/                        # [旧] Rust + Tauri + Preact 实现（仅参考）
├── docs/                          # 架构 / API / 用户手册 / 插件规范
├── tools/                         # 打包与本地化脚本（build-installer.ps1 等）
└── examples/plugins/             # 插件示例（.mclplug）：hello-panel / instance-report
```

## 文档

- [架构说明](docs/ARCHITECTURE.md)
- [核心库 API 参考](docs/API.md)
- [产品定位与主张](PRODUCT.md)
- [视觉设计系统](DESIGN.md)
- [用户手册](docs/USER_MANUAL.md)
- [插件开发规范](docs/plugin-spec.md) / [插件规范 2.0（草案）](docs/plugin-spec-v2.md) / [插件 SDK 速查](docs/plugin-sdk.md)
- [跨平台选型与 POC 结论](crossplatform/docs/POC-FINDINGS.md)
- [设计记录](docs/designs/)（按日期归档的单点设计决策）

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
