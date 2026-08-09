# Design System: Starcloud Minecraft Launcher

> 版本 0.2.0 | 2026-08-09
> 基于 stitch-design-taste skill 生成的设计规范，作为 GUI 前端（ui/）与插件 UI 的统一设计源。
> 引擎：Tauri 2 WebView（Preact + Vite）· 动画：GSAP 3

---

## 1. Visual Theme & Atmosphere

**"深夜控制舱"（Night Cockpit）** — 密度 4、非对称 5、动效 7。

一个克制而自信的深色启动器界面：绝对中性蓝黑基底（Zinc 家族），单个荧光绿 accent 作为唯一的"生命信号"，所有控件悬浮在带色调的液态玻璃表面之上。界面像一座安静运行的飞行舱——信息层级靠排印重量而非尺寸尖叫，交互响应靠弹簧物理而非机械跳变。氛围冷静、精密、微光。

- **密度**：4/10 — 充裕留白，每屏一个主任务
- **非对称**：5/10 — 侧栏固定 + 主区呼吸，启动面板右对齐留出负空间
- **动效**：7/10 — 弹簧入场、级联揭示、持续微光；全部 transform/opacity

## 2. Color Palette & Roles

- **Abyss Ink** (`#0b0e13`) — 窗口画布底色，禁止使用纯黑
- **Cockpit Glass** (`rgba(18,22,30,0.72)`) — 窗口本体液态玻璃（blur 30px + saturate 1.35）
- **Deep Panel** (`#151a21`) — 次级表面（状态栏/抽屉）
- **Sunken Fill** (`rgba(255,255,255,0.03)`) — 深嵌控件（输入框/轨道/按钮底）
- **Surface Frost** (`rgba(255,255,255,0.045)`) — 面板/列表项表面
- **Surface Frost Strong** (`rgba(255,255,255,0.07)`) — hover 表面
- **Ink Frost** (`#eef1f6`) — 主文本
- **Muted Steel** (`#8b93a3`) — 次级文本、描述、元数据
- **Faint Steel** (`#5f6878`) — 三级文本、占位、装饰
- **Signal Green** (`#1bd96a`) — **唯一 accent**：CTA、激活态、焦点环、进度、运行状态（饱和度 <80%，无霓虹紫/蓝）
- **Signal Green Deep** (`#16b25a`) — accent 渐变端
- **Signal Green Soft** (`rgba(27,217,106,0.12)`) — accent 基底（激活项背景）
- **Alert Coral** (`#ff5c66`) — 危险/错误（仅文本与细边，禁止大块填充）
- **Ember Amber** (`#f0a020`) — 进行中/警告状态
- **Border Frost** (`rgba(255,255,255,0.07~0.16)`) — 1px 结构线；永不使用实心高亮边框

**约束**：整个界面单一 accent；无暖/冷灰混用；无渐变文字；无霓虹外发光（accent 按钮允许的柔和辉光 ≤ rgba(27,217,106,0.3)）。

## 3. Typography Rules

- **Display / 标题**：`Outfit` — 收字距（letter-spacing -0.02em）、受控字号（24px 页标题封顶）、层级靠字重（800）而非尺寸尖叫
- **Body / 正文**：`Outfit` + `Segoe UI`/`Microsoft YaHei` — 宽松行高（1.6）、正文 13.5–14px、次级色 #8b93a3
- **Mono / 等宽**：`JetBrains Mono` — 版本号、进度百分比、日志、设备码、时间戳（tabular-nums）
- **Banned**：Inter（所有场景）、衬线字体（控制台类 UI 一律禁用）、字母间距过大的 ALL-CAPS 装饰性标题（section-title 限制在 10.5–11.5px 微妙级）

## 4. Component Stylings

