# Starcloud 启动器 · Windows 端 UI 设计文档

> 本文是 Windows 桌面端（`dotnet/src/Starcloud.App`，.NET 8 + WinUI 3）界面的**现状设计说明**：
> 每个页面的分区结构、每个区域的文案与控件、每个图标的字形码与来源、点击后发生什么、
> 空态 / 加载态 / 错误态文案，以及设计 token。
>
> **范围**：只覆盖 Windows 端。**不含**安卓端（`android/`）与已弃用的 `src-tauri/`。
>
> **素材来源**：逐文件精读源码（XAML + code-behind + `App.xaml` + Core 侧服务）。
> 所有文案取自 `loc:Loc.*` 或 `Text=` 的**实际中文值**，字形码逐条抄自 XAML。
> 凡本次未读到源码的细节，一律标注「**未确认**」，不做推断。

---

## 0. 一个必须先对齐的术语问题

用户描述里的「**下载界面右边的二级导航栏**」，在代码里实际位于**左侧 180px 一栏**，
右侧（`*` 列）是内容区。下载页根布局是：

```
Grid (MaxWidth=1280)
└─ Grid (ColumnSpacing=16)
   ├─ Column 0 = 180px   ← 分类导航（「原版游戏」/「社区资源」两组，共 6 项）
   └─ Column 1 = *       ← 内容面板（原版游戏面板 / 社区资源面板 / 详情面板）
```

所以本文按**代码实际布局**描述：分类导航在左栏。这类「左右与我描述相反」的地方，
本文都以源码为准并在对应小节加一句提示。

同类结构在本项目中出现过三次，都是「左栏 180 分类导航 + 右栏内容」，文档里会分别写：

| 页面 | 左栏 180 的导航项 |
| --- | --- |
| 下载页 | 原版游戏 / Mod / 整合包 / 数据包 / 资源包 / 光影包 |
| 版本详情页 | 概览 / Mod 管理 / 游戏设置 / 启动参数 |
| 设置页 | 游戏 / Java / 下载与资源 / 通用 |
| 账户详情页 | 形象与皮肤 / 账户信息 |
| 添加账户页 | 微软账户 / 第三方账户 / 离线账户（列宽 200） |

---

## 1. 设计原则

从代码注释与本仓库设计文档中提炼出的、直接约束实现的硬原则：

1. **WinUI 原生 Fluent + 苹果式简约**。层级靠**字阶 / 字重 / 留白**表达，不靠边框盒子；
   分隔线是发丝级且内缩的；全局只有**一个品牌色**加**一语义红**；完成态静默后退。
2. **强调色面上一律黑字加粗**。品牌黄 `#FFD322` 亮度高，白字对比度不足（约 1.4:1），
   所以 `TextOnAccentFillColorPrimaryBrush = #E61A1A1A`，且 `AccentButtonForeground*` 一族
   被显式覆盖（框架默认会取到悬浮色 `#FFBC00` + 白字，黄底白字且同卡出现两种黄）。
3. **动效必须可关闭、绝不卡顿**。所有动效走合成层（Opacity / Translation），不触发布局；
   总开关 `ui_animations` = `system` / `on` / `off`，关闭时直接落终态、零开销。
4. **紫色只允许出现在 Nova 相关视觉**（星云紫 `#7C3AED` 一族）。其余界面不得出现紫色。
5. **无第三方启动器的「离线登录服务」**：离线身份以「离线账户」形态出现在账户页，
   不伪装成正版登录；第三方登录只作为「外置 Yggdrasil 认证服务器」提供。
6. **卡片一律毛玻璃**（`GlassCardBrush` 亚克力），内容区保持框架默认半透明让窗口毛玻璃透出；
   侧栏与标题栏必须是**不透明实色**，否则会透出亚克力读成「挖下去一块」。
7. **悬停反馈只有一种语言：跟手光斑**。行 / 卡片 / 按钮的悬停不靠框架整行底色
   （硬边直角矩形会和圆角光斑叠成两层框），框架的 `ListViewItemBackground*` 一律置 `Transparent`。
8. **选中态 = 品牌色药丸指示器平移**（分段控件）或**控件模板内三层底**（分类导航项），
   不用框架自带选中底色。选中文字强制黑字。
9. **一个页面只留一条主 CTA**（`AccentButtonStyle`），其余动作用 `GlowButton` / `HyperlinkButton`。

---

## 2. 设计 Token

### 2.1 色彩（`App.xaml` → `ResourceDictionary.ThemeDictionaries`）

品牌色两个主题同值，只有文字对比色与中性色分主题：

| 语义 | 键名 | 浅色 | 深色 |
| --- | --- | --- | --- |
| 品牌黄（默认强调） | `AccentFillColorDefaultBrush` / `SystemAccentColor` | `#FFD322` | `#FFD322` |
| 强调-悬浮 | `AccentFillColorSecondaryBrush` / `SystemAccentColorDark1` | `#FFBC00` | `#FFBC00` |
| 强调-按下 | `AccentFillColorTertiaryBrush` | `#E6A800` | `#E6A800` |
| 强调-禁用 | `AccentFillColorDisabledBrush` | `#99FFD322` | `#99FFD322` |
| 强调面上文字 | `TextOnAccentFillColorPrimaryBrush` | `#E61A1A1A` | `#E61A1A1A` |
| 强调色文字（浅底可读） | `AccentTextFillColorPrimaryBrush` | `#B38500` | `#FFD322` |
| 正文主 | `TextFillColorPrimaryBrush` | `#FF1A1A1A` | `#FFF5F5F5` |
| 正文次 | `TextFillColorSecondaryBrush` | `#FF666666` | `#FFA0A0A0` |
| 正文三级 | `TextFillColorTertiaryBrush` | `#99666666` | `#87A0A0A0` |
| 文字禁用 | `TextFillColorDisabledBrush` | `#5C666666` | `#57A0A0A0` |
| 卡片描边 | `CardStrokeColorDefaultBrush` | `#FFE2E2E2` | `#FF3A3A3A`(未确认，深色主题键存在) |
| 侧栏 / 标题栏底 | `AppPaneBackgroundBrush` | `#FFF6F6F6` | `#FF161616` |
| 行「压暗底」 | `ScRowShadeBrush` | `#0A000000` | `#38000000` |
| 危险（语义） | `DangerBrush` / `SystemFillColorCriticalBrush` | `#FFF44336` | 同值 |
| 成功（语义） | `SuccessBrush` / `SystemFillColorSuccessBrush` | `#FF3DDC84` | 同值 |
| 提示芯片底 | `CautionChipBackgroundBrush` | `#1FF59E0B` | `#2EF59E0B` |
| 提示芯片字 | `CautionChipTextBrush` | `#FFB45309` | `#FFFBBF24` |
| 键盘焦点圈 | `FocusVisualPrimaryBrush` | `#B38500` | `#FFD322` |

**Nova 五态色**（`App.xaml` 定义，仅 Nova 视觉使用）：

| 状态 | 键 | 值 |
| --- | --- | --- |
| 待命 Idle | `NovaStateIdleBrush` | `#FF71717A` |
| 思考中 Thinking | `NovaStateThinkingBrush` | `#FF7C3AED` |
| 有提案 Plan | `NovaStatePlanBrush` | `#FFF59E0B` |
| 观察中 Observing | `NovaStateObservingBrush` | `#FF10B981` |
| 离线 Offline | 取 `TextFillColorDisabledBrush` | — |

`MainWindow.ApplyNovaState` 把这套色画在 NovaOrb 的**外圈描边**上（品牌标记是多色图片，画不了字形）。
状态文案 `NovaRuntime` 原文：`待命` / `思考中…` / `有提案` / `观察中` / `离线`。

### 2.2 材质

| 键 | 用途 | 浅色 | 深色 |
| --- | --- | --- | --- |
| `GlassCardBrush` | 所有卡片底（`AcrylicBrush`，应用内亚克力，透出并模糊背后的自定义背景） | Tint `#FFFFFF` / 0.72，Fallback `#FFF9F9F9` | Tint `#2B2B2B` / 0.75，Fallback `#FF1F1F1F` |

- **内容区**始终保持框架默认（`LayerFillColorDefaultBrush`，半透明），让窗口毛玻璃透出；
  一旦刷成不透明实色整窗毛玻璃就没了。
- **侧栏**在 `NavigationView.Resources` 里覆盖 `NavigationViewDefaultPaneBackground` /
  `NavigationViewExpandedPaneBackground`（模板级 key，应用级覆盖不到）：浅 `#FFF6F6F6`、深 `#FF161616`。
- 侧栏同时去掉模板自带描边：`NavigationViewBorderThickness=0`、
  `NavigationViewContentGridBorderThickness=0`、`NavigationViewContentGridCornerRadius=0`
  （默认会在标题栏 / 侧栏 / 内容区交界处留下黑色拼接缝）。

### 2.3 字体与字阶

- 正文字体 `HarmonyOS Sans SC`（资源键 `ScUiFontFamily`，同时写入
  `ContentControlThemeFontFamily` 供所有控件模板使用）。
- 该字体本机只装了 **Medium / Bold / Black 三档，没有 Regular**。因此：
  **正文与标题一律 Medium**，只有「选中 / 未选中靠字重表达状态」的切换类控件在选中时提到 Bold。
- 基样式 `ScText`（`FontFamily` + `FontSize=14` + `FontWeight=Medium`），隐式应用到所有 `TextBlock`。
  `ScText` 刻意**不设 Foreground**：强调色按钮里的白字（实为黑字）靠 `ContentPresenter` 继承。
- `RichTextBlock` 有单独隐式样式（字号 14、同字体），因为 RTE 不在 TextBlock 隐式样式覆盖范围内。

常见字阶（源码实测）：

| 字号 | 用途 |
| --- | --- |
| 32 Bold | 版本详情页标题（全站唯一） |
| 24 Medium | 插件导航页 / 插件面板页页头标题 |
| 17 Medium | 调度中心标题、下载页 hero 瓦片版本号 |
| 16 Medium | 卡片主标题（如「启动游戏」、社区筛选卡标题） |
| 15 Medium | 首页「已安装版本」、账户页「Starcloud ID」、下载页「开始下载」 |
| 14 Medium | 正文 / 列表行主文本 / 分区标题（`ScSectionTitle`） |
| 13 Medium | 分段控件文字、任务状态正文 |
| 12 | 次级说明、列表行副标题、状态行 |
| 11 | 元信息标签（`ScMetaLabel`）、芯片文字（`ScChipText`）、卡片提示（`ScCardHint`）、时间戳 |
| 9 | 皮肤卡小角标 |
| 30 SemiBold `Cascadia Code` | 运行状态页大数值（CPU / 内存 / GPU） |

### 2.4 圆角（`App.xaml` 全局键）

| 键 | 值 | 用途 |
| --- | --- | --- |
| `ControlCornerRadius` | 12 | 按钮 / 输入框（全局默认） |
| `CardCornerRadius` | 20 | 卡片（全局默认） |
| `OverlayCornerRadius` | 16 | 弹出层 |

实际使用中另有按控件手写的值：分类导航项 **10**、列表行光斑载体 **8**、
`ScCard` **16**、`ScHoverCard` **12**、下载页 hero 瓦片 **14**、
「开始下载」胶囊按钮 **24**、Nova 面板 **20**、窗口按钮 **8**。

### 2.5 间距与布局

- **内容区留白统一 24**：`<Grid Padding="24">` 包住 `ContentFrame`，标题栏底到内容上沿不再比其它三边窄。
- 各页根 `Grid` 均 `MaxWidth="1280"`（Nova 页 1080、联机页 880）+
  `HorizontalAlignment="Stretch"`，宽窗口下内容不无限拉伸。
- 两列布局列间距统一 `ColumnSpacing=16`，行间距统一 `RowSpacing=12` 或 `16`。
- 首页刻意**不用** `Grid.RowSpacing`：整块收起（行高 0）时 `RowSpacing` 照收，
  会把版本卡推下去；改由元素自己的 `Margin` 承担行距。

### 2.6 芯片（Chip）

一行结构化标签的唯一形态：

| 样式键 | 底 | 字 | 用途 |
| --- | --- | --- | --- |
| `ScChip` + `ScChipText` | `SubtleFillColorTertiaryBrush` | 11px 次级色 | 中性元信息（类型 / 类别 / 加载器） |
| `ScChipAccent` + `ScChipAccentText` | `AccentFillColorDefaultBrush` | 11px 强调面上黑字 | 「推荐」「首选」「使用中」「运行中」 |
| `ScChipCaution` + `ScChipCautionText` | `CautionChipBackgroundBrush` | 11px 琥珀 | 「第三方未验证」「采样已暂停」「上一轮启动器启动」 |

芯片公共形态：`Padding="6,1"`、`CornerRadius="4"`、`VerticalAlignment=Center`。

### 2.7 动效（`Services/MotionFx.cs` + `MotionService`）

**总开关**：设置项 `ui_animations`（`LauncherSettings.UiAnimations`，默认 `"system"`）
→ `MotionService.Enabled`：`"on"` = 始终开、`"off"` = 关、其余跟随系统
`UISettings.AnimationsEnabled`（读取失败按开启）。
`MotionFx` 的 `FadeSlideIn` / `FadeOut` / `FadeIn` / `StaggerListItems` 入口都先查它，关闭时直接落终态。

**全局节奏系数 `Pace = 1.5`**：所有时长 = 标准档 × 1.5（只放大时长，不改曲线），
刻意比 Fluent 标准档慢，读起来更从容。

| 成员 | 值 | 用途 |
| --- | --- | --- |
| `EnterMs` | 375 ms | 默认入场（250 × 1.5） |
| `ExitMs` | 210 ms | 默认退场（140 × 1.5） |
| `PointerOverMs` | ≈251 ms | 框架 PointerOver 基准（167 × 1.5） |
| `HoverFadeInMs` | 300 ms | 悬停淡入 |
| `HoverFadeOutMs` | 450 ms | 悬停淡出（刻意比淡入长） |
| `ChevronMs` | 330 ms | 折叠区箭头旋转（220 × 1.5） |
| `DrawerExpandMs` | 420 ms | 抽屉展开（280 × 1.5） |
| `DrawerCollapseMs` | 300 ms | 抽屉收起（200 × 1.5） |
| `DrawerFadeMs` | 240 ms | 抽屉收起时内容淡出（必须短于收起时长） |
| `StaggerIn` | step 39 ms、maxStagger 9、fromY 18 | 列表项错峰入场 |
| `StaggerListItems(list, maxItems=12)` | — | 列表回填后整体错峰入场 |

缓动曲线：入场 `(0.1,0.9)/(0.2,1)`（Fast Out, Slow In）、
退场 `(0.7,0)/(1,0.5)`（Slow Out, Fast In）、控件 `(0.16,1)/(0.3,1)`、纯明暗 `(0.33,0)/(0.67,1)`。
页面转场走 `MotionService.TransitionFor(enabled, drillIn)`；关闭时返回
`SuppressNavigationTransitionInfo`，二级页返回走 `BackTransition(enabled)`。

**不受总开关控制的两处**（已确认，属实现细节）：
- `NovaChatView` 的「等待中」三点打字动画（`TypingDot_Loaded` 里直接 `Storyboard.Begin()`）；
- `ListView.ItemContainerTransitions`（`AddDeleteThemeTransition` / `RepositionThemeTransition`，
  框架原生转场）。

### 2.8 跟手光斑（`HoverShadowBehavior`）

全站统一的悬停语言，实现是**径向渐变层跟着光标移动**：

- 载体 `Carrier` 是一层 `Border`，`CornerRadius` 必须与承载物一致，否则光斑四角会溢出。
- 渐变六档（浅色主题内联叠黑）：
  `#28000000`(0) → `#1F000000`(0.18) → `#16000000`(0.40) → `#0D000000`(0.62) → `#05000000`(0.82) → `#00000000`(1)。
  **深色主题由行为统一改写成叠白**（否则「阴影是白的」）。
- 关闭条件：调用点自己写了 `Background="Transparent"` 的图标按钮，
  `GlowButton` 会自动跳过光斑与上浮；`HoverShadowBehavior` 自建层同理。
- `GlowRadius` 可显式指定（首页账户分段条给 30，因为条内可见区只有 34 高）。

`GlowButton`（`Views/Controls/GlowButton.cs`）把光斑收进按钮模板，两个模板：

| 模板 / 样式 | 行为 |
| --- | --- |
| `ScGlowButtonTemplate` | 普通按钮，保留 Fluent 悬停 / 按下换色 |
| `ScGlowCardButtonTemplate` | 卡片式按钮（`ScGlowCardButtonStyle`）：自带 `GlassCardBrush` 底、悬停不换色（换色会从深色卡片突变成浅色），反馈只走高亮层与光斑 |
| `ScStaticCardButtonStyle` | 静态瓦片：完全不要悬停反馈（下载页两枚「最新版本」瓦片用） |

### 2.9 自定义背景（`MainWindow.BackgroundLayer`）

亚克力之上、内容之下共三层：`BackgroundImage`（清晰图）→ `BackgroundBlurHost`（模糊层）→
`BackgroundMask`（毛玻璃遮罩）。内置壁纸 5 张：`aurora` / `dawn` / `mist` / `nebula` / `sand`
（`Assets/Wallpapers/` 下另有 `midnight.jpg`，设置页壁纸网格按 `AppearanceService.Wallpapers` 列出）。

相关设置项：背景类型（`none` / `builtin` / `file`）、图片路径、模糊强度（默认 24，0–64）、
不透明度（默认 0.7，0.2–1）、毛玻璃强度（默认跟随主题）、账户页走路动画（默认开）。
背景项**即改即存**（滑块拖动 400ms 节流落盘）。

---

## 3. 图标体系

### 3.1 来源

- 图标字体：`Assets/Fonts/HarmonyOSIcons.ttf`，家族名 `harmonyos`。
- 来源项目：**IKKI2000/harmonyos-icons**（MIT，字形取自 HarmonyOS Design 图标库），
  许可证随包发布在 `Assets/Fonts/HarmonyOSIcons-LICENSE.txt`。
- 注册位置：`App.xaml` 的 `<FontFamily x:Key="ScIconFont">ms-appx:///Assets/Fonts/HarmonyOSIcons.ttf#harmonyos</FontFamily>`。
- 代码里取值入口：`Services/IconFont.cs` 的 `IconFont.Family`
  （`(FontFamily)Application.Current.Resources["ScIconFont"]`），避免 URI 写两遍。
- 字形码落在 **E700–F8FF 的 PUA 段**，与 Segoe Fluent Icons 同编码，因此很多码位可直接互换。

### 3.2 用法约定（重要）

- **逐元素**指定字体：`<FontIcon FontFamily="{StaticResource ScIconFont}" Glyph="&#xXXXX;" />`。
- **绝不做 `FontIcon` 隐式样式**：框架模板（下拉箭头 / 消息条关闭按钮 / 导航展开箭头）
  也用 `FontIcon`，隐式样式会把它们的 Segoe 字形打成缺字方框。
- 位图标（`NavigationViewItem.Icon` / `MenuFlyoutItem.Icon`）里要画多色品牌标记时用
  `<ImageIcon Source="{StaticResource NovaMarkSmall}" />`
  （`NovaMarkSmall` = `ms-appx:///Assets/Brand/starcloud-mark-small.svg`，在 `App.xaml` 里显式建成 `SvgImageSource`）。
- **窗口三个按钮（最小化 / 最大化 / 关闭）不是字体图标**，是内联 SVG `<PathIcon>` 几何。
- **品牌标记也不是字体图标**：`Assets/Brand/` 下有
  `starcloud-mark-light.svg`（浅底）、`starcloud-mark-dark.svg`（深底）、
  `starcloud-mark-small.svg`（16–24px 单块版，供图标位使用），由 `ctrl:BrandMark` 控件按主题自动换源。

### 3.3 字形码总表（实测）

| 码位 | 含义 | 出现位置（摘要） |
| --- | --- | --- |
| `E71B` | 链接 / 从链接导入 | 版本页菜单与工具条「从链接导入」 |
| `E8AC` | 重命名（**旧码位**） | 账户详情皮肤右键「重命名」代码硬编码 `\uE8AC`；新码为 `EA97` |
| `E904` | 右箭头 / 展开 | 账户行尾、hero 瓦片行尾、联机页折叠箭头、默认皮肤折叠箭头 |
| `E908` | 返回 | 版本详情页、账户二级页页头返回；下载详情返回 |
| `E90A` | 导出 | 版本页「导出整合包」 |
| `E90C` | 下载 / 导入整合包 | 导航「下载」、版本页「导入整合包」、下载页「开始下载」与 hero 兜底 |
| `E918` | 刷新 | 版本页 / 存档页 / 运行状态页 / 首页版本卡刷新 |
| `E973` | 外链 | 资源下载窗「转到 Modrinth」 |
| `EA6D` | 启动参数 | 版本详情页左栏「启动参数」 |
| `EA6F` | 搜索（放大镜） | 下载页搜索框前置、存档页 QueryIcon、社区搜索按钮、Nova 浮窗收起 |
| `EA7C` | 检查 / 验证 / 自检 | 首页自检三项、版本详情「验证文件」、Nova 确认清单抬头 |
| `EA86` | 关闭 | 调度中心关闭按钮 |
| `EA90` | 文件夹 / 目录 | 导航「存档」、版本页「打开存档文件夹」「打开游戏目录」、版本详情「存档/截图文件夹」 |
| `EA93` | Mod / 模组 | 导航图标位、版本页「打开模组文件夹」、版本详情「Mod 管理」、下载页 Mod 分类 |
| `EA94` | 版本 / 版本文件夹 | 导航「版本」、版本页「打开版本文件夹」与空态、版本详情「版本文件夹」 |
| `EA97` | 重命名 | 版本页菜单「重命名」、版本详情「重命名」 |
| `EA9F` | 复制 | 版本页「复制版本」、版本详情「复制版本」、资源下载窗「复制名称」 |
| `EAA3` | 编辑 / 更换图标 | 版本详情「更换图标」、账户详情左栏「形象与皮肤」 |
| `EAA5` | 删除（垃圾桶） | 版本页行删除 + 菜单「删除」、版本详情「删除版本」、各类删除按钮（部分代码写 `\uEAA5`） |
| `EAA7` | 更多（省略号） | 版本页行「更多操作」 |
| `EABC` | 插件（拼图） | 导航「插件」、插件面板入口、插件空态 |
| `EBA0` | 版本详情 / 信息 | 版本页菜单「版本详情」、首页行菜单 |
| `EBA2` | 百科 | 资源下载窗「转到 MC 百科」 |
| `EB8F` | 账户 / 人物 | 导航「账户」、标题栏个人中心、账户页空态、添加账户「微软账户」、账户详情「账户信息」 |
| `EB91` | 联机 | 导航「联机」、添加账户「第三方账户」 |
| `EBDF` | 启动（播放） | 版本页菜单「启动」、版本详情「启动」 |
| `EC04` | 设置 | 导航「设置」、版本详情「游戏设置」、Nova 面板设置入口 |
| `EC0C` | 光影包 | 下载页分类「光影包」、版本详情内容分行 |
| `EC20` | 原版游戏 | 下载页分类「原版游戏」、设置页分类「游戏」 |
| `EC24` | 数据包 | 下载页分类「数据包」、版本详情内容分行 |
| `EC3B` | 运行状态 | 导航「运行状态」 |
| `EC49` | 资源包 | 下载页分类「资源包」、版本详情内容分行 |
| `EC62` | **图标兜底**（未知版本） | 版本页行图标兜底、版本详情 `IconFallback`、账户添加页「离线账户」、资源下载窗兜底 |
| `EC64` | 日志 | 导航「启动器日志」 |
| `EC68` | 备份 / 回档 | 版本页菜单「备份与回档」、Nova 操作日志空态 |
| `EC70` | 空状态（无内容） | 运行状态页无游戏卡、调度中心队列空态 |
| `EC77` | 首页 / 概览 | 导航「首页」、版本详情「概览」 |
| `EC89` | 消息 / 已读 | 调度中心图标与标题、标题栏调度入口 |
| `ECAF` | 整合包（亦作通用兜底） | 下载页分类「整合包」、hero 瓦片与下载详情图标的兜底层 |

> 表中 `E904` 有两处语义（行尾「展开箭头」与「跳转提示」），是同一字形在不同上下文复用，非笔误。

---

## 4. 外壳与导航（`MainWindow.xaml`）

### 4.1 窗口骨架

根 `Grid` 两行：`48` 标题栏 + `*` 内容区。

```
RootGrid
├─ BackgroundLayer   (RowSpan=2, IsHitTestVisible=False, 默认 Collapsed)  ← 自定义背景三层
├─ AppTitleBar       (Row=0, 48px, AppPaneBackgroundBrush 不透明)
├─ 顶栏右侧 StackPanel(Row=0, 右对齐)
└─ NavigationView    (Row=1)
   └─ Grid(Padding=24) → Frame x:Name="ContentFrame"
```

- 标题栏走 `ExtendsContentIntoTitleBar`，**自绘**，底色 `AppPaneBackgroundBrush`（不透视背景）。
- `NavigationView`：`PaneDisplayMode="LeftCompact"`、`IsPaneOpen="False"`、
  `OpenPaneLength="232"`、`IsBackButtonVisible="Collapsed"`、`IsSettingsVisible="False"`、
  `IsTitleBarAutoPaddingEnabled="False"`，字体与字重显式给 `ScUiFontFamily` / Medium。

### 4.2 标题栏左侧

