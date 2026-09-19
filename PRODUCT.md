# Product

<!-- impeccable:product-schema 1 -->

## Platform

Windows 桌面应用（WinUI 3 / Windows App SDK 1.8，仅 Windows，x64）。
2026-09-06 起由 Tauri 2（WebView）全面迁移至 .NET 原生栈；无移动端目标。

## Stack

**.NET 8 + WinUI 3**：`Starcloud.Core`（纯 C# 类库：清单/下载/安装/启动/账户/Java/Fabric）+
`Starcloud.App`（WinUI 3 前台：亚克力背景、NavigationView、Windows App SDK 1.8.260804001，
SelfContained 发布）。解决方案 `dotnet/Starcloud.slnx`。已存在，非新建。
旧实现（Tauri 2 Rust 后端、Preact 前端）整体归档于 `legacy/`，仅作参考，不再维护。

## Users

面向 Minecraft Java 版玩家（中文社区为主，界面文案为简体中文）：在校学生与年轻玩家为主，
运行于个人 Windows 10 1809+ / Windows 11 桌面机。主要场景为"启动游戏、管理版本与
Mod 加载器、管理微软/第三方账户"。单机单人使用，非团队/机构场景。

## Product Purpose

Starcloud Minecraft Launcher 是一个现代化 Minecraft 启动器：解析官方版本清单、
安装游戏本体与资源（jar + 库 + natives + 全量资产）、支持微软设备码登录与第三方
（authlib-injector）登录、安装 Fabric / Quilt / Forge / NeoForge 加载器、
托管下载 Adoptium Java、启动并监控游戏进程。
成功 = 玩家在 30 秒内完成"选版本 → 启动"主任务。

## Positioning

自包含的版本管理 + Mod 加载器安装 + Java 托管一体化启动器：官方版本元数据由
launcher 自身解析（非依赖第三方下载器），微软 OAuth 设备码登录全程在 launcher 内完成
（官方启动器 client_id），第三方登录经 authlib-injector 注入且认证服务器地址
完全由用户提供（仓库内不硬编码任何第三方认证服务地址），Java 运行时由 Adoptium API 一键托管下载。
用户可见主张：原生 Windows 11 体验（亚克力 + Fluent），无需额外安装任何东西，
一站式完成 Java、游戏安装、加载器与账户配置。

## Operating Context

- 原生窗口：自定义亚克力背景（`TransparentAcrylicBackdrop`）+ ExtendsContentIntoTitleBar
  自定义标题栏（系统 caption 按钮）
- 导航：左侧 NavigationView，默认收拢为图标条、点击临时展开至 232px
  （游戏组：首页/版本/下载/存档；扩展组：账户/插件/Nova；底部：启动器日志、设置）
- 顶栏右侧：调度中心入口（徽标显示运行中任务数，点开 432px 任务抽屉）+
  Nova Orb（点开 380px 助手面板）+ 个人中心入口（悬浮卡显示昵称/ID/登录态）
- 状态栏：底部 28px 一行实例指示，运行中显示当前实例
- 核心工作流：首页左列版本列表 + 右侧账户/启动卡 → 启动（进度条 + InfoBar 状态）→ 日志页排查
- 版本管理：官方清单列表安装（缓存优先秒开）；Fabric / Quilt profile 合并继承安装，
  Forge / NeoForge 官方安装器接入；
  版本详情子页管理隔离设置、Mod、存档与导出
- 存档：跨版本聚合展示（含共享目录与各隔离版本存档），标注所属版本，
  可一键复制到其他版本启动并在结束后回传成果
- 账户：微软设备码登录（启动器内轮询）、第三方登录（用户自填认证服务器地址，
  支持 `serverLocation` 别名跳转）、默认账户切换；鉴权令牌仅存内存
- Java：系统检测（PATH/JAVA_HOME）+ Temurin 21/8 一键托管下载
- 配置存于 `%APPDATA%/starcloud-launcher/`（launcher.settings.json / accounts.json，
  蛇形命名与旧版兼容），游戏数据默认 `%APPDATA%/.minecraft`

## Capabilities and Constraints

