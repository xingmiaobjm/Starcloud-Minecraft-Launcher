# DESIGN.md — Fluent Native（Starcloud 启动器视觉系统，WinUI 3 实现）

> 本文档描述 .NET / WinUI 3 迁移后的视觉系统，是当前视觉世界的 ground truth。
> 旧版基于 Web（Preact + tokens.css）的 Fluent Native 实现已随 `ui/` 一起归档。
> 工艺基准：Microsoft Store + Windows 11 设置（用户指定，见 PRODUCT.md Brand Commitments）。

## 1. 世界

把启动器做成「Windows 11 的第 17 个系统应用」——但不再自绘模拟，而是直接使用
**WinUI 3（Windows App SDK 1.8）** 渲染：与 Microsoft Store、Windows 11 设置同引擎、
同控件库、同动效系统。Mica、圆角、强调色、焦点环、开关与滑块的全部交互
细节由框架原生提供，应用层零 CSS。

拒绝深色电竞风、霓虹、大面积毛玻璃；拒绝 WebView 假原生。

## 2. 窗口与材质（MainWindow.xaml / MainWindow.xaml.cs）

- **Mica 背景**：`SystemBackdrop = new MicaBackdrop()`——桌面壁纸衍生的分层表面，
  深浅色自动跟随系统
- **标题栏延伸**：`ExtendsContentIntoTitleBar = true` + `SetTitleBar(AppTitleBar)`——
  48px 高品牌区（FontIcon 星标 + "Starcloud" SemiBold），右上角系统最小化/最大化/关闭按钮
- **主题**：不强制 RequestedTheme，WinUI 默认 `Default`（跟随系统亮/暗），
  全部颜色引用 `ThemeResource`（如 `AccentTextFillColorPrimaryBrush`）
- **强调色**：Windows 系统强调色唯一 accent（系统设置 → 个性化 → 颜色），
  由 XAML 主题资源直接下发，应用不自定义色板

## 3. 导航壳（NavigationView）

- `PaneDisplayMode="Left"`、`IsPaneOpen="True"`、`OpenPaneLength="232"`——
  **常驻展开的 Store 式侧栏**，激活项自带 Fluent pill 指示器（3px 圆头强调色条）
- 顶部导航：首页（Home）/ 下载 / 模组资源 / 账户 / 设置
- 底部 Footer：启动器日志（仅一类工具性入口沉底，与 Microsoft Store 下载列表同位）
- 无返回按钮、无内嵌设置项（设置是独立页面）
- 内容区 `Grid Padding="24,12,24,24"` + `Frame`，页面间导航由 `ContentFrame.Navigate` 完成

## 4. 字体与图标

- 字体：Segoe UI Variable（XAML 默认字体栈），中文回退 Microsoft YaHei UI——由系统处理，无需声明
- 字阶沿用 WinUI Type Ramp：Caption 12 / Body 14 / Body Strong / Subtitle 20 / Title 28
- 图标：**Segoe Fluent Icons 字体**（`FontIcon` Glyph），与系统应用完全一致；
  常用字形：首页 E728 / 下载 E896 / 模组 E71D / 账户 E77B / 设置 E713 / 日志 E9F5
- 应用图标：`Assets/AppIcon.ico`（`AppWindow.SetIcon`）

## 5. 控件体系

全部使用 WinUI 3 原生控件，无自定义视觉：

| 场景 | 控件 |
| --- | --- |
| 启动主按钮 | `Button`（Style="AccentButtonStyle"，强调色填充） |
| 版本/账户选择 | `ComboBox` |
| 内存/并发设置 | `NumberBox` / `Slider` |
| 开关项（内置 JVM 优化等） | `ToggleSwitch` |
| 下载进度 | `ProgressBar`（不确定态用 `ProgressRing`） |
| 路径选择 | `TextBox` + `Button`（文件/文件夹对话框） |
| 提示与错误 | `InfoBar` |
| 二次确认 | `ContentDialog`（危险操作必须） |
| 列表 | `ListView`（版本/账户/Java） |
| 文本区（日志） | `TextBox`（IsReadOnly + 等宽 Cascadia Code） |

## 6. 布局

- **首页：PCL2 式左卡布局**——左列固定宽度启动面板（账户区 → 版本选择 →
  强调色主按钮「启动游戏」），右列为内容辅助区；这是与 Microsoft Store
  「左导航 + 内容」骨架对齐的主工作区
- 其余页面：单列内容 + 页头（Title 28 标题 + 描述），设置页为
  分组卡片 + 左标签右控件的行式布局
- 窗口缩放：XAML 布局自适应，NavigationView 在窄宽度自动收窄为图标模式

## 7. 动效

全部由 WinUI 原生提供，应用不写动画代码：

- 页面切换：Frame 默认 NavigationThemeTransition
- 控件按压/hover：Fluent 标准按压变形与强调色高亮
- pill 指示器入场、Expander 展开、ContentDialog 缩放入场均为框架内置
- 系统动画缩放（设置 → 辅助功能 → 视觉效果）自动生效

## 8. 人性化承诺（实现落点）

- 错误以 InfoBar（Error 级别）就地呈现，中文文案
- 删除版本 / 删除账户 / 删除 Java 等危险操作必须经 `ContentDialog` 二次确认
- 加载失败 ≠ 空列表：InfoBar 报错并提供重试按钮
- 键盘可达：WinUI 控件天然完整支持 Tab / 方向键 / Enter / Esc 与焦点环
- 文案简体中文；界面禁 emoji
