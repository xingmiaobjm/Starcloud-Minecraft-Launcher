<!-- 【已归档 · 2026-09-06】本规范为旧「机场导视」设计方向，已被根目录 DESIGN.md
     （WinUI 3 Fluent Native 视觉系统）取代，仅作历史参考。 -->

---
name: Starcloud Minecraft Launcher
description: 现代化 Minecraft 启动器 — 机场导视设计语言，白昼航站楼与夜间航站楼双主题
colors:
  signage-yellow: "#ffc400"
  signage-yellow-deep: "#f0b600"
  signage-yellow-ink: "#17191f"
  signage-yellow-soft: "rgba(255, 196, 0, 0.30)"
  surface-canvas: "#f4f5f7"
  surface-panel: "#ffffff"
  surface-panel-2: "#ffffff"
  surface-panel-3: "#f7f8fa"
  glass-canvas: "rgba(244, 245, 247, 0.62)"
  glass-panel: "rgba(255, 255, 255, 0.76)"
  text-primary: "#17191f"
  text-dim: "#4c535e"
  text-faint: "#59606b"
  hairline: "rgba(23, 25, 31, 0.10)"
  hairline-strong: "rgba(23, 25, 31, 0.22)"
  danger: "#e5484d"
  danger-soft: "rgba(229, 72, 77, 0.08)"
  warn: "#e8871e"
  warn-soft: "rgba(232, 135, 30, 0.10)"
  success: "#17a34a"
  success-soft: "rgba(23, 163, 74, 0.10)"
  skylight: "rgba(255, 196, 0, 0.035)"
typography:
  display:
    fontFamily: "'Hanken Grotesk', 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif"
    fontSize: "26px"
    fontWeight: 800
    lineHeight: 1.15
    letterSpacing: "-0.02em"
  headline:
    fontFamily: "'Hanken Grotesk', 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif"
    fontSize: "20px"
    fontWeight: 800
    lineHeight: 1.15
    letterSpacing: "-0.02em"
  title:
    fontFamily: "'Hanken Grotesk', 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 700
    lineHeight: 1.15
  body:
    fontFamily: "'Hanken Grotesk', 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "'Hanken Grotesk', 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 600
    lineHeight: 1.5
  eyebrow:
    fontFamily: "'Hanken Grotesk', 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif"
    fontSize: "11px"
    fontWeight: 600
    lineHeight: 1.15
    letterSpacing: "0.14em"
  mono:
    fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.5
rounded:
  window: "8px"
  outer: "18px"
  inner: "14px"
  card: "16px"
  sm: "10px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "20px"
  2xl: "24px"
  3xl: "32px"
components:
  button-primary:
    backgroundColor: "{colors.signage-yellow}"
    textColor: "{colors.signage-yellow-ink}"
    typography: "{typography.label}"
    rounded: "{rounded.sm}"
    padding: "8px 16px"
  button-primary-hover:
    backgroundColor: "{colors.signage-yellow-deep}"
    textColor: "{colors.signage-yellow-ink}"
  button-secondary:
    backgroundColor: "{colors.surface-panel}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.sm}"
    padding: "8px 16px"
  button-secondary-hover:
    backgroundColor: "{colors.surface-panel-3}"
  button-danger:
    textColor: "{colors.danger}"
    rounded: "{rounded.sm}"
    padding: "8px 16px"
  card:
    backgroundColor: "{colors.surface-panel}"
    rounded: "{rounded.card}"
    padding: "20px 24px"
  input:
    backgroundColor: "{colors.surface-panel}"
    textColor: "{colors.text-primary}"
    rounded: "8px"
    padding: "9px 13px"
  nav-item-active:
    backgroundColor: "{colors.signage-yellow}"
    textColor: "{colors.signage-yellow-ink}"
    rounded: "{rounded.sm}"
---

# Design System: Starcloud Minecraft Launcher

## Overview

**Creative North Star: "Departures 起航"**