| 元素 | 规格 |
| --- | --- |
| 品牌标记 | `ctrl:BrandMark Width=22 Height=22`，按主题自动换源（浅/深两套） |
| 应用名 | `TextBlock "Starcloud"`，14px Medium |
| 版本号徽标 | `Border`（`CornerRadius=6`、`Padding="6,2"`、`ControlFillColorSecondaryBrush`）内 11px 次级色文字，读 csproj `<Version>`（见 `MainWindow.xaml.cs`） |

整组 `StackPanel Spacing=10`、`Margin="16,0,0,0"`。

### 4.3 标题栏右侧（四组，从左到右）

**① NovaOrb** — `Button 44×36`、`CornerRadius=8`、透明底无边框，`loc:Loc.Name="Nova 助手"`、
`loc:Loc.Tip="Nova：待命"`，光斑载体 `NovaShadow`、`GlowRadius=16`。

内部 `Grid 32×32`：
- `Border NovaOrbRing`：`CornerRadius=8`、`GlassCardBrush` + 1px `CardStrokeColorDefaultBrush`，
  **五态色画在这圈描边上**；
- `Border NovaShadow`：内阴影层（加强档 `#3E000000` 起），悬停渐显；
- `ctrl:BrandMark Small Width=15 Height=15`（居中）；
- `Ellipse NovaOrbDot`：8×8、`#F59E0B`、右上角 `Margin="0,3,3,0"`，**有提案时可见**。

**② 调度中心** — `Button 44×36`（同 NovaOrb 结构），`loc:Loc.Name="调度中心"`，
图标 `EC89` 15px，配**任务数徽标**（accent 底 + 黑字）。

**③ 个人中心** — `Button 44×36`，`loc:Loc.Name="个人中心"`；无账户时显示首字母，
图标兜底 `FontIcon Glyph="EB8F"` 15px（`AccountAvatarGlyph`）。

**④ 窗口按钮三枚** — 自绘 SVG `<PathIcon>`，`CaptionButtonStyle`：

| 属性 | 值 |
| --- | --- |
| 尺寸 | `Width=46 Height=48`（与系统 caption 一致），`Padding=0`、`MinWidth/MinHeight=0` |
| 模板 | `CaptionButtonTemplate`：**只要透明底 + 内容**，四态都不画底色（默认模板会在 PointerOver/Pressed 刷整块 46×48 底色，与跟手光斑叠成大小两层） |
| 悬停反馈 | 只由按钮内那层跟手光斑表达 |
| `loc:Loc.Name/Tip` | `最小化` / `最大化` / `关闭` |

> 热区 46×48、点亮内收 6 → 34×36、光斑载体 40×40 圆角方块（据设计文档；本次未逐帧核对具体三元组）。

### 4.4 左侧导航（NavigationView）

`MenuItems` 顺序：

| 顺序 | 文案 | Tag | 图标 |
| --- | --- | --- | --- |
| 1 | 首页 | `home` | `EC77` |
| 2 | 版本 | `versions` | `EA94` |
| 3 | 下载 | `download` | `E90C` |
| 4 | 存档 | `saves` | `EA90` |
| 5 | 联机 | `multiplayer` | `EB91` |
| 6 | 运行状态 | `running` | `EC3B` + 文案右侧 6×6 绿色圆点 `RunningDot` |
| — | `NavigationViewItemSeparator` | — | — |
| 7 | 账户 | `accounts` | `EB8F` |
| 8 | 插件 | `plugins` | `EABC` |
| 9 | Nova | `ai` | `<ImageIcon Source="{StaticResource NovaMarkSmall}" />` |
| 10+ | 插件贡献的导航项 | `plugin:<插件id>:<导航id>` | 插件提供的 `Glyph` |

`FooterMenuItems`（固定底部，设置在最底）：

| 文案 | Tag | 图标 |
| --- | --- | --- |
| 启动器日志 | `logs` | `EC64` |
| 设置 | `settings` | `EC04` |

**运行状态圆点规则**：`Fill="{ThemeResource SystemFillColorSuccessBrush}"`（绿），
有游戏进程存活时 `Visible`、否则 `Collapsed`，**静态不做呼吸动画**。

**插件导航组**（`MainWindow.RebuildPluginNavGroup()`）：在 Nova 之后**追加一条分隔线 + 最多 3 项**
（`MaxPluginNavItems=3`，按插件加载顺序取前 3，超出写日志）；
**没有插件导航项时连分隔线都不加**。点选后走 `PluginExtensionPoints.TryParseNavTag`
→ 导航到 `PluginNavPage` 并收起侧栏；插件被停用时回落「插件」页。

**「运行状态」项的特殊写法**：因为 `Content` 被换成横排（文案 + 圆点），
不能在条目上用 `loc:Loc.Text`（`ContentControl` 分支会覆盖整个 `Content`），
文案落在内部 `TextBlock` 上，条目自身用 `loc:Loc.Name`。

### 4.5 Nova 面板（`Popup NovaPopup`）

由顶栏 NovaOrb 弹出，`IsLightDismissEnabled="True"`、`AllowFocusOnInteraction="True"`。

| 项 | 规格 |
| --- | --- |
| 面板 | `Border NovaPanelHost Width=440`、`CornerRadius=20`、`Padding=16`、底 `SolidBackgroundFillColorBaseBrush`、1px 描边 |
| 定位 | 按 Orb 实际位置水平夹在 `12 ~ 根宽-面板宽-12`，`VerticalOffset = 48+8`；`MaxHeight = max(240, 窗口高-偏移-12)` |
| 入场 | `MotionFx.Reset` + `FadeSlideIn(10f, EnterMs)` |

> 源码注释明确：**不要**设 `ShouldConstrainToRootBounds=False`，会命中 WinUI 的 IME bug（中文只出拼音字母）。

结构（`Grid RowSpacing=10`，两行）：

1. **标头** `Grid ColumnSpacing=10`：
   - 图标位 `Border 32×32`（圆角 8、`LayerFillColorDefaultBrush` + 1px 描边）内 `BrandMark Small 15×15`；
   - `TextBlock "Nova"` 14px Medium + `NovaPanelQuota`（11px 次级色，**登录后常驻一行**，
     文案形如 `云端额度：{RemainingText}`，取不到时 `云端额度：暂未取到，可到 Nova 页刷新`，
     仅「云端相关」时显示）；
   - 齿轮按钮：图标 `EC04` 13px，`loc:Loc.Name="打开 Nova 页"`、
     `loc:Loc.Tip="打开 Nova 页（许可层 / 操作日志 / LLM 设置）"` → 关面板并 `NavigateTo("ai")`。
2. **未登录**（`NovaPanelLogin`）：`Nova 尚未登录`(Medium) +
   `登录 Starcloud ID 后可使用云端模型，以及邀请、时刻等在线能力；凭据只保存在本机。` +
   按钮「去 Nova 页登录」(`AccentButtonStyle`)。
3. **已登录**：`ctrl:NovaChatView x:Name="NovaPanelChat"`。

**浮窗提醒**：`TeachingTip NovaToastTip`（`Title="Nova"`、`PreferredPlacement=Bottom`、
Target = NovaOrb，8s 后自动收起）。

### 4.6 调度中心抽屉（`TaskCenterOverlay`）

由顶栏调度中心按钮打开；`Grid.Row=1` 覆盖内容区。

| 项 | 规格 |
| --- | --- |
| 遮罩 `TaskCenterScrim` | `Rectangle Fill="#5A000000"`，点击关闭 |
| 抽屉 `TaskCenterDrawer` | **`Width=600`**、右对齐、底 `AppPaneBackgroundBrush`、`BorderThickness="1,0,0,0"` |
| 内容 `Grid` | `Margin="20,16,14,12"`、`RowSpacing=10`、`TabFocusNavigation="Cycle"`（**Tab 锁在抽屉内**） |
| 键盘 | `Escape` 走 `KeyboardAccelerator`（不依赖焦点），`KeyboardAcceleratorPlacementMode="Hidden"`（不渲染 Esc 徽标） |
| 入场 | `ApplyTaskDrawerTransition` 加 `EntranceThemeTransition(FromHorizontalOffset=32)`，**仅 `Motion.Enabled` 时** |

> **口径不一致（以实际值为准）**：该处的源码注释写「432px 面板」，
> 但 `TaskCenterDrawer.Width` 实测为 **600**。文档按 **600**。

结构（三行 `Auto / Auto / *`）：

1. **标题行**：`FontIcon EC89` 15px（accent 色）+ `调度中心` 17px Medium +
   关闭按钮（`GlowButton`，图标 `EA86` 13px，`loc:Loc.Name="关闭"`）。
2. **状态行** `TaskSummaryText`（11px 次级色）：`{N} 个任务进行中 · {M} 条通知` 或 `{M} 条通知`。
3. **滚动区**（`ScrollViewer`，`loc:Loc.Name="任务列表"`）：
   - 分区标题 `调度队列`（14px Medium）→ `TaskRunningHost` 承载任务卡（进度 / 速度 / 多源贡献带）；
   - **队列空态** `QueueEmptyHost`：`FontIcon EC70` 22px（三级色，居中）+ `无` 13px 次级色；
   - 分区标题 `历史通知`（14px Medium）+ 右侧「清空通知」按钮 `NoticeClearBtn`
     （默认 `Collapsed`，12px、透明底、`Padding="8,2"`）→ `NoticeHost` 承载时间流水。

**刷新策略**：300ms 定时器按 `Task.Id` **复用行**刷新；全量重建会让进度条抽搐。
关闭时焦点还给入口，打开时焦点给关闭按钮。

### 4.7 全局通知

| 元素 | 说明 |
| --- | --- |
| 插件通知 `InfoBar PluginNotifyTip` | 右下角，`MaxWidth="420"`、`Margin="0,0,24,24"`；对应插件 API `launcher:notification` |
| 顶栏浮窗提醒 | `TeachingTip NovaToastTip`（见 4.5） |

> 底部状态栏（原「未选择实例」条）**已移除**，源码留注释「底部不再有」。

---

## 5. 首页 `HomePage.xaml`

**入口**：导航第 1 项「首页」（`EC77`），启动后默认页。

**根布局**：刻意**不用 `ScrollViewer`**——整体填满视口，卡片与窗口同高（PCL2 式）。

```
Grid                                  ← 根：HomeContent + LaunchOverlay 两层
├─ Grid x:Name="HomeContent" (MaxWidth=1280)
│  ├─ Column 0 = *   (Margin="0,0,24,0")
│  │  ├─ Row 0 Auto  LeftNoticeArea        （通知区 + 启动自检卡，全不可见时整块 Collapsed）
│  │  ├─ Row 1 Auto  PluginContributionPanel Point="home.top"
│  │  ├─ Row 2 Auto  HomeCardStrip x:Name="HomeCards"（固定 120 高、最多 3 张）
│  │  └─ Row 3 *     VersionCard           （已安装版本，撑满余下整格，列表在卡内滚动）
│  └─ Column 1 = 360 AccountCard           （账户 / 启动卡，与窗口同高，可收起成 Dock）
└─ LaunchOverlay                           （启动页全屏覆盖层）
```

**行距说明（重要）**：左列**不用** `Grid.RowSpacing`。`RowSpacing` 按行边界收取，
通知区 / 挂载区 / 卡片带整块收起（行高 0）时它照收一份，版本卡就被推下去一截、
与右栏账户卡上边缘对不齐。行距改由**元素自己的 `Margin`** 承担。

### 5.1 左列 · 通知与启动自检（`LeftNoticeArea`）

`StackPanel Spacing=8`，整块默认 `Visibility="Collapsed"`，由 `RefreshLeftNoticeArea` 统一切换。
**必须显式给 `Collapsed`**：三根 `InfoBar` 初始就是 `IsOpen=False`、自检卡也是 `Collapsed`，
属性没变化 → 回调一次都不触发；若留给默认 `Visible`，零高 `InfoBar` 仍会被 `Spacing` 收
2×8 = 16 DIP 间距，版本卡正好被推下去这么多。

| 元素 | 类型 | 标题原文 | 关闭按钮 |
| --- | --- | --- | --- |
| `GlobalErrorBar` | `InfoBar Severity=Error` | `启动出错` | 可关闭 |
| `LocalErrorBar` | `InfoBar Severity=Error` | `操作失败` | 可关闭 |
| `WarnBar` | `InfoBar Severity=Warning` | `提示` | 可关闭 |

**启动自检卡 `CheckCard`**（`GlassCardBrush` + 1px 描边 + `CornerRadius=8` + `Padding=16`），
默认 `Collapsed`：

- 标题行：`启动自检`（15px Medium）+ 右对齐「重新检测」按钮
  （透明底图标按钮，图标 `E918` 14px，`loc:Loc.Name/Tip="重新检测"`）。
- 检查项三行，每行 = `FontIcon` + 项名 + 「去处理」`HyperlinkButton`：

| 元素 | 图标 | 含义 |
| --- | --- | --- |
| `CheckIconVersion` | `EA7C` 14px | 检查「版本」 |
| `CheckIconAccount` | `EA7C` 14px | 检查「账户」 |
| `CheckIconJava` | `EA7C` 14px | 检查「Java」 |

### 5.2 左列 · 插件挂载区与首页卡片带

| 元素 | 说明 |
| --- | --- |
| `ctrl:PluginContributionPanel Point="home.top"`（`Margin="0,0,0,16"`） | 插件挂载点，无贡献时高度为 0 |
| `ctrl:HomeCardStrip x:Name="HomeCards"`（`Margin="0,0,0,16"`） | 插件首页卡片带（`contributes.homeCards`）：**固定 120 高、最多 3 张**，可收起成一条窄条；没装插件时整块 `Collapsed` |

### 5.3 左列 · 已安装版本卡（`VersionCard`）

外壳：`GlassCardBrush` + 1px `CardStrokeColorDefaultBrush` + `CornerRadius=16` + `Padding=12`。

**卡头**（`Grid x:Name="VersionHeader"`，`Padding="6,2,0,2"`、`Margin="0,0,0,8"`、`ColumnSpacing=8`）：

| 列 | 内容 |
| --- | --- |
| 0 | `已安装版本`（15px Medium） |
| 1 | 数量徽标 `VersionCountText`：`Border`（`CornerRadius=5`、`Padding="6,1"`、`SubtleFillColorSecondaryBrush`）内 11px 次级色文字，初始 `(0)` |
| 2 | `*` 占位 |
| 3 | **收起 / 展开** `VersionCollapseBtn`：`GlowButton` 透明底图标按钮（`Padding=6`），内为自绘 `Path` 刻度（`14×8`，`Stroke` 次级色，`StrokeThickness=1.6`，圆头圆角），`loc:Loc.Name/Tip="收起版本列表"`；**展开态刻度朝上**（按一下把列表卷起来），收起态朝下 |
| 4 | **刷新** `RefreshVersionsBtn`：透明底 `GlowButton`，图标 `E918` 14px，`loc:Loc.Name/Tip="刷新列表"` |

> 收起 / 展开与刷新**同一套透明底图标按钮**，卡头高度分毫不动。

**列表区 `VersionListArea`**（`Grid.Row=1`）：`ListView x:Name="VersionList"`，
`SelectionMode=Single`，行模板 `VersionRowTemplate`（在 `Page.Resources` 里，折叠态复用同一份）。

行内控件层（`Border CornerRadius=8` + `HoverShadowBehavior`，载体 `VersionRowGlow`）：

| 列 | 宽 | 内容 |
| --- | --- | --- |
| 0 | Auto | 版本方块图标块 `Border 32×32 CornerRadius=8`；底层兜底 `FontIcon EC62`，上层真实图标 `Image` |
| 1 | **260** | 版本名 `Id`（Medium，省略号截断） |
| 2 | `*` | 芯片组（`KindText` / 类别 / 「版本隔离」），按可见性显隐 |
| 3 | Auto | 数据列：`ScMetaLabel`「上次启动」+ `ScMetaValue` `LastPlayedText`；`ScMetaLabel`「大小」+ `ScMetaValue` `SizeText` |

光斑层 `VersionRowGlow`：`CornerRadius=8`、`Margin="0,4"`（竖向内缩 4，让相邻两行之间留可见留白）、
六档径向渐变、`Opacity=0`。

**行的右键菜单**（`Border.ContextFlyout`，右键触发 `VersionRow_RightTapped`，共 4 项）：

| 菜单项（原文） | 图标 | 行为 |
| --- | --- | --- |
| 问 Nova | `<ImageIcon Source="{StaticResource NovaMarkSmall}" />`（**图片，非字形**） | `AskNova_Click` |
| （分隔线） | — | — |
| 版本详情 | `EBA0` | `VersionDetail_Click` |
| 打开游戏目录 | `EA90` | `OpenVersionFolder_Click` |
| 删除此版本 | `EAA5` | `DeleteVersion_Click` |

**空态** `ctrl:EmptyStateView`：`Glyph="EA94"`、`loc:Loc.Title="暂无已安装版本"`、
`loc:Loc.ActionText="前往下载页安装"`。

**收起行为（代码细节，供实现参考）**：

- 收起后卡片收成 **280×88** 窄条（高度 = 边框 2 + 内边距 24 + 紧凑行 62），
  `HorizontalAlignment=Right` + `VerticalAlignment=Bottom`，再整体右移 `VersionCollapsedShift`
  贴到右栏启动卡左边，间距回到两列间的 24。
- 收起态浮起靠 `VersionCardShadow`：卡片外沿之外的**四条同心描边环带**
  （`#0F000000` / `#0B000000` / `#08000000` / `#04000000`，边距 −4 / −8 / −12 / −16），
  刻意不做垫底实底（卡片底是半透明亚克力，垫底会透出来）；环带靠负边距往外长、不参与布局。
- 七段尺寸的交替由 `SetVersionsCollapsed` 逐帧补间；`OnPageSizeChanged` 只失效缓存不重算位移，
  `SyncCollapsedVersionBar()` 由右栏卡片补间 / 终态调用。
- 过渡期间靠 `VersionCardBody` 挂 `RectangleGeometry` 裁掉溢出的 60 高行模板；稳态 `Clip` 置空。
  裁剪框由补间逐帧直接算，**不能**挂 `SizeChanged` 跟着 `ActualHeight` 走（布局回调晚于补间帧，
  晚的那一拍裁剪框偏大，溢出的内容会被画到卡片外，看着就是抽搐）。

### 5.4 右列 · 账户 / 启动卡（`AccountCard`）

外壳：`GlassCardBrush` + 1px 描边 + `CornerRadius=16` + `Padding=12`。
展开态高度 `NaN` + 拉伸（跟窗口同高）；收起态 **88 高 × 280 宽**、右／底边对齐，
与左栏版本窄条同尺寸（两角各一颗同宽同高的 Dock），由 `SetCardExpanded` 逐帧补间。

内部 `Grid x:Name="CardBody"` 四行（`Auto / * / Auto / Auto`）：

**① 账户模式分段条 `ModeBar`**（`Grid.Row=0`）

- 外框：`ControlFillColorSecondaryBrush` + 1px 描边 + `CornerRadius=10` + `Padding=2`，
  整条 40 高（1 边框 + 2 内缩 + 34 按钮 + 2 + 1）。
- 悬停浮起跟手光斑 `ModeBarGlow`：`CornerRadius=7`，`GlowRadius` **显式给 30**
  （自动半径 = 短边 ×2.5 = 85，在只有 34 高的条里竖直方向淡不完，会被上下沿硬切一刀，
  读成一条上下硬边的灰带；30 时上下沿只剩约 6% 余量，看不出硬边）。
- 选中指示器 `ModeIndicator`：`Height=34`、`CornerRadius=7`、`AccentFillColorDefaultBrush`、
  横跨三列（`ColumnSpan=3` 避免被列裁剪），位置由合成动画平移（260ms / 进场曲线）。
- 三段 `ToggleButton`（等宽 `*`、`Height=34`、`CornerRadius=7`、无边框、透明底）：

| x:Name | 文案 | Tag |
| --- | --- | --- |
| `ModeMsBtn` | 正版 | `microsoft` |
| `ModeExternalBtn` | 外置 | `external` |
| `ModeOfflineBtn`（**未确认命名**） | 离线 | `offline` |

三段自身的选中底色 / 悬停底 / 按下底**全部置空**（避免与指示器叠成两个）：
选中态只保留黑字 `#E61A1A1A`。

> 段宽不写死，交给 `ModeGrid_SizeChanged` 按实测宽度分配，窗口缩放 / 语言切换（文字变长）都不跑偏。
> 取色板与下载页「正式版 / 快照版 / 远古版本」是同一套分段控件语汇。

**② 账户区**（`Grid.Row=1`，`*`）

- 头像：`72×72` 圆角方块，accent 底 + 首字母兜底，皮肤图覆盖。
- 账户名 `AccountNameText`：`FontWeight=Medium`，默认文案 `未选择账户`。
- 「切换账号」按钮：`loc:Loc.Text/Name="切换账号"`，**指针进入该行才淡入**。

**③ 启动 CTA**（`Grid.Row=2`）

- 底部 **60 高**大按钮 `loc:Loc.Name="启动游戏"`：
  第一行 = 自绘轮廓播放 / 停止图标 + `启动游戏`（16px Medium，`LaunchTitleText`）；
  第二行 = 版本名，未选版本时 `未选择版本`。
- 右侧 **32×60 收起刻度**：`loc:Loc.Tip/Name="收起卡片"`。

**④ 状态行**（`Grid.Row=3`，可折叠）

同样**不用** `RowSpacing`：`StatusText` 收起成 `Collapsed`（行高 0）时它两侧的间距照收，
会把启动按钮推开成双倍距离；改由 `Margin` 跟着元素一起收。

> 过渡期间靠 `CardBody_SizeChanged` 挂 `RectangleGeometry` 裁掉溢出内容；稳态 `Clip` 置空。

### 5.5 启动页覆盖层（`LaunchOverlay`）

全屏覆盖，启动时出现。

| 元素 | 文案原文 | 规格 |
| --- | --- | --- |
| 主视觉 | — | 像素镐 SVG（品牌色对角渐变） |
| 标题 | `正在启动`（`LaunchStageTitle`） | 24px |
| 进度 | — | `ProgressBar` |
| 步骤行 | `当前步骤` | 13px、`Opacity=0.7` |
| 登录方式行 | `登录方式` | 13px、`Opacity=0.7` |
| 小贴士卡 | — | 「你知道吗」式小贴士，内容来自 `Services/LaunchTips.cs` |
| 实时日志开关 | `显示日志`（`LaunchLogToggle`，`ToggleButton`） | 12px |
| 打开日志 | `打开日志文件`（`LaunchLogOpenBtn`，`HyperlinkButton`） | 12px |
| 取消 | `取消`（`LaunchCancelBtn`，`GlowButton`） | `HorizontalAlignment=Stretch` |
| 返回主页 | `返回主页`（`LaunchBackBtn`，`Button`） | 与「取消」并列 |

**贴士池**（`Services/LaunchTips.cs`）：`LaunchTips.All` 共 **24 条**中文贴士
（启动器功能 8 + MC 小知识 8 + 常见问题 8），`Next()` 洗牌出队、取空重洗；
`Enqueue(tip)` 供 Nova 动态贴士入队尾。

---

## 6. 版本页 `VersionsPage.xaml`

**入口**：导航第 2 项「版本」（`EA94`）；也可由首页「版本详情」/ 行菜单跳转过来。

**根布局**：`Grid MaxWidth=1280 RowSpacing=16`，两行：`Auto`（工具条）/ `*`（列表卡片）。
页面 `NavigationCacheMode=Disabled`，且 `AllowDrop="True"`
（`DragOver=Page_DragOver` / `Drop=Page_Drop`，支持拖入整合包）。

### 6.1 顶部工具条（`StackPanel Spacing=12`）

| 顺序 | 控件 | 文案 / 内容原文 | 尺寸 |
| --- | --- | --- | --- |
| 1 | 目录框 `Border`（`GlassCardBrush`、`CornerRadius=8`、`Padding="12,8"`） | 前置 `FontIcon EA90` 14px + `DirText`（12px，显示 `Settings.DotMinecraft` 路径） | — |
| 2 | `GlowButton` | 图标 `E90C` + `导入整合包` | — |
| 3 | `GlowButton` | 图标 `E71B` + `从链接导入`（`loc:Loc.Name="从链接导入整合包"`） | — |
| 4 | `TextBox SearchBox` | `loc:Loc.Placeholder="搜索版本 / 类别"`，`loc:Loc.Name="搜索版本"` | `Width=180` |
| 5 | `ComboBox CategoryFilter` | `loc:Loc.Name="按类别筛选"`，选项：全部 / 未分类 / 各实际类别 | `Width=130`、`SelectedIndex=0` |
| 6 | `GlowButton` | 图标 `E918` + `刷新` | — |
| 7 | `GlowButton` | 图标 `EA90` + `打开游戏目录` | — |
| 8 | `ctrl:PluginContributionPanel Point="versions.top"` | 插件挂载区，无贡献不占位 | — |

### 6.2 列表卡片与行结构

外壳 `Border`：`GlassCardBrush` + 1px 描边 + `CornerRadius=16` + `Padding=4`。
列表项 `MinHeight=64`、`Margin="0,1"`；框架各级行底色资源**全部置 `Transparent`**。

行内容 `Grid ColumnSpacing=12 Padding="12,8"`：