- 功能：原版版本安装/删除、Fabric / Quilt / Forge / NeoForge 安装、微软设备码登录、
  第三方登录（authlib-injector / Yggdrasil）、游戏启动与防双开、调度中心（全局任务进度）、
  插件系统（`.mclplug`：启动参数扩展与插件面板）、存档跨版本管理与复制启动、
  Nova 助手（Starcloud ID 登录、多服务商模型接入、游戏记录记忆、动手许可层与流水）、
  设置（游戏目录/内存/Java/下载并发/主题）、启动器日志
- 约束：仅 Windows x64；鉴权令牌只存内存不落盘，每次应用会话首次启动游戏时
  需重新登录（微软设备码 / 第三方密码）；启动器与核心逻辑同进程直接调用（无 IPC）
- 未决：OptiFine 未接入（原版 / Fabric / Quilt / Forge / NeoForge 已支持）；
  陶瓦联机（Terracotta）为旧 Tauri 架构特性，.NET 版暂未实现；
  跨平台版（`crossplatform/`）POC 已过，三平台 CI 冒烟待跑通

## Brand Commitments

- 名称：Starcloud；项目曾从 "furry" 重命名为 "starcloud"，重构禁止回退旧名
- 用户明确绑定的视觉方向（2026-09-06）：**WinUI 3 原生 Fluent**——
  分层背景材质、NavigationView 侧栏（pill 指示器）、Segoe Fluent Icons 字体图标、
  框架原生圆角与动效；拒绝深色电竞风、拒绝霓虹、拒绝 WebView 假原生
- accent 与材质（2026-09-10 据实现校准）：原「系统强调色唯一 accent + Mica 背景」
  已改为品牌色覆盖（浅色主题黄 `#FFD322` / 深色主题绿 `#3DD322`）与
  自定义亚克力背景 `TransparentAcrylicBackdrop`；详见 DESIGN.md §2
- 首页为 PCL2 式双栏布局：左＝弹性版本列表，右＝固定 360px 账户/启动卡
  （2026-09-10 据实现校准；2026-09-06 曾指定「卡片移到左侧」）
- 主题跟随系统亮/暗自动切换，设置中可固定浅色/深色
- 界面文案简体中文；禁 emoji；图标用 Segoe Fluent Icons（非第三方库）
- 人性化承诺：错误文案中文（InfoBar 就地呈现）、删除类操作必须二次确认
  （ContentDialog）、加载失败与空态区分并提供重试、键盘全程可达（WinUI 原生焦点体系）

## Evidence on Hand

- 代码：`dotnet/Starcloud.slnx`（Starcloud.Core + Starcloud.App，已编译通过）
- 编译产物：`dotnet/src/Starcloud.App/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Starcloud.App.exe`
- 文档：docs/ARCHITECTURE.md、docs/API.md、docs/USER_MANUAL.md、DESIGN.md（WinUI 3 视觉）、
  docs/plugin-spec.md + docs/plugin-sdk.md（插件规范与 SDK）
- 旧实现参考：`legacy/`（Rust + Tauri + Preact，不再维护）
- 无截图/用户研究数据；无可复用商业数据

## Product Principles

1. 主任务优先：选版本→启动的路径永不被打断，30 秒内可完成
2. 任务状态永远可见：安装/启动进度、运行状态、错误恢复路径在固定位置呈现
3. 一致性高于表达：全部使用 WinUI 原生控件，同一操作全应用同形；品牌活在精确细节而非装饰
4. 真原生：由 Windows App SDK 渲染——分层材质、强调色、焦点环、动效皆为框架级，
   而非 Web 模拟
5. 克制单色：中性材质表面 + 单一品牌 accent，状态色仅作状态信号（InfoBar）

## Accessibility & Inclusion

- 键盘可达：WinUI 控件原生完整支持 Tab / 方向键 / Enter / Esc 与焦点环
- 动效遵循系统动画缩放设置（Windows 辅助功能）
- 深浅色高对比主题资源由框架保证（ThemeResource）；文本对比满足 WCAG
- 无闪烁元素；危险操作二次确认防止误删