Starcloud Minecraft Launcher 的视觉系统是一个机场航站楼的导视世界——启动器是一段"出发"航程。玩家打开即懂"我要去哪个版本、怎么起飞"：版本号是登机口巨号，启动按钮是黄色指路牌，状态栏是航班信息条，正版/离线是翻牌式分段控件。

整个系统建立在唯一的视觉语法之上：**导视黄只负责指路**（启动、激活、进度），其余全部中性——云白表面、墨黑文字、发丝线。拒绝玻璃、拒绝霓虹、拒绝圆润小卡片堆叠。这是一个干净、克制、功能至上的 Operate 模式界面：品牌活在精确的细节中，而非装饰。

支持双主题：**白昼航站楼**（默认浅色）与**夜间航站楼**（深色）。浅色主题以云白画布 `#f4f5f7` 为基底，白色面板承载内容；深色主题转为近黑基底 `#12141a`，导视黄在夜间如同灯箱，保持同样的亮度和指路能力。窗口壳和侧栏使用半透明毛玻璃表面，透出 DWM Acrylic 底层纹理。

**Key Characteristics:**
- 单一饱和色（导视黄 `#ffc400`），仅用于指路操作，永远以黑字登台
- 云白平面卡片 + 发丝线边框 + 轻阴影，面板不透明，无毛玻璃无 blur
- 窗口壳/侧栏/标题栏允许半透明毛玻璃，透出系统 DWM Acrylic
- 双主题：白昼航站楼（浅色）与夜间航站楼（深色），通过 `data-theme` 切换
- 状态色（绿/琥珀/红）仅作信号点，不参与品牌
- 柔和圆角系统（8px–18px），触感温暖而非机械
- 8 级字号 Token 系统（`--text-2xs` 至 `--text-2xl`），排版角色统一
- 三次方缓动曲线（`power3.easeIn` / `power3.easeOut`）替代弹性缓动，适合操作类界面
- 所有文本使用 Hanken Grotesk + JetBrains Mono 等宽字体

## Colors

基于功能角色命名的中性调色板，导视黄是唯一饱和色。颜色值以 CSS 变量定义在 `styles.css` 的 `:root` 和 `[data-theme="dark"]` 中。

### Primary
- **Signage Yellow** (`#ffc400`): 唯一 accent——启动按钮、激活导航项、进度填充、品牌块。永远以黑色文字登台。仅用于"指路"：告诉玩家下一步该去哪里。
- **Signage Yellow Deep** (`#f0b600`): 主按钮 hover 状态，略深以提供按压反馈。深色主题下为 `#ffd23d`（更亮以补偿暗背景）。
- **Signage Yellow Ink** (`#17191f`): 黄色面板上的文字颜色，必须为深色以保证对比度。
- **Signage Yellow Soft** (`rgba(255, 196, 0, 0.30)`): 焦点环颜色，用于 `focus-visible` 状态。深色主题下为 `rgba(255, 196, 0, 0.25)`。

### Neutral
- **Surface Canvas** (`#f4f5f7`): 窗口级画布底色，云白基调。深色主题下为 `#12141a`。
- **Surface Panel** (`#ffffff`): 卡片、面板、侧栏、状态栏——主要承载面。深色主题下为 `#191c23`。
- **Surface Panel 2** (`#ffffff`): 浮层、抽屉、输入框背景（浅色模式下与 Panel 同色）。深色主题下为 `#1d2129`。
- **Surface Panel 3** (`#f7f8fa`): hover 时面板提升色，微妙的暖灰区别。深色主题下为 `#232833`。
- **Glass Canvas** (`rgba(244, 245, 247, 0.62)`): 窗口壳毛玻璃画布，透出 DWM Acrylic。深色主题下为 `rgba(18, 20, 26, 0.62)`。
- **Glass Panel** (`rgba(255, 255, 255, 0.76)`): 侧栏/标题栏毛玻璃面板。深色主题下为 `rgba(25, 28, 35, 0.76)`。
- **Text Primary** (`#17191f`): 主文本，墨黑色。深色主题下为 `#f2f4f8`。
- **Text Dim** (`#4c535e`): 次级文本，描述、标签、元信息。深色主题下为 `#b7becb`。
- **Text Faint** (`#59606b`): 三级文本，占位符、辅助信息、计数器。深色主题下为 `#9ca3af`。
- **Hairline** (`rgba(23, 25, 31, 0.10)`): 发丝线——卡片边框、分隔线、侧栏分割。深色主题下透明度为 `0.09`。
- **Hairline Strong** (`rgba(23, 25, 31, 0.22)`): 强调线——hover 边框、开关边框、输入框焦点边框。深色主题下透明度为 `0.18`。