| 列 | 宽 | 内容 |
| --- | --- | --- |
| 0 | Auto | 图标块 `Border 32×32 CornerRadius=8`（`SubtleFillColorSecondaryBrush`）；内含 `Image Icon`（32×32）与兜底 `FontIcon EC62` 14px，按 `IconImageVisibility` / `IconFontVisibility` 切显隐 |
| 1 | **300** | 版本名 `Id`（`FontWeight=Medium`，省略号截断） |
| 2 | `*` | 芯片组：`ScChip` + `ScChipText` 显示 `KindText`；类别芯片（`CategoryVisibility`）；「版本隔离」芯片（`IsolatedVisibility`） |
| 3 | Auto（右对齐） | 数据列：`ScMetaLabel`「上次启动」+ `ScMetaValue LastPlayedText`；`ScMetaLabel`「大小」+ `ScMetaValue SizeText`（数值列宽 64） |
| 4 | Auto | 「更多操作」`GlowButton`（`Padding=6`、透明底、无边框、`Flyout=RowMenu`、`loc:Loc.Name/Tip="更多操作"`），图标 `EAA7` 14px |
| 5 | Auto | 删除 `GlowButton`（`loc:Loc.Name/Tip="删除该版本"`），图标 `EAA5` 14px |

行内另有 `VersionRowGlow` 跟手光斑层（六档径向渐变、`CornerRadius=8`、`Margin="0,4"`）。

### 6.3 行菜单（右键与「…」共用 `MenuFlyout x:Key="RowMenu"`）

`Opening` 时从 `Target.DataContext` 取行数据。

| # | 菜单项（原文） | Glyph | 点击行为 |
| --- | --- | --- | --- |
| 1 | 启动 | `EBDF` | `App.PendingLaunchVersionId = row.Id` → 跳转 `home` |
| 2 | 版本详情 | `EBA0` | `NavigateToVersionDetail(row.Id)` |
| 3 | 打开版本文件夹 | `EA94` | 打开 `versions/<id>` |
| 4 | 打开存档文件夹 | `EA90` | 打开实例根 `saves` |
| 5 | 打开模组文件夹 | `EA93` | 打开实例根 `mods` |
| — | （分隔线） | — | — |
| 6 | 重命名 | `EA97` | `InstanceActions.RenameAsync`，失败弹「重命名失败」 |
| 7 | 复制版本 | `EA9F` | `InstanceActions.CopyAsync`，失败弹「复制失败」 |
| 8 | 导出整合包 | `E90A` | `ModPackDialogs.ExportAsync` |
| 9 | 导入整合包 | `E90C` | 文件选择器（`.mrpack/.zip`）→ `ModPackDialogs.ImportAsync` |
| 10 | 从链接导入 | `E71B` | `ModPackDialogs.ImportFromLinkAsync` |
| 11 | 备份与回档 | `EC68` | `ModPackDialogs.BackupAsync` |
| — | （分隔线） | — | — |
| 12 | 问 Nova | `<ImageIcon Source="{StaticResource NovaMarkSmall}" />`（图片） | `NovaRuntime.SetCurrentInstance(row.Id)` + 送入「帮我给 {id} 做一次游戏体检，有隐患直接说」→ `OpenNovaPanel()` |
| 13 | 删除 | `EAA5` | 确认弹窗（见第 17 章） |

> 文件夹入口说明（源码注释）：版本目录固定 `versions/<id>`，存档 / 模组跟随实例根
> （隔离实例各自持有一份，共享实例落在 `.minecraft` 下）。

### 6.4 空态与交互

- 空态 `ctrl:EmptyStateView x:Name="EmptyHint" Glyph="EA94"`，标题**动态**：
  - 无任何版本：`还没有已安装的版本，去「下载」页安装一个吧`
  - 过滤后为空：`没有匹配的版本，试试调整搜索或筛选`
  - 本页未设 `ActionText`，故无操作按钮。
- 列表回填后调用 `MotionFx.StaggerListItems(VersionListView)`。
- 整页拖入 `.mrpack/.zip` 时 `DragUIOverride.Caption = "导入整合包"`。

---

## 7. 版本详情页 `VersionDetailPage.xaml`

**入口**：版本页点行 / 右键「版本详情」→ `NavigateToVersionDetail(id)`。**它不是导航项**。

**根布局**：`Grid MaxWidth=1280` → `ScrollViewer`
→ `StackPanel Spacing=16 Padding="0,4,0,24"`；内部两列 **180 + `*`**、`ColumnSpacing=16`。

### 7.1 头部

| 行 | 内容 |
| --- | --- |
| 1 | 返回按钮 `GlowButton`（透明底无边框）：图标 `E908` + `返回版本列表`（`loc:Loc.Name="返回版本列表"`）；返回走 `MotionService.BackTransition` |
| 2 | `Grid ColumnSpacing=16`（列 `Auto / * / Auto`）：<br>① 图标块 `Border 36×36 CornerRadius=8`（`SubtleFillColorSecondaryBrush`）内 `Image IconImage` 36×36 + 兜底 `FontIcon IconFallback` `EC62` 16px；<br>② `TextBlock TitleText`：**32px Bold**（全站唯一的大号数据标题）；<br>③ 芯片行：`CategoryBadge`（`Padding="8,2"`、圆角 4）+ `CategoryBadgeText`；`IsolatedBadge` + `版本隔离`；<br>④ 右侧按钮：更换图标 `GlowButton`（`loc:Loc.Name/Tip="更换图标"`，图标 `EAA3`）+ 启动 `GlowButton LaunchBtn`（`loc:Loc.Name="启动该版本"`，图标 `EBDF` + `启动`） |

**运行中横幅** `InfoBar RunningBanner`：`Severity=Warning`、`IsClosable=False`，
Title = `该版本正在运行`，Message = `游戏进程存活期间无法修改其隔离方式或重命名。`

下方 `ctrl:PluginContributionPanel Point="version.detail.top"`。

### 7.2 左侧分区导航（180px）

容器 `Border`（`Padding="8"`、圆角 8）内 `ListView x:Name="NavList"`，条目 `MinHeight=40`：

| x:Name | Tag | Glyph | 文案原文 | 默认 |
| --- | --- | --- | --- | --- |
| `NavOverview` | `"0"` | `EC77` | 概览 | ✅ 选中 + `IsActive` |
| `NavContent` | `"1"` | `EA93` | Mod 管理 | — |
| `NavSettings` | `"2"` | `EC04` | 游戏设置 | — |
| `NavArgs` | `"3"` | `EA6D` | 启动参数 | — |

切换由 `SetSection(i)` 控制 `SecOverview` / `SecContent` / `SecSettings` / `SecArgs` 显隐，
并做 **220ms 淡入上移**（动效关闭时不播，`PlayFadeIn` 在 `!Motion.Enabled` 时直接 return）。

### 7.3 分区「概览」`SecOverview`

每张卡都是 `ScCard`，抬头 = `ScSectionSpine`（3×14 accent 脊柱）+ `ScSectionTitle`。

| 卡片抬头 | 字段 / 控件 |
| --- | --- |
| 概览 | `VdLabel`「游戏目录」+ `VdValue GameRootText`（Consolas、可选中）；「上次启动」+ `LastPlayedText` |
| 个性化 | `VdLabel`「类别」+ `TextBox CategoryBox`（`Placeholder="未分类"`、`MaxLength=24`）；「备注」+ `TextBox NoteBox`（`Placeholder="给这个版本写点什么…"`、`AcceptsReturn`、`MinHeight=72`、`MaxLength=500`） |
| 快捷方式 | 四个 `GlowButton`（`VdActionBtn`，`MinWidth=148 MinHeight=40`）：`版本文件夹`(`EA94`) / `存档文件夹`(`EA90`) / `模组文件夹`(`EA93`) / `截图文件夹`(`EA90`) |
| 高级管理 | `验证文件`(`EA7C`) / `重命名`(`EA97`) / `复制版本`(`EA9F`) / `删除版本`(`EAA5`，`Foreground=SystemFillColorCriticalBrush` 危险色) |

末尾另有 `ctrl:PluginContributionPanel Point="version.detail.sections" Chrome="Section"`。

### 7.4 分区「Mod 管理」`SecContent`

- 顶部分类芯片行（`Border` 圆角 12，`ChipStyle` 圆角 8、字号 14）：
  `Mod 模组` / `资源包` / `光影包` / `存档`（Tag 0/1/2/3；**前三个点击还会打开下载弹窗**）。
- 内容卡 `ContentCard`（`AllowDrop`）：
  - 抬头「内容」，右侧 `ModTools`：「检查更新」「冲突扫描」（**仅 Mod 分类可见**）；
  - 说明行 `ContentHint`：`可直接把 mods / 资源包 / 光影 / 数据包文件拖到此处安装；停用仅改名为 .disabled，可随时还原。`
  - `InfoBar ContentInfoBar`（`ShowContentInfo` 动态填充，如
    `已扫描 {0} 个模组，未发现冲突。`、`发现 {0} 个冲突。[{1}] {2}　建议：{3}`）。
- 四个子面板 `ModSection` / `PackSection` / `ShaderSection` / `SaveSection`；
  分区标题由 `SectionHeader("[{0}（{1}）]")` 生成。
- 文件行结构：类型字形 + 文件名（14px）+ 状态（12px）+ 大小（12px）+「启停」按钮 +「删除」按钮（红字）。
- 分类收尾：有内容 → 「路径 + 打开目录」；空分类 → `EmptyStateView`：

| 分类 | 空态标题 | 副标题 | 操作 |
| --- | --- | --- | --- |
| Mod | `还没有安装 Mod` | `把 .jar / .zip 拖进这张卡片即可安装；停用仅改名为 .disabled，可随时还原。` | `打开目录` |
| 资源包 | `还没有资源包` | 同上 | `打开目录` |
| 光影包 | `还没有光影包` | 同上 | `打开目录` |
| 存档 | `暂无存档` | `把存档文件夹放进 saves 目录即可。` | `打开目录` |

### 7.5 分区「游戏设置」`SecSettings`

抬头「游戏设置」，说明：`各项默认跟随全局设置；修改后立即保存。带「恢复跟随」的项可清除本版本的覆盖值。`
`InfoBar SettingsRunningBanner`（Warning）：`该版本正在运行，设置将在下次启动生效。`

每个子项都是「`HyperlinkButton`「恢复跟随」+ `RadioButtons`（跟随 / 自动 / 自定义）」的三态结构：

| 子项 | 单选项（原文） | 附加控件 |
| --- | --- | --- |
| 版本隔离 | `跟随全局默认` / `共享目录（.minecraft）` / `版本隔离（versions/<版本名>）` | 预览 `GameRootPreview`：`游戏目录：{0}` |
| 内存分配 | `跟随全局设置` / `自动配置` / `自定义` | `MaxMemSlider`（1–16，步 1）；标签「最大内存（整数 GB）」；`MemAutoHint`；占用条（底槽 + `MemoryAllocBar` / `MemoryUsedBar`，`260×7`）；`MemoryUsageText = 已使用 {0} / 总 {1}　·　本版本分配 {2}` |
| Java | `跟随全局设置` / `自动配置（按版本推荐）` / `自定义` | `ComboBox JavaCombo Width=440`（`Placeholder="未选择（将自动匹配）"`）+「浏览…」；`JavaHintText` |
| 游戏窗口 | `跟随全局设置` / `自动配置（854 × 480 窗口化）` / `自定义` | 两个 `NumberBox`（200–32767，步 10，`Header`「宽」/「高」）；`RadioButtons`：`跟随全局默认` / `开启全屏` / `关闭全屏`；`WindowHintText` |
| 游戏语言 | `跟随全局` / `简体中文（zh_CN）` / `繁體中文（zh_TW）` / `English（en_US）` / `日本語（ja_JP）` | `LangHintText` |

### 7.6 分区「启动参数」`SecArgs`

抬头「启动参数」，说明：`连服地址、环境变量、启动前后命令与额外参数；修改后立即保存。`

| 项 | 控件与文案 |
| --- | --- |
| 自动连服地址 | `TextBox ServerBox`，`Placeholder="例如 play.example.com 或 play.example.com:25565"`；`ServerHintText` |
| 环境变量 | `EnvPanel` + 「添加变量」`GlowButton`（`loc:Loc.Name="添加环境变量"`）；每行 = 名称框(200) / 值框(360) + 删除按钮（`\uEAA5`）；非法或重名时黄字 `EnvWarnText` |
| 启动前命令 | `PreCmdBox`，`Placeholder="例如 echo 准备启动"`；说明 `启动前执行；退出码非 0 会询问是否继续。支持 $GAME_DIR 占位符。` |
| 启动后命令 | `PostCmdBox`，`Placeholder="例如 echo 已退出"`；说明 `游戏退出后执行；失败仅记录日志。支持 $GAME_DIR 占位符。` |
| 额外 JVM 参数 | `ExtraJvmBox`，`Placeholder="每行一个参数，例如\n-XX:+UseG1GC"`；说明 `追加到 JVM 参数区；空行忽略。` |
| 额外游戏参数 | `ExtraGameBox`，`Placeholder="每行一个参数"`；说明 `追加到游戏参数区；空行忽略。` |

### 7.7 本页 InfoBar 汇总

| 名称 | 标题 | 正文 |
| --- | --- | --- |
| `RunningBanner` | `该版本正在运行` | `游戏进程存活期间无法修改其隔离方式或重命名。` |
| `SettingsRunningBanner` | `该版本正在运行，设置将在下次启动生效。` | （标题即正文） |
| `ContentInfoBar` | 动态 | 扫描 / 冲突结果 |

---

## 8. 下载页 `DownloadPage.xaml`（重点章节）

**入口**：导航第 3 项「下载」（`E90C`）。

**根布局**：

```
Grid (MaxWidth=1280, HorizontalAlignment=Stretch)
└─ Grid (ColumnSpacing=16)
   ├─ Column 0 = 180px   ← 分类导航卡片（Margin="0,24,0,0"）
   └─ Column 1 = *       ← 内容区（RowSpacing=12，三行 Auto / Auto / *）
      ├─ Row 0 Auto  空行（只贡献 RowSpacing，用来对齐左栏的 24 上边距）
      ├─ Row 1 Auto  ctrl:PluginContributionPanel Point="download.top"
      └─ Row 2 *     当前分类面板（VanillaPanel / CommunityPanel，二者叠在同一格）
另有一张同列定义的浮层 Grid（见 8.6）
```

> **左栏上边距为什么是 24**：24 = 右栏 `RowSpacing(12)` × 两个空行
> （提示区收起时高度为 0、插件挂载区无贡献）。右栏首个可见元素落在 24 之下，
> 左栏不补同样的边距就比内容区高出一截。
>
> **左栏底色为什么是 `GlassCardBrush`**：页面底是 `#232324` 一档，
> `AppPaneBackgroundBrush(#161616)` 比它更暗，会读成「挖下去一块」而不是卡片。

### 8.1 左侧分类导航（180px）

容器 `Border`：`VerticalAlignment=Stretch`、`Margin="0,24,0,0"`、`GlassCardBrush`、
`CardStrokeColorDefaultBrush` 1px、`CornerRadius=8`、`Padding=8`。
内为 `ListView x:Name="CategoryList"`（`SelectionMode=Single`，`SelectionChanged=Category_SelectionChanged`），
条目样式：`MinHeight=40`、`Padding=0`、`Margin=0`、横向与纵向都 `Stretch`。

`ListView.Resources` 里把框架六个整行底色 key **全部置 `Transparent`**
（`ListViewItemBackground` / `PointerOver` / `Pressed` / `Selected` / `SelectedPointerOver` / `SelectedPressed`）——
框架给的是**硬边直角矩形**，压上圆角光斑后四个角会互相啃掉一块。
悬停 / 选中反馈只由 `ctrl:CategoryNavItem` 模板里的三层表达（选中底 / 悬停底 / 跟手光斑，
三层圆角都读同一个 `CornerRadius=10`）。

**逐项清单**（这是用户最关心的「二级导航」）：

| # | `ListViewItem.Tag` | x:Name | Glyph | 显示文案 | 说明 |
| --- | --- | --- | --- | --- | --- |
| 1 | `vanilla` | `CatVanilla` | `EC20` | **原版游戏** | `IsSelected="True"`、`IsActive="True"`，默认选中 |
| — | （无 Tag，`IsEnabled=False IsHitTestVisible=False`） | — | — | **社区资源** | **分组标题**，不是导航项：12px、`TextFillColorTertiaryBrush`、`Margin="10,8,0,2"` |
| 2 | `mod` | `CatMod` | `EA93` | **Mod** | `Content="Mod"`（英文，非 loc） |
| 3 | `modpack` | `CatModpack` | `ECAF` | **整合包** | |
| 4 | `datapack` | `CatDatapack` | `EC24` | **数据包** | |
| 5 | `resourcepack` | `CatResourcepack` | `EC49` | **资源包** | |
| 6 | `shader` | `CatShader` | `EC0C` | **光影包** | |

- **图标位置**：导航项文字**左侧**，`Spacing=10`、行内 `Padding="8,0"`，图标 `FontSize=14`。
- **图标来源**：全部来自 `Assets/Fonts/HarmonyOSIcons.ttf`（`ScIconFont`），逐元素指定，
  见第 3 章。**没有任何图片图标**（第 2 项起）。第 1 项 `EC20` 亦为字体字形。
- **选中态**：`CategoryNavItem.IsActive` 驱动模板里 `ActiveLayer`（`ScRowShadeBrush`）不透明度，
  与悬停底 `HoverLayer`、跟手光斑 `Glow` 三层严格重合（同一 `CornerRadius`）。
- **点击行为**：`Category_SelectionChanged` 读 `SelectedItem.Tag` 判断分类，
  设置各导航项 `IsActive`，然后在「原版面板」与「社区面板」之间做**交叉淡入淡出**（见 8.7）。
  切到社区分类时还会重建筛选行并触发一次搜索。

### 8.2 原版游戏面板（`VanillaPanel` → `ListPanel`）

`VanillaPanel` 内含两个**叠在同一格**的子面板：`ListPanel`（列表视图）与
`DetailPanel`（详情视图，默认 `Collapsed`）。

`ListPanel` 三行 `Auto / Auto / *`：

#### 8.2.1 工具条（`Grid.ColumnSpacing=12`）

| 列 | 控件 | 细节 |
| --- | --- | --- |
| 0 | 版本类型分段控件 | 外框 `Border`（`ControlFillColorSecondaryBrush` + `CardStrokeColorDefaultBrush` 1px、`CornerRadius=11`、`Padding=3`）；内为 `TypeIndicator`（`88×30`、`CornerRadius=8`、`AccentFillColorDefaultBrush`，位置由合成动画平移）+ 三段等宽 `ToggleButton`（`88×30`、`CornerRadius=8`、无边框、透明底、13px）：**正式版**(`release`，`IsChecked=True`) / **快照版**(`snapshot`) / **远古版本**(`old`)。三段自带的选中底色 / 悬停 / 按下底刷**全部置空**，选中态只保留黑字 `#E61A1A1A`（accent 面强制黑字） |
| 1 | 搜索框 + 缓存徽标 | `StackPanel` 靠左、`Spacing=10`：`Grid MaxWidth=320 Width=300` 内 `TextBox SearchBox`（`Height=32`、`Padding="32,5,10,5"`、`loc:Loc.Placeholder="搜索版本 ID…"`）+ 前置 `FontIcon EA6F` 14px（`Margin="12,0,0,0"`、`IsHitTestVisible=False`、次级色）；右侧 `VersionCacheBadge`（默认 `Collapsed`，`CornerRadius=6`、`Padding="8,3"`、`AccentFillColorTertiaryBrush`）内 `缓存` 11px 黑字 |
| 2 | `Auto` | （列已声明，本行无内容） |

#### 8.2.2 Hero 瓦片（`LatestCard`，`ColumnSpacing=12`，默认 `Collapsed`）

两枚**等宽**（各 `*`）瓦片，点击直接进详情（`LatestTile_Click`）：

| 项 | 左瓦片 `LatestReleaseTile` | 右瓦片 `LatestSnapshotTile` |
| --- | --- | --- |
| 样式 | `ScStaticCardButtonStyle`（**一点悬停反馈都不要**） | 同左 |
| 外观 | `GlassCardBrush` + 1px 描边 + `CornerRadius=14` + `Padding="14,12"` + `Margin=0` | 同左 |
| 术语（第一个文字行，11px 次级色） | **最新正式版** | **最新预览版** |
| 版本号（17px Medium） | `LatestReleaseId` | `LatestSnapshotId` |
| 元信息（11px 次级色） | `LatestReleaseMeta` | `LatestSnapshotMeta` |
| 图标槽 | `Grid 36×36`：底层兜底 `FontIcon ECAF` 18px，上层 `Image LatestReleaseIcon` 36×36 `Stretch=Uniform` | `Image LatestSnapshotIcon` |
| 行尾 | `FontIcon E904` 12px（`TextFillColorTertiaryBrush`） | 同左 |

> 取不到对应版本时**整块 `LatestCard` 隐藏**（不是留空瓦片）。

#### 8.2.3 版本列表（`Grid.Row=2`）

外壳 `Border`：`GlassCardBrush` + 1px 描边 + `CornerRadius=16` + `Padding=4`。
`ListView x:Name="VersionList"`（`SelectionMode=Single`、`IsItemClickEnabled=True`、
`ItemClick=VersionList_ItemClick`），条目 `MinHeight=60`、`Margin="0,1"`、纵向 `Stretch`
（默认 `Center` 会让承载光斑的行比行矮一圈，上下留下「不亮光斑的死区」），
框架六个行底色 key 全部置 `Transparent`。

行模板 `VersionFlatTemplate`（`x:DataType="rows:DownloadRow"`，**全部版本统一平铺**，
外观与主页版本行一致）：

| 列 | 宽 | 内容 |
| --- | --- | --- |
| 0 | Auto | 图标块 `Border RowIconHost 32×32 CornerRadius=8`（`SubtleFillColorSecondaryBrush`）内兜底 `FontIcon ECAF` 14px + `Image Icon` 32×32 `Stretch=Uniform`（取不到图标时留空底块） |
| 1 | `*` | `Id`（14px Medium，省略号截断）+ `TimeText`（11px 次级色，省略号截断），`Spacing=2` |
| 2 | Auto | `Badge`（11px，`Foreground={x:Bind BadgeBrush}`，`Margin="8,0,2,0"`） |

光斑层 `RowShadow`：`CornerRadius=8`、`Margin="0,4"`（竖向刻意内缩 4）、六档径向渐变、`Opacity=0`。

**行模型 `DownloadRow`**：`(string Id, string TimeText, string Badge, Brush? BadgeBrush, ImageSource? Icon)`。

**数据填入**（`DownloadPage.xaml.cs`）：全部版本（含正式版）一起平铺到同一个列表，
填完后 `MotionFx.StaggerListItems(VersionList)`。

### 8.3 原版游戏 → 详情面板（`DetailPanel`）

三行 `Auto / * / Auto`。

**① 标题行**（`Grid ColumnSpacing=12`，列 `Auto / Auto / *`）

| 元素 | 细节 |
| --- | --- |
| 返回 | `GlowButton`（`Padding="10,8"`、`CornerRadius=8`、透明底）：图标 `E908` 14px，`loc:Loc.Name/Tip="返回"` |
| 图标 | `Grid DetailIconHost 36×36`：底层兜底 `FontIcon ECAF` 18px（次级色），上层 `Image DetailIcon` 36×36 |
| 版本号框 | `Border`（`ControlFillColorDefaultBrush` + `ControlStrokeColorDefaultBrush` 1px、`CornerRadius=8`、`Padding="16,8"`）内 `DetailTitle`（18px Medium）+ `DetailMeta`（11px 次级色），`Spacing=1` |

**② 加载器抽屉（手风琴）** — `ScrollViewer VerticalAlignment=Top` → `StackPanel Spacing=10`，
共 **4 个 `Expander` + 1 张静态卡**：

| 顺序 | 控件 | 标题 | 说明 |
| --- | --- | --- | --- |
| 1 | `ForgeExpander` | `Forge` | `TextBlock Text="Forge"`（**硬编码英文，非 loc**） |
| 2 | `NeoForgeExpander` | `NeoForge` | 同上 |
| 3 | `FabricExpander` | `Fabric` | 同上 |
| 4 | `QuiltExpander` | `Quilt` | 同上 |
| 5 | `Border`（OptiFine 卡） | `OptiFine` | `Opacity="0.6"`；右侧 `loc:Loc.Text="暂不支持"` 12px 三级色。**不是 Expander** |

> 源码注释：选中任一构建后**其余加载器锁定点不动**（禁用后续切换）。

每个 `Expander` 的 `Header` 是 `Grid ColumnSpacing=12`（`Auto / *`）：
`Forge`（Medium）+ 右侧状态文本（如 `获取中…`，`loc:Loc.Text="获取中…"`，12px 次级色）。

内容 `StackPanel Spacing=10 Padding="4,4,4,8"`：

1. **推荐卡** `GlowButton`（`HorizontalAlignment=Stretch`、`HorizontalContentAlignment=Stretch`、
   `CornerRadius=8`、`Padding="12,9"`、`BorderThickness=1`），`Click` 直接选中该构建。
   内容 `Grid ColumnSpacing=12`（`* / Auto`）：
   - 左 `StackPanel Spacing=2`：`ForgeRecVersion`（Medium）+ `ForgeRecNote`（11px 次级色，**推荐卡的副标题**）；
   - 右 `Border ForgeRecBadge`（`SystemFillColorSuccessBackgroundBrush`、`CornerRadius=4`、`Padding="7,2"`）
     内 `ForgeRecBadgeText`（11px、`SystemFillColorSuccessBrush`），文案在 `推荐` / `已选` 之间切换。
