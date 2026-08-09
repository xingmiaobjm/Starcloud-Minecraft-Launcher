# Design System: Starcloud Minecraft Launcher

> 版本 0.2.0 | 2026-08-09
> Ethereal Glass 深空极简 — 视觉世界（PRODUCT.md §Brand Commitments）
> 引擎：Tauri 2 WebView（Preact + Vite）· 动画：GSAP 3

---

## 1. Visual Theme & Atmosphere

**"Ethereal Glass 深空极简"（Deep-Space Minimal）** — 密度 5、非对称 6、动效 8。

近 OLED 空黑画布之上，玻璃面板以 white/10 发丝线勾勒边界，单个淡蓝 accent（`#8FAEFF`）像悬浮在深空中的冷光。界面像一块精密加工的深空仪器面板——机械嵌套的 Double-Bezel 结构带来"玻璃板嵌在铝托盘里"的物理质感，所有交互以弹簧物理响应。冷静、精密、微光。

- **密度**：5/10 — 宏观留白，每屏一个主任务
- **非对称**：6/10 — 侧栏固定 + 主区呼吸，启动面板右对齐留出负空间
- **动效**：8/10 — 弹簧入场、级联揭示、持续微光（蓝色系）；全部 transform/opacity

## 2. Color Palette & Roles

- **Deep Space** (`#05060a`) — 窗口画布底色（近 OLED 深空黑，禁止纯黑 `#000`）
- **Deepest Space** (`#04050a`) — 窗口最外层壳（`.window` 8px 底衬）
- **Glass Panel** (`#0a0d15`) — 内层面板表面
- **Glass Panel 2** (`#0e1320`) — 浮层/抽屉/输入框
- **Glass Panel 3** (`#141a2b`) — hover 提升
- **Hairline** (`rgba(255,255,255,0.07)`) — 发丝结构线（永不使用实心高亮边框）
- **Hairline Strong** (`rgba(255,255,255,0.13)`) — hover/强调线
- **Moon White** (`#eef1f8`) — 主文本
- **Frost Gray** (`#9aa2b5`) — 次级文本、描述
- **Faint Gray** (`#5d6578`) — 三级文本、占位、装饰
- **Azure Bloom** (`#8faeff`) — **唯一 accent**：CTA、激活态、焦点环、进度、运行状态（用户指定：比 `#9CAEEE` 更偏蓝）
- **Azure Deep** (`#6c8cf0`) — accent 渐变端
- **Azure Ink** (`#0a1026`) — accent 上的深色文字
- **Azure Soft** (`rgba(143,174,255,0.12)`) — accent 基底（激活项背景）
- **Azure Glow** (`rgba(143,174,255,0.35)`) — accent 柔和辉光
- **Alert Coral** (`#ff6b76`) — 危险/错误（文本与细边，禁止大块填充）
- **Ember Amber** (`#f5a63c`) — 进行中/警告状态
- **Signal Green** (`#4ade80`) — 运行中状态点（仅状态语义，非品牌 accent）

**约束**：单一 accent（蓝）；无暖/冷灰混用；无渐变文字；accent 辉光 ≤ 35% 透明度。

## 3. Typography Rules

- **Display / 标题**：`Outfit` — 收字距（-0.02em）、层级靠字重（800）而非尺寸尖叫（页标题 25px 封顶）
- **Body / 正文**：`Outfit` + `Segoe UI`/`Microsoft YaHei` — 行高 1.6、正文 13.5–14px、次级色 `#9aa2b5`
- **Mono / 等宽**：`JetBrains Mono` — 版本号、进度百分比、日志、设备码、时间戳（tabular-nums）
- **Eyebrow / 分区标签**：10.5–11.5px 大写 + 0.12–0.14em 字距（`.nav-section`/`.page-subtitle`）
- **Banned**：Inter、Roboto、Arial、Open Sans、Helvetica、衬线字体（控制台类 UI 一律禁用）

## 4. Component Stylings