### Status
- **Danger** (`#e5484d`): 错误/危险——仅用于文本和细边框，禁止大块填充。深色主题下为 `#ff6b76`。
- **Danger Soft** (`rgba(229, 72, 77, 0.08)`): 错误提示背景。深色主题下为 `rgba(255, 107, 118, 0.10)`。
- **Warn** (`#e8871e`): 进行中/警告状态——安装中信号点、启动前自检提示。深色主题下为 `#f5a63c`。
- **Warn Soft** (`rgba(232, 135, 30, 0.10)`): 警告提示背景。深色主题下为 `rgba(245, 166, 60, 0.12)`。
- **Success** (`#17a34a`): 运行中状态点、已安装标记。仅状态语义，非品牌 accent。深色主题下为 `#4ade80`。
- **Success Soft** (`rgba(23, 163, 74, 0.10)`): 成功提示背景。深色主题下为 `rgba(74, 222, 128, 0.12)`。

### Ambient
- **Skylight** (`rgba(255, 196, 0, 0.035)`): 航站楼天光——极弱的暖色 radial-gradient，为窗口壳提供微温的呼吸感，随圆角裁剪。深色主题下为 `rgba(255, 196, 0, 0.05)`。

### Named Rules
**The Signage-Only Rule.** 导视黄 `#ffc400` 仅用于"指路"操作——启动按钮、激活导航、进度填充、品牌标识。不得用于装饰、不得用于非交互元素、不得用于普通文本。其稀缺性就是它的力量。

**The Black-on-Yellow Rule.** 任何黄色面板上的文字必须为黑色（`#17191f`）。黄色按钮上的白色文字是不可接受的。

**The No-Glass Rule.** 所有内容面板均为不透明平面。无毛玻璃、无 backdrop-blur、无半透明叠加。深度由阴影和边框传达，而非透明度。**唯一例外：** 模态遮罩层（`.modal-overlay`）允许 `backdrop-filter: blur(3px)`，因其为临时浮层覆盖整个窗口，不属于面板范畴，且需要模糊背景来建立操作焦点。窗口壳/侧栏/标题栏的表面允许半透明毛玻璃以透出系统 DWM Acrylic 底层纹理——这些是窗口框架层，不是内容面板。

## Typography

**Display Font:** Hanken Grotesk (with Segoe UI / Microsoft YaHei fallback)
**Mono Font:** JetBrains Mono (with Cascadia Code / Consolas fallback)

**Character:** Hanken Grotesk 的几何骨架提供清晰、现代、无衬线的阅读体验，与机场导视的理性气质一致。JetBrains Mono 用于所有需要等宽对齐的数据场景（版本号、进度百分比、日志、时间戳），全部启用 `tabular-nums`。

排版系统使用 8 级字号 Token，分别对应 CSS 变量 `--text-2xs` 至 `--text-2xl`，配合行高、字距、字重的语义 Token。

