# Starcloud Minecraft Launcher

现代化 Minecraft Java 版启动器，采用 **C# / .NET 8 核心库 + WinUI 3 原生界面**：自定义亚克力背景、NavigationView 侧栏导航、Fluent 控件体系，视觉与 Windows 11 系统应用一致。

> 2026-09 起，项目主实现已全面从 Rust + Tauri + Web 前端迁移至 .NET + WinUI 3（`dotnet/`）。
> 旧实现整体归档于 `legacy/`（见 [legacy/README.md](legacy/README.md)），仅作参考，不再随新版本演进。

## 功能特性

- **版本管理**：获取 Mojang 官方版本清单（本地缓存，秒开），一键安装原版——客户端 jar / 库文件 / 资源文件 / natives 解压，全程 SHA1 校验
- **ModLoader**：为已安装的原版一键安装 Fabric / Quilt（`meta.fabricmc.net` / `meta.quiltmc.org` profile 与原版 JSON 合并，`inheritsFrom` 继承链完整支持）、Forge / NeoForge（官方安装器）
- **启动**：新旧两代参数格式兼容（`minecraftArguments` / `arguments.game|jvm`）、内置 G1GC 优化参数、启动前自动补齐缺失 Java、运行中会话识别（防双开）
- **账户**：微软官方设备码登录（OAuth 2.0 device code，XBL → XSTS → Minecraft API 完整链路）+ 第三方登录（authlib-injector / Yggdrasil，认证服务器地址由用户自行填写）；**鉴权令牌只存内存不落盘**，每次应用会话首次启动游戏时就地重新登录
- **插件系统**：`.mclplug` 插件包（`manifest.json` + `main.js` 进程内 Jint 沙箱 / `main.py` 子进程），权限白名单制，可扩展游戏启动参数与插件面板；规范见 [docs/plugin-spec.md](docs/plugin-spec.md)
- **调度中心**：下载、游戏启动、Java 搜索等长任务统一登记，顶栏入口实时查看聚合进度与分源贡献
- **Java 管理**：自动检测系统 Java（PATH / JAVA_HOME），一键下载 Adoptium Temurin JDK 8 / 21
- **设置**：游戏目录、内存分配、下载并发、主题（跟随系统 / 浅色 / 深色）即时生效
- **界面**：WinUI 3 原生渲染——自定义亚克力背景、ExtendsContentIntoTitleBar 自绘标题栏、NavigationView 侧栏导航（默认收拢为图标条）、PCL2 式首页双栏布局（左版本列表 / 右 360px 账户与启动卡）、顶栏 Nova 助手面板与个人中心入口

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
```

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
│   │       └── Views/                     # 首页/版本/存档/下载/账户/插件/Nova/设置/日志 等页面
│   └── tests/Starcloud.Core.Tests/        # Core 单元与集成测试（xUnit）
├── crossplatform/                 # 跨平台版（Uno Platform：macOS / Linux / Windows）
│   ├── Starcloud.Cross.slnx
│   ├── docs/POC-FINDINGS.md       # 选型与冒烟验证结论
│   └── src/Starcloud.Cross/
├── legacy/                        # [旧] Rust + Tauri + Preact 实现（仅参考）
├── docs/                          # 架构 / API / 用户手册 / 插件规范
├── tools/                         # 打包与本地化脚本（build-installer.ps1 等）
└── examples/plugins/hello-panel/  # 插件示例（.mclplug）
```

## 文档

- [架构说明](docs/ARCHITECTURE.md)
- [核心库 API 参考](docs/API.md)
- [视觉设计系统](DESIGN.md)
- [用户手册](docs/USER_MANUAL.md)
- [插件开发规范](docs/plugin-spec.md) / [插件 SDK 速查](docs/plugin-sdk.md)
- [跨平台选型与 POC 结论](crossplatform/docs/POC-FINDINGS.md)

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