- **Double-Bezel 嵌套**（核心架构）：一切主要卡片/容器为「外层环 + 内层芯」——外层 hairline 边框 + 1px padding + 大圆角（22px），内层独立背景 + inset 顶部高光 + 同心小圆角（16px）。首页启动面板即 `.launch-shell`（渐变 hairline 环）+ `.launch-panel`（玻璃芯）
- **按钮（btn / btn-primary / btn-danger）**：**岛屿 pill**（`border-radius: 999px`）；active 态 `translateY(1px) scale(0.97)` 触觉下压；主按钮 accent 渐变填充 + 深蓝文字 `#0a1026` + 柔和辉光
- **导航项（nav-item）**：激活态 Azure Soft 底 + accent 文字 + 左侧 3px accent 指示条；图标 **stroke 1.6 细线**（Phosphor 风格，非粗描边）
- **卡片（card）**：`.card` 内层面板 + 1px hairline + inset 顶部高光
- **列表项（list-item）**：霜面 + 1px hairline，hover `translateY(-1px)` + accent 边线提亮 + 阴影抬升
- **输入框（input/select/textarea）**：标签在上；焦点 = accent 边线 + 3px Azure Soft 焦点环
- **加载态**：进度条渐变填充（Azure Deep→Azure Bloom）+ 流动动画 + 柔和辉光；无环形 spinner
- **空状态（empty）**：虚线框 + 图标 + 引导文案，非裸 "No data"
- **错误态（error-box）**：危险色 soft 底 + 细边 + 内联错误文本，可关闭
- **状态点（status-dot）**：running 绿 / installing·launching 蓝呼吸 / error 红
- **噪点层**：`#app::after` 固定伪元素 fractalNoise（0.028 opacity，`mix-blend-mode: overlay`，pointer-events none）

## 5. Layout Principles

- 固定骨架：**窗口双层壳**（`.window` 8px 深底 → `.window-shell` 22px Double-Bezel 外环）→ 自绘标题栏（44px 拖动区）→ 侧栏（196px）+ 状态栏（36px）+ 内容区
- 内容区固定填满窗口（`overflow: hidden` + flex column），**整页禁止滚动**；长列表/日志在 `.page-scroll` / `.game-log` 内部滚动
- 非对称：首页启动面板（3:4 Double-Bezel 玻璃）右对齐，左侧留白由环境光填充
- 环境光：`#app` 三层 radial-gradient 蓝色光斑（Azure Bloom / Azure Deep 系 4–7% 强度）
- 层级系统：`--z-shell: 1` / `--z-overlay: 90` / `--z-drawer: 95`，禁止任意大 z-index

## 6. Motion & Interaction（GSAP 3）

- **弹簧默认**：`cubic-bezier(0.32, 0.72, 0, 1)`（`--ease`）；滑块/抽屉 `cubic-bezier(0.16, 1, 0.3, 1)`（`--ease-spring`）；无 linear/ease-in-out
- **页面切换**：内容区 autoAlpha 0→1 + y 10→0（0.35s），同屏关键元素 stagger 0.045s 级联揭示
- **列表揭示**：`.list-item` 入场 y 10 + autoAlpha 0→1，stagger 0.04
- **持续微交互（perpetual micro-loops）**：
  - 首页启动按钮：boxShadow 蓝色呼吸脉动（1.6s yoyo，`rgba(143,174,255,…)`）
  - 状态点：accent 辉光 pulse（`rgba(143,174,255,0.85)`）
  - 进度填充：渐变流动（2.2s linear，仅背景定位动画）
- **性能**：只动画 `transform` 与 `opacity`，绝不动画 width/height/top/left；`backdrop-filter` 仅用于固定/抽屉层（drawer 28px blur），不用于滚动容器
- **可及性**：全部动画用 `gsap.matchMedia("(prefers-reduced-motion: no-preference)")` 包裹，偏好减少动态时回退为静态；组件卸载 `mm.revert()` 清理

## 7. Anti-Patterns（Banned）

- 无 emoji（图标一律线性 SVG）
- 无 Inter / Roboto / Arial / Open Sans / Helvetica / 衬线字体
- 无纯黑 `#000000`（用 `#05060a`）
- 无霓虹/外发光阴影（accent 柔和辉光 ≤ 35% 透明度例外）
- 无过饱和 accent
- 无大标题渐变文字
- 无元素重叠——每个元素占独立空间区域（Double-Bezel 的 1px 环不视为重叠）
- 无三等分卡片行（列表用堆叠条目）
- 无通用占位名、无假整数
- 无 AI 文案套话（"Elevate/Seamless/Unleash/Next-Gen"）
- 无整页滚动（内容固定窗口内，局部滚动）
- 无 `h-screen`（全高用 `100%` + flex 链）
- 无 LiquidGlass 折射/扭曲效果（已被 Double-Bezel 玻璃取代）