### Hierarchy
- **Display** (`--text-2xl`: 26px, `--weight-extrabold`: 800, `--leading-tight`: 1.15, `--tracking-tight`: -0.02em): 页面标题（`.page-title`）。每页只有一个。
- **Headline** (`--text-xl`: 20px, `--weight-extrabold`: 800, `--leading-tight`: 1.15): 区域标题、模组详情标题。登机口巨号。
- **Title** (`--text-lg`: 16px, `--weight-bold`: 700, `--leading-tight`: 1.15): 强调文本、按钮文字、启动按钮。
- **Body** (`--text-md`: 14px, `--weight-normal`: 400, `--leading-normal`: 1.5): 正文、默认。页面描述最大宽度 640px。
- **Label** (`--text-base`: 13px): 控件标签、通知消息、输入框、下拉选择框。
- **Small** (`--text-sm`: 12px, `--weight-semibold`: 600, `--leading-normal`: 1.5): 字段标签、提示文本、元数据、次要信息。
- **Eyebrow** (`--text-xs`: 11px, `--weight-semibold`: 600, `--tracking-widest`: 0.14em, uppercase): 导航分区标签、页面副标题、时间戳。
- **Micro** (`--text-2xs`: 10px): 微标签、徽章、极细辅助信息。

### Line Height Tokens
- `--leading-tight: 1.15` — 标题
- `--leading-snug: 1.35` — 紧凑正文
- `--leading-normal: 1.5` — 正文
- `--leading-relaxed: 1.6` — 描述、提示

### Tracking Tokens
- `--tracking-tight: -0.02em` — 大标题
- `--tracking-normal: 0` — 正文
- `--tracking-wide: 0.04em` — 标签
- `--tracking-wider: 0.08em` — 小标签
- `--tracking-widest: 0.14em` — 眉题

### Weight Tokens
- `--weight-normal: 400`
- `--weight-medium: 500`
- `--weight-semibold: 600`
- `--weight-bold: 700`
- `--weight-extrabold: 800`

### Named Rules
**The Token-Only Rule.** 所有排版属性必须通过 Token 变量引用（`--text-*` / `--leading-*` / `--tracking-*` / `--weight-*`），禁止硬编码 `px`/`em` 值。仅一次性展示元素（品牌名、登机牌巨号、版本号）允许直接使用数值。

## Layout

**Shell Structure.** 窗口自绘壳（`.window-shell`）由三层组成：标题栏（44px 固定高度）→ 主体（侧栏 + 主区）→ 状态栏（38px 固定高度）。侧栏默认收起为 64px 图标列，鼠标悬停展开至 196px。

**Content Area.** 主区内容层（`.content`）内边距 24px 32px 32px，垂直滚动。每页顶部为 `.page-title`（margin-bottom: 8px）+ `.page-desc`（margin-bottom: 24px），之后是卡片和列表。

**Card Grid.** 卡片（`.card`）以 18px 间距垂直堆叠。设置页面使用 `.settings-page` 容器统一 `gap: 18px`。卡片内部区块标题（`.section-title`）距下方内容 14px。卡片内分区使用 `.card-divider` 分隔线（`margin: 16px 0; border-top`）。

**List Rhythm.** 列表项（`.list-item`）间距 8px（`.list` gap），每项内边距 14px 18px。大版本分组（`.dl-group`）间距 12px。

**Field Layout.** 水平字段行使用 `.field-row`（`display: flex; gap: 10px; align-items: center`），双列字段使用 `.field-pair`（`flex: 1; min-width: 150px`），换行字段行使用 `.field-row-wrap`（`flex-wrap: wrap; gap: 20px`）。

**Spacing Scale.** 基础间距系统：4px / 8px / 12px / 14px / 16px / 18px / 20px / 24px / 32px。元素间距优先使用 8px 的倍数，内部 padding 使用 14–18px。

**Mods Page.** 工具栏主行：分类标签 + 搜索输入框 + 搜索按钮 + "筛选"切换按钮。高级筛选面板可折叠，使用 `grid-template-rows: 0fr → 1fr` 动画。详情视图与列表并排显示，占满右侧空间。

## Elevation & Depth