* **按钮（btn / btn-primary / btn-danger / btn-ghost）**：无霓虹外发光；主按钮 accent 填充 + 深绿文字 `#07170e`；active 态 `translateY(1px) scale(0.98)` 触觉下压；hover 仅增强 8px 柔和辉光
* **卡片（card）**：12px 圆角（控制台级，非营销页 2.5rem）、霜面 + 1px 淡边 + 顶部 1px 内高光（inset）；仅当层级需要高度感时使用
* **列表项（list-item）**：霜面、hover `translateY(-1px)` + 边线提亮；高密度时以 10px 间距分隔而非大卡片
* **输入框（input/select/textarea）**：标签在上、错误在下、无浮动标签；焦点 = accent 边线 + 3px accent-soft 焦点环
* **导航项（nav-item）**：激活态 accent-soft 底 + accent 文字 + 左侧 3px accent 指示条；图标 stroke 1.8 线性
* **加载态**：进度条为渐变填充（accent-2→accent）+ 柔和辉光；禁用骨架，不出现环形 spinner
* **空状态（empty）**：虚线框 + 图标 + 引导文案（"暂无账户，先添加…"），非裸 "No data"
* **错误态（error-box）**：危险色 soft 底 + 细边 + 内联错误文本，可关闭

## 5. Layout Principles

- 固定三栏骨架：自绘标题栏（44px 拖动区）→ 侧栏（196px）→ 状态栏 + 内容区
- 内容区固定填满窗口（`overflow: hidden` + flex column），**整页禁止滚动**；长列表/日志在 `.page-scroll` / `.game-log` 内部滚动
- 非对称：首页启动卡片（3:4 液态玻璃）右对齐，左侧留白由环境光填充
- 网格优先：`.grid-2` 用于设置/账户表单双列；禁用 flex 百分比 hack
- 间距：内容 padding `34px 38px 44px`，卡片内 padding 20px，列表间隙 10px
- 环境光：`#app` 三层 radial-gradient（accent 光斑 12%/8%/5% 强度），为液态玻璃提供可模糊光源

## 6. Motion & Interaction（GSAP 3）

- **弹簧默认**：`power3.out` 入场；滑块/抽屉 `cubic-bezier(0.16,1,0.3,1)`；无线性缓动
- **页面切换**：内容区 autoAlpha 0→1 + y 10→0（0.35s），同屏关键元素 stagger 0.05s 级联揭示
- **列表揭示**：`.list-item` 入场 y 10 + autoAlpha 0→1，stagger 0.05，延迟 0.1
- **持续微交互（perpetual micro-loops）**：
  - 首页启动按钮：boxShadow 呼吸脉动（1.6s yoyo）
  - 运行状态点：accent 辉光 pulse
  - 进度填充：渐变流动
- **性能**：只动画 `transform` 与 `opacity`（GSAP x/y/scale/autoAlpha），绝不动画 width/height/top/left
- **可及性**：全部动画用 `gsap.matchMedia("(prefers-reduced-motion: no-preference)")` 包裹，偏好减少动态时回退为静态；组件卸载 `mm.revert()` 清理

## 7. Anti-Patterns（Banned）

- 无 emoji（图标一律线性 SVG）
- 无 Inter / 无衬线字体
- 无纯黑 `#000000`（用 `#0b0e13`）
- 无霓虹/外发光阴影（accent 柔和辉光 ≤ 30% 透明度例外）
- 无过饱和 accent（绿色饱和度 < 80%）
- 无大标题渐变文字
- 无自定义鼠标光标
- 无元素重叠——每个元素占独立空间区域
- 无三等分卡片行（列表用堆叠条目）
- 无通用占位名（"Acme/Nexus"）、无假整数（"99.99%"）
- 无 AI 文案套话（"Elevate/Seamless/Unleash/Next-Gen"）
- 无 "Scroll to explore" / 跳动箭头等填充 UI
- 无整页滚动（内容固定窗口内，局部滚动）
- 无 `h-screen`（全高用 `100%` + flex 链）