2. **计数文案** `ForgeCountText`：`全部版本 (0)`（13px Medium、次级色、`Margin="4,0,0,0"`）。
3. **构建列表** `ListView ForgeBuildList`（`SelectionMode=None`、`IsItemClickEnabled=True`、
   内部滚动禁用、条目 `MinHeight=48`、`Margin="0,1"`、纵向 `Stretch`），行模板 `LoaderBuildRow`：
   - 行容器 `Grid CornerRadius=8` + 跟手光斑 `BuildGlow`（载体与行同形）；
   - 内容 `Grid Padding="10,7" ColumnSpacing=12`（`* / Auto`）：`Version`（Medium）+ `Sub`（11px 次级色）；
     右列 `Border`（`AccentFillColorDefaultBrush`、`CornerRadius=4`、`Padding="7,2"`，
     `Visibility={x:Bind SelectedVisibility, Mode=OneWay}`）内 `已选` 11px（`TextOnAccentFillColorPrimaryBrush`）。
4. **空态** `ForgeEmptyText`：`loc:Loc.Text="暂无可用构建"`（12px 三级色，`Collapsed`）。
   Quilt 另有「暂不支持」分支文案（见 8.3 表末注）。

> **构建行为什么做成可变 + 通知**（源码注释）：选中某个构建时只改这一行的状态、
   列表不重建——重建会让整个抽屉在点击的一瞬间闪一下、滚动位置也回顶，读起来就是「不丝滑」。
   首次铺开时 `MotionFx.StaggerListItems(list, maxItems: 6)` 做错峰入场。

**`LoaderBuildRow`**：`Version` / `Sub` / `Selected`（可变，`INotifyPropertyChanged`，
通知属性是 `SelectedVisibility`）。

**③ 底部操作区**（`StackPanel Spacing=6`）

| 元素 | 细节 |
| --- | --- |
| 状态文本 `DetailStateText` | 居中、12px 次级色 |
| 主 CTA `StartBtn` | 居中胶囊大按钮：`Padding="30,10"`、`CornerRadius=24`、`AccentButtonStyle`、`FontWeight=Medium`；内容 = `FontIcon E90C` 16px + `开始下载`（15px Medium） |

### 8.4 社区资源面板（`CommunityPanel`）

三行 `Auto / *（MinHeight=260）/ Auto`。

#### 8.4.1 筛选卡

`Border`（`GlassCardBrush` + 1px 描边 + `CornerRadius=16` + `Padding=16`）→ `StackPanel Spacing=12`：

- 标题 `SearchTitle`（16px Medium）——**随分类动态**：
  `搜索 Mod` / `搜索整合包` / `搜索数据包` / `搜索资源包` / `搜索光影包`
  （由 `Sections` 表驱动：`("mod","搜索 Mod","mods","安装")`、
  `("modpack","搜索整合包","modpacks","下载")`、`("datapack","搜索数据包","datapacks","安装")`、
  `("resourcepack","搜索资源包","resourcepacks","安装")`、`("shader","搜索光影包","shaderpacks","安装")`）。
- 筛选网格 `Grid ColumnSpacing=24 RowSpacing=12`（列 `* / Auto`，行 3 × `Auto`）：

| 行 | 列 | 标签 | 控件与选项原文 |
| --- | --- | --- | --- |
| 0 | 0 | `名称` | `TextBox CommunitySearchBox`，`loc:Loc.Placeholder="如：Sodium、JEI、Create…"` |
| 0 | 1 | `分类` | `ComboBox CategoryCombo MinWidth=170`（项**按资源类型动态加载**；无分类的类型整行隐藏） |
| 1 | 0 | `版本` | `ComboBox VersionCombo`，`IsEditable="True"`，`loc:Loc.Placeholder="全部 (也可自行输入)"`，`MaxDropDownHeight=360` |
| 1 | 1 | `加载器` | `ComboBox LoaderCombo MinWidth=170`——**仅 Mod 分区可见**：`自动检测`(`auto`，`IsSelected=True`) / `全部`(`any`) / `Fabric` / `Forge` / `NeoForge` / `Quilt`（后四者为英文硬编码） |
| 2 | 0 | — | 「搜索」`Button`（`AccentButtonStyle`、`MinWidth=110`，图标 `EA6F` 14px + 文字 `搜索`）+ 「重置条件」`GlowButton`（`MinWidth=110`） |
| 2 | 1 | `来源` | `ComboBox SourceCombo MinWidth=140`：`Modrinth`(`modrinth`，`IsSelected=True`) / `CurseForge`(`curseforge`) |
| 2 | 1 | `排序` | `ComboBox SortCombo MinWidth=170`：`相关性`(`relevance`，`IsSelected=True`) / `下载最多`(`downloads`) / `最新发布`(`newest`) / `最近更新`(`updated`) |

- **来源联动**（`UpdateSourceUi`）：选 CurseForge 时 `CategoryCombo.IsEnabled=False`、`Opacity=0.5`，
  且 `CacheBadge` 隐藏。

#### 8.4.2 结果列表

`Border`（`GlassCardBrush` + 1px 描边 + `CornerRadius=16` + `Padding=4`）内 `Grid`：

- `ListView x:Name="ModList"`（`SelectionMode=None`；条目 `MinHeight=68`、`Margin="0,1"`、
  纵向 `Stretch`；六个行底色 key 置 `Transparent`）。
- 行模板 `ModRow`（`x:DataType="rows:ModRow"`），**整卡可点**（`Tapped="ModRow_Tapped"`
  → 打开资源下载窗，PCL2 式）：

| 列 | 宽 | 内容 |
| --- | --- | --- |
| 0 | Auto | 图标 `Grid 48×48 CornerRadius=8`：底层兜底 `FontIcon EA93` 20px（次级色，图片解码失败时可见），上层 `Image Icon` `Stretch=UniformToFill` |
| 1 | `*` | `StackPanel Spacing=3`：<br>① 标题行 `StackPanel Horizontal Spacing=8`：`Title`（14px Medium、`MaxWidth=420`、省略号）+ 标签 `ItemsControl`（`Tags`，每个标签是 `Border`：`ControlFillColorDefaultBrush` + 1px 描边 + `CornerRadius=4` + `Padding="6,1"`，11px 次级色）<br>② `Description`（12px 三级色、`MaxLines=1`、省略号；**中文译文到达后原地替换英文简介**）<br>③ 元信息行 `StackPanel Horizontal Spacing=12`：`LoadersText` / `DownloadsText` / `UpdatedText`（均 12px 次级色） |

  光斑层 `ModRowGlow`（`CornerRadius=8`，铺满整行**不带行内边距**；内容再靠 `Padding` 让开——
  光斑若跟内容一起内缩就会比行小一圈，外面露出框架底色 → 两层框）。

- **空态** `ctrl:EmptyStateView EmptyHint`（居中，默认 `Collapsed`）：`Glyph="EA6F"`，
  `loc:Loc.Title="没有找到相关资源，换个关键词或筛选条件试试"`。

**行模型 `ModRow`**：`ProjectId` / `Title` / `IconUrl` / `Author` / `Tags` / `LoadersText` /
`DownloadsText` / `UpdatedText` / `Icon` + 可变 `Description`（`INotifyPropertyChanged`，
模板用 `OneWay` 绑定）。

**分类中文化**：`CategoryNames` 字典把 Modrinth 分类 slug 翻成中文，未映射的回落到原 slug。
已确认条目示例：`adventure`→`冒险`、`cursed`→`猎奇`、`decoration`→`装饰`、
`economy`→`经济`、`equipment`→`装备`、`food`→`食物与烹饪`（完整表见
`DownloadPage.xaml.cs` 的 `CategoryNames`）。

#### 8.4.3 分页

`Grid ColumnSpacing=8`（列 `* / Auto`）：

- 左 `StackPanel`（`Spacing=8`）：`ResultCountText`（结果数，12px 次级色）+
  `TranslatedHint`（默认 `Collapsed`，12px **accent 色**，提示「中文搜索词被翻译成英文时实际用于搜索的词」）+
  `CacheBadge`（默认 `Collapsed`，`CornerRadius=4`、`Padding="8,2"`、`AccentFillColorTertiaryBrush`）内 `缓存` 11px。
- 右 `StackPanel Spacing=8`：`上一页`(`PrevBtn`) / `下一页`(`NextBtn`)，均为 `GlowButton`。

### 8.5 页面级提示与进度浮层

**独立的一层 `Grid`**，与主体共用同一套列定义（`180` + `ColumnSpacing 16`），
`StackPanel` 落在**右列顶部**、`VerticalAlignment=Top`：

| 元素 | 类型 | 标题原文 | 默认 |
| --- | --- | --- | --- |
| `ErrorBar` | `InfoBar Severity=Error` | `操作失败` | `IsOpen=False`、可关闭 |
| `OkBar` | `InfoBar Severity=Success` | `完成` | `IsOpen=False`、可关闭 |
| `InstallProgress` | `ProgressBar` | — | `IsIndeterminate=True`、`Collapsed` |
| `ProgressText` | `TextBlock`（12px 次级色，省略号） | — | `Collapsed` |

> 它们**不再占右栏高度**：下载时进度条浮在内容上方，不会把内容区推下去。
> 这也解释了右栏 Row 0 为何没有子元素——它只靠 `RowSpacing` 与左栏的上边距对齐。

### 8.6 分类切换与过渡动画（`DownloadPage.xaml.cs`）

**面板交叉过渡**（`ShowPanel` 一类方法）：
两个面板叠在同一格，旧面板快速淡出、新面板从下方浮入（合成动画，不闪白）：

```
MotionFx.Reset(incoming);
incoming.Visibility = Visible;
MotionFx.FadeSlideIn(incoming, 18f, MotionFx.EnterMs);      // 375ms，fromY=18
if (outgoing 可见) {
    MotionFx.FadeOut(outgoing, MotionFx.ExitMs);            // 210ms
    await Task.Delay(MotionFx.ExitMs + 20);
    outgoing.Visibility = Collapsed;
    MotionFx.Reset(outgoing);
}
```

**列表 ↔ 详情过渡**：

| 方向 | 动画 |
| --- | --- |
| → 详情 | `ListPanel` 收 → `MotionFx.Reset(DetailPanel)` → `FadeSlideIn(DetailPanel, 22f, EnterMs)`；同时尝试 `ConnectedAnimationService` 让列表图标「飞」到详情页头图（框架原生，失败静默） |
| ← 返回 | `DetailPanel` 收 → `MotionFx.Reset(ListPanel)` → `FadeSlideIn(ListPanel, -14f, EnterMs)`（从上方落回） |

**分段指示器平移**：`anim.Duration = MotionFx.Scale(260)`（= 390ms），缓动 `MotionFx.ControlEase`，
动画属性 `Translation`。

**列表入场**：`MotionFx.StaggerListItems(VersionList)`；
加载器构建列表首次铺开 `StaggerListItems(list, maxItems: 6)`。

上述所有 `MotionFx` 入口都会先查 `AppServices.Instance.Motion.Enabled`，关闭时直接落终态。

---

## 9. 存档页 `SavesPage.xaml`

**入口**：导航第 4 项「存档」（`EA90`）。

**根布局**：`Grid MaxWidth=1280 RowSpacing=16`，四行：
① 状态提示 / ② 跨版本运行态 / ③ 工具条 / ④ 世界列表。

### 9.1 状态条

| 名称 | 类型 | 说明 |
| --- | --- | --- |
| `OkBar` | `InfoBar Severity=Success`，可关闭 | 动态消息，如 `已备份到 {0}` |
| `ErrorBar` | `InfoBar Severity=Error`，可关闭 | 如 `备份失败：{0}`、`已复制到 {0}：{1}`、`没有其他已安装版本，请先在下载页安装一个版本` |
| `RunBar` | `InfoBar Severity=Informational`，**不可关闭** | Title = `正在运行`，`ActionButton` = `停止游戏`；Message = `正在以 {0} 运行「{1}」` |

### 9.2 工具条

| 位置 | 控件 | 文案 | 启用条件 |
| --- | --- | --- | --- |
| 左 | `Button` | `用其他版本启动` | `AccentButtonStyle`；**未选中行时禁用** |
| 左 | `Button` | `复制到其他版本` | 未选中行时禁用 |
| 左 | `Button` | `备份所选世界` | 未选中行时禁用 |
| 左 | `Button` | `删除` | 未选中行时禁用 |
| 左 | `Button` | `打开世界文件夹` | 未选中行时禁用 |
| 右 | `AutoSuggestBox SearchBox` | `Placeholder="搜索世界 / 版本"`，`QueryIcon` 用字形 `EA6F` | `Width=200` |
| 右 | `ComboBox VersionFilter` | 全部 / 共享 / 各已装版本 | `MinWidth=150` |
| 右 | `Button` | `打开存档目录` | — |
| 右 | 刷新按钮 | 图标 `E918`，`loc:Loc.Name/Tip="刷新"` | — |

### 9.3 世界列表行（`SaveRow`）

行 `MinHeight=72`、`Margin="0,2"`，内含 `SaveCardGlow` 跟手光斑层（六档径向渐变、圆角 8）。

| 列 | 内容 |
| --- | --- |
| 0 | 图标块 `Border 56×56 CornerRadius=8`（`LayerFillColorDefaultBrush` + 1px 描边）：兜底 `FontIcon EA90` 22px + `Image WorldIcon`（`x:Load={x:Bind HasIcon}`、`UniformToFill`） |
| 1 | 名称块：世界名 `Name`（Medium）+ 文件夹名 `FolderName`（12px 次级色），`MaxWidth=420` |
| 2 | 芯片：所属版本 `VersionLabel`（`ScChip`）；隔离芯片 `隔离`（`IsolatedVisibility`） |
| 3 | 数据列（标签宽 Auto，数值宽 72）：`ScMetaLabel`「上次游玩」+ `ScMetaValue LastPlayedText`；`ScMetaLabel`「大小」+ `ScMetaValue SizeText` |

> `VersionLabel` 在**共享实例**下取字面量 `共享`（该值是路径豁免项，不参与本地化）。

**大小格式化** `FormatSize`：`<= 0` → `空`，否则 KB / MB / GB。

### 9.4 空态

`ctrl:EmptyStateView x:Name="EmptyHint" Glyph="EA90"`：

| 情形 | 标题 | 副标题 |
| --- | --- | --- |
| 无存档 | `暂无存档` | `启动游戏创建世界后，可在此进行备份与管理` |
| 过滤无结果 | `没有匹配的存档` | `试试换个关键词或版本筛选` |

### 9.5 跨版本流程（弹窗文案见 17 章）

- 标题：`用其他版本打开世界`。
- 正文含：`InfoBar`（`warn`）、版本 `ComboBox`、`basic` =
  `基础启动模式：不含文件校验与自动修复，失败请回首页启动。`、
  `account` = `使用账户：{0}（{1}）`（括号内是 `正版` 或 `第三方`）、
  `CheckBox` = `我已了解跨版本风险`。
- `warn.Message` 分两种：
  - 同根：`此世界来自 {0}，与 {1} 使用同一游戏目录，无需复制。`
  - 跨根：`此世界来自 {0}，将由 {1} 打开。跨版本打开可能触发世界数据升级，且改动不可逆。`
- 按钮随选中动态：同根 → Primary `启动`、Secondary 隐藏；
  跨根 → Primary `复制并启动`、Secondary `仅复制`；关闭 `取消`。
- 成果归属弹窗标题 `世界成果归属`，见 17 章。
- 回传提示：`已用副本替换源存档，原存档备份在 {0}`；
  失败 `回传失败：{0}（副本仍在 {1}）`。

---

## 10. 联机页 `MultiplayerPage.xaml`

**入口**：导航第 5 项「联机」（`EB91`）。

**根布局**：`Grid MaxWidth=880` → `ScrollViewer`（`Padding="0,0,8,0"`）→ `StackPanel Spacing=16`。

### 10.1 页头与提示

| 元素 | 内容 |
| --- | --- |
| 右侧 `ScMetaLabel` | `联机方案` |
| `ComboBox ProviderCombo` | `MinWidth=190`；第三方项文案 = `{DisplayName} · {Vendor}`，内置项 = 仅 `DisplayName` |
| `InfoBar NoticeBar` | 默认收起，可关闭 |

### 10.2 状态卡（`ScCard`）

三列：`ScMetaLabel` `联机核心` / `核心会话` / `当前状态`，对应 `CoreText` / `SessionText` / `PhaseText`。

| 字段 | 取值 |
| --- | --- |
| `CoreText` | `v{Version}` 或 `未安装` |
| `SessionText` | `运行中` / `已停止` |
| `PhaseText` | 按状态着色：`Failed` 用 `DangerBrush`；`HostReady` / `GuestReady` 用 `SuccessBrush` |

- `StatusHint`（`ScCardHint`，仅需用户操作时显示）。
- 安装进度 `InstallPanel`：`ProgressBar InstallBar`（0–100）+ `InstallText`。
- **折叠「高级选项」**：`GlowButton`（`ScGlowCardButtonStyle`）头 + `FontIcon E904` 12px +
  `ExpandBehavior`；`AdvancedPane` 内含：
  - 「玩家名」`TextBox NicknameBox`；
  - 「节点地区」`ComboBox NodeRegionCombo`：`自动` / `优先大陆节点` / `仅国际节点`（默认第一项）；
  - 说明：`玩家名留空则跟随当前账户；同一房间的玩家建议选同一地区节点，跨地区节点之间常常互不联通。`

### 10.3 操作卡（`ScCard`，三态切换）

| 状态 | 按钮 |
| --- | --- |
| 未装核心 `InstallActions` | `安装联机核心`（`AccentButtonStyle`、`MinWidth=140`）+ `取消安装` |
| 已装 `PlayActions` | `创建房间`（`AccentButtonStyle`、`MinWidth=120`）/ `加入好友的房间` / `查看日志` |
| 会话中 `StopActions` | `停止联机` / `回到空闲` |

`InstallHint`：`首次使用需下载 {DisplayName} 核心（约 8 MB，国内镜像优先，可随时取消）；核心由启动器在首次使用时从官方 Releases 下载，不随安装包分发。`

### 10.4 联机详情（`DetailsPanel`，按需出现，顶部 1px 分隔线）

| 块 | 内容 |
| --- | --- |
| 房间已创建 `RoomBlock` | 标题 `房间已创建，把邀请码发给好友`（`ScSectionTitle`）；邀请码芯片 `Border ScChip Padding="10,6"` 内 `RoomCodeText`（**Consolas 14px，可选中**）+ `GlowButton`「复制邀请码」；说明 `好友在本页点「加入好友的房间」，把邀请码粘贴进弹出的窗口即可。` |
| 加入侧 `GuestBlock` | `GuestTitle` = 阶段标签；`GuestHint` 按阶段三选一：<br>`打洞进行中，稍等片刻，状态会自动更新。`／<br>`P2P 难度：{难度}。可先在游戏里打开「多人游戏」。`／<br>`打开游戏里的「多人游戏」，在局域网世界列表中进入房主的世界。`<br>`GuestPlayers` = `房间成员：{列表}` |
| 异常块 `ErrorBlock` | 红色 `ScSectionSpine` + `ErrorTitle` = `联机异常（{ErrorKind}）`（无种类时 `unknown`）；<br>`ErrorHint` = 具体错误或 `联机发生未知错误`；`GlowButton`「回到空闲重试」 |

### 10.5 折叠区（机制同下载页抽屉：`GlowButton` 头 + 箭头旋转 + `ExpandBehavior` 高度过渡）

| 区 | 内容 |
| --- | --- |
| 联机核心日志 `LogPane` | `TextBox LogBox`（只读、`AcceptsReturn`、Consolas 11px、高 180）+ `刷新` / `收起` |
| 使用步骤 `StepsPane` | ① `房主：游戏内「对局域网开放」→ 回启动器点「创建房间」。`<br>② `房主：状态变成「房间已创建」后，把邀请码发给好友。`<br>③ `好友：在本页点「加入好友的房间」，在弹出的窗口里粘贴邀请码。`<br>④ `双方：状态变成「已进入虚拟局域网」后，在游戏内「多人游戏」的局域网列表里进入房主的世界。`<br>另加：`联机核心由厂商官方发布，启动器只按官方方式下载并驱动它，不随安装包分发。` |
| 第三方联机 SDK `SdkPane` | 说明 `把第三方联机 SDK 的清单（JSON）放进下面这个目录，重启启动器后它就会出现在上方的「联机方案」里，无需等启动器更新。` + 「打开清单目录」按钮 + 目录路径 `SdkDirText` + `SdkListText`（无清单 = `当前没有加载到第三方联机 SDK 清单。`，有 = `已加载第三方联机 SDK：{列表}`） |

页脚 `VendorLine`：`联机由 {Vendor} 提供技术支持`（居中，带 `TextOutlineBehavior`）。

### 10.6 状态与提示文案（原文）

**阶段标签 `PhaseLabel`**：`空闲` / `创建房间中` / `房间启动中` / `房间已创建` / `连接房间中` /
`已进入虚拟局域网` / `联机成功` / 默认 `联机异常`。

**状态提示 `StatusHintText`**：
- HostScanning → `请先在游戏内开启「对局域网开放」，核心会自动接上那个世界。`
- Failed → `可以「回到空闲重试」，或展开下方日志看核心报了什么。`
- 其余 → 空串（整行隐藏）。

**难度标签**：`最简单` / `简单` / `中等` / `困难` / `未知`。

**`NoticeBar` 文案**：

| 场景 | 文案 |
| --- | --- |
| 安装中 | 起步 `正在准备安装…`；有速度时 `{stage} · {MB:F1} MB · {KB/s:F0} KB/s` |
| 安装完成 | `联机核心安装完成。` |
| 取消安装 | `已取消安装。` / `正在取消安装…` |
| 安装失败 | `安装失败：{msg}` |
| 建房 | `已开始创建房间，等状态变成「房间已创建」后把邀请码发给好友。` |
| 进房超时 | `操作超时：请检查游戏是否已开启「对局域网开放」，或稍后重试。` |
| 通用失败 | `联机操作失败：{msg}` |
| 回到空闲 / 停止 | `已回到空闲。` / `已停止联机。` |
| 复制邀请码 | `邀请码已复制，发给好友即可加入。` |
| 探活失败 | `联机状态查询失败：{err}`（`Severity=Warning`） |
| 打开目录失败 | `打开目录失败：{msg}` |
| 日志为空 | `（暂无日志）` |

**图标**：各折叠区箭头 `E904`（收起态指右、展开旋转 90°，时长 `ChevronMs`）。

---

## 11. 运行状态页 `RunningStatusPage.xaml`

**入口**：导航第 6 项「运行状态」（`EC3B`，带绿色圆点）。

**根布局**：`ScrollViewer` → `Grid MaxWidth=1280 RowSpacing=16 Padding="0,0,0,16"`；
性能卡三列 `1*/1*/1*`，详情两列 `1*/1*`。

### 11.1 页头

| 控件 | 文案 | 说明 |
| --- | --- | --- |
| `RetryBtn` | `重试`（`loc:Loc.Tip="重新开始采样"`） | 默认 `Collapsed`，**仅采样暂停时显示** |
| 刷新按钮 | 图标 `E918`，`loc:Loc.Tip="重新开始采样"` | — |
| `DropDownButton SampleBtn` | `采样设置` | `MenuFlyout Placement="BottomEdgeAlignedRight"`、`GroupName="sampleInterval"` |

**采样设置菜单**：

| 项 | Tag | 默认 |
| --- | --- | --- |
| `每 1 秒采样` | `1000` | ✅（`IntervalMs` 初值 1000） |
| `每 2 秒采样` | `2000` | — |
| `每 5 秒采样` | `5000` | — |
| （分隔线） | — | — |
| `ToggleMenuFlyoutItem GpuToggle` = `采集 GPU 占用` | — | ✅ 默认开（`CollectGpu` 默认 `true`） |

`SyncSampleMenu` 按实际值回勾。

### 11.2 Hero 卡（`HeroCard`，`ScCard`）

| 行 | 内容 |
| --- | --- |
| 1 | `ScChipAccent` 芯片 `运行中`；`ComboBox HeroInstanceCombo MinWidth=260`（`loc:Loc.Name="运行中的实例"`、`Tip="切换要查看的实例"`，各项 = 版本 id + `PID {pid}`）；`HeroInstanceCount`（`ScMetaLabel`，多开时 `共 {0} 个实例`）；`HeroUptime`（`ScMetaValue`，Cascadia Code）；`RecoveredChip`（`ScChipCaution`）`上一轮启动器启动` |
| 2 | `ScMetaLabel` `实例模式` + `HeroIsolation`；`内存` + `HeroMemory`（`{0} MB`）；`启动时间` + `HeroStarted`（`yyyy-MM-dd HH:mm:ss`） |
| 右侧 | 动作按钮：`打开日志` / `诊断` / `复制命令` / `重启` / `结束进程`（`KillBtn` 用 `Foreground=DangerBrush`） |

**无游戏占位 `EmptyCard`**：`EmptyStateView Glyph="EC70"`、Text `现在没有游戏在运行`、
Subtitle `去首页启动一个版本`、ActionText `去首页`。

### 11.3 性能三卡（`PerfGrid`，三列 `1*`、`ColumnSpacing=16`，各为 `ScCard`）

| 卡 | 标题（`ScMetaLabel`） | 数值 | 曲线 | 副标题 |
| --- | --- | --- | --- | --- |
| CPU | 处理器占用 | `CpuValue`：`--`，**30px SemiBold `Cascadia Code`** | `Canvas CpuSparkArea`（高 44）+ `Polyline CpuSpark`（`Stroke=AccentFillColorDefaultBrush`、`StrokeThickness=1.5`） | `CpuSub`（如 `{0} 线程`） |
| 内存 | 内存占用 | `MemValue`（同规格） | `Canvas MemSparkArea` + `Polyline MemSpark` | `MemSub`（`峰值 {0} · {1} 线程`） |
| 显卡 | 显卡占用 | `GpuValue`（同规格） | `Canvas GpuSparkArea` + `Polyline GpuSpark` | `GpuSub`（`GPU 引擎占用` / `计数器不可用` / `未采集 GPU`） |