系统采用**平面层叠**策略：内容面板均为不透明平面，深度由发丝线边框 + 轻阴影传达。窗口壳/侧栏/标题栏使用半透明毛玻璃透出系统 DWM Acrylic 底层纹理——这是窗口框架层的特权，内容面板不得使用。

### Shadow Vocabulary
- **Ambient** (`--shadow-1`: `0 1px 2px rgba(20, 23, 28, 0.06)`): 默认按钮、卡片的基础阴影。极轻，仅提供微弱的"脱离画布"感。
- **Hover Lift** (`--shadow-2`: `0 8px 24px rgba(20, 23, 28, 0.08)`): 列表项 hover 时提升，配合 `translateY(-1px)` 产生悬浮感。
- **Elevated** (`--shadow-3`: `0 20px 48px rgba(20, 23, 28, 0.12)`): 弹出层、下拉菜单等高层级阴影。
- **Card** (`--card-shadow`: `0 1px 2px rgba(20, 23, 28, 0.05), 0 8px 24px rgba(20, 23, 28, 0.06)`): 卡片默认双层阴影。
- **Drawer** (`--drawer-shadow`: `0 28px 64px rgba(20, 23, 28, 0.16), 0 2px 8px rgba(20, 23, 28, 0.08)`): 抽屉最强阴影，明确与主界面的层级分离。

### Z-Index Scale
- `--z-shell: 1` — 窗口壳
- `--z-overlay: 90` — 模态遮罩层
- `--z-drawer: 95` — 抽屉/设置面板

### Named Rules
**The Flat-By-Default Rule.** 表面在静止时是平的。阴影仅作为状态响应出现（hover 提升、drawer 浮层）。无阴影的面板通过发丝线边框与画布区分。

**The No-Glass-on-Content Rule.** 内容面板为不透明平面。禁止 `backdrop-filter: blur()`、禁止半透明背景叠加。深度由阴影和边框传达，而非透明度。**唯一例外：** 模态遮罩层（`.modal-overlay`）允许 `backdrop-filter: blur(3px)`。窗口壳/侧栏/标题栏允许半透明毛玻璃。

## Shapes

**Corner Strategy.** 系统使用柔和圆角，从紧致的 8px（窗口边缘、输入框）到舒适的 18px（外层容器）。圆角体系按层级递进：窗口壳 8px → 外层面板 18px → 内层面板 14px → 卡片 16px → 按钮/控件 10px。

**Border Language.** 所有面板、卡片、列表项使用 1px 发丝线边框（`var(--hairline)`，10% 透明度）。启动面板特殊处理：2px 墨黑外框 + 7px 黄色顶带（"登机牌"识别特征）。

**Button Shape.** 按钮使用 `--radius-sm`（10px），非全圆角也非直角。主按钮带黄色辉光投影（`0 6px 16px rgba(255, 196, 0, 0.28)`），hover 时辉光增强 + 上移 1px。

**Tag Shape.** 标签使用全圆角胶囊（`border-radius: 999px`），内边距 2px 8px。已安装标签使用绿色柔化背景。

**Switch Shape.** 翻牌式开关，46×26px 全圆角胶囊，内嵌 18px 圆形滑块。激活态为导视黄，滑块右移 20px 变为墨黑色。

## Components

### Buttons

**Character:** 按钮像机场指路牌——清晰、可触、即时反馈。使用 `--radius-sm`（10px）圆角，内边距 8px 16px。所有按钮共享 `.btn` 基类，变体通过 `.btn-primary` / `.btn-danger` 叠加。

