# Starcloud Minecraft Launcher

高性能 Minecraft 启动器，采用 Rust 核心库 + Tauri 2 桌面壳 + Preact 前端的架构。

## 功能特性

- **版本管理**：获取 Mojang 官方版本清单，一键安装原版（客户端 jar / 库文件 / 资源文件 / 版本 JSON），支持正式版 / 快照 / 旧版
- **ModLoader**：为已安装的原版一键安装 Fabric（内联合并继承的 version.json，无需额外步骤）
- **启动**：正版 / 离线账户，自动检测 Java，下载缺失组件后启动，实时日志与退出码/崩溃判定
- **账户**：微软官方设备码登录（OAuth 2.0 device code flow，access_token 自动续期）、离线账户
- **GUI 插件**：view / replacement 双通道插件机制，`furry://` 自定义协议托管插件前端，支持安装 / 删除 / 启停
- **核心插件**：Rust 插件系统（版本安装 / 启动前后事件钩子）

## 构建

### 前置要求

- [Rust](https://rustup.rs/)（stable，2024 及以后版本）
- [Node.js](https://nodejs.org/) 18+（构建 GUI 前端）
- [Tauri 2 CLI](https://v2.tauri.app/start/prerequisites/)（`cargo install tauri-cli --version "^2"`）

### 构建步骤

```bash
# 1. 构建 GUI 前端
cd ui
npm install
npm run build
cd ..

# 2. 开发模式运行
cargo tauri dev

# 3. 发布构建（产出安装包）
cargo tauri build
```

### 系统依赖（各平台）

#### Windows

- Windows 10 / 11 x64
- 构建依赖：Rust MSVC 工具链（`stable-x86_64-pc-windows-msvc`）+ [Visual Studio Build Tools](https://visualstudio.microsoft.com/zh-hans/downloads/)（含 C++ 生成工具）
- WebView2 Runtime（Windows 10/11 通常已内置，[手动安装](https://developer.microsoft.com/microsoft-edge/webview2/)）

#### macOS

- macOS 10.15+
- 构建依赖：Xcode Command Line Tools（`xcode-select --install`）
- 打包依赖：`cargo tauri build` 需安装 [@tauri-apps/cli](https://www.npmjs.com/package/@tauri-apps/cli)（dmg 打包自带）

#### Linux

Tauri 2 依赖 WebKitGTK，需要以下系统包：

```bash
# Debian / Ubuntu
sudo apt update
sudo apt install libwebkit2gtk-4.1-dev build-essential curl wget file \
  libxdo-dev libssl-dev libayatana-appindicator3-dev librsvg2-dev
```

```bash
# Fedora
sudo dnf install webkit2gtk4.1-devel openssl-devel curl wget file \
  libxcb-devel libxdo-devel gtk3-devel librsvg2-devel
```

```bash
# Arch Linux / Manjaro
sudo pacman -S webkit2gtk-4.1 base-devel curl wget file openssl \
  appmenu-gtk-module librsvg libxdo
```

> 注：WebKitGTK 版本必须为 **4.1**（`libwebkit2gtk-4.1-dev`），与 Tauri 2 匹配；运行期还需图形环境（X11 / Wayland）。

## 目录结构

```
├── src/                  # 核心库（无 Tauri 依赖，可独立作为库/CLI 使用）
│   ├── core/             # 服务层：InstallService / LaunchService / AccountService
│   ├── download/         # 下载引擎（并发、重试、进度、SHA1 校验）
│   ├── launch/           # 启动引擎（参数构建、Java 检测）
│   ├── modloader/        # Fabric / Forge / Quilt 版本查询
│   ├── plugin/           # 核心插件系统
│   ├── account/          # 账户（微软设备码 / 离线）
│   └── version_manager/  # 版本安装器 / 校验 / 缓存
├── src-tauri/            # Tauri 2 桌面壳
│   ├── src/commands.rs   # 后端命令
│   ├── src/plugin_host.rs# GUI 插件宿主（furry:// 协议）
│   └── tauri.conf.json   # 打包配置（nsis / dmg / deb-AppImage）
├── ui/                   # Preact 前端（6 页面 + 插件 SDK）
├── docs/                 # 架构 / API / 性能报告 / 使用手册
└── examples/             # 示例：基础启动器、GUI 插件 demo
```

## 文档

- [架构说明](docs/ARCHITECTURE.md)
- [API 参考](docs/API.md)
- [GUI 插件开发指南](docs/PLUGIN_DEV_GUIDE.md)
- [用户手册](docs/USER_MANUAL.md)
- [性能报告](docs/PERFORMANCE_REPORT.md)
