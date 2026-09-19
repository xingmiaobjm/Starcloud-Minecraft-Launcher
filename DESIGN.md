# DESIGN.md — Fluent Native（Starcloud 启动器视觉系统，WinUI 3 实现）

> 本文档描述 .NET / WinUI 3 迁移后的视觉系统，是当前视觉世界的 ground truth。
> 旧版基于 Web（Preact + tokens.css）的 Fluent Native 实现已随 `ui/` 一起归档。
> 工艺基准：Microsoft Store + Windows 11 设置（用户指定，见 PRODUCT.md Brand Commitments）。

## 1. 世界

把启动器做成「Windows 11 的第 17 个系统应用」——但不再自绘模拟，而是直接使用
**WinUI 3（Windows App SDK 1.8）** 渲染：与 Microsoft Store、Windows 11 设置同引擎、
同控件库、同动效系统。亚克力材质、圆角、强调色、焦点环、开关与滑块的全部交互
细节由框架原生提供，应用层零 CSS。

拒绝深色电竞风、霓虹、大面积毛玻璃；拒绝 WebView 假原生。

## 2. 窗口与材质（MainWindow.xaml / MainWindow.xaml.cs）

- **亚克力背景**：`SystemBackdrop = new TransparentAcrylicBackdrop()`（`MainWindow.xaml.cs`
  自定义 `SystemBackdrop`，底层 `DesktopAcrylicController`）——比系统默认 Mica 更通透，
  深浅色各一套配方（深色 Tint `#141414` / TintOpacity 0.45 / LuminosityOpacity 0.30；
  浅色 `#FCFCFC` / 0.25 / 0.18），随根元素 `ActualThemeChanged` 自动切换
- **标题栏延伸**：`ExtendsContentIntoTitleBar = true` + `SetTitleBar(AppTitleBar)`——
  48px 高品牌区（FontIcon 星标 + "Starcloud" SemiBold），右上角系统最小化/最大化/关闭按钮
- **主题**：不强制 RequestedTheme，WinUI 默认 `Default`（跟随系统亮/暗），
  全部颜色引用 `ThemeResource`（如 `AccentTextFillColorPrimaryBrush`）；
  设置页可对根元素显式指定 `RequestedTheme` 固定亮/暗
- **强调色**：不跟随系统 accent，由 `App.xaml` 主题字典整体覆盖 `SystemAccentColor`
  与 `AccentFillColor*` / `AccentTextFillColor*`——浅色主题品牌色 **`#FFD322`（黄）**，
  深色主题 **`#3DD322`（绿）**；配套覆盖 `TextFillColor*` / `CardStrokeColorDefaultBrush`
  与 NavigationView 窗格背景，品牌色亮度高故 accent 面上强制黑字
  （`TextOnAccentFillColorPrimaryBrush` = `#E61A1A1A`）

## 3. 导航壳（NavigationView）

- `PaneDisplayMode="LeftCompact"`、`IsPaneOpen="False"`、`OpenPaneLength="232"`——
  默认收起为左侧图标条，点击汉堡按钮或选中项后临时展开；激活项自带 Fluent pill 指示器
  （3px 圆头强调色条）
- 顶部导航：首页（Home）/ 版本 / 存档 / 下载 / 账户 / Nova / 设置
- 底部 Footer：启动器日志（仅一类工具性入口沉底，与 Microsoft Store 下载列表同位）
- 无返回按钮、无内嵌设置项（设置是独立页面）；`IsTitleBarAutoPaddingEnabled="False"`
- 内容区 `Grid Padding="32,12,24,24"` + `Frame`，页面间导航由 `ContentFrame.Navigate` 完成

## 4. 字体与图标

- 字体：Segoe UI Variable（XAML 默认字体栈），中文回退 Microsoft YaHei UI——由系统处理，无需声明
- 字阶沿用 WinUI Type Ramp：Caption 12 / Body 14 / Body Strong / Subtitle 20 / Title 28
- 图标：**Segoe Fluent Icons 字体**（`FontIcon` Glyph），与系统应用完全一致；
  导航字形：首页 E728 / 版本 E81C / 存档 E8B7 / 下载 E896 / 账户 E77B / Nova E99A /
  设置 E713 / 日志 E9F5；另 模组 E71D（下载页分类与主页模组入口）、
  顶栏品牌星标 E728（`BrandBlueBrush` 着色）
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
| 浮窗一句话提醒 | `TeachingTip`（8s 自动收起） |
| 二次确认 | `ContentDialog`（危险操作必须） |
| 列表 | `ListView`（版本/账户/Java） |
| 文本区（日志） | `ScrollViewer` + `TextBlock`（Cascadia Code 12，`IsTextSelectionEnabled`） |

## 6. 布局

- **首页：PCL2 式双栏**——左列为弹性版本列表（欢迎 → 启动自检 → 版本列表 → 快捷入口），
  右列为固定 360px 的账户与启动卡（账户区 → 版本选择 → 强调色主按钮「启动游戏」），
  两列与窗口同高（首页根容器不用 ScrollViewer）；这是与 Microsoft Store
  「左导航 + 内容」骨架对齐的主工作区
- 其余页面：单列内容 + 页头（Title 28 标题 + 描述），设置页为
  分组卡片 + 左标签右控件的行式布局
- **版心**：全部页面根容器统一 `MaxWidth="1280"` 居中铺满——宽窗口下所有页面的
  左边缘落在同一竖线上，窄窗口下贴边自适应
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