- **Primary (`.btn-primary`):** 导视黄背景（`--signage`）+ 墨黑文字（`--signage-ink`），字重 `--weight-extrabold`，无边线。带黄色辉光投影（`0 6px 16px rgba(255, 196, 0, 0.28)`）。Hover 时背景加深至 `--signage-2`，上移 1px，辉光扩大至 `0 8px 20px`。
- **Secondary (`.btn`):** 白色面板背景 + 发丝线边框 + 墨黑文字。Hover 时背景提升至 `--panel-3`，边框加深至 `--hairline-strong`。
- **Danger (`.btn-danger`):** 透明背景 + 红色文字（`--danger`）+ 红色半透明边框（`rgba(229, 72, 77, 0.35)`）。Hover 时显示红色柔化背景（`--danger-soft`），边框加深至 `rgba(229, 72, 77, 0.55)`。
- **Focus:** 所有按钮 `focus-visible` 使用 3px 黄色柔化焦点环（`--signage-soft`）。
- **Disabled:** 整体透明度 45%，禁止点击，禁止指针事件。
- **Double-Submit Prevention:** 操作期间按钮通过 `busy` / `saving` 状态禁用，防止重复提交。

### Cards

**Character:** 航站楼信息板——白底、发丝线收边、轻阴影，承载结构化内容。使用 `contain: layout style` 隔离布局。

- **Shape:** 16px 圆角（`--radius-card`），1px 发丝线边框。
- **Background:** 纯白面板（`--panel`）。
- **Shadow:** 双层卡片阴影（`--card-shadow`）。
- **Internal Padding:** 20px 24px。
- **Section Title:** 13px 700 字重（`--text-base` / `--weight-bold`），距内容 14px（`margin-bottom`）。
- **Card Divider:** 卡片内分区使用 `.card-divider`，`margin: 16px 0; border-top: 1px solid var(--hairline)`。

### Inputs / Fields

**Character:** 干净、克制的输入控件，与卡片视觉一致。

- **Style:** 白色背景 + 1px 发丝线边框 + 8px 圆角，内边距 9px 13px。
- **Font:** 13px（`--text-base`）Hanken Grotesk，等宽数字使用 JetBrains Mono。
- **Focus:** 边框加深至 `--hairline-strong` + 3px 黄色柔化焦点环（`--signage-soft`）。
- **Placeholder:** `--text-faint` 颜色。
- **Field Label:** 12px（`--text-sm`）600 字重（`--weight-semibold`），灰色（`--text-dim`）。
- **Input Constraints:** 玩家名输入框 `maxLength={16}`，插件源输入框 `maxLength={2048}`。

### Switch

**Character:** 翻牌式开关，激活态为导视黄。

- **Shape:** 46×26px 全圆角胶囊（`border-radius: 999px`），内嵌 18px 圆形滑块（`.switch-knob`）。
- **Off:** 中性灰背景（`rgba(var(--rgb-ink), 0.08)`）+ 发丝线强边框（`--hairline-strong`）。
- **On:** 导视黄背景（`--signage`）+ 滑块右移 20px（`translateX(20px)`），滑块变为墨黑色（`--signage-ink`）。
- **Transition:** 三次方缓出（`--ease-out-cubic`），背景 0.25s，滑块 0.3s。
- **Focus:** `focus-visible` 使用 3px 黄色柔化焦点环。

### Tags / Chips

**Character:** 胶囊标签，用于版本标识、安装状态、平台标记。

- **Style (`.tag`):** 全圆角胶囊（`border-radius: 999px`），内边距 2px 8px，11px（`--text-xs`）600 字重（`--weight-semibold`），字距 0.04em（`--tracking-wide`）。
- **Default:** 墨色 6% 透明背景 + 发丝线边框 + 灰色文字（`--text-dim`）。
- **Installed (`.tag.installed`):** 绿色柔化背景（`--ok-soft`）+ 绿色边框（`rgba(23, 163, 74, 0.3)`）+ 绿色文字（`--ok`）。
- **Accent (`.tag.tag-accent`):** 黄色柔化背景（`--signage-soft`）+ 黄色边框（`rgba(255, 196, 0, 0.45)`）+ 墨黑文字（`--signage-ink`）。

### List Items

**Character:** 可点击的版本行 / 模组行 / 账户行，hover 时有悬浮提升效果。使用 `contain: layout style` 隔离布局。

