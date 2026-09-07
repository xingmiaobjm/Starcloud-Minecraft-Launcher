# Product

<!-- impeccable:product-schema 1 -->

## Platform

Windows 桌面应用（WinUI 3 / Windows App SDK 1.6，仅 Windows，x64）。
2026-09-06 起由 Tauri 2（WebView）全面迁移至 .NET 原生栈；无移动端目标。

## Stack

**.NET 8 + WinUI 3**：`Starcloud.Core`（纯 C# 类库：清单/下载/安装/启动/账户/Java/Fabric）+
`Starcloud.App`（WinUI 3 前台：Mica、NavigationView、Windows App SDK 1.8.260804001，
SelfContained 发布）。解决方案 `dotnet/Starcloud.slnx`。已存在，非新建。
旧实现（Tauri 2 Rust 后端 `src-tauri/`、Preact 前端 `ui/`）保留仅作参考，不再维护。

## Users

面向 Minecraft Java 版玩家（中文社区为主，界面文案为简体中文）：在校学生与年轻玩家为主，
运行于个人 Windows 10 1809+ / Windows 11 桌面机。主要场景为"启动游戏、管理版本与
Mod 加载器、管理微软/离线账户"。单机单人使用，非团队/机构场景。

## Product Purpose

Starcloud Minecraft Launcher 是一个现代化 Minecraft 启动器：解析官方版本清单、
安装游戏本体与资源（jar + 库 + natives + 全量资产）、支持微软设备码登录与离线账户、
安装 Fabric 加载器、托管下载 Adoptium Java、启动并监控游戏进程。
成功 = 玩家在 30 秒内完成"选版本 → 启动"主任务。

## Positioning

自包含的版本管理 + Mod 加载器安装 + Java 托管一体化启动器：官方版本元数据由
launcher 自身解析（非依赖第三方下载器），微软 OAuth 设备码登录全程在 launcher 内完成
（官方启动器 client_id），Java 运行时由 Adoptium API 一键托管下载。
用户可见主张：原生 Windows 11 体验（Mica + Fluent），无需额外安装任何东西，
一站式完成 Java、游戏安装、加载器与账户配置。

## Operating Context

- 原生窗口：Mica 背景 + ExtendsContentIntoTitleBar 自定义标题栏（系统 caption 按钮）
- 导航：常驻左侧 NavigationView（首页/下载/模组资源/账户/设置，底部日志）
- 核心工作流：首页左卡选版本/账户 → 启动（进度条 + InfoBar 状态）→ 日志页排查
- 版本管理：官方清单列表安装（缓存优先秒开）；Fabric profile 合并继承安装
- 账户：微软设备码登录（启动器内轮询）、离线账户（MD5 v3 UUID）、默认账户切换
- Java：系统检测（PATH/JAVA_HOME）+ Temurin 21/8 一键托管下载
- 配置存于 `%APPDATA%/starcloud-launcher/`（launcher.settings.json / accounts.json，
  蛇形命名与旧版兼容），游戏数据默认 `%APPDATA%/.minecraft`

## Capabilities and Constraints

- 功能：原版版本安装/删除、Fabric 安装、微软设备码登录、离线账户、游戏启动、
  设置（游戏目录/内存/Java/下载并发/主题）、启动器日志
- 约束：仅 Windows x64；微软令牌不自动刷新（过期需重新设备码登录）；
  启动器与核心逻辑同进程直接调用（无 IPC）；离线账户使用占位 access token
- 未决：Forge / Quilt / OptiFine 未接入；插件系统与陶瓦联机（Terracotta）为
  旧 Tauri 架构特性，.NET 版暂未实现

## Brand Commitments

- 名称：Starcloud；项目曾从 "furry" 重命名为 "starcloud"，重构禁止回退旧名
- 用户明确绑定的视觉方向（2026-09-06）：**WinUI 3 原生 Fluent**——
  Mica 分层背景、NavigationView 常驻侧栏（pill 指示器）、系统强调色唯一 accent、
  Segoe Fluent Icons 字体图标、框架原生圆角与动效；拒绝深色电竞风、拒绝霓虹、
  拒绝 WebView 假原生
- 首页为 PCL2 式左卡布局（用户 2026-09-06 指定：卡片移到左侧）
- 主题跟随系统亮/暗自动切换，设置中可固定浅色/深色
- 界面文案简体中文；禁 emoji；图标用 Segoe Fluent Icons（非第三方库）
- 人性化承诺：错误文案中文（InfoBar 就地呈现）、删除类操作必须二次确认
  （ContentDialog）、加载失败与空态区分并提供重试、键盘全程可达（WinUI 原生焦点体系）

## Evidence on Hand

- 代码：`dotnet/Starcloud.slnx`（Starcloud.Core + Starcloud.App，已编译通过）
- 编译产物：`dotnet/src/Starcloud.App/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Starcloud.App.exe`
- 文档：docs/ARCHITECTURE.md、docs/API.md、docs/USER_MANUAL.md、DESIGN.md（WinUI 3 视觉）
- 旧实现参考：`src/`、`src-tauri/`、`ui/`（不再维护）
- 无截图/用户研究数据；无可复用商业数据

## Product Principles

1. 主任务优先：选版本→启动的路径永不被打断，30 秒内可完成
2. 任务状态永远可见：安装/启动进度、运行状态、错误恢复路径在固定位置呈现
3. 一致性高于表达：全部使用 WinUI 原生控件，同一操作全应用同形；品牌活在精确细节而非装饰
4. 真原生：由 Windows App SDK 渲染——Mica、强调色、焦点环、动效皆为系统级，
   而非 Web 模拟
5. 克制单色：Mica 中性表面 + 系统强调色唯一 accent，状态色仅作状态信号（InfoBar）

## Accessibility & Inclusion

- 键盘可达：WinUI 控件原生完整支持 Tab / 方向键 / Enter / Esc 与焦点环
- 动效遵循系统动画缩放设置（Windows 辅助功能）
- 深浅色高对比主题资源由框架保证（ThemeResource）；文本对比满足 WCAG
- 无闪烁元素；危险操作二次确认防止误删
