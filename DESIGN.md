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
- **版本方块图标取用顺序**（`VersionRowHelpers.DisplayIconPath`）：自定义图标（`starcloud/icons/`）→
  加载器图标（neoforge / forge / fabric / quilt）→ 版本类型图标（正式版草方块 / 快照紫水晶 /
  远古版圆石与泥土）。加载器判定与类型标签 `DetectKind` **同源**：先看版本 JSON 里的加载器痕迹，
  再看版本 id 关键字兜底——只看 id 会让整合包/自建实例（名字与加载器无关）错落到原版草方块；
  原版类型读版本 JSON 的根 `type` 字段，不靠文本子串猜。

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

- **首页：PCL2 式双栏**——左列为版本库（启动自检 → **已安装版本** → 弹性留白 → 快捷入口），
  右列为固定 360px 的账户与启动卡（账户区 → 版本选择 → 强调色主按钮「启动游戏」），
  两列与窗口同高（首页根容器不用 ScrollViewer）；这是与 Microsoft Store
  「左导航 + 内容」骨架对齐的主工作区
- **版本库卡片贴内容、不撑满**：卡头（「已安装版本」+ 数量徽标 + 刷新）→ 列表 → 卡结束，
  列表 `MaxHeight` 由 `HomePage.OnPageSizeChanged` 按窗口高度在 160–420 之间封顶
  （窗口高时不留大片空白，窗口矮时收成卡内滚动区，快捷入口不会被挤出可视区）；
  行与版本页同款——32px 方块图标块（加载器图标优先）+ 版本名 + 类型/类别/隔离芯片 +
  行尾「上次启动 / 大小」，行高 60，列表直接坐在卡片上（不做"卡中卡"）
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
- **结构（两段）**：标题行（accent 着色 E9F5 图标 + 「调度中心」17 SemiBold + 关闭按钮）→
  状态行（11px secondary，如「2 个任务进行中 · 7 条通知」）→ **调度队列** + **历史通知**
- **调度队列**（标题 14 SemiBold primary）：只放**正在执行**的任务卡
  （圆角 8、`LayerFillColorDefaultBrush` + 1px 描边、内边距 12/10）。卡内自上而下：
  状态点（accent）→ 标题（13 SemiBold）与右对齐关键数字（13 SemiBold）同行 →
  4px 自绘进度条 → 详情（11px secondary）→ 多源贡献带。
  队列为空时是**极简空态**：E8FD 字形 22px（tertiary）+「无」13px（secondary），居中留白
- **历史通知**（标题 14 SemiBold primary，右侧「清空通知」文本按钮）：一条时间流水，
  行内三列 = `HH:mm:ss`（11px tertiary）+ 级别（11px，信息 `SystemFillColorAttentionBrush` /
  警告 `SystemFillColorCautionBrush` / 错误 `SystemFillColorCriticalBrush`）+ 文案（12px primary，
  最多三行、超出用 CharacterEllipsis 并给 ToolTip），最新在最前，面板最多渲染最近 50 条
- **任务结束不留队列**：`TaskCenterService` 收尾时向 `NotificationCenter` 落一条通知
  （完成 = 信息、失败 = 错误、取消 = 警告），插件通知除右下角浮条外同样进流水；
  通知只保留本次会话（内存 200 条），面板提供「清空通知」
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

## 11. 动效系统

原则：**动效只解释状态变化，不做装饰**。所有动效都过 `MotionService` 这道闸门
（设置项「跟随系统 / 始终开启 / 关闭」+ 系统「显示动画」偏好），关闭时一律瞬时到位、零开销；
任一环节异常静默降级，绝不影响功能。

曲线一律取 **WinUI 3 / 微软商店同源的 Fluent 关键样条**，非线性且**不使用弹簧回弹**：

- 进场 **Fast Out, Slow In** = `cubic-bezier(0.1, 0.9, 0.2, 1)`（`MotionFx.EnterControl1/2`）
- 退场 **Slow Out, Fast In** = `cubic-bezier(0.7, 0, 1, 0.5)`（`MotionFx.ExitControl1/2`）
- 控件级小位移用更快的 `ControlFastOutSlowIn` = `cubic-bezier(0.16, 1, 0.3, 1)`

上不了合成层的地方（`EnableDependentAnimation` 的依赖型属性，如抽屉高度）用
`DoubleAnimationUsingKeyFrames` + `SplineDoubleKeyFrame` 把同一条样条写进 XAML 动画，
不做"内置缓动类近似"。时长分档同样对齐 Fluent：小控件 100–150ms、中等表面 200–250ms。