- **Shape (`.list-item`):** 10px 圆角（`--radius-sm`），1px 发丝线边框，内边距 14px 18px。
- **Background:** 白色面板（`--panel`）。
- **Hover:** 背景提升至 `--panel-3`，上移 1px（`translateY(-1px)`），阴影加深至 `--shadow-2`，边框加深至 `--hairline-strong`。
- **Layout:** flex 行布局，`justify-content: space-between`，gap 14px。
- **Primary Text:** 14px（`--text-md`）600 字重（`--weight-semibold`），行高 1.5。
- **Secondary Text:** 12px（`--text-sm`），灰色（`--text-dim`）。

### Navigation

**Character:** 侧栏导航，激活项为黄色指路牌——黑字黄底，与其余中性项形成强烈对比。

- **Sidebar:** 毛玻璃面板（`--glass-panel`），64px 收起 / 196px 展开，右侧 1px 发丝线。展开/收起使用三次方缓出（`--ease-out-cubic`）0.28s。
- **Nav Item (`.nav-item`):** 10px 圆角（`--radius-sm`），内边距 9px 12px，gap 11px（图标+文字）。
- **Default:** 透明背景 + 灰色文字（`--text-dim`）。Hover 时 5% 墨色背景 + 文字变深。
- **Active:** 导视黄背景（`--signage`）+ 墨黑文字（`--signage-ink`），字重 `--weight-bold`。图标描边加粗。
- **Brand Block (`.brand`):** 顶部 38px 黄色方块（`--signage`）+ 10px 圆角，带黄色辉光投影。文字为品牌名（15.5px 800）+ 副标题（11px 大写）。

### Drawer

**Character:** 白色浮层面板，从右侧滑入，带强投影和发丝线边框。

- **Shape:** 16px 圆角（`--radius-card`），1px 强发丝线边框（`--hairline-strong`），强投影（`--drawer-shadow`）。
- **Position:** 固定定位，top 52px / right 10px / bottom 10px，宽度 `min(380px, 84%)`，z-index `--z-drawer`。
- **Header (`.drawer-head`):** 18px 20px 内边距，底部发丝线分隔，标题 15px 700 字重。
- **Body (`.drawer-body`):** 16px 18px 20px 内边距，可滚动。
- **Entry Animation:** 从右侧滑入（`translateX(100%)` → 0），三次方缓出（`--ease-out-cubic`）0.3s。

### Modal

**Character:** 居中浮层，用于确认操作、展示信息。遮罩层是系统唯一允许 `backdrop-filter: blur()` 的组件。

- **Overlay (`.modal-overlay`):** 固定全屏，`rgba(10, 12, 16, 0.42)` 半透明黑色背景 + `backdrop-filter: blur(3px)`，z-index `--z-overlay`。淡入动画 0.18s。
- **Modal (`.modal`):** 宽度 `min(420px, calc(100vw - 48px))`，18px 圆角（`--radius-outer`），1px 发丝线边框，强投影（`--drawer-shadow`），内边距 22px，可滚动。入场动画使用三次方缓出（`--ease-out-cubic`）0.22s。

### Progress Bar

**Character:** 黄色进度条，用于下载/安装进度。

- **Track (`.progress-bar`):** 8px 高，2px 圆角，墨色 8% 透明背景。
- **Fill (`.progress-fill`):** 导视黄背景，带流动斜纹动画（`repeating-linear-gradient`）。
- **Info (`.progress-head`):** 上方显示阶段名 + 百分比（等宽 `--weight-bold` 字重），下方显示速度/ETA/已下载（等宽 `--text-xs` 灰色）。

### Launch Panel (Signature)

**Character:** 登机牌造型的启动面板——墨黑外框 + 黄色顶带 + 白色芯，是系统的标志性组件。

