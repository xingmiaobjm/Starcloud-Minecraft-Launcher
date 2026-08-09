# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

> 桌面应用（Tauri 2 WebView：Windows WebView2 / WebKitGTK / WKWebView）。设计语言由 WebView 渲染，记录为 `web`；无移动端目标。

## Stack

Preact + Vite + TypeScript + vanilla CSS（ui/），Tauri 2 Rust 后端（src-tauri/），GSAP 3 动画，LiquidGlass（首页启动面板）。已存在，非新建。

## Users

面向 Minecraft Java 版玩家（中文社区为主，界面文案为简体中文）：在校学生与年轻玩家为主，运行于个人 Windows/macOS/Linux 桌面机。主要场景为"启动游戏、管理版本与 Mod 加载器、管理微软/离线账户、安装插件"。单机单人使用，非团队/机构场景。

## Product Purpose

Starcloud Minecraft Launcher 是一个现代化 Minecraft 启动器：解析官方版本清单、安装游戏本体与资源、支持微软设备码登录与离线账户、安装 Fabric 加载器、托管 GUI 插件（iframe 双通道）、启动并监控游戏进程。成功 = 玩家在 30 秒内完成"选版本 → 启动"主任务。

## Positioning

自包含的版本管理 + Mod 加载器安装 + 插件生态一体化启动器：官方版本元数据由 launcher 自身解析（非依赖第三方下载器），GUI 插件通过 `furry://` 自定义协议 iframe 双通道托管，微软 OAuth 设备码登录全程在 launcher 内完成。用户可见主张：无需额外安装器，一站式完成游戏安装、加载器与账户配置。

## Operating Context

- 窗口自绘：自绘标题栏（44px 拖动区 + 最小化/最大化/关闭），窗口最小 960×640
- 核心工作流：首页选版本/账户 → 启动/安装（进度条 + 状态栏）→ 运行中监控（停止按钮/日志页）
- 版本管理：官方清单列表安装；Fabric 加载器内联继承合并安装
- 账户：微软设备码登录（前端轮询）、离线账户、默认账户切换
- 插件：URL/本地 zip 安装 GUI 插件，禁用/删除；replacement 插件整体接管主界面
- 日志页实时读取 `latest.log` 尾部
- 配置存于 dot_minecraft 侧（accounts.json / launcher.json 等），启动时由 Rust 端 `load_config()` 加载

## Capabilities and Constraints

- 功能：版本安装/删除、Fabric 安装、微软登录、离线账户、GUI 插件（view/replacement 双模式）、游戏启动与停止、设置（Java 路径/内存/下载并发）、游戏日志
- 约束：核心库无 tauri 依赖；`furry://` 协议仅在 Tauri 环境可用；插件清单 `plugin.json`（api_version + min_launcher_version）版本兼容校验；zip 路径穿越防护（enclosed_name + 插件 id 白名单）
- 未决：Forge/Quilt 仅提供版本查询能力（modloader 模块），未接入完整安装与 GUI 入口

## Brand Commitments

- 名称：Starcloud（启动器）/ starcloud-minecraft（核心库）；GUI 插件协议协议名为 `furry://`
- 项目曾从 "furry" 重命名为 "starcloud"，重构禁止回退旧名
- 用户明确绑定的视觉方向（2026-08-09）：**Ethereal Glass 深空极简**——近 OLED 深空黑基底、white/10 发丝线、Double-Bezel 嵌套卡片、圆形岛屿按钮、弹簧物理动画；唯一 accent 为**比 `#9CAEEE` 更偏蓝的淡蓝**（`#8FAEFF` 系）；参考 XMCL（Voxelum/x-minecraft-launcher）keystone UI 的分层导航与列表结构
- 界面文案简体中文；禁 emoji；图标线性单 stroke

## Evidence on Hand

- 生产计划：`.trae/documents/launcher-production-plan.md`（5 阶段，已完成至 v0.2.0）
- 文档：docs/ARCHITECTURE.md、API.md、PERFORMANCE_REPORT.md、DESIGN.md（旧液态玻璃规范，本次重写后更新）、USER_MANUAL.md、PLUGIN_DEV_GUIDE.md
- 前端实现：ui/src/（styles.css 旧版 / app.tsx / pages/）、ui/index.html（Google Fonts Outfit+JetBrains Mono、SVG favicon）
- 无截图/用户研究数据；无可复用商业数据

## Product Principles

1. 主任务优先：选版本→启动的路径永不被打断，30 秒内可完成
2. 任务状态永远可见：安装/启动进度、运行状态、错误恢复路径在固定位置呈现
3. 一致性高于表达：同一操作全应用同形（Operate 模式），品牌活在精确细节而非装饰
4. 原生期望：自绘窗口行为、系统级交互（拖动/缩放/焦点环）优先
5. 克制单色：中性深空黑 + 单一蓝色 accent，状态色仅作状态信号

## Accessibility & Inclusion

- 全部动画经 `prefers-reduced-motion` 降级（GSAP matchMedia 包裹，卸载时 revert）
- 键盘可聚焦：焦点环（accent soft ring）在交互控件上可见
- 无闪烁 >3Hz 元素（禁用态呼吸光除外）；文本对比 ≥4.5:1
