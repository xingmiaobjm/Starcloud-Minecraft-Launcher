# 界面打磨与动效实现计划（D 组 ②：UI Polish & Motion）

> 设计：`docs/designs/2026-09-11-ui-polish-motion-design.md`
> 目标：两个项目 `dotnet build` **0 警告 0 错误**；整组完成后一次性提交。

## 任务分解

### T1 设置模型：界面动效开关
- `Starcloud.Core/Services/LauncherPaths.cs`：`LauncherSettings` 新增 `UiAnimations`（`"system"` 默认，蛇形 `ui_animations`），并在 `ClampBackgroundValues()` 末尾钳制非三值 → `"system"`。
- `dotnet build dotnet\src\Starcloud.Core\Starcloud.Core.csproj -v q -nologo` → 0/0。

### T2 MotionService
- 新增 `Starcloud.App/Services/MotionService.cs`：
  - `static bool SystemAnimationsEnabled`（`Windows.UI.ViewManagement.UISettings.AnimationsEnabled`，`try/catch` → `true`）。
  - `bool Enabled`（读 `LauncherSettings.UiAnimations` 解析）。
  - `static NavigationTransitionInfo TransitionFor(bool enabled, bool drillIn = false)` 返回 `EntranceNavigationTransitionInfo` / `DrillInNavigationTransitionInfo` / `SuppressNavigationTransitionInfo`。
- `AppServices.cs`：注入 `public MotionService Motion { get; }`（构造顺序放 `Appearance` 之后）。
- 构建 0/0。

### T3 页面转场接线
- `MainWindow.xaml.cs`：
  - ctor 里的 `ContentFrame.Navigate(typeof(HomePage))` → 传 `MotionService.TransitionFor(AppServices.Instance.Motion.Enabled)`。
  - `Nav_SelectionChanged` 里 `ContentFrame.Navigate(page)` → 同上。
  - `NavigateToVersionDetail` 里 → `TransitionFor(..., drillIn: true)`。
- 构建 0/0。

### T4 CardHoverBehavior 附加属性
- 新增 `Starcloud.App/Views/CardHoverBehavior.cs`：附加属性 `IsEnabled`；`PointerEntered/Exited/Pressed/Released/CaptureLost` + `SizeChanged` 维护 `Scale` 动画（WinAppSDK 1.8 投影无 `Visual.Translation`，故不做上浮）。
  - 动效关闭时直接不接线；全程 `try/catch` 静默降级；`Unloaded` 解绑。
  - 中心点：`Visual.CenterPoint = (w/2, h/2, 0)`；`Visual.Size` 同步。
  - 用 `Compositor.CreateVector3KeyFrameAnimation`（显式播放）+ `CubicBezierEasingFunction`（0.1,0.9,0.2,1.0），时长 120ms。
- `App.xaml`：新增全局 `Border` 样式 `ScHoverCard`（实心卡片外观 + `local:CardHoverBehavior.IsEnabled=True`），并声明 `xmlns:local="using:Starcloud.App.Views"`。
- 构建 0/0。

### T5 卡片挂载悬浮反馈
- `SettingsPage.xaml`：`PclCard` 改为 `BasedOn="{StaticResource ScHoverCard}"`（43 处卡片一次性生效）。
- `VersionsPage` / `SavesPage` / `AccountsPage` / `NovaPage` / `HomePage`：对自绘行容器（`Border`/`Grid`）挂 `local:CardHoverBehavior.IsEnabled="True"`；`ListView` 行容器走 `ItemContainerStyle` 的 `Setter`。
- 构建 0/0。

### T6 空状态统一组件
- 新增 `Views/EmptyStateView.xaml(.cs)`：`Glyph` / `Title` / `Subtitle` / `ActionText` 依赖属性 + `ActionClick` 事件。
- 替换：`HomePage`（版本列表）、`VersionsPage`、`SavesPage`、`AccountsPage`、`NovaPage`（模型 / 流水）、`DownloadPage`（搜索结果）；各页 code-behind 中改写空状态文案的位置同步改为设置组件属性。
- 不替换：加载器抽屉「暂无可用构建」、`LogsPage` 日志文本框占位。
- 构建 0/0。

### T7 视觉一致性收敛
- `DownloadPage.xaml` 徽章 `CornerRadius 9 → 4`（2 处）；`NovaPage.xaml` / `VersionsPage.xaml` `6 → 8`（3 处）；`VersionDetailPage.xaml` 图标容器 `10 → 8`、内存条 `3.5 → 4`（3 处）、主标题 `FontSize 24 → 28`。
- 构建 0/0。

### T8 设置页：界面动效下拉
- `SettingsPage.xaml`：`SectionGeneral` 的「主题外观」卡片内新增「界面动效」下拉（跟随系统 / 始终开启 / 关闭，`Tag = system|on|off`）。
- `SettingsPage.xaml.cs`：`Loaded` 回填（`_loaded = true` 之前）；`Motion_Changed` 即改即存并 `SavedBar.IsOpen = true`。
- 构建 0/0。

### T9 收尾：构建校验 + 文档 + 提交
- 两个项目构建 0/0。
- `docs/USER_MANUAL.md`：设置页表格补「界面动效」；新增「界面动效与无障碍」小节（简述开关与悬浮/转场表现）。
- `docs/ARCHITECTURE.md`：外观层补 `MotionService` / `CardHoverBehavior` / `EmptyStateView`。
- 一次性提交：`实现界面打磨与动效（D 组：动效/悬浮反馈/空状态统一）`。

## 风险与回退

| 风险 | 处理 |
| --- | --- |
| Composition 隐式/显式动画在部分环境不可用 | 全部 `try/catch` 静默降级为无动效（参照 D1 `WindowBackgroundHost`） |
| 附加属性在 `Style` 的 `Setter` 中不生效 | 若 `Setter Property="local:CardHoverBehavior.IsEnabled"` 编译报错，则改为在每处 XAML 元素上直接挂属性 |
| 空状态替换导致可见性判断错乱 | 保留各页原有 `Visibility` 判定逻辑，仅替换 UI 载体与文案赋值入口 |
| `UISettings` 在非交互会话抛异常 | 捕获后按「动画开启」处理 |