## 9. 顶栏右侧入口与 Nova 内嵌（MainWindow.xaml）

标题栏 48px 行右侧并列两个 44×36 的按钮（`Margin="0,0,140,0"` 让位系统 caption 按钮），
均 `Background="Transparent" BorderThickness="0"`，靠内层图形表达：

- **NovaOrb**：内层 32×32 圆角 8 的**实心卡片色**底（`CardBackgroundFillColorDefaultBrush`
  + `CardStrokeColorDefaultBrush` 1px），中央 E99A 字形按状态着色——
  待命 `TextFillColorSecondaryBrush` / 思考 `#7C3AED` / 有提案 `#F59E0B` /
  观察 `#3DD322` / 离线 `TextFillColorDisabledBrush`；`HasPlan` 态右上角叠加 8px 琥珀圆点
- **Nova 面板**：点击 Orb 后 380px 圆角 16 卡片（`LayerFillColorDefaultBrush`）
  水平贴齐 Orb 实际位置（最小留 12px，不越右边界），纵向顶栏下 8px；
  面板内容随登录态切换（离线＝引导登录，已登录＝完整对话，复用 `NovaChatView`）
- **AccountButton（个人中心）**：内层 32×32 圆形（`CardBackgroundFillColorSecondaryBrush`），
  已登录显示昵称首字（SemiBold 13），未登录显示 E77B 人形字形；
  悬浮 ToolTip 为卡片式，展示昵称 / Starcloud ID / 登录态与通道，点击跳账户页
- **浮窗提醒**：`NovaToastTip`（`TeachingTip`，`Target=NovaOrb`，Bottom 放置）承接
  Nova 的一句话提醒，8s 后自动收起

## 10. 调度中心（MainWindow.xaml 右侧任务抽屉）

调度中心回答一个问题：**启动器现在在忙什么、多快**。因此这里的排版原则是
「关键数字当主角」——每行右对齐的那个值（百分比 / 速度 / 结果）比标题更重要。

- **入口**：顶栏右侧 44×36 透明按钮（`TaskCenterBtn`），内层 32×32 圆角 8 的
  `GlassCardBrush` 卡片 + E9F5 字形；运行中任务数 > 0 时右上角叠
  `AccentFillColorDefaultBrush` 徽标（accent 面强制 `TextOnAccentFillColorPrimaryBrush` 黑字加粗）。
  徽标只表示「有几件事在做」，不做动画，避免顶栏一直闪
- **抽屉**：432px 固定宽，`AppPaneBackgroundBrush` 实色面 + 左侧 1px
  `CardStrokeColorDefaultBrush` 描边；左侧遮罩 `#5A000000` 点击即关
- **结构（三段）**：标题行（accent 着色 E9F5 图标 + 「调度中心」17 SemiBold +
  「清除已完成」文本按钮 + 关闭按钮）→ 状态行（11px secondary，如「3 个任务进行中，共 5 条」）
  → 任务列表
- **任务列表 = 两个语义分区**，分区标题 13 SemiBold secondary（「进行中 · N」/「已完成 · N」）：
  - **进行中**：材质卡片（圆角 8、`LayerFillColorDefaultBrush` + 1px 描边、内边距 12/10）。
    行内自上而下：状态点（accent）→ 标题（13 SemiBold）与右对齐关键数字（13 SemiBold）同行 →
    4px 自绘进度条 → 详情（11px secondary）→ 多源贡献带
  - **已完成**：**无卡片的纯文本行**（同一行：状态点 + 标题 + 右对齐结果）。
    活跃的工作「有实体」，历史只是记录，二者不抢注意力
- **状态点 / 进度色只用语义刷**：进行中 `AccentFillColorDefaultBrush`（浅色主题黄、深色主题绿，
  随 `App.xaml` 的 accent 覆盖走）、完成 `SystemFillColorSuccessBrush`、
  失败 `SystemFillColorCriticalBrush`、取消 `TextFillColorDisabledBrush`；
  进度条底槽 `ControlStrongFillColorDefaultBrush`
- **多源竞速的「贡献带」（本面板唯一的记忆点）**：一根 6px 高、按各源字节占比分段的横条
  （段间 3px 间隙，圆角只给首尾两段），**领先源实心品牌 accent，其余中性色按名次递减透明度**
  （1 → 0.85 → 0.70 → 0.55…，下限 0.35）；下方图例逐源列出「色块 + 源名 + 体积 · 占比」。
  一眼看出这一票是谁在扛；只有一个源时退化回普通进度条（没有可比性就不画）
- **进度条自绘**：两列 Star 网格改列宽，不做数值动画——框架 `ProgressBar` 的值变化自带缓动，
  300ms 刷新节奏下动画永远追不上，会表现为进度条抽搐；不确定进度时才用框架流动条
- **动效**：抽屉入场用框架 `EntranceThemeTransition`（右侧 32px 位移），
  由 `MotionService` 开关控制（关闭动效时不挂任何转场）；系统「显示动画」关闭时自动不播
- **键盘与无障碍**：打开时焦点落到关闭按钮，关闭时焦点归还入口按钮；
  `TabFocusNavigation="Cycle"` 把 Tab 锁在抽屉内；Esc 经 `KeyboardAccelerator` 关闭
  （不依赖焦点位置）；每行 `AutomationProperties.Name` = 任务名，HelpText = 当前进度或结果