- **Shape:** 18px 圆角（`--radius-outer`），2px 墨黑外框（`--text`），7px 黄色顶带（`--signage`）。
- **Size:** 宽度 `min(440px, 46%)`，高度 `min(100%, 640px)`。
- **Internal:** 内边距 28px 26px，内部 gap 18px。
- **Onboard Steps:** 三步启动指引（安装版本 → 配置账户 → 启动游戏），当前步骤高亮为导视黄圆形数字 + 发光环，其余灰色淡化。级联延迟入场动画，三次方缓出 0.35s。

### Mod Detail View

**Character:** 模组详情视图，与列表并排显示，占满右侧空间。

- **Layout:** `flex: 1; min-height: 0; display: flex; flex-direction: column; gap: 16px`。
- **Entry Animation:** 水平滑入（`translateX(16px)` → 0）+ 淡入，三次方缓出（`--ease-out-cubic`）0.22s。
- **Exit Animation:** 水平滑出（0 → `translateX(16px)`）+ 淡出，三次方缓入（`--ease-in-cubic`）0.2s。
- **Performance:** 使用 `contain: layout style` + `will-change: transform` 隔离并预提升到合成层。

### Mod Hit Row

**Character:** 模组搜索结果行，可点击展开详情。

- **Layout:** flex 行，图标 48px + 主信息区 + 右侧 chevron。
- **Hover:** 上移 1px + 阴影提升 + 边框加深 + chevron 右移 2px。
- **Performance:** 使用 `contain: layout style` 隔离布局。
- **Combo Files:** 展开后文件列表使用 `content-visibility: auto` + `contain-intrinsic-size: 48px` 跳过屏外渲染。

## Do's and Don'ts

### Do:
- **Do** 将导视黄仅用于指路操作——启动按钮、激活导航、进度填充、品牌标识。
- **Do** 黄色面板上的文字始终使用墨黑色（`#17191f`）。
- **Do** 使用发丝线边框（`rgba(23, 25, 31, 0.10)`）而非实色边框来分隔面板。
- **Do** 使用 `--radius-sm`（10px）作为按钮和控件的默认圆角。
- **Do** 所有排版属性通过 Token 变量引用（`--text-*` / `--leading-*` / `--tracking-*` / `--weight-*`）。
- **Do** 版本号等数据使用 JetBrains Mono 等宽字体 + `tabular-nums`。
- **Do** 状态色（绿/琥珀/红）仅用于状态信号点，不参与品牌装饰。
- **Do** 卡片使用 20px 24px 内边距，区块标题 14px 间距。
- **Do** 所有 `focus-visible` 使用 3px 黄色柔化焦点环（`--signage-soft`）。
- **Do** 使用 `contain: layout style` 隔离独立卡片和列表行。
- **Do** 长列表使用 `content-visibility: auto` 跳过屏外渲染。
- **Do** 操作期间按钮通过 `busy` / `saving` 状态禁用，防止重复提交。
- **Do** 入场动画使用 `--ease-out-cubic`（三次方缓出），退场动画使用 `--ease-in-cubic`（三次方缓入）。

### Don't:
- **Don't** 在非指路场景使用导视黄——不用于装饰、不用于普通文本、不用于非交互元素。
- **Don't** 在内容面板上使用毛玻璃效果（`backdrop-filter: blur()`）——模态遮罩层（`.modal-overlay`）是唯一例外。
- **Don't** 使用渐变文字。强调由字重和大小传达。
- **Don't** 在卡片上使用超过 1px 的彩色左边框或顶边框（启动面板的 7px 黄色顶带是该规则的唯一例外）。
- **Don't** 混合暖色和冷色灰色——所有中性色保持一致的墨色基调。
- **Don't** 使用 Inter、Roboto、Arial 或衬线字体。正文为 Hanken Grotesk，等宽为 JetBrains Mono。
- **Don't** 在非代码/数据场景使用等宽字体作为装饰。
- **Don't** 嵌套卡片。一层卡片足以承载内容。
- **Don't** 硬编码排版属性——使用 Token 变量。
- **Don't** 使用 `--ease-spring` 弹性缓动作为操作类动画——仅保留给呼吸动画（状态点脉动）。