**指针悬停反馈单独一档**，因为它不是"内容进出场"而是"亮度 + 微位移的交叉过渡"：

- 曲线用 `MotionFx.FadeEase` = `cubic-bezier(0.33, 0, 0.67, 1)`（匀称的缓入缓出，中途最快）。
  Fluent 的 Enter/Exit 样条都是"起步极快"型（30% 时间走完 87%），套在阴影、高亮这类**纯明暗渐变**上
  会退化成"啪一下没了"——悬停显得生硬、卡顿正是这个原因。
- 时长：淡入 200ms（`HoverFadeInMs`，Fluent Normal 档）、淡出 300ms（`HoverFadeOutMs`，Fluent Slow 档），
  淡出刻意比淡入长，退出来才有可见的尾迹；按压反馈另走 100ms，按下去必须立刻有反应。
- 参照值：WinUI 3 框架自己的 `VisualTransition`／状态切换取 167ms（`MotionFx.PointerOverMs`），
  控制模板的 `BrushTransition` 取 83ms——两者都是"起步极快"，所以本项目的悬停刻意不照抄这两档。

| 场景 | 实现 | 时长 / 曲线 |
| --- | --- | --- |
| 内容进入（列表项、面板切换） | `MotionFx.FadeSlideIn`：合成层 `Opacity` + `Translation`，独立动画 | 250ms / 进场曲线 |
| 内容退场 | `MotionFx.FadeOut`：只动 `Opacity` | 140ms / 退场曲线 |
| 列表首屏入场 | `MotionFx.StaggerIn`：第 i 项延迟 `min(i,9)×26ms`，最多 12 项 | 进场曲线 |
| 抽屉展开 / 收起 | `ExpandBehavior`：高度 `0 ↔ 自然高度`（`EnableDependentAnimation`）+ 内容淡入上移 12px + 箭头旋转 | 展开 250ms / 收起 150ms / 内容 200ms，进场曲线 |
| 悬停抬升（卡片内阴影） | `HoverShadowBehavior`：合成 `Translation` 抬升 + 贴光标的径向高光 | 进 200ms / 出 300ms，`FadeEase` |
| 行悬停 | `RowHoverBehavior`：合成 `Translation.Z` 抬升 + 覆盖层淡入 | 进 200ms / 出 300ms，`FadeEase`；按压 100ms |
| 卡片悬停缩放 | `CardHoverBehavior`：合成 `Scale`（悬停 1.012 / 按压 0.985） | 悬停 200ms / 按压 100ms，`FadeEase` |
| 分段控件选中指示器 | 合成 `Translation.X` 平移品牌色药丸 | 260ms / 进场曲线 |

## 12. 下载页（下载原版）

- **工具条**：版本类型用**等宽分段控件**（三段 × 88px，容器圆角 11、内缩 3），
  选中态是**品牌强调色药丸 + 黑字**，指示器随点击平移（260ms）；分段自带的选中底色已置空，
  避免出现两个指示器。搜索框圆角 10、前置 E721 放大镜，「缓存」徽标紧随其后
- **最新版本**：两枚 hero 瓦片并排（最新正式版 / 最新预览版），36px 版本图标 +
  11px 标签 + 17 SemiBold 版本号 + 11px 发布时间 + 行尾 chevron；悬停抬升阴影，点击直接进详情
- **版本行卡**：圆角 10、`ControlFillColorSecondaryBrush` 底、内边距 12/9，28px 图标 +
  14 SemiBold 版本号 + 11px「日期 · 类型」，右侧状态徽标（如「已安装」）；
  悬停时同时走行高亮（`RowHoverBehavior`）与抬升阴影（`HoverShadowBehavior`）
- **大版本抽屉**：组头 16 SemiBold 大版本号（`AccentTextFillColorPrimaryBrush`）+ 版本数 chip +
  行尾箭头；点击展开时高度平滑推开、组内行淡入、箭头旋转 90°
- **分类切换**：左侧分类（原版游戏 / 社区资源五类）切换时两块面板交叉过渡——
  新面板从下方 18px 浮入（260ms），旧面板淡出（150ms）后收起，不做整页闪烁
- **列表 ↔ 详情**：进入详情从下方 22px 浮入，返回时列表从上方 14px 落回