- 每卡右上各有 `ScChipCaution` 芯片 `CpuPaused` / `MemPaused` / `GpuPaused`，
  文案 `采样已暂停`，**三卡同步显隐**（由 `SamplingPaused` 驱动）。
- 曲线是最近 **60 点**（`SparkPoints=60`）的 sparkline；CPU / GPU 固定量程 0–100，
  内存按窗口自适应（含 8% padding）。

### 11.4 双栏详情（`DetailGrid`，两列 `1*`）

**左「启动配置与 JVM」卡**：抬头 + `ConfigFallbackChip`（`ScChipCaution`）
`启动参数按当前版本配置显示`；`ConfigRows`（标签列宽 120）逐行：
`Java` / `内存`（`-Xmx {0} MB · -Xms {1} MB`）/ `窗口` / `实例模式` / `额外 JVM 参数`（无则 `无`）；
读不到上下文时显示 `读取不到这一局的启动参数`；另有 `InfoBar DiagBar`。

**右「内容与实时日志」卡**：三个 `ScChip` 芯片 `ChipMods` / `ChipShaders` / `ChipPacks`
（`Mod {0}` / `着色器 {0}` / `资源包 {0}`）；日志框（`ControlFillColorDefaultBrush`、圆角 8）内
`ScrollViewer LogScroll`（高 180）→ `TextBlock LogText`（Cascadia Code 12、`NoWrap`、可选中），
展示**尾 200 行**（`LogLines=200`）；右下角 `ToBottomBtn` `回到底部`（上滚离开底部时显示）。

**诊断 InfoBar 文案**：标题 `诊断`；进行中 `正在扫描版本目录…`；
成功 `未发现问题` + `扫描 {0} 个版本，未发现问题`；有问题 `发现 {0} 个问题` + 逐条；失败 `诊断失败`。

### 11.5 状态圆点颜色规则

| 位置 | 规则 |
| --- | --- |
| 导航项「运行状态」右侧圆点 `RunningDot` | `SystemFillColorSuccessBrush`（绿）；有游戏进程存活时 `Visible`，否则 `Collapsed`；**静态，不做呼吸动画** |
| 本页「运行中」 | `ScChipAccent`（accent 实底 + 黑字） |
| 本页「采样已暂停」 | `ScChipCaution`（琥珀系） |

---

## 12. 启动器日志页 `LogsPage.xaml`

**入口**：导航底部 Footer 第 1 项「启动器日志」（`EC64`）。
运行状态页点「打开日志」会置 `App.PendingLogSource="game"`，本页 `Loaded` 时消费一次跳到「游戏日志」。

**根布局**：`Grid MaxWidth=1280 RowSpacing=16`，两行 `Auto / *`
（源码注释强调内容行必须是 `*`）；日志卡 `CornerRadius=16`、`Padding=12`。

### 12.1 过滤控件

| 控件 | 文案 / 选项 | 说明 |
| --- | --- | --- |
| `RadioButtons LogSource` | `MaxColumns=2`；选项由代码按语言重建（`ApplySourceLabels`）：**启动器日志** / **游戏日志** | 默认索引 0（启动器日志） |
| `ComboBox GameLogCombo` | `loc:Loc.Placeholder="选择一局游戏日志"` | `MinWidth=240`；**仅游戏日志来源可见**；填最近 10 个 `game-*.log`（文件名倒序，默认最新） |
| `OpenLogDirBtn` | `打开日志目录` | 本页唯一按钮 |

### 12.2 日志行结构与着色

行正则：`^(?<ts>\[[^\]]+\])\s*(?<cat>\[[^\]]+\])?\s*(?<msg>.*)$`
—— 时间戳 `[..]` + 可选类别 `[..]` + 正文。三级着色：

| 部分 | 颜色 |
| --- | --- |
| 时间戳 | `TextFillColorTertiaryBrush`（兜底 `#8C8C8C`） |
| 类别 | `TextFillColorSecondaryBrush`（`#6E6E6E`），且 `SeverityOf` 按语义着色：含 `ERROR/FATAL/SEVERE/错误` → `DangerBrush`（`#F44336`）；含 `WARN/警告` → `CautionChipTextBrush`（`#B35309`）；其余中性 |
| 正文 | `TextFillColorPrimaryBrush`（`#202020`） |
| 不匹配行（如堆栈续行） | 整行用类别色（更轻） |

- 整块 `TextWrapping="NoWrap"`，长行横向滚动。
- 只读文件**尾部 400 行**（`TailLines=400`，环形缓冲）。

### 12.3 空态 / 异常

| 情形 | 文案 |
| --- | --- |
| 无游戏日志 | `（暂无游戏日志：启动一次游戏后这里会出现）` |
| 无启动器日志 | `（暂无日志）` |
| 读取失败 | `读取日志失败：{0}` |

---

## 13. 账户页 `AccountsPage.xaml`

**入口**：导航第 7 项「账户」（`EB8F`）；标题栏右上角个人中心也导航到此；
首页多处 `NavigateTo("accounts")`。

**根布局**：`Grid MaxWidth=1280`、`RowSpacing=16`，三行：
`Auto`（状态条）/ `Auto`（Starcloud ID 卡）/ `*`（游戏账户区，`MinHeight=220`）。

### 13.1 状态条

| 名称 | 类型 | 标题原文 |
| --- | --- | --- |
| `ErrorBar` | `InfoBar Severity=Error`，可关闭 | `操作失败` |
| `StatusInfo` | `InfoBar Severity=Success`，可关闭 | `完成` |

### 13.2 Starcloud ID 卡（卡内三个互斥面板，**不弹窗**）

外壳：`GlassCardBrush` + 1px 描边 + `CornerRadius=16` + `Padding=20`。

| 面板 | 内容 |
| --- | --- |
| `ScLoginPanel`（未登录） | `BrandMark Small 16×16` + `Starcloud ID`（15px Medium）+ `登录后可使用邀请 / 时刻等在线功能`（12px）；`TextBox ScIdBox`（`Width=250`，`Placeholder="Starcloud ID（用户名或邮箱）"`）；`PasswordBox ScPwdBox`（`Width=150`，`Placeholder="密码"`，通道=测试通道时改为 `密码（测试通道任意内容）`）；`ComboBox ScChannelBox`（`Width=128`、`SelectedIndex=0`，选项 **测试通道** / **云端通道**）；`Button ScLoginBtn`「登录」(`AccentButtonStyle`)；`ProgressRing ScLoginProgress`（24×24，默认 `Collapsed`）；`HyperlinkButton ScToRegisterBtn`「没有账号？注册」（`Padding="4,0"`） |
| `ScRegisterPanel`（注册，默认 `Collapsed`） | `BrandMark` + `注册 Starcloud ID`（15px Medium）+ `注册后回到登录即可使用在线功能`（12px）；`TextBox ScRegIdBox`（`Width=250`，`Placeholder="用户名或邮箱"`）；`PasswordBox ScRegPwdBox`（`Width=150`，`Placeholder="至少 6 位"`）；`PasswordBox ScRegPwd2Box`（`Width=150`，`Placeholder="再次输入密码"`）；`Button ScRegisterBtn`「创建账户」(`AccentButtonStyle`)；`GlowButton`「取消」 |
| `ScUserPanel`（已登录，默认 `Collapsed`） | `Border` 头像 40×40 `CornerRadius=20` + `ScAvatarText`（首字母，17px）+ `ScNameText`（显示名）+ `ScUserSubText`（`{UserId} · {通道}`）；`GlowButton`「退出登录」（`Height=30`、`Padding="12,0"`、`Foreground=SystemFillColorCriticalBrush`） |

**校验文案原文**：空 ID → `Starcloud ID 不能为空`；密码 < 6 位 → `密码至少 6 位`；
两次不一致 → `两次输入的密码不一致`；成功 → `已登录，在线功能已就绪` / `注册成功，请登录`。

### 13.3 游戏账户区

- 主操作行：右对齐 `Button`「添加账户」(`AccentButtonStyle`)。
- 标题行：`Border ScSectionSpine`（3×14 accent）+ `TextBlock`「游戏账户」(`ScSectionTitle`) +
  `AccountCountChip`（`ScChip`，内容 `{0} 个账户`）。
- 空态：`ctrl:EmptyStateView x:Name="AccountEmptyText"`，`Glyph="EB8F"`，
  `loc:Loc.Title="还没有游戏账户"`，`loc:Loc.ActionText="添加账户"`。
- 列表 `ListView x:Name="AccountList"`（容器样式基于 `ScPlainListItem`，框架整行底色置 `Transparent`）。

### 13.4 账户行（`DataTemplate x:DataType="rows:AccountRow"`）

行容器 `Grid CornerRadius=10`、`Background=ScRowShadeBrush`，挂 `RowHoverBehavior` + `HoverShadowBehavior`。

| 列 | 内容 |
| --- | --- |
| 0 | 头像 `Border 40×40 CornerRadius=8`（`AccentFillColorDefaultBrush`）叠 `Image Avatar`（`UniformToFill`，失败露出 `Initial` 首字母） |
| 1 | 用户名 `Username`（Medium，省略号） |
| 2 | 芯片组：类型 chip（`ScChip` + `TypeText`）+「首选」chip（`ScChipAccent`，`loc:Loc.Text="首选"`，按 `DefaultBadgeVisibility`） |
| 3 | 数据列：`ScMetaLabel`「皮肤」+ `SkinText`（`MaxWidth=160`），整列按 `SkinVisibility` 显隐 |
| 4 | 行尾按钮 `GlowButton Content={x:Bind DefaultActionText}`（`Height=30`、`Padding="12,0"`） |
| 5 | 行尾箭头 `FontIcon E904` 12px、`Opacity=0.5`，`loc:Loc.Name="查看账户详情"` |

**行尾按钮文案随类型**：微软 → `设为首选微软账户`；第三方 → `设为首选第三方账户`；
离线 → `设为首选离线账户`；其他 → `设为首选账户`。已为首选时隐藏（`DefaultBtnVisibility`）。

**类型 chip 文案**（`TypeLabel`）：微软 → `微软账户`；
第三方 → `第三方账户 · {ServerName}` 或 `第三方账户`；离线 → `离线账户`。

**头像来源**：`AvatarHelper.Resolve(...)`——微软 / 第三方走皮肤源，离线走本机皮肤路径。

### 13.5 交互与动画

| 操作 | 行为 |
| --- | --- |
| 登录（或密码框回车） | `StarcloudId.LoginAsync`；成功 → 刷新 + `NovaRuntime.NotifyLoginChanged()` + 状态 `已登录，在线功能已就绪` |
| 「没有账号？注册」 | 切到注册面板并聚焦 `ScRegIdBox`、清空提示条 |
| 「退出登录」 | `ThemedDialog`：标题 `退出登录`、正文 `退出后本地登录状态将被清除，确定退出吗？`、主按钮 `退出`、关闭 `取消`，默认按钮 = 关闭 |
| 「添加账户」/ 空态按钮 | `NavigateToAddAccount()` |
| 点列表行 | `NavigateToAccountDetail(uuid)` |
| 行尾「设为…」 | `Accounts.SetDefault(uuid)` 并刷新列表 |

- 列表填充后 `MotionFx.StaggerListItems(AccountList)`。
- 二级页返回时读取 `AccountsPage.PendingStatus` 并**只显示一次**状态。

---

## 14. 添加游戏账户页 `AccountAddPage.xaml`（二级页）

**入口**：账户页「添加账户」/ 空态按钮 → `NavigateToAddAccount()`（`drill-in` 转场）。

**根布局**：`Grid MaxWidth=1280 RowSpacing=16`，三行 `Auto / Auto / *`；
主体两列 **200 + `*`**、`ColumnSpacing=16`。

### 14.1 页头与左分类

| 元素 | 内容 |
| --- | --- |
| 返回 | `GlowButton`（透明底无边框，`loc:Loc.Name="返回账户列表"`）：图标 `E908` + `返回账户列表` |
| 状态条 | `InfoBar ErrorBar`，标题 `操作失败` |
| 左分类容器 | `Border`（`AppPaneBackgroundBrush` + `CardStrokeColorDefaultBrush` + `CornerRadius=8` + `Padding=8`）内 `ListView x:Name="KindList"`（条目 `MinHeight=40`） |

**左分类导航逐项**：

| 顺序 | Tag | Glyph | 文案原文 | 默认 |
| --- | --- | --- | --- | --- |
| 1 | `microsoft` | `EB8F` | 微软账户 | ✅ `IsSelected=True` |
| 2 | `external` | `EB91` | 第三方账户 | — |
| 3 | `offline` | `EC62` | 离线账户 | — |

### 14.2 右表单三张卡（按身份切换显隐）

**微软正版 `MsPanel`**（`GlassCardBrush` / `CornerRadius=16` / `Padding=20`）

- 标题 `Microsoft 正版账户（在线验证）`
- 说明 `登录后即可进入正版服务器；身份只保存在本机，令牌不写入磁盘。`
- 次说明（12px 三级色）`登录会打开内置登录窗口，在里面完成微软验证即可（也可以在「设置 → 通用 → 使用内置登录窗口」改为浏览器方式）。`
- `Button MsLoginBtn`「使用内置登录窗口登录」(`AccentButtonStyle`) + `ProgressRing MsProgress`（20×20）+ `TextBlock MsStatus`

**第三方外置登录 `ExternalPanel`**（`MaxWidth=520`）

- 标题 `第三方认证服务器账户（外置登录）`
- 说明 `填写你自己认证服务器的 Yggdrasil API 地址与账密；启动器会注入 authlib-injector 让游戏走该服务器。`
- `认证服务器地址` + `TextBox ExtUrlBox`（`Placeholder="认证服务器地址"`）
- `用户名` + `TextBox ExtNameBox`（`Placeholder="邮箱或用户名"`）
- `密码` + `PasswordBox ExtPwdBox`（`Placeholder="密码"`，回车触发登录）
- `Button ExtLoginBtn`「登录」+ `ProgressRing ExtProgress`（20×20）+ `TextBlock ExtStatus`
- 尾注 `密码只用于本次登录、不落盘；令牌只存在内存里，重启应用后首次启动游戏会要求重新登录。`

**离线 `OfflinePanel`**（`MaxWidth=520`）

- 标题 `离线账户（无需登录，仅单人 / 局域网）`
- `玩家名` + `TextBox OfflineNameBox`（`Placeholder="玩家名（3-16 位字母、数字或下划线）"`、`MaxLength=16`）
- `TextBlock OfflineTip`（实时提示）：
  - 合法 → `离线 UUID：{0}（各启动器通用算法，本地存档与局域网身份保持一致）`
  - 非法 → `离线账户无需登录，可用于单人游戏、局域网联机与允许离线登录的服务器。`
- `Button OfflineAddBtn`「添加」+ `TextBlock OfflineStatus`

### 14.3 错误态与交互

**`ErrorBar` 文案原文**：

| 场景 | 文案 |
| --- | --- |
| 第三方必填缺失 | `请填写认证服务器地址、用户名和密码` |
| 第三方失败 | `登录失败：{0}` |
| 离线名非法 | `用户名不合法：需为 3-16 位字母、数字或下划线` |
| 离线失败 | `添加失败：{0}` |
| 微软登录中状态 | `请在登录窗口中完成验证…` |

- 本页**无空态组件**；加载态是三处 `ProgressRing`（微软 / 第三方各 20×20）。
- 左分类切换 → `Show(kind)`，切换右表单可见性并关闭 `ErrorBar`。
- 微软登录 → `AuthDialogs.MicrosoftDeviceCodeAsync(...)`；成功 → `Finish("Microsoft 账户已登录：{0}")`。
- 第三方 → `AccountFlows.LoginExternalAsync`；成功 → `第三方登录 {0} 已添加`。
- 离线 → `Accounts.AddOrUpdateOffline`；成功 → `离线账户 {0} 已添加`。
- `Finish()` 统一行为：写 `AccountsPage.PendingStatus` → `GoBack()`。**本页不弹任何对话框。**
- 返回：`Frame.GoBack(MotionService.BackTransition(Motion.Enabled))`；不可回退时 `NavigateTo("accounts")`。

---

## 15. 账户详情页 `AccountDetailPage.xaml`（二级页）

**入口**：账户页点某一行 → `NavigateToAccountDetail(uuid)`（`drill-in` 转场）。

**根布局**：外层 `ScrollViewer`（`VerticalScrollBarVisibility=Auto`、横向滚动禁用、`AllowDrop=True`，
`DragOver` / `Drop` 挂在最外层 → **全页可收 PNG**）；
内层 `Grid MaxWidth=1280 RowSpacing=16 Padding="0,0,0,16"`，三行 `Auto / Auto / *`。

### 15.1 页头

| 元素 | 内容 |
| --- | --- |
| 返回 | `GlowButton`（`loc:Loc.Name="返回账户列表"`）：图标 `E908` + `返回账户列表` |
| 头像 | `Border 52×52 CornerRadius=12`（`AccentFillColorDefaultBrush`）+ `HeaderInitial`（22px）+ `HeaderAvatar` |
| 徽标行 | 类型 chip（`HeaderType`）+ `HeaderDefaultBadge`（`ScChipAccent`，`首选账户`） |
| 首选按钮 | `SetDefaultBtn`（`GlowButton`，文案由代码设为 `设为首选微软账户 / 第三方账户 / 离线账户`，已为首选时隐藏） |
| 状态条 | `ErrorBar`（`操作失败`）、`StatusBar`（`完成`） |

### 15.2 左分区导航（180px）

容器 `Border`（`AppPaneBackgroundBrush` / `CornerRadius=8` / `Padding=8`）内 `ListView SectionList`：

| 顺序 | Tag | Glyph | 文案原文 | 默认 |
| --- | --- | --- | --- | --- |
| 1 | `skin` | `EAA3` | 形象与皮肤 | ✅ `IsSelected=True` |
| 2 | `info` | `EB8F` | 账户信息 | — |

### 15.3 分区「形象与皮肤」（`SkinPanel`，列宽 300 + `*`、`ColumnSpacing=20`、`MinHeight=460`）

**左：3D 形象区**（`Border Style=ScCard Padding="14,16"`）

- `ctrl:SkinFigureView x:Name="PlayerView" Height=310 HorizontalAlignment=Stretch`。

  > **实现说明**：Windows 端的 3D 玩家形象是 `SkinFigureView`——原生渲染（Win2D + Core 光栅化，
  > GPU 优先、软件回退）。**`Skin3DView`（旧版 WebView2 + skinview3d 抓帧）已被取代**，
  > 本页不使用。`Assets/3d/player.html` 与 `skinview3d.bundle.js` 为旧实现遗留资源。

- 交互：拖拽转视角（yaw / pitch）、滚轮缩放 0.7–3.0；走路动画由 `WalkEnabled`
  （绑定 `Settings.AccountWalkAnimation`）控制。
- 提示文案 `拖动以旋转 · 滚轮缩放`（11px 三级色，居中）。
- 分隔线 `Border Height=1 Margin="2,4"`。
- 皮肤信息：`SkinNameText`（16px Medium）、`SkinMetaText`（11px）、`SkinUsageText`（11px）。
- `SkinActions`（离线 / 正版可见，**第三方隐藏**）：
  - `型号`（`ScMetaLabel`）+ `ComboBox ModelBox`，选项由代码添加：
    `自动（按纹理判断）`(Tag=Auto) / `经典（粗手臂）`(Tag=Classic) / `纤细（细手臂）`(Tag=Slim)；
  - `Button ApplyBtn`「应用到该账户」(`AccentButtonStyle`，横向拉伸)；
  - `HyperlinkButton ImportBtn`「导入皮肤…」；`HyperlinkButton UnbindBtn`「取消使用」（默认 `Collapsed`）。
- `SkinNote`（按类型三选一）：
  - 离线 → `离线账户没有可上传的账号：皮肤通过启动器本机注入生效（单人 / 局域网可见，其他玩家通常看到默认皮肤）。`
  - 正版 → `正版账户的皮肤归微软账号所有：这里的换肤只在本机生效，不改账号上的官方皮肤；每次更换前都会先警告。`
  - 第三方 → `第三方账户的皮肤在皮肤站上：从右边「皮肤站衣柜」选一张就换掉。`

**右：本地皮肤库 `LocalSkinLibrary`**

- 标题行：`ScSectionSpine` + `我的皮肤` + `SavedCountText`（`ScChip`，`{0} 张`）+ 说明
  `衣柜在所有账户间共用（皮肤只存一份），每个账户各选各的。` + `HyperlinkButton`「从链接导入」。
- `GridView SavedList`（`ItemClick` 选中；右键 `ContextRequested`）：
  `ItemsWrapGrid ItemWidth=104 ItemHeight=152`；高度由 `LayoutCardGrids` 按列数计算（我的皮肤最多两行）。
  - **「添加皮肤」占位卡**（首位，`AddVisibility`）：文本 `+`（26px）+ `添加皮肤`（12px）+
    `拖放 PNG`（10px）。**加号用文本而非字形**（图标库无「添加」，避免缺字方框）。
  - **皮肤卡 `SkinCardRow`**：`Image Preview`(`Uniform`) + `Name`(11px) +
    角标 `使用中`（`ScChipAccent`，9px，`InUseVisibility`）；
    选中态另叠**强调色描边 2px**（`SelectionVisibility`）。卡片 `CornerRadius=10`，`ToolTip={Detail}`。
- 「默认皮肤」折叠标题（`GlowButton PresetToggleBtn`）：`FontIcon PresetChevron E904`（旋转 0°/90°）+
  `默认皮肤` + 说明 `启动器自带的官方皮肤；点一张即加入衣柜并应用到当前账户。`；
  `PresetPane` 挂 `beh:ExpandBehavior.IsExpanded="True"`。
  - `GridView PresetList`：`ItemsWrapGrid ItemWidth=104 ItemHeight=146`；
    卡片 `SkinPresetRow`：`Image` + `Name`；角标 `使用中`（`ScChipAccent`）/ `已在库中`（`ScChip`，9px）。
- `TextBlock SkinStatus`（操作状态行）。
- 预置皮肤资源：`Assets/Skins/default/` 下 8 张 PNG——
  `alex` / `ari` / `efe` / `kai` / `makena` / `noor` / `steve` / `sunny` / `zuri`（另有 `steve.png` 在 `Assets/` 根）。

**右：皮肤站衣柜 `StationSkinLibrary`**（**仅第三方账户**，`GlassCardBrush` / `CornerRadius=16` / `Padding=16`）

- 标题 `皮肤站衣柜` + 说明 `这里列的是这个账号在皮肤站上的皮肤；点一张就换成它，游戏里（用启动器启动后）看到的就是这张。`
- `GlowButton`「刷新衣柜」、「重新登录该账号」+ `ProgressRing StationBusy`（20×20）。
- `GridView StationList`（`ItemWidth=104 ItemHeight=152 MinHeight=160`）：卡片显示
  `Image Preview` + `Name`，`ToolTip={ModelText}`。
- `TextBlock StationStatus`。

**卡片状态文案**（`SkinPresetRow.ActionText`）：`使用中` / `已在库中` / `点击使用`。

### 15.4 分区「账户信息」（`InfoPanel`）

- 标题 `账户信息` + `Border`（`GlassCardBrush` / `CornerRadius=16` / `Padding=20`）内
  `StackPanel InfoRows`（行由代码构建，标签列 112 + 值列 `*`，13px）。
- 信息行标签原文：`类型`、`用户名`、`UUID`（值用 Consolas 等宽）、`认证服务器`（仅第三方）、`会话状态`。
- 会话状态取值：
  - `离线身份不需要登录`
  - `本次运行已登录，可直接启动游戏`
  - `本次运行未登录，启动游戏时会要求就地登录（令牌不落盘，重启应用即失效）`
  - 第三方无 server 时值 `（未记录）`
- 标题 `账户操作` + `Border`：`GlowButton InfoSetDefaultBtn`（文案同页头）+
  `GlowButton DeleteAccountBtn`「删除账户」（`Foreground=SystemFillColorCriticalBrush`）；
  尾注 `删除只移除本机保存的身份信息，不动游戏存档与版本文件。`

### 15.5 图标与本页专属字形

| 码位 | 用途 |
| --- | --- |
| `E908` | 页头返回 |
| `EAA3` | 左栏「形象与皮肤」 |
| `EB8F` | 左栏「账户信息」 |
| `E904` | 「默认皮肤」折叠箭头 |
| `\uE8AC`（代码硬编码） | 皮肤右键「重命名」（**旧码位**；映射文档中新码为 `EA97`） |
| `\uEAA5`（代码硬编码） | 皮肤右键「删除」 |

### 15.6 空态 / 加载态 / 错误态

- 错误态：`ErrorBar`（`操作失败`）；账户不存在时正文
  `这个账户已经不存在了（可能在别处被删除）`。
- 加载态：`StationBusy` 转圈；`SkinStatus` / `StationStatus` 文本
  （如 `正在下载…`、`正在读取皮肤站上的皮肤…`、`正在换成「{0}」…`）。
- 空态（文本形态，**无空态组件**）：

| 情形 | 文案 |
| --- | --- |
| 未选中皮肤 | `SkinNameText` = `还没有选中皮肤`；`SkinMetaText` = `从右侧「我的皮肤」或「默认皮肤」里挑一张` |
| 正版未选本地皮肤 | `账号形象` + `皮肤由微软账号提供` |
| 第三方 | `账号形象` + `皮肤由认证服务器（皮肤站）提供` |
| 皮肤站为空 | `皮肤站上还没有皮肤：先到皮肤站网页上传或收藏几张，再回来刷新。` |
| 无令牌 | `需要先登录该账号：令牌不落盘，点「重新登录该账号」后再试。` / `本次运行还没有该账号的登录凭证（令牌不落盘）：点「重新登录该账号」后就能读取皮肤站上的皮肤。` |

