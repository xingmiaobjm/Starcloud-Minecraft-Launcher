# Starcloud Minecraft Launcher

现代化 Minecraft Java 版启动器，采用 **C# / .NET 8 核心库 + WinUI 3 原生界面**：Mica 背景材质、NavigationView 侧栏导航、Fluent 控件体系，视觉与 Windows 11 系统应用一致。

> 2026-09 起，项目主实现已全面从 Rust + Tauri + Web 前端迁移至 .NET + WinUI 3（`dotnet/`）。旧实现（`src/`、`src-tauri/`、`ui/`）保留在仓库中作为参考，不再随新版本演进。

## 功能特性

- **版本管理**：获取 Mojang 官方版本清单（本地缓存，秒开），一键安装原版——客户端 jar / 库文件 / 资源文件 / natives 解压，全程 SHA1 校验
- **ModLoader**：为已安装的原版一键安装 Fabric（`meta.fabricmc.net` profile 与原版 JSON 合并，`inheritsFrom` 继承链完整支持）
- **启动**：新旧两代参数格式兼容（`minecraftArguments` / `arguments.game|jvm`）、内置 G1GC 优化参数、离线 UUID 按 Mojang 规则生成（MD5 v3）
- **账户**：微软官方设备码登录（OAuth 2.0 device code，XBL → XSTS → Minecraft API 完整链路）+ 离线账户，按 UUID 去重管理
- **Java 管理**：自动检测系统 Java（PATH / JAVA_HOME），一键下载 Adoptium Temurin JDK 8 / 21
- **设置**：游戏目录、内存分配、下载并发、主题（跟随系统 / 浅色 / 深色）即时生效
- **界面**：WinUI 3 原生渲染——Mica 背景、ExtendsContentIntoTitleBar 自绘标题栏、NavigationView 常驻侧栏、PCL2 式首页左卡布局

## 构建

### 前置要求

- **Windows 10 1809+（build 17763）/ Windows 11**，x64
- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)（8 / 9 / 10 均可构建）
- 无需其他依赖——Windows App SDK 以自包含（SelfContained）方式打包，目标机器无需单独安装运行时

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
├── dotnet/                        # .NET 主实现（当前活跃）
│   ├── Starcloud.slnx             # 解决方案
│   └── src/
│       ├── Starcloud.Core/        # 核心库（纯 C#，无 UI 依赖，可复用）
│       │   ├── Models/Minecraft.cs        # 版本清单/版本 JSON/资源索引 等数据模型
│       │   └── Services/
│       │       ├── LauncherPaths.cs       # 数据目录与设置持久化
│       │       ├── DownloadService.cs     # 并发下载引擎（重试/SHA1/进度回调）
│       │       ├── ManifestService.cs     # 版本清单 + 本地缓存 + 已安装列表
│       │       ├── InstallerService.cs    # 原版/Fabric 安装器（含版本合并）
│       │       ├── GameLauncher.cs        # 启动参数构建 + 进程启动
│       │       ├── AccountService.cs      # 账户存储 + 微软设备码登录
│       │       ├── JavaService.cs         # Java 检测 + Adoptium 下载
│       │       └── FabricService.cs       # Fabric loader 版本查询
│       └── Starcloud.App/         # WinUI 3 桌面应用
│           ├── MainWindow.xaml(.cs)       # Mica 窗口 + NavigationView 壳
│           ├── Services/AppServices.cs    # 服务单例容器
│           └── Views/                     # 首页/下载/模组/账户/设置/日志 六页
├── src/                           # [旧] Rust 核心库（参考，不再演进）
├── src-tauri/                     # [旧] Tauri 2 桌面壳（参考，不再演进）
├── ui/                            # [旧] Preact 前端（参考，不再演进）
├── docs/                          # 架构 / API / 用户手册等文档
└── MC-Icons/                      # Minecraft 图标素材库
```

## 文档

- [架构说明](docs/ARCHITECTURE.md)
- [核心库 API 参考](docs/API.md)
- [视觉设计系统](DESIGN.md)
- [用户手册](docs/USER_MANUAL.md)

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