- 拖放提示：`DragOver` → `松开即可加入皮肤库`；非 PNG → `只支持 PNG 皮肤图片`。

### 15.7 交互与对话框（文案见 17 章）

| 操作 | 行为 / 文案 |
| --- | --- |
| 返回 | 不可回退时 `NavigateTo("accounts")` |
| 分区切换 | `skin` ↔ `info` 切 `SkinPanel` / `InfoPanel` 可见性 |
| 点皮肤卡 | 选中并刷新左侧预览 |
| 点「添加皮肤」占位卡 | 打开文件选择器（`.png`） |
| 「导入皮肤…」 | `FileOpenPicker`（`.png`）；成功 → `已导入「{0}」，点「应用到该账户」即可使用` |
| 「从链接导入」 | 弹窗 `从链接导入皮肤`（输入框 `Placeholder="皮肤图片链接（http/https）"`、`Width=360`，主按钮 `导入`）；空链接 → `请先粘贴皮肤图片链接`；成功 → `已从链接导入「{0}」` |
| 「应用到该账户」 | `Accounts.SetSkin`；**正版账户先过强制警告**；成功 → `已应用：下次启动游戏就在本机看到这张皮肤（单人 / 局域网）` |
| 「取消使用」 | 清空该账户皮肤；成功 → `已取消使用：该账户恢复默认形象` |
| 点默认皮肤卡 | （正版先告警）加入衣柜并应用；状态 `已应用「{0}」：下次启动游戏就在本机看到（单人 / 局域网）`；首次另加 `已加入皮肤库并选中「{0}」` |
| 皮肤卡右键 → 重命名 | 弹窗 `重命名皮肤`（主按钮 `保存`）；空名 → `请先填新名字`；成功 → `已重命名` |
| 皮肤卡右键 → 删除 | 直接删除；成功 → `已删除「{0}」` 或 `已删除「{0}」，{1} 个账户已恢复默认形象` |
| 「刷新衣柜」 | 读取并渲染缩略图 |
| 「重新登录该账号」 | `AuthDialogs.ExternalPasswordAsync` |
| 点站上皮肤 | `SkinStation.ApplyAsync`，并刷新左侧 3D 形象 |
| 「删除账户」 | 弹窗 `删除账户`（正文 `确定要删除账户 {0} 吗？`、主 `删除`）；成功后写 `PendingStatus="已删除账户 {0}"` 并返回 |
| 「设为…」 | `Accounts.SetDefault`，`StatusBar` = `已将 {0} 设为首选账户` |

**动画**：皮肤库填充后 `MotionFx.StaggerListItems(SavedList)`；
默认皮肤折叠箭头走 `SetChevronAngle`（合成层旋转，时长 `MotionFx.ChevronMs`，缓动 `MotionFx.ControlEase`，
`animate && Motion.Enabled` 为假时直接到位）；展开 / 收起走
`Behaviors.ExpandBehavior.SetIsExpanded(PresetPane, _)`（高度平滑 + 内容淡入）。
皮肤卡缩略图由 Core 软件光栅化渲染（`SkinThumbnails`），**不是**原始纹理。

---

## 16. 设置页 `SettingsPage.xaml`

**入口**：导航**最底部** Footer 第 2 项「设置」（`EC04`）；
首页、`NovaHostActions`、`CrashDialog` 也可 `NavigateTo("settings")`。

**根布局**：`Grid MaxWidth=1280`；两列 **180 + `*`**、`ColumnSpacing=16`。
左栏容器 `Border`：`VerticalAlignment=Stretch`、`Margin="0,12,0,0"`、`GlassCardBrush` +
`CardStrokeColorDefaultBrush` + `CornerRadius=8` + `Padding=8`。
右栏 `Grid RowSpacing=12`，三行 `Auto / * / Auto`（内容 `SectionScroll` 在行 1，底部操作栏在行 2）。

**页内样式资源**（本页局部）：

| 键 | 定义 |
| --- | --- |
| `PclCard` | 基于 `ScHoverCard`（`GlassCardBrush` + 1px 描边 + 悬浮反馈；`ScHoverCard` 本体 `CornerRadius=12 Padding=16`） |
| `PclSectionTitle` | 基于 `ScSectionTitle`（中性 14px，强调色交给左侧脊柱） |
| `PclSectionHead` | 水平 `StackPanel Spacing=8` |
| `PclSectionSpine` | 基于 `ScSectionSpine`（3×14、圆角 2、accent 底） |
| `PclLabel` | `TextBlock`，`Width=86`、13px、垂直居中 |

### 16.1 左侧分类导航

| 顺序 | Tag | x:Name | 图标 | 文案原文 | 默认 |
| --- | --- | --- | --- | --- | --- |
| 1 | `game` | `CatGame` | `Glyph="EC20"`（原版游戏 / 游戏） | 游戏 | ✅ `IsActive` + `IsSelected` |
| 2 | `java` | `CatJava` | **自绘咖啡杯 `Path`**（`Viewbox 14×14`，内 `Grid 16×16`，`StrokeThickness=1.25`，`Stretch=None`） | Java（`Content="Java"`） | — |
| 3 | `download` | `CatDownload` | `Glyph="E90C"`（下载） | 下载与资源 | — |
| 4 | `general` | `CatGeneral` | `Glyph="EC04"`（设置） | 通用 | — |

**底部操作栏**（`Border Padding="12,8" CornerRadius=8`、`LayerFillColorDefaultBrush`、
`BorderThickness="0,1,0,0"`）：

- 提示文本 `修改后在下方保存；未保存的改动会在切换分类时保留。`（12px）
- `Button`「保存设置」(`AccentButtonStyle`、`MinWidth=120`)

### 16.2 分区「游戏」`SectionGame`

**卡「游戏目录」**

| 设置项 | 控件 | Placeholder | 默认值 |
| --- | --- | --- | --- |
| （`TextField`） | `TextBox DotMcBox` | `.minecraft 目录路径` | `LauncherSettings.DotMinecraft`（= `PlatformInfo.DefaultDotMinecraft`；XAML 未写死） |

说明：`版本、资源与库文件将安装到该目录`。

**卡「内存分配」**

| 设置项 | 控件 | 可选项 | 默认值 |
| --- | --- | --- | --- |
| 最大内存（整数 GB） | `Slider MaxMemorySlider`（`Minimum=1`、`Maximum=16` 起，**代码按物理内存改写上界**；`StepFrequency=1`）+ `MaxMemoryText` | — | `DefaultMaxMemoryMb = 4096` → 4 GB |

辅助说明 `MemoryAutoHint`：`1 ~ {0} GB（物理内存 {1} GB，自动上限 {2} GB）；最小内存固定 512 MB。`
（最小内存 `DefaultMinMemoryMb = 512` 固定）。

**卡「启动可靠性」**

| 设置项 | 控件 | 默认值 |
| --- | --- | --- |
| 启动失败自动重试一次 | `ToggleSwitch LaunchAutoRetrySwitch` | `LaunchAutoRetry = true` |
| 启动页默认展开实时日志 | `ToggleSwitch ShowGameLogSwitch` | `ShowGameLogOnLaunch = false` |

说明：`游戏启动后 8 秒内异常退出时，自动重试一次（不重复环境体检）。` /
`启动时在启动页显示游戏实时输出，便于排查启动问题。`
子块「智能内存推荐」（12px Medium）+ `MemoryRecommendText`（`MemoryAdvisor.Explain` 生成）+
`GlowButton`「应用为默认最大内存」。
两个开关**切换即落盘**并置开 `SavedBar`。

**卡「默认窗口」**

| 设置项 | 控件 | 默认值 |
| --- | --- | --- |
| 窗口尺寸 | `NumberBox DefaultWidthBox`（`Min=320 Max=7680`、`SpinButtonPlacementMode=Compact`、`Width=120`）+「×」+ `NumberBox DefaultHeightBox`（`Min=240 Max=4320`、`Width=120`） | 854 × 480（代码回落值同为 854 / 480） |
| 全屏启动 | `ToggleSwitch DefaultFullscreenSwitch` | `DefaultFullscreen = false` |

**卡「启动参数」**

| 设置项 | 控件 | Placeholder | 默认值 |
| --- | --- | --- | --- |
| 额外 JVM 参数（每行一条） | `TextBox ExtraJvmArgsBox`（`AcceptsReturn`、`Height=72`） | `-Xss1M` | `ExtraJvmArgs = []`（空） |
| 内置 JVM 参数 | `ToggleSwitch UseBuiltinJvmSwitch` | — | `UseBuiltinJvmArgs = true` |

说明：`关闭后将不再自动附加官方推荐 JVM 参数`。

**卡「全局环境变量」**

- 说明：`对所有版本生效；版本级同名变量会覆盖此处。`
- `StackPanel GlobalEnvPanel`（动态行）+ `GlowButton`「添加环境变量」。
- 行内控件（代码构建）：`TextBox`（`Placeholder="名称"`、`Width=200`）、
  `TextBox`（`Placeholder="值"`、`Width=360`）、删除按钮（`FontIcon \uEAA5`）。默认空（`GlobalEnvVars = new()`）。

**卡「版本隔离（默认）」**

| 设置项 | 控件 | 可选项原文 | 默认值 |
| --- | --- | --- | --- |
| （`loc:Loc.Name="默认版本隔离方式"`） | `RadioButtons IsolationDefaultRadio` | `版本隔离（推荐）` / `共享目录` | `VersionIsolated = true` → 选中「版本隔离（推荐）」 |

说明：`新版本的默认隔离方式。开启后只隔离 mods/存档/配置/资源包/光影 5 类内容，库与资源文件仍共享；单个版本可在其详情页覆盖此设置。`

### 16.3 分区「Java」`SectionJava`（进入时自动重新搜索）

**卡「Java」**

说明：`进入本页时自动搜索本机 Java，搜索完成后在下方选择；也可以手动添加。`

| 控件 | 文案 / 选项 | 默认值 |
| --- | --- | --- |
| `ComboBox JavaCombo`（`Width=380`，`loc:Loc.Name="Java"`） | `Placeholder="正在搜索本机 Java…"`；动态项：`自动选择（按版本推荐）`、`Java {Major} · {Version} · {Exe}`、`手动添加 · {Path}` | 选中 `自动选择（按版本推荐）`（即 `JavaExecutable = null`） |
| `GlowButton JavaScanBtn` | `搜索 Java`（进行中临时变为 `正在搜索…`） | — |
| `GlowButton JavaBrowseBtn` | `手动添加…` | — |
| `GlowButton` | `下载 Java 21（Temurin）` | — |
| `GlowButton` | `下载 Java 8（Temurin）` | — |
| `GlowButton JavaDeleteBtn` | `删除选中运行时` | `IsEnabled=False`（**仅启动器下载的运行时可用**） |
| `ProgressBar JavaProgress` | — | `Collapsed` |

删除确认弹窗：标题 `删除 Java`，正文 `将从启动器目录删除 Java {0}（{1}）。此操作不可撤销。`，
主按钮 `删除`，关闭 `取消`。

### 16.4 分区「下载与资源」`SectionDownload`

**卡「下载」**

| 设置项 | 控件 | 可选项 | 默认值 |
| --- | --- | --- | --- |
| 下载并发数 | `NumberBox ConcurrencyBox`（`Min=1 Max=64`） | — | `DownloadConcurrency = 16` |
| 失败重试次数 | `NumberBox RetriesBox`（`Min=0 Max=10`） | — | `DownloadMaxRetries = 3` |
| 下载源 | `ComboBox SourceCombo`（`Width=200`） | `自动（多源聚合）`(`auto`) / `仅官方`(`official`) / `BMCLAPI`(`bmclapi`) / `FastMinecraftMirror`(`fastmirror`) / `RainYun`(`rainyun`) | `DownloadSource = "auto"` |
| 自定义镜像地址 | `TextBox CustomMirrorBox`（`Width=320`） | `Placeholder="自定义镜像地址"` | `DownloadCustomMirrorUrl = ""` |
| CurseForge API Key（可空） | `TextBox CurseForgeKeyBox`（`Width=320`） | `Placeholder="留空则使用启动器内置密钥"` | `CurseForgeApiKey = ""` |

**卡「资源列表与图标缓存」**

| 设置项 | 控件 | 默认值 |
| --- | --- | --- |
| 启用资源列表拉取缓存 | `ToggleSwitch ResourceCacheSwitch` | `ResourceCacheEnabled = true` |

说明：`打开资源页时先用缓存瞬时展示，后台拉到新内容后自动替换；社区资源图标同样缓存到本地，命中后不再联网下载。关闭后完全不读不写缓存。`
`GlowButton`「清空缓存」→ 清空资源缓存与图标缓存，并把 `SavedBar.Title` 改成 `缓存已清空` 后打开。

### 16.5 分区「通用」`SectionGeneral`

**卡「主题外观」**

| 设置项 | 控件 | 可选项 | 默认值 |
| --- | --- | --- | --- |
| 外观 | `ComboBox ThemeCombo`（`Width=160`） | `跟随系统`(`system`) / `浅色`(`light`) / `深色`(`dark`) | `Theme = "system"` |
| 界面动效 | `ComboBox MotionCombo`（`Width=160`） | `跟随系统`(`system`) / `始终开启`(`on`) / `关闭`(`off`) | `UiAnimations = "system"` |
| 界面语言 | `ComboBox LanguageCombo`（`Width=160`） | `简体中文`(`zh-CN`) / `English`(`en-US`) | `UiLanguage = "zh-CN"` |

三者均**切换即落盘**（外观即时 `RefreshAppearance`；语言即时热替换词典并重放已本地化文案）。

**卡「背景与外观」**

| 设置项 | 控件 | 可选项 / 范围 | 默认值 |
| --- | --- | --- | --- |
| 背景 | `ComboBox BackgroundModeCombo`（`Width=160`，`loc:Loc.Name="背景类型"`） | `关闭（纯毛玻璃）`(`none`) / `内置壁纸`(`builtin`) / `本地图片`(`file`) | `BackgroundMode = "none"` |
| （内置壁纸网格） | `GridView BuiltinWallpaperGrid`（`Single`、禁滚动；缩略图 128×72 + 名称） | 选项来自 `AppearanceService.Wallpapers` | 首张（`BackgroundBuiltinId` 非法时回落 `Wallpapers[0]`） |
| 图片文件 | `GlowButton`「选择图片…」+ `GlowButton`「清除」+ `ImagePathText` | 文件类型 `.jpg/.jpeg/.png/.bmp/.webp` | `BackgroundImagePath = null` |
| 模糊强度 | `Slider BgBlurSlider`（`Width=240`、`Min=0 Max=64 Step=1`）+ `BgBlurText`（`Width=56`） | — | `BackgroundBlurRadius = 24` |
| 不透明度 | `Slider BgOpacitySlider`（`Width=240`、`Min=0.2 Max=1 Step=0.05`）+ `BgOpacityText` | — | `BackgroundImageOpacity = 0.7` |
| 毛玻璃强度 | `Slider MaskOpacitySlider`（`Width=240`、`Min=0 Max=1 Step=0.05`）+ `ToggleSwitch MaskFollowThemeCheck`（`OnContent/OffContent="跟随主题"`） | — | `BackgroundFollowTheme = true`（跟随主题时滑杆禁用）；`BackgroundMaskOpacity = null` |
| 账户页走路动画 | `ToggleSwitch WalkAnimSwitch` | — | `AccountWalkAnimation = true` |

说明：`关闭后，账户页右侧的玩家形象将保持站立姿势。`
联动：背景「关闭」时模糊 / 不透明度滑杆禁用；切到内置壁纸但未选过时默认第一张；
本地图片文件失效时**静默回落**「关闭」。背景相关项**即改即存**（滑块拖动 400ms 节流落盘）。

**卡「使用内置登录窗口」**

| 设置项 | 控件 | 默认值 |
| --- | --- | --- |
| 使用内置登录窗口 | `ToggleSwitch EmbeddedLoginSwitch` | `MicrosoftLoginEmbedded = true` |

说明：`微软（正版）登录在启动器内的窗口里完成，验证码自动填入，不跳系统浏览器；关闭后改为显示验证码并打开浏览器。`切换即落盘。

**卡「Nova 游戏内浮窗」**

| 设置项 | 控件 | 默认值 |
| --- | --- | --- |
| Nova 游戏内浮窗 | `ToggleSwitch OverlaySwitch` | `NovaOverlayEnabled = false`（**默认关 = 显式授权**） |

说明：`游戏运行时在屏幕角落显示 Nova 浮窗，Ctrl+N 随时呼出对话；下局游戏启动时生效。`切换即落盘。

**卡「软件更新」**

| 设置项 | 控件 | 默认值 |
| --- | --- | --- |
| 自动检查更新 | `ToggleSwitch AutoUpdateSwitch` | `AutoUpdateEnabled = true` |

说明：`启动几秒后后台检查新版本，发现新版本时询问是否更新；游戏运行期间不打扰。`
`GlowButton CheckUpdateBtn`「检查更新」+ `TextBlock UpdateStatusText`：

| 情形 | 文案 |
| --- | --- |
| 检查中 | `正在检查更新…` |
| 已最新 | `已是最新版本（{0}）` |
| 有新版 | 弹 `UpdateDialog` |
| 失败 | `ErrorBar`：`检查失败：{0}` |

> 备注：设置页内**未出现 `HyperlinkButton`**。各分区的动作一律是 `ctrl:GlowButton`
> （`应用为默认最大内存` / `添加环境变量` / `搜索 Java` / `手动添加…` / `下载 Java 21（Temurin）` /
> `下载 Java 8（Temurin）` / `清空缓存` / `选择图片…` / `清除` / `检查更新`），说明文字均为普通 `TextBlock`。

### 16.6 保存行为

| 方式 | 覆盖设置 |
| --- | --- |
| **「保存设置」按钮**统一写入 | `DotMinecraft`、内存（`DefaultMaxMemoryMb` / `DefaultMinMemoryMb`）、窗口尺寸与全屏、`JavaExecutable`、`ExtraJvmArgs`、`UseBuiltinJvmArgs`、下载并发 / 重试 / 源 / 自定义镜像 / CurseForge Key、全局环境变量、`Theme` / `UiAnimations` / `UiLanguage` / `VersionIsolated` / `MicrosoftLoginEmbedded`；随后 `RefreshAppearance` 并置 `SavedBar`「已保存」 |
| **切换即落盘**（并置 `SavedBar`） | 启动可靠性两开关、主题外观三项、背景与外观各项、走路动画、内置登录窗口、Nova 浮窗、自动检查更新 |

提示条为**页面级浮层**（独立 `Grid`，列 `180` + `*`，`StackPanel` 落在右列顶部，展开不挤压主体）：
`SavedBar`（`Severity=Success`，标题 `已保存`）、`ErrorBar`（`Severity=Error`，标题 `操作失败`）。

### 16.7 动画

- 分区切换 `Category_SelectionChanged`：可见性**瞬时切换**（无布局动画），进入的区块调用 `AnimateSectionIn`：
  `MotionFx.Reset(section)` → `MotionFx.FadeSlideIn(section, 18f)`；起始下移常量 `SectionSlideOffset = 18` px。
- `FadeSlideIn` 内部受 `AppServices.Instance.Motion.Enabled` 控制，关闭时直接落终态。
- 滚动位置归零：`SectionScroll.ChangeView(null, 0, null, disableAnimation: false)`（框架动画回滚）。
- `ui_animations` 默认 `"system"`；`MotionService.Enabled` 解析：`on` → 真、`off` → 假、其他 → 系统偏好。

---

## 17. Nova 助手

Nova **不是一个独立页面**，而是内嵌助手：**多个入口共享同一份状态机（`NovaRuntime`）与同一份全局消息流**
（`ObservableCollection<NovaChatMessage> Messages`）：

| # | 入口 | 说明 |
| --- | --- | --- |
| 1 | 顶栏 `NovaOrb` | 状态着色 + 有提案角标；点击展开 440px 面板（见 4.5） |
| 2 | 左侧导航「Nova」页（`Tag="ai"`） | **只管设置**（本节 17.1） |
| 3 | 实例右键「问 Nova」 | `NovaRuntime.RequestInput` → `InputRequested` 事件回填各入口输入框 |
| 4 | 游戏内浮窗 `NovaOverlayWindow` | 需在设置中显式授权（本节 17.3） |
| 5 | 顶栏浮窗提醒 | `TeachingTip` + 状态文案 |

**状态机**（`NovaState`，`NovaRuntime.cs`）：`Idle` → `Thinking` → `HasPlan` → `Observing`，以及 `Offline`。
状态文案原文：`待命` / `思考中…` / `有提案` / `观察中` / `离线`（着色见 2.1）。

### 17.1 Nova 设置页 `NovaPage.xaml`

**入口**：导航第 9 项，`Content="Nova"`（**未使用 `loc:Loc.Text`，直接内联**）、`Tag="ai"`，
图标 `<ImageIcon Source="{StaticResource NovaMarkSmall}" />`；排在「账户」「插件」之后。

**根布局**：`Grid MaxWidth=1080 HorizontalAlignment=Stretch` → `ScrollViewer(Padding="0,0,8,0")`
→ `StackPanel Spacing=16`。

**排版约定**（源码头注释）：卡片外壳 = `ScCard`（`GlassCardBrush` + 1px 描边 + 圆角 16 + 内边距 16）；
卡片抬头 = `ScSectionSpine`（accent，3×14，圆角 2）+ `ScSectionTitle`（14px）；
卡片说明 = `ScCardHint`（11px 次级色）；状态芯片 = `ScChip` / `ScChipAccent`。

**八个分区**：

1. **「Nova 账户」** — 说明 `登录后可用云端模型与邀请、时刻等在线能力；凭据只保存在本机。`
   - 已登录块 `AccountInfo`（默认 `Collapsed`）：`ctrl:BrandMark Small`（20×20）+ `已登录`（13px Medium）+
     `UserSub`（12px）+ `GlowButton`「退出登录」。
   - 未登录块 `LoginForm`：`TextBox IdBox`（Header=`Starcloud ID`，`Placeholder="用户名或邮箱"`）；
     `PasswordBox PwdBox`；`ComboBox ChannelBox`（Header=`通道`）：
     `测试通道（本地模拟，无需真实账号）` / `云端通道（真实账号）`，默认 `SelectedIndex=0`；
     `Button`「登录」(`AccentButtonStyle`，`MinWidth=160`) + `ProgressRing` + `LoginError`。
2. **「云端模型」** — 说明 `登录 Starcloud ID 后 Nova 默认走云端模型，不用自己填 Key；用量记在账号上，按周重置。`
   右上角 `GlowButton`「刷新」（12px）。
   - 数值态 `QuotaBody`：`QuotaUsedText`（13px Medium）+ `QuotaChip`（`ScChipAccent`）`QuotaRemainingText`
     + `ProgressBar`（0–100）+ `QuotaResetText`。
   - `QuotaHintText`（无可用数值时说明原因，如未登录 / 测试通道 / 已接自有模型）。
   - `ToggleSwitch GatewaySwitch`（Header=`使用云端模型`，`OnContent="" OffContent=""`）；
     下方说明 `关掉后 Nova 只用下面接入的模型；云端额度仍记在账号上，不浪费。`
3. **「模型设置」** — 说明 `接入任意 OpenAI 兼容服务：选服务商 → 填 Key → 选模型。可添加多个模型并随时切换；接了自己的模型就优先用它，不可用时走上面的云端模型，最后才落到本地模板。Key 只保存在本机。`
   - `ListView ProfileList`（`ScPlainListItem`）：行显示 `Name` + `当前使用`（`ScChipAccent`）+
     `Detail`（`{BaseUrl} · {Model} · Key {掩码}`）；行按钮「设为当前」「删除」。
   - 空态 `EmptyStateView ProfileEmptyText`：`IconImage=NovaMarkSmall`，Title=`还没有添加模型`，
     Subtitle=`接一个 OpenAI 兼容服务作为备用；不接也能用上面的云端模型`。
   - 「新增模型」按钮（`AccentButtonStyle`、`MinWidth=120`）→ 展开 `LlmAddPanel`：
     `ComboBox LlmPresetBox`（Header=`服务商预设`，默认选第一个 **DeepSeek**）；
     `PresetUrlText`；`TextBox LlmBaseUrlBox`（`Placeholder="例如 https://api.example.com/v1"`）；
     `TextBox ProfileNameBox`（Header=`备注名`，`Placeholder="自动带出，可改"`）；
     `PasswordBox LlmKeyBox`（Header=`API Key`，`Placeholder="sk-…"`）；
     `ComboBox LlmModelBox`（Header=`模型`，`IsEditable="True"`）；
     按钮「保存」/「测试连接」/「取消」；提示 `LlmSaveHint`、`LlmTestHint`。
   - **内置预设**（`LlmPresets`）：`DeepSeek`、`OpenAI`、`通义千问 Qwen（阿里云百炼）`、`智谱 GLM`、
     `Moonshot Kimi`、`OpenRouter`、`硅基流动 SiliconFlow`、`Ollama（本机，无需 Key）`、
     `自定义（手填接入地址）`。
   - 运行时赋值的 Header：`PwdBox.Header=密码`、`ChannelBox.Header=通道`、
     `LlmPresetBox.Header=服务商预设`、`LlmBaseUrlBox.Header=接入地址（Base URL，OpenAI 兼容）`、
     `ProfileNameBox.Header=备注名`、`LlmModelBox.Header=模型`。
   - `PwdBox.PlaceholderText` 随通道变化：云端 = `云端通道密码`，
     测试 = `测试通道任意内容即可（至少 4 位）`。
   - 选预设后 `LlmKeyBox.PlaceholderText`：需 Key = `sk-…`；Ollama = `本机 Ollama 无需 Key，留空即可`。
     `LlmModelBox.PlaceholderText` = `从列表选择或手输模型名` / `填写模型名，例如 my-model`。
4. **「游戏内辅助浮窗」** — 说明 `启动游戏时自动弹出浮窗，游戏内随时查资料；与顶栏 Nova 共用同一份对话。`
   `ToggleSwitch OverlaySwitch`（`loc:Loc.Name="游戏内辅助浮窗"`，`OnContent="" OffContent=""`）。
5. **「游戏记录记忆」** — 说明 `Nova 从游戏日志里记录进度与挑战，退出后生成一局回顾。`
   + `MemoryStatsText`（`已记录对局 {N} 场 · 收藏时刻 {M} 个`）
   + `MemoryReportText`（无回顾时 `暂无回顾。完成一局后会自动生成。`）。
6. **「录屏」** — 说明 `默认关闭。未开启时不会读取屏幕内容；开启前会先向你确认。`
   `ToggleSwitch RecordingSwitch`（`Tag="recording"`，`loc:Loc.Name="录屏许可"`）；
   补充说明 `只在游戏运行时采集画面并硬件编码，写入本地磁盘；不采集其他窗口。`；
   按钮「立即录屏」（录制中切换为「停止录屏」）/「录像目录」；`RecordingStatusText`。
7. **「操作日志」** — 右上角 `GlowButton`「清空」。
   空态 `EmptyStateView LogEmptyText`：`Glyph="EC68"`，Title=`暂无记录`，
   Subtitle=`Nova 的每次执行（含开关授权）都会记入这里`。
   `ListView LogList` 行显示 `Title` + `State · TimeText`（最多取 10 条）。
8. **「能力路线」** — 说明 `越往下权限越大，每一层都可以单独关闭。` 四行：

| 层 | 名称 / 说明 | 芯片 |
| --- | --- | --- |
| ① | `顾问 · 只给建议` / `只回答和解释，不动你的文件。` | `已解锁` |
| ② | `动手 · 确认后执行` / `每次执行前列出清单，确认后执行，过程记入上方记录。` | `已解锁` |
| ③ | `主动 · Nova 时刻` / `退出游戏后自动汇总本局进度，形成游戏记录。` | `已解锁` |
| ④ | `在场 · 游戏内浮窗` / `游戏里随时呼出，不用切回启动器。` | `尚未解锁（灰度中）` |

**本页唯一显式字形**：空态的 `EC68`；Nova 品牌图标全部用图片
（`BrandMark Small` / `NovaMarkSmall`）。

**空 / 加载 / 错误态**：登录时 `LoginProgress` 转圈、失败写 `LoginError`（`SystemFillColorCriticalBrush`）；
额度取不到时 `QuotaUsedText = 还没取到额度数值（可点右上角刷新）`、`QuotaRemainingText = 未知`、进度条隐藏；
`LlmTestHint` 进行中 `正在连接…`（次级色），失败红 / 成功绿；
保存校验提示如 `请填写接入地址（Base URL）`、`请选择或填写模型名`、`请填入该服务商的 API Key`。

**本页对话框**（标题 + 按钮均为**硬编码中文，未走 `Loc.T`**）：

| 触发 | 标题 | 正文 | 主 / 关闭 |
| --- | --- | --- | --- |
| 退出登录 | `退出登录` | `退出后将删除本地保存的会话与 API Key。Nova 的操作日志会保留。确定退出吗？` | `退出` / `取消`（默认 Close） |
| 删除模型 | `删除模型` | `删除后该模型的 API Key 也会从本机移除，确定吗？` | `删除` / `取消` |
| 清空操作日志 | `清空操作日志` | `日志记录 Nova 执行过的每一步操作，清空后不可恢复，确定吗？` | `清空` / `取消` |
| 许可开关确认 | `授权开启「{title}」` 或 `关闭「{title}」许可` | 开启 = `开启后 Nova 将：{boundary}。\n\n此授权会记入操作日志，可随时在这里关闭。`；关闭 = `关闭后 Nova 立即停用该能力，关闭动作同样记入操作日志。` | `授权开启` / `确认关闭`，关闭 `取消` |

> 许可开关的行为：**开关先还原、确认后才真正切换**。

### 17.2 Nova 对话组件 `NovaChatView.xaml`

被 Nova 面板（440）与游戏内浮窗（320）共用；**不写死行宽**（气泡 `MaxWidth` + 对齐伸缩，快捷能力可换行）。
自身不持有会话，直接订阅 `NovaRuntime`。

**根布局**：`Grid RowSpacing=8`，三行：`*`（对话流）/ `Auto`（完整日志开关行）/ `Auto`（输入行）。

**对话流**：`Border Style="{StaticResource ScCard}" Padding="8"` 内
`ListView ChatList`（`SelectionMode=None`、`Padding=4`），
`ItemContainerTransitions` = `AddDeleteThemeTransition` + `RepositionThemeTransition`（新气泡淡入并让位）。
空会话叠加 `EmptyStateView ChatEmpty`：`IconImage=NovaMarkSmall`，Title=`描述你的问题`，
Subtitle=`例如启动崩溃、帧数偏低、模组冲突、找不到整合包；日志与配置由 Nova 读取，不需要你手动翻找。`

**消息气泡模板**：外层 `StackPanel MaxWidth=720`、`HorizontalAlignment={x:Bind Align}`、`Margin="0,2"`。

| 部件 | 细节 |
| --- | --- |
| 工具动作提示行（气泡外，`ToolStatusVisible` 时显示） | `ProgressRing` 13×13 + `ToolStatus`（12px 次级色，`Opacity=0.75`） |
| 文本气泡 | `Border`：`Background={x:Bind Bubble}`、`CornerRadius={x:Bind Corners}`、`Padding="12,9"`，`BubbleVisible` 控制显隐 |
| 用户气泡 | `Bubble=AccentFillColorDefaultBrush`、文字 `TextOnAccentFillColorPrimaryBrush`、**右对齐**、圆角 `12,12,4,12` |
| 助手气泡 | `Bubble=GlassCardBrush`、文字 `TextFillColorPrimaryBrush`、**左对齐**、圆角 `12,12,12,4` |
| 等待首个增量 | 三个 7×7 `Ellipse`（`Fill=TextBrush`、`Opacity=0.3`）；`TypingDot_Loaded` 用永续 `Storyboard`，`BeginTime = phase*150ms`，Opacity 0.25→1→0.25（240ms / 480ms 关键帧）做打字节奏 |
| 正文 | `RichTextBlock ctrl:NovaMarkdown.Text="{x:Bind Content}"`，`TextWrapping=Wrap`、`IsTextSelectionEnabled=True`、`Foreground=TextBrush` |
| 时间戳 | `TimeText`（11px、`Opacity=0.65`，内容非空才显示） |
| 确认清单卡（`HasPlanVisible`） | `Border`（`GlassCardBrush` + 1px 描边 + 圆角 12 + `Padding="16,12"`）；抬头 = `FontIcon EA7C`（14px、`BrandBlueBrush`）+ `Plan.Title`；`ItemsControl` 逐条列 `NovaActionItem`（`Title` 13px Medium + `Detail` 12px 次级色）；按钮 `确认执行`(`AccentButtonStyle`) / `取消`(`GlowButton`)；`PlanStateText` 展示执行结果 |

**Markdown 渲染方式**：附加属性 `NovaMarkdown.Text`（`NovaMarkdown.cs`）把 `MarkdownDocument.Parse`
的 token 转成 `RichTextBlock` 内联——支持粗体、斜体、删除线、行内代码（Consolas）、
链接（**仅 http/https**）、代码块（Consolas，字号 `max(baseSize-1, 11)`）、
标题（`baseSize+6/+4/+2/+1`）、列表（悬挂缩进 `Margin.Left=14, TextIndent=-14`）、
引用（前缀 `│ ` 且斜体）、`\n` 软换行。

**输入区**：

| 元素 | 细节 |
| --- | --- |
| 完整日志行 | `ToggleSwitch FullLogSwitch`（`OnContent="" OffContent=""`）+ 文本 `发送完整日志`（13px）；开关打开时显示 `FullLogHint`：`完整日志会显著增加令牌消耗，费用以你接入的模型服务计费，仅在裁剪后仍无法定位时使用` |
| 输入行 | `Grid ColumnSpacing=8`：`TextBox InputBox`（`Placeholder="描述问题，例如「启动崩溃」「帧数很低」"`，**回车发送**）+ `ProgressRing SendProgress`（22×22）+ `Button`「发送」(`AccentButtonStyle`、`Padding="16,5"`、`MinWidth=80`) |

**交互**：`Send_Click` / 回车 → `NovaRuntime.SendAsync(input, FullLogSwitch.IsOn)`；
空串或思考中忽略；发送后清空并聚焦。思考中（`State == Thinking`）发送按钮禁用、`SendProgress.IsActive=true`。
确认清单：`PlanConfirm_Click` → `NovaRuntime.ConfirmPlanAsync`（仅 `PlanState == "pending"`）；
`PlanCancel_Click` → `CancelPlan`。

**`PlanStateText` 原文**：done = `✔ 已确认执行（已记入操作日志）`（+ 换行结果）；
cancelled = `已取消，未做任何改动`；failed = `执行失败（已记入操作日志）：{0}`。

**未登录 / 出错**：未登录时 `SendAsync` 追加助手消息
`先在侧栏 Nova 页登录 Starcloud ID，我才能上线干活。`；出错时气泡正文写 `出错了：{0}`。

### 17.3 Nova 游戏内浮窗 `NovaOverlayWindow.xaml`

**入口**：游戏启动且 `Settings.NovaOverlayEnabled` 开启时，由 `NovaRuntime.OnGameLaunched` →
`ShowOverlay()` 懒创建显示；`Ctrl+N` 全局热键切换展开 / 收起。

**窗口形态**：`ExtendsContentIntoTitleBar`；
`OverlappedPresenter`：`IsAlwaysOnTop`、不可调整 / 最小化 / 最大化，
`SetBorderAndTitleBar(false, false)`；`SystemBackdrop = TransparentAcrylicBackdrop`；
`WS_EX_TOOLWINDOW`（**不进任务栏 / Alt-Tab**）；独占全屏时 DXGI 轮询 2s 自动隐藏
（**宁可漏判不误判**）；位置记忆写回 `Settings.NovaOverlayX/Y`。

**三种形态**：收起态 `48×48`；Toast 态窗口 `272×72`（注释：216 toast + 8 + 48）；展开态面板 `320×420`。

| 形态 | 结构 |
| --- | --- |
| 收起态 | `Grid CollapsedHost`，三列 `*,8,48`。`Border ToastHost`（`AcrylicBackgroundFillColorDefaultBrush` + 1px 描边 + 圆角 12 + `Padding="12,8"` + `MaxWidth=216`，`ToastText` 12px、`MaxLines=3`，10s 自收）。`GlowButton OrbBtn` 48×48 圆角 24（`Background=AcrylicBackgroundFillColorDefaultBrush`）内含 `BrandMark Small 20×20` + `Ellipse OrbDot`（8×8 `#F59E0B`，右上，`HasPlan` 时可见） |
| 展开态 | `Border PanelHost` `320×420`、圆角 16、`AcrylicBackgroundFillColorDefaultBrush`、`Padding="12"`；标头 `PanelTitle = "Nova"`（Medium）+ 收起按钮 `CollapseBtn`（`SubtleButtonStyle`，`FontIcon EA6F` 14px，`loc:Loc.Name="收起浮窗"`）；下方内嵌 `<views:NovaChatView />` |

**图标**：`EA6F`（收起浮窗）。

**交互**：Orb 点击展开（**4px 位移阈值**区分点击 / 拖动，拖动结束落在 Orb 内不算点击）；
面板内 `NovaChatView` 全量可用；
`ShowToast(string)` 时窗口加宽且**右缘不动**（Orb 恒贴原位，Toast 向左展开），
打开 / 关闭边界夹回工作区；`ApplyNovaState` 把五态色画在 `OrbBtn.BorderBrush`，
`ToolTip = "Nova：{StateText}"`。

---

## 18. 插件

### 18.1 插件管理页 `PluginsPage.xaml`

**入口**：导航第 8 项「插件」（`EABC`）。

**根布局**：`Grid MaxWidth=1280` → `ScrollViewer(Padding="0,0,8,0")` → `StackPanel Spacing=16`。

| # | 分区 | 内容 |
| --- | --- | --- |
| 1 | 页头 | 右侧按钮「安装插件…」(`AccentButtonStyle`、`MinWidth=120`) |
| 2 | 免责声明 `InfoBar DisclaimerBar` | `IsOpen=True`、`IsClosable=True`、`Severity=Warning`；Title = `插件由第三方独立开发`；Message = `启动器本体不对插件内容、合法性负责；插件无权修改游戏认证、UUID 与登录校验逻辑。加载第三方插件前会再次向你确认。`。**看过一次（`IsPageTipShown`）后不再常驻** |
| 3 | 「插件面板」卡（`PanelsCardVisible` 时显示） | 外壳 `GlassCardBrush` / 1px / 圆角 16 / `Padding=16`；标题 `插件面板`（13px Medium）；`ItemsControl` 逐行 `GlowButton`（`Tag={Id}`）内含 `FontIcon EABC`（`BrandBlueBrush` 14px）+ `Title` + `PluginName`（11px、`Opacity=0.6`）；点击进 `PluginPanelPage` |
| 4 | 「已安装」卡（圆角 16、`Padding=16`） | 标题 `已安装`；`ListView PluginList`（六个框架行底色 key 置 `Transparent`；容器 `Horizontal/VerticalContentAlignment=Stretch`、`MinHeight=0`、`Padding=0`、`Margin="0,2"`；`AddDeleteThemeTransition` + `RepositionThemeTransition`） |
| 5 | 页脚说明 | `把 .mclplug 文件放到这里：点「安装插件…」选择插件包，安装后打开开关启用。`（12px 三级色） |
| 6 | 结果条 `InfoBar NotifyBar` | 默认收起，`Severity=Success`；安装成功 = `插件已安装：{Name} {Version}` |

**插件行卡**：`Grid CornerRadius=8`（`LayerFillColorDefaultBrush`）+ 悬停光斑 `Border PluginCardGlow`
（`RadialGradientBrush` 多档 `#28000000` → 透明，`Opacity="0"`）+ 内容 `Grid Padding="12,10"`。

**行字段**：

| 字段 | 内容 |
| --- | --- |
| `Name` | Medium |
| `Version` | 11px、`Opacity=0.65` |
| 签名芯片 | `官方`（`ScChipAccent`，官方可见）/ `第三方未验证`（`ScChipCaution`，非官方可见） |
| API 芯片 | `ScChip`，**2.0 插件才有**，文案运行时拼：可运行 = `API {版本}`；不可运行 = `API {版本} · 暂不可启用` |
| `Description` | 12px、`Opacity=0.75`、次级色 |
| `Detail` | 11px、`TextFillColorTertiaryBrush`；2.0 = `作者 {Author} · 能力：{perms}`，1.x = `作者 {Author} · 权限：{perms}` |

**行按钮**（`Grid.Column 1/2/3`）：

| 列 | 控件 |
| --- | --- |
| 1 | 「打开面板」`GlowButton`（12px，**仅注册了面板时可见**） |
| 2 | 「卸载」`GlowButton`（12px） |
| 3 | `ToggleSwitch`（`OnContent="" OffContent=""`，`IsOn={Enabled}`，`Tag={Id}`，仅用于 Enable / Disable） |

**空态** `EmptyStateView EmptyText`：`Glyph="EABC"`，Title=`还没有安装插件`。

**权限白名单文案映射**（`DescribePermissions`）：空权限 → `无特殊权限`；
1.x → 原样 ` · ` 拼接；2.0 → 按 `PluginPermissions.RiskOf` 标注：
High = `{raw}（高风险）`、Medium = `{raw}（中风险）`、Low = `{raw}（低风险）`。

**本页对话框**（原文）：

| 触发 | 标题 | 正文 | 主 / 关闭 |
| --- | --- | --- | --- |
| 启用第三方插件（非官方且未接受过） | `加载第三方插件「{Name}」？` | `该插件由第三方（{Author}）独立开发，启动器本体不对插件内容、合法性负责。\n\n插件声明的权限：{perms}\n插件无权修改游戏认证、UUID 与登录校验逻辑；越权参数会被启动器拦截丢弃。\n\n确定启用吗？` | `我已知晓风险，启用` / `取消`（默认 Close） |
| 卸载 | `卸载插件「{Name}」？` | `插件文件与启用状态将被删除，此操作不可撤销。` | `卸载` / `取消` |
| 安装失败 | `插件安装失败` | 错误列表 | 仅「知道了」 |
| 启用失败 | `插件启动失败` | err | 仅「知道了」 |

取消免责确认 → 开关保持关闭；确认后 `AcceptDisclaimer`。
`Install_Click` 用 `FileOpenPicker`（过滤 `.mclplug` / `.zip`）→ `PluginService.Install`；
`OpenPanel_Click` → `Frame.Navigate(typeof(PluginPanelPage), panel, MotionService.TransitionFor(...))`。

### 18.2 插件一级导航页 `PluginNavPage.xaml`

**职责**：宿主按 `contributes.navItems` 在**左侧导航栏**为插件开一级入口
（生成逻辑见 4.4）。点选后导航到本页（带 `(pluginId, navId)` 参数）并收起侧栏；
插件被停用时回落「插件」页。

**根布局**：`Grid RowSpacing=8`，三行 `Auto / * / Auto`。

| 行 | 内容 |
| --- | --- |
| 0 | 页头仅标题 `NavTitle`（**24px Medium**，`TextWrapping`）——**没有返回按钮**（导航栏本身即返回） |
| 1 | `Border`（`GlassCardBrush` + 1px + 圆角 16 + `Padding="4"`）内 `ScrollViewer`（横向滚动禁用）→ `StackPanel ViewRoot Margin="16,14,16,14" Spacing="10"`；内容由插件提供的**声明式视图 JSON** 经 `PluginViewRenderer.RenderFile` 渲染（与插件页面板同源、同一能力层、同一失败兜底） |
| 2 | `InfoBar ActionBar`（动作 / 绑定写回结果，`IsOpen=False`，可关闭） |

**空 / 错误态** `ShowGone()`：`ViewRoot` 显示
`插件已不在已安装列表中，这一页无法渲染。`（13px、`Opacity=0.75`）+
`GlowButton`「去插件页」（→ `NavigateTo("plugins")`）。

### 18.3 插件面板宿主页 `PluginPanelPage.xaml`

**入口**：从「插件」页的「插件面板」列表 / 行内「打开面板」按钮，
`Frame.Navigate(typeof(PluginPanelPage), panelRegistration, ...)`。

**根布局**：`Grid RowSpacing=8`，四行 `Auto / Auto / * / Auto`。

| 行 | 内容 |
| --- | --- |
| 0 | 页头：`GlowButton`「返回」（`Padding="12,5"`）+ `PanelTitle`（24px Medium） |
| 1 | 来源声明 `InfoBar SourceBar`：`IsOpen=True`、`IsClosable=True`、`Severity=Informational`；Title = `panel.PluginId`；Message = `该面板由第三方插件提供，内容与启动器本体无关。`（按插件持久化，看过一次不再弹） |
| 2 | 面板内容：`Border`（`GlassCardBrush` + 1px + 圆角 16 + `Padding="4"`）内 `Grid`：<br>· 1.x → `controls:WebView2 x:Name="Web"`（`DefaultBackgroundColor="Transparent"`），用 `file:///` 加载插件目录内 HTML；<br>· 2.0 → `ScrollViewer DeclarativeHost`（默认 `Collapsed`）→ `StackPanel ViewRoot`，由 `PluginViewRenderer.Render` 画原生控件（**无 HTML / 脚本入口**） |
| 3 | `InfoBar ActionBar`（动作 / 绑定结果） |

**错误态**：安装列表找不到 → `插件已不在已安装列表中，面板无法渲染。`；
解析失败 → `视图无法渲染：\n{errors}`；渲染异常 → `视图渲染失败：{ex.Message}`
（均 13px、`Opacity=0.75`）。

---

## 19. 跨页规则

### 19.1 版本图标四级优先级（`Views/Rows/VersionRowHelpers.cs`）

`DisplayIconPath` 按以下顺序取，先命中先用：

**① 自定义图标**（`ResolveIconPath(dotMc, customIconPath)`）

- 目录固定 `{DotMc}\starcloud\icons\`；
- `iconPath` 是绝对路径就直接用；否则 `Path.Combine(dotMc, "starcloud", "icons", iconPath)`；
- **必须 `File.Exists` 才返回**，否则返回 `null` 继续下一级；空白字符串直接跳过。
- 更换图标时（`VersionDetailPage.ChangeIcon_Click`）把所选文件复制为
  `{版本名}{原扩展名}` 存进该目录。

**② 加载器图标**（`LoaderIconFiles`，**顺序敏感**）

| 顺序 | 匹配 key | 文件名 |
| --- | --- | --- |
| 1 | `neoforge` | `neoforge.png`（**必须先于 forge**：id 中 `neoforge` 含 `forge` 子串） |
| 2 | `forge` | `forge.png` |
| 3 | `fabric` | `fabric.png` |
| 4 | `quilt` | `quilt.png` |

匹配规则（`LoaderKeyOf`）：先看**版本 JSON 文本**是否忽略大小写包含该 key，
再退回看**版本 id** 是否包含；命中即用该加载器图标。
（只看 id 不行：整合包 / 自建实例名常与加载器无关。）

**③ 版本类型图标**（`VersionIconFiles`，随输出目录发布在 `Assets\MC-Icons\`）

| 版本 JSON `type` | png 文件名 | 意象 |
| --- | --- | --- |
| `release` | `natural_grass_block.png` | 正式版草方块 |
| `snapshot` | `block_of_amethyst.png` | 快照紫水晶块 |
| `old_beta` | `masonry_cobblestone.png` | 远古版石砖 |
| `old_alpha` | `natural_dirt.png` | 远古版泥土 |

`type` 缺失 / 损坏默认 `release`；未知 type 回落 `VersionIconFiles["release"]`；
文件不存在则 `BuiltInIconPath` 返回 `null`。

> **资源来源**：这 4 张图**不在仓库的 `Assets/` 下**，而是用 csproj 的 `<Content Include>`
> 从仓库根 `MC-Icons/07-Mini-Blocks-128px/`、`MC-Icons/03-Loaders/` **链接**到
> 输出目录的 `Assets/MC-Icons/`，不复制源文件：

```xml
<Content Include="..\..\..\MC-Icons\07-Mini-Blocks-128px\natural_grass_block.png"
         Link="Assets\MC-Icons\natural_grass_block.png" CopyToOutputDirectory="PreserveNewest" />
<Content Include="..\..\..\MC-Icons\03-Loaders\fabric.png"
         Link="Assets\MC-Icons\fabric.png" CopyToOutputDirectory="PreserveNewest" />
```

内置图标缓存：`VersionIconCache` / `BuiltInIconPaths` / `MetaCache`，按文件写入时间失效。

**④ 兜底 FontIcon 字形码：`EC62`**

| 位置 | 字号 |
| --- | --- |
| 版本页行内 | 14 |
| 版本详情页 `IconFallback` | 16 |
| 资源下载窗 `DlIconFallback` | 22 |

`LocalVersionRow.Icon` 为 `null` 时 `IconFontVisibility=Visible`、`IconImageVisibility=Collapsed`。

> 返回的是文件路径字符串；实际渲染由 `ResolveIconSource`（`new BitmapImage(new Uri(path))`）完成。
> 类型标签 `DetectKind` 与图标**同源**（同一份缓存），避免出现「标签 Fabric、图标草方块」。

### 19.2 「推荐 / 最新稳定版」这类标记从哪来

`LoaderBuild` 定义于 `Core/Services/ForgeInstallerService.cs`：

```csharp
public sealed record LoaderBuild(string Version, string? Note = null, bool Stable = true);
```

注释原文：`Note 为推荐标记如 "推荐"/"最新稳定版"`。

| 加载器 | 数据源 | Note 规则 |
| --- | --- | --- |
| Forge（`ListForgeBuildsAsync`） | `maven-metadata.xml` 全量构建 + `promotions_slim.json` | 命中 `{mc}-recommended` → `推荐`；命中 `{mc}-latest` → `最新`；否则 `null` |
| NeoForge（`ListNeoForgeBuildsAsync`） | `maven.neoforged.net` releases（1.20.1 走 legacy `forge` 构件） | 版本号以 `-beta` 结尾 → `Beta`；第一个非 beta → `最新稳定版`；其余 `null`。**此支不产出「推荐」** |
| Fabric / Quilt（App 侧 `DownloadPage.xaml.cs`） | `meta.fabricmc.net` / `meta.quiltmc.org` | `Stable` 为真 → `Note = null`；否则 `Note = Loc.T("预览版")` |

**UI 侧渲染**（`DownloadPage.xaml.cs`）：

- 推荐卡副标题 `RecNote.Text` = `Note is { } note ? Loc.T(note) : Loc.T("稳定版")`
  —— Note 直接按「源语言即键」本地化；**无 Note 时回落「稳定版」**。（11px 次级色。）
- 推荐卡右上芯片 `RecBadgeText`：已选中该构建 → `已选`，否则 `推荐`
  （XAML 默认文案 `推荐`，底色 `SystemFillColorSuccess*`）。
- 推荐构建挑选（`RecommendedOf`）：Forge = `Note=="推荐"` ?? `Note=="最新"` ?? 首项；
  其余加载器 = 首项。
- 构建列表行 `LoaderBuildRow.Sub` = `Note is { } n ? Loc.T(n) : (Stable ? "稳定版" : "预览版")`。
- 计数文案：`全部版本 ({0})`。

### 19.3 本地化

- 机制：**源语言即键**——中文原文本身就是 key，因此 `zh-CN` **不需要词典文件**，缺词条时回退原文。
  只需维护一份英文词典 `Strings/en-US.json`（嵌入式资源）。
- 不走 `x:Uid` / `.resw` / `PrimaryLanguageOverride`：非打包应用下那套机制不持久化、无法清空，
  且 `ResourceLoader` 有不遵循 override 的历史问题（WindowsAppSDK #5335 / #2806）。
- 两个入口：XAML 附加属性 `loc:Loc.Text` / `Name` / `Tip` / `Title` / `Placeholder` / `Header`；
  代码里 `Loc.T("中文")`。
- 即时切换：`Loc.SetLanguage` → 换词典 → `ReplayAll()` 重放全部已本地化元素 → 触发 `LanguageChanged`。
- **已知未本地化缺口**：`NovaPage` 的四个对话框标题 / 正文为硬编码中文；
  各页的 `Forge` / `NeoForge` / `Fabric` / `Quilt` / `OptiFine` / `Modrinth` / `CurseForge` /
  `Fabric|Forge|NeoForge|Quilt` 加载器选项均为英文硬编码（属专有名词，未走 `Loc`）。
- 存档页 `VersionLabel` 在共享实例下取字面量 `共享`（该值是路径豁免项，不参与本地化）。

### 19.4 主题与背景的应用链路

1. `Settings.Theme`（`system` / `light` / `dark`）→ `App` 根元素 `RequestedTheme`；
2. `Settings.BackgroundMode`（`none` / `builtin` / `file`）→ `MainWindow.BackgroundLayer` 三层；
3. `Settings.UiAnimations` → `MotionService.Enabled` → 所有 `MotionFx` 入口；
4. `Settings.UiLanguage` → `Loc.SetLanguage`。

> **`ThemedDialog` 存在的理由**：WinUI 3 的 `PopupRoot` **不继承主题**。
> 它在 `Loading` 时把 `RequestedTheme` 钉到 `XamlRoot` 根元素的 `ActualTheme`；
> 构造时还会 `ApplyShadowButtons` 给 Close / Secondary 挂 `ScShadowButtonStyle`
> （按钮由 `ContentDialog` 模板生成，XAML 里够不到，没法逐个挂附加属性）。

---

## 20. 弹窗清单

**通用基类 `ThemedDialog : ContentDialog`**：全项目**没有**直接 `new ContentDialog`，
一律走 `ThemedDialog` 或 `ContentDialog` 的子类（如 `ContentDownloadDialog`、`ModPackExportDialog`，
后者显式指定 `DefaultContentDialogStyle` 并自己挂阴影）。

### 20.1 认证类

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| `AuthDialogs.MicrosoftDeviceCodeAsync`（浏览器回退） | `登录 Microsoft 账户` | `使用一次性验证代码登录到你的 Microsoft 账户` + 大号验证码 + `使用浏览器打开 {0}，输入以上代码…` + 「复制并打开验证网站」+ `ProgressRing` + `等待授权…` | 关闭 `取消` |
| `AuthDialogs.ExternalPasswordAsync`（账户详情「重新登录该账号」） | `第三方登录` | 字段「认证服务器地址」「用户名」「密码」+「登录」按钮 + 状态 / 错误行（占位分别为 `认证服务器地址` / `邮箱或用户名` / `密码`） | 关闭 `取消` |
| `MsLoginWindow`（`Window`，内置登录） | Title = `登录 Microsoft 账户` | 抬头 `验证码已自动填入下方页面，登录完成后窗口会自动关闭。` + `WebView2` + 底栏状态 `等待授权…` | 内嵌按钮「复制验证码」/「改用浏览器打开」（**非 ContentDialog 按钮**） |

### 20.2 启动前 / 启动失败类

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| `EnvCheckDialogs.ShowAsync`（首页启动前体检） | `运行环境检查` | `检测到以下问题，可先修复再继续启动。` + 逐项（色点 + 标题 + 细节 + 修复按钮文案取 `FixHint` 或 `修复` / `修复中…` / `已修复`） | 主 `仍然启动` / 关闭 `取消` |
| `CrashDialog.ShowAsync`（首页 `ShowCrashDialogAsync`） | `游戏异常退出（退出码 {0}）` | 诊断标题（16px Medium）+ Summary（13px）+ 建议列表（`• …`）+ 证据区（`查看证据` / `收起证据` + `证据来源：`）+ 修复说明 | 主按钮**动态**（`FixLabel` 或 `重新启动`，修复后变 `重新启动`）/ 次 `让 Nova 看看` / 关闭 `返回主页` |
| 首页「补全失败」 | `补全失败`（**硬编码，未本地化**） | 失败文件清单（`• path`，> 5 个追加 `\n等 {0} 个文件`） | 主 `仍然启动` / 关闭 `取消` |
| 首页「启动前命令失败」 | `启动前命令失败`（**硬编码**） | `退出码 {0}\n{tail}` | 主 `仍然启动` / 关闭 `取消` |

### 20.3 版本 / 内容类

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| 版本页 `Delete_Click`（运行中） | `删除版本` | `版本 {0} 正在运行，请先停止游戏再删除。` | Primary 隐藏 / 关闭 `关闭` |
| 版本页 `Delete_Click`（未运行） | `删除版本` | `确定要删除版本 {0} 吗？\n将删除其版本文件夹，此操作不可恢复。` | `删除` / 关闭 `关闭` |
| 版本页删除失败 | `删除失败` | `ex.Message` | 关闭 `确定` |
| 版本详情 `Delete_Click`（隔离时追加） | `删除版本` | `确定要删除版本 {0} 吗？…不可恢复。` + （隔离）`\n注意：版本隔离下的 mods / 存档等也将一并删除。` | `删除` / 关闭 `关闭` |
| 版本详情 `DeleteFile_Click` | `删除内容文件` | `确定要删除 {0} 吗？此操作不可恢复。` | `删除` / `取消` |
| 版本详情 `Verify_Click`（正常） | `验证文件` | `一切正常，未发现缺失或损坏的文件。` | 关闭 `确定` |
| 同上（JSON 无法恢复） | `验证文件` | `版本 JSON 缺失或损坏且无法自动恢复，请到下载页重新安装。` | 关闭 `确定` |
| 同上（确认补全） | `验证文件` | `发现 {0} 个缺失或损坏的文件，立即补全吗？` | `立即补全` / `取消` |
| 同上（进度） | `正在补全缺失文件` | `ProgressBar` + `{done}/{total}（{pct}%）` | 无按钮（完成后 Hide） |
| 同上（完成） | `验证文件` | `补全完成，文件已恢复完整。` | 关闭 `确定` |
| 同上（失败） | `补全失败` | `以下文件补全失败：\n{0}` | 关闭 `确定` |
| 版本详情 `IsolationRadio_SelectionChanged` | `切换版本隔离` | `隔离方式将切换为「{0}」，当前没有需要迁移的内容目录。` 或 `…\n\n以下目录将从\n{1}\n移动到\n{2}\n\n` + 目录清单 | `立即移动` / `仅切换不移动` / `取消` |
| 版本详情 `JavaBrowse_Click` | `无法识别的 Java` | `未能从该文件解析出 Java 版本：\n{0}` | 关闭 `确定` |
| `ContentDownloadDialog`（版本详情内容分类、社区行点击） | 按类型：`下载 Mod` / `下载 资源包` / `下载 光影包` / `下载 整合包` / `下载数据包` | 项目详情卡 / 版本芯片 / 文件行；安装目标 `DlTarget`；图标 `EA6F` 搜索、`E973` 转到 Modrinth、`EBA2` 转到 MC 百科、`EA9F` 复制名称、`E908` 返回搜索结果、兜底 `EAA5` / `EC62` | 关闭 `关闭` |
| `ContentDownloadDialog.PickSaveAsync` | `装载到存档` | `把数据包装载到「{0}」的哪个存档？`（无存档时 `该版本没有存档，请先创建世界。`） | `装载` / `取消` |

### 20.4 整合包 / 备份类

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| `ModPackExportDialog.ShowAsync` | `导出整合包` | 源版本行 + 小节「导出格式」「包元数据」「加载器与依赖（可手改）」「打包内容」「打包策略与排除规则」；格式选项 `Modrinth 整合包（.mrpack）` / `CurseForge 整合包（.zip）` / `MultiMC / Prism 实例包（.zip）` | `下一步` / `取消` |
| `ModPackDialogs.ImportAsync` | `导入整合包` | `格式：{0}`、`包名：{1}` + MC / 加载器 / 文件数行 +「新建版本名称」+「版本隔离（推荐，整合包内容单独存放）」+ 说明 | `导入` / `取消` |
| `ModPackDialogs` 导入进度 | `导入整合包` | 进度面板（`Placeholder="准备中…"` + `ProgressBar`） | 关闭 `后台导入` |
| `ModPackDialogs.ImportFromLinkAsync` | `从链接导入` | `支持 Modrinth / CurseForge 整合包页面链接，以及 .mrpack / .zip 压缩包直链。` + 输入框 | `下载并导入` / `取消` |
| `ModPackDialogs.BackupAsync` | `备份与回档 · {0}` | 「备份粒度」单选项 `最小（版本定义与启动器设置）` / `标准（该版本的内容）` / `完整（含共享 .minecraft 内容）` +「附加内容」勾选 `世界存档` / `配置` / `资源包` / `光影` +「备份名称」+ 预估 +「已有备份」列表（每行「回档」「删除」）+「打开备份目录」「重建索引」 | `开始备份` / `关闭` |
| `ModPackDialogs` 进度（多态） | `正在备份` / `正在回档` / `导出整合包` / `从链接导入` | 进度面板（状态 + `ProgressBar`） | 关闭按钮分别为 `后台备份` / `后台回档` / `后台导出` / `后台下载` |
| `ModPackDialogs.ConfirmAsync`（删除备份 / 确认回档） | `删除备份` / `确认回档` | `确定删除备份「{0}」？此操作不可撤销。` / `将把「{0}」的内容覆盖回当前版本。\n回档前会先自动备份当前状态。` | `确定` / `取消` |
| `ModPackDialogs.InfoAsync` / `ErrorAsync` | `无法导入整合包` / `导入完成` / `导出完成` / `备份完成` / `回档完成` / `导出失败` / `导入失败` / `备份失败` / `回档失败` / `备份与回档` / `下载失败` | 传入 message | 关闭 `确定` |

### 20.5 存档类

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| 存档页 `Backup_Click` | `备份世界` | `将「{0}」压缩备份到 backups 文件夹，可能需要一些时间，继续吗？` | `开始备份` / `取消` |
| 存档页 `Delete_Click` | `删除世界` | `确定要删除「{0}」吗？\n世界文件夹将被移除，建议先备份。此操作不可恢复。` | `删除` / `取消` |
| 存档页跨版本 | `用其他版本打开世界` | 见 9.5 | `启动` / `复制并启动`·`仅复制` / `取消` |
| 存档页成果归属 | `世界成果归属` | 副本为空时 `「{0}」已复制到 {1}，但游戏未成功启动。这份副本要如何处理？`；否则 `「{0}」在 {1} 中的改动将如何处理？` | `保存到当前版本` / `保留在原本的版本` / `丢弃本次改动` |

### 20.6 运行状态 / 联机类

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| 运行状态 `Kill_Click` | `结束游戏进程` | `将强制结束 {0}（PID {1}），未保存的进度会丢失。` | `结束进程` / `取消` |
| 运行状态 `Restart_Click` | `重启游戏` | `将结束当前进程，并回到首页用 {0} 重新启动。` | `重启` / `取消` |
| 联机 `Stop_Click` | `停止联机` | `确定要停止联机吗？该房间的所有玩家都会断开连接。` | `停止联机` / `取消` |
| `MultiplayerDialogs.AskJoinCodeAsync` | `加入好友的房间` | `房主把邀请码发给你后，粘贴到这里即可进房。` + `TextBox`（`Placeholder="粘贴房主分享的邀请码…"`、`MinWidth=320`） | 主 `加入房间`（默认禁用，非空才启用）/ 关闭 `取消`；空输入错误 = `请先粘贴房主分享的邀请码。` |

### 20.7 账户 / 皮肤类

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| 账户页 `ScLogout_Click` | `退出登录` | `退出后本地登录状态将被清除，确定退出吗？` | `退出` / `取消`（默认 Close） |
| 账户详情「从链接导入」 | `从链接导入皮肤` | `TextBox`（`Placeholder="皮肤图片链接（http/https）"`） | `导入` / `取消` |
| 账户详情皮肤右键「重命名」 | `重命名皮肤` | `TextBox` | `保存` / `取消` |
| 账户详情 `ConfirmSkinChangeAsync`（仅微软账户） | `正版账户换肤警告` | 多行说明（只影响本机 / 不改动上传删除官方皮肤 / 他人看到的仍是官方皮肤 / 下次启动生效 / 每次更换都会再确认） | `我已了解，仍然更换` / `取消`（默认 Close） |
| 账户详情 `DeleteAccount_Click` | `删除账户` | `确定要删除账户 {0} 吗？` | `删除` / `取消` |
| 首页「添加离线账户」 | `添加离线账户` | `TextBox`（`Placeholder="玩家名（3-16 位字母、数字或下划线）"`） | `添加` / `取消` |
| 首页 `DeleteVersion_Click` | `删除版本` | `确定要删除版本 {0} 吗？\n将删除其版本文件夹，此操作不可恢复。` | `删除` / `取消` |

### 20.8 插件 / Nova / 其他

| 名称 / 入口 | 标题原文 | 正文要点 | 按钮 |
| --- | --- | --- | --- |
| `PluginHostActions.DialogAsync`（info） | 传入 title 或 `插件提示` | message | 关闭 `知道了` |
| `PluginHostActions.DialogAsync`（确认） | 传入 title 或 `插件提示` | message | `确认` / `取消` |
| 插件页 `ShowDisclaimerAsync` | `加载第三方插件「{0}」？` | 见 18.1 | `我已知晓风险，启用` / `取消` |
| 插件页 `Uninstall_Click` | `卸载插件「{0}」？` | `插件文件与启用状态将被删除，此操作不可撤销。` | `卸载` / `取消` |
| 插件页 `ShowMessageAsync` | 传入 title | 传入 message | 关闭 `知道了` |
| 设置页删 Java | `删除 Java` | `将从启动器目录删除 Java {0}（{1}）。此操作不可撤销。` | `删除` / `取消` |
| `UpdateDialog.ShowAsync` | `发现新版本 {0}` | `当前版本 {0} → 新版本 {1}` +「更新说明」（可滚动）+ `更新期间启动器会自动关闭，完成后自动重启。` + 进度 | 主 `立即更新`（就绪后 `立即重启安装`）/ 关闭 `稍后` |
| `NovaPage` 退出登录 | `退出登录` | `退出后将删除本地保存的会话与 API Key。Nova 的操作日志会保留。确定退出吗？` | `退出` / `取消` |
| `NovaPage` 删除模型 | `删除模型` | `删除后该模型的 API Key 也会从本机移除，确定吗？` | `删除` / `取消` |
| `NovaPage` 清空操作日志 | `清空操作日志` | `日志记录 Nova 执行过的每一步操作，清空后不可恢复，确定吗？` | `清空` / `取消` |
| `NovaPage` 许可开关 | `授权开启「{title}」` / `关闭「{title}」许可` | 见 17.1 | `授权开启` / `确认关闭` / `取消` |

---

## 21. 附录

### 21.1 资源键速查（按用途分组）

**颜色 / 材质**

| 键 | 用途 |
| --- | --- |
| `AccentFillColorDefaultBrush` / `SecondaryBrush` / `TertiaryBrush` / `DisabledBrush` | 强调色底（默认 / 悬浮 / 按下 / 禁用） |
| `AccentTextFillColorPrimaryBrush` / `SecondaryBrush` / `DisabledBrush` | 浅底上的强调色文字 |
| `TextOnAccentFillColorPrimaryBrush` / `SecondaryBrush` / `DisabledBrush` | 强调色面上的文字（**黑字**） |
| `AccentButtonBackground*` / `AccentButtonForeground*` | 主按钮配色修正（框架别名必须直接覆盖才生效） |
| `TextFillColorPrimaryBrush` / `SecondaryBrush` / `TertiaryBrush` / `DisabledBrush` | 文字四级 |
| `CardStrokeColorDefaultBrush` | 卡片 1px 描边 |
| `AppPaneBackgroundBrush` | 标题栏 / 侧栏 / 调度抽屉底色（不透明实色） |
| `ScRowShadeBrush` | 列表行 / 分类项的压暗底（悬停与选中共用） |
| `GlassCardBrush`（`AcrylicBrush`） | 所有卡片底 |
| `SubtleFillColorSecondaryBrush` / `TertiaryBrush` | 图标底块 / 中性芯片底 |
| `ControlFillColorDefaultBrush` / `SecondaryBrush` | 输入框底 / 分段条外框 |
| `ControlStrokeColorDefaultBrush` | 输入框描边 |
| `LayerFillColorDefaultBrush` | 插件行卡底 / 图标位底 |
| `SolidBackgroundFillColorBaseBrush` | Nova 面板底（不透明） |
| `AcrylicBackgroundFillColorDefaultBrush` | Nova 浮窗底 |
| `SystemFillColorSuccessBackgroundBrush` / `SystemFillColorSuccessBrush` | 推荐芯片（底 / 字） |
| `SystemFillColorCriticalBrush` / `DangerBrush` / `SuccessBrush` | 危险 / 危险 / 成功 |
| `CautionChipBackgroundBrush` / `CautionChipTextBrush` | 提示芯片（底 / 字） |
| `FocusVisualPrimaryBrush` / `FocusVisualSecondaryBrush` | 键盘焦点圈 |
| `BrandBlueBrush`（= `#FFD322`） | 品牌黄（用于图标着色，如插件面板图标） |
| `NovaStateIdleBrush` / `ThinkingBrush` / `PlanBrush` / `ObservingBrush` | Nova 五态色 |

**形状 / 文字 / 卡片**

| 键 | 用途 |
| --- | --- |
| `ControlCornerRadius`(12) / `CardCornerRadius`(20) / `OverlayCornerRadius`(16) | 全局圆角 |
| `ScUiFontFamily` / `ContentControlThemeFontFamily` / `ScIconFont` | 正文 / 控件模板 / 图标字体 |
| `NovaMarkSmall`（`SvgImageSource`） | Nova 小图标（图片） |
| `ScText` / `ScCardHint` | 文字基样式 / 卡片说明（11px 次级色） |
| `ScSectionSpine` / `ScSectionTitle` | 小节脊柱（3×14 accent）/ 小节标题（14px） |
| `ScChip` / `ScChipText` / `ScChipAccent` / `ScChipAccentText` / `ScChipCaution` / `ScChipCautionText` | 芯片三态 |
| `ScMetaLabel`(11 tertiary) / `ScMetaValue`(12 secondary) | 行尾数据列 |
| `ScCard` / `ScHoverCard` | 静态卡片 / 悬浮卡片 |
| `ScPlainListItem` | 「行内容自己画底」的列表容器样式 |
| `ScGlowButtonStyle` / `ScGlowCardButtonStyle` / `ScStaticCardButtonStyle` | `GlowButton` 三档 |
| `ScShadowButtonStyle` | 对话框底部白底按钮（由 `ThemedDialog` 挂载） |
| `CaptionButtonTemplate` / `CaptionButtonStyle` | 窗口三按钮 |

### 21.2 文件对照表

| 关注点 | 文件 |
| --- | --- |
| 全局 token / 样式 / 模板 | `App.xaml` |
| 外壳 / 导航 / Nova 面板 / 调度中心 | `MainWindow.xaml` + `.xaml.cs` |
| 首页 + 启动覆盖层 | `Views/HomePage.xaml` + `.xaml.cs` |
| 版本 / 版本详情 / 下载 | `Views/VersionsPage.*`、`Views/VersionDetailPage.*`、`Views/DownloadPage.*` |
| 存档 / 联机 / 运行状态 / 日志 | `Views/SavesPage.*`、`Views/MultiplayerPage.*`、`Views/RunningStatusPage.*`、`Views/LogsPage.*` |
| 账户三页 | `Views/AccountsPage.*`、`Views/AccountAddPage.*`、`Views/AccountDetailPage.*` |
| 设置 | `Views/SettingsPage.*` |
| Nova | `Views/NovaPage.*`、`Views/Controls/NovaChatView.*`、`Views/Overlays/NovaOverlayWindow.*`、`Services/NovaRuntime.cs` |
| 插件 | `Views/PluginsPage.*`、`Views/PluginNavPage.*`、`Views/PluginPanelPage.*`、`Views/Plugins/PluginViewRenderer.cs` |
| 弹窗 | `Views/Dialogs/*`（含 `ThemedDialog` / `AuthDialogs` / `EnvCheckDialogs` / `CrashDialog` / `ContentDownloadDialog` / `ModPackDialogs` / `ModPackExportDialog` / `UpdateDialog` / `MsLoginWindow`） |
| 行模型 | `Views/Rows/RowModels.cs`、`Views/Rows/VersionRowHelpers.cs` |
| 通用控件 | `Views/Controls/`（`BrandMark` / `GlowButton` / `CategoryNavItem` / `EmptyStateView` / `HomeCardStrip` / `PluginContributionPanel` / `SkinFigureView` / `NovaChatView`） |
| 行为 | `Views/Behaviors/`（`HoverShadowBehavior` / `RowHoverBehavior` / `CardHoverBehavior` / `ExpandBehavior`） |
| 动效 | `Services/MotionFx.cs`、`Services/MotionService.cs` |
| 图标字体入口 | `Services/IconFont.cs`、`Assets/Fonts/HarmonyOSIcons.ttf` |
| 本地化 | `Localization/Loc.cs`、`Strings/en-US.json` |
| 图标资源（版本 / 加载器） | 仓库根 `MC-Icons/`（经 csproj `<Content Include>` 链接到输出目录 `Assets/MC-Icons/`） |
| 品牌资源 | `Assets/Brand/`（`starcloud-mark-light.svg` / `-dark.svg` / `-small.svg`） |
| 壁纸 | `Assets/Wallpapers/`（`aurora` / `dawn` / `mist` / `nebula` / `sand` / `midnight`） |
| 预置皮肤 | `Assets/Skins/default/`（8 张 PNG） |

### 21.3 已知口径不一致 / 未确认项

| 项 | 说明 |
| --- | --- |
| 调度中心抽屉宽度 | 源码注释写「432px 面板」，实测 `Width=600`。**文档按 600** |
| 窗口三按钮三元组 | 「热区 46×48、点亮内收 6 → 34×36、光斑载体 40×40」来自设计文档，本次未逐帧核对 |
| 首页 `ModeOfflineBtn` | 命名未确认（只确认了 `ModeMsBtn` / `ModeExternalBtn` 与三段的文案、Tag） |
| 深色主题 `CardStrokeColorDefaultBrush` | `App.xaml` 深色主题字典中存在该键，但本次读取的片段未包含其色值，标「未确认」 |
| 首页 `HomeCardStrip` 内部结构 | 只确认「固定 120 高、最多 3 张、可收起成窄条」；卡片内部布局未细读 |
| `PluginContributionPanel` 渲染细节 | 只确认挂载点与「无贡献不占位」；声明式 / HTML 两条渲染路径的细节未细读 |
| 下载页工具条第 3 列 | 列已声明为 `Auto`，但本行未观察到内容 |
| `NovaBar`（底部对话条） | 未逐字精读（也不属本次范围） |
| 设置页 `SectionJava` 的 Java 下载按钮 | 文案已确认；下载流程细节未细读 |

### 21.4 相关设计文档（仓库内既有）

- `docs/designs/2026-09-21-harmonyos-icons-design.md`（图标字体引入与字形映射）
- `docs/designs/2026-09-21-brand-mark-design.md`（品牌标记三套尺寸）
- `docs/designs/2026-09-11-ui-polish-motion-design.md`（动效与视觉基元）
- `docs/designs/2026-09-11-custom-background-theme-design.md`（自定义背景三层）
- `docs/designs/2026-09-11-localization-design.md`（源语言即键）
- `docs/designs/2026-09-26-game-running-status-panel-design.md`（运行状态页）
- `docs/designs/2026-09-21-multiplayer-sdk-design.md`（联机页与第三方 SDK 清单）
- `docs/designs/2026-09-23-accounts-page-refactor-design.md`（账户页重构）
- `docs/designs/2026-09-23-skin-library-design.md`（皮肤库 / 皮肤站衣柜）
- `docs/designs/2026-09-29-home-cards-design.md`（首页卡片带）
- `docs/designs/2026-09-29-plugin-nav-items-design.md`（插件一级导航项）
- `docs/designs/2026-09-08-nova-overlay-design.md`（Nova 游戏内浮窗）
- `docs/designs/2026-09-12-skin-viewer-design.md` / `2026-09-20-skin-gpu-acceleration-design.md`（3D 形象渲染）
- `docs/designs/2026-09-12-packaging-release-design.md`（打包与自更新）

---

**文档结束。** 本文只描述**现状**，不含后续规划；所有文案、字形码、尺寸、默认值均来自源码实读，
读不到的已在 21.3 明确标注。

