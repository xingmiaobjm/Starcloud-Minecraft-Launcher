# 界面打磨与动效设计（D 组 ②：UI Polish & Motion）

> 日期：2026-09-11　范围：`Starcloud.App` 表现层
> 前序：D1 自定义背景与主题（`2026-09-11-custom-background-theme-design.md`）
> 后续：D3 多语言与无障碍（全量 .resw）

## 1. 背景与现状

对全仓（11 个页面 / 组件）只读调研后的结论：

| 维度 | 现状 |
| --- | --- |
| 页面转场 | 3 处 `Frame.Navigate` **全部不传** `NavigationTransitionInfo`，全仓 0 命中 |
| 动画 | 全项目仅 1 处 `Storyboard`（`HomePage` 启动覆盖层 150ms 淡入） |
| 悬浮反馈 | 自绘卡片 / 列表行**零** `PointerOver` / 按下反馈；仅依赖 `ListView` 自带态 |
| 空状态 | 三档并存：图标+文案（Versions/Saves/Download 搜索）、纯文字（Home/Accounts/Nova）、无（Download 资源库） |
| 加载态 | 无统一模式：长任务有 `ProgressBar`/`ProgressRing`，列表加载几乎无指示 |
| 圆角 | `App.xaml` 定义了 `CardCornerRadius=16` 等 3 个令牌，**页面 0 引用**；硬编码 84 处，含 6 / 9 / 10 / 3.5 等离群值 |
| 主题动画偏好 | 存在系统「动画效果」开关（`UISettings.AnimationsEnabled`），但应用**不感知** |

问题本质：外观层（D1）已能定制背景与主题，但**界面本身没有质感**——切页生硬、卡片像静态贴图、空状态像报错、离群圆角破坏整体感。

## 2. 目标与非目标

### 目标
1. 页面切换有**统一、克制**的转场（淡入上移，< 250ms）。
2. 自绘卡片 / 可点击行有**细腻的悬浮与按下反馈**（缩放 + 轻移，不改变配色语义）。
3. 空状态**统一为同一组件**（图标 + 标题 + 副标题 + 可选操作）。
4. 尊重系统「动画效果」开关，并提供应用内覆盖（无障碍友好，为 D3 打基础）。
5. 收敛视觉离群值（圆角 / 主标题字号）。

### 非目标（明确排除）
- 不做骨架屏（Skeleton）与列表级加载遮罩——列表多为本地同步读取，收益低、易闪烁。
- 不重构页面布局、不迁移全部硬编码圆角到令牌（工作量大、纯机械、风险高于收益）。
- 不加 `ConnectedAnimation`（跨页共享元素）——本项目无对应场景。
- 不改 `NavigationView` 侧栏展开/收起动画（框架自带且已足够）。
- 不引入第三方动效库（`CommunityToolkit` 等）——全部用 WinUI 原生 Storyboard / Composition 隐式动画。

## 3. 设计原则

1. **克制**：动效只用于表达「状态变化」，不做装饰性弹跳；单次时长 120–250ms。
2. **可降级**：任何动效失败（组合器不可用、动画 API 异常）必须静默退化为无动效，**绝不影响功能**（沿用 D1 `WindowBackgroundHost` 的容错口径）。
3. **尊重用户**：系统关闭动画 → 全部动效关闭（含页面转场）。
4. **不夺舍**：悬浮反馈不得覆盖页面既有的 `Background` / `BorderBrush` 语义，只用 `Scale` + `OffsetY`（组合层），避免主题色错乱。
5. **集中复用**：空状态一个组件、悬浮反馈一个附加属性、动效开关一个服务。

## 4. 动效开关（MotionService）

### 4.1 设置项

`LauncherSettings` 新增：

```csharp
/// <summary>界面动效：system（跟随系统）/ on（始终开启）/ off（关闭）</summary>
[JsonPropertyName("ui_animations")]
public string UiAnimations { get; set; } = "system";
```

`ClampBackgroundValues()` 同处新增钳制：非三值之一 → `"system"`。

### 4.2 服务

```csharp
public sealed class MotionService
{
    public bool Enabled { get; }        // 解析结果：system 时读系统开关
    public static bool SystemAnimationsEnabled { get; }  // UISettings.AnimationsEnabled
}
```

- 系统判定用 `Windows.UI.ViewManagement.UISettings.AnimationsEnabled`（WinRT 投影已在 WinAppSDK 中可用），失败时按 `true` 处理。
- 页面转场需要瞬时判定 → 提供 `MotionService.TransitionFor(bool animate)` 之类的辅助，或直接由调用方 `motion.Enabled ? new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo()`。

### 4.3 设置页

「通用」分组「主题外观」卡片内新增一行下拉「界面动效」（跟随系统 / 始终开启 / 关闭），即改即存（沿用现有 `if (!_loaded) return;` + `Save()` + `SavedBar` 惯例），并即时刷新 `MainWindow` 的动效开关缓存。

## 5. 页面转场

3 处 `Frame.Navigate` 显式指定：

| 场景 | 转场 |
| --- | --- |
| `MainWindow` 启动进首页 | `EntranceNavigationTransitionInfo` |
| `MainWindow.Nav_SelectionChanged` 顶层切页 | `EntranceNavigationTransitionInfo` |
| `MainWindow.NavigateToVersionDetail`（进版本详情） | `DrillInNavigationTransitionInfo`（层级下钻语义） |

动效关闭时统一传 `SuppressNavigationTransitionInfo`。

辅助：`MotionService.TransitionFor(bool enabled, bool drillIn = false)`，调用点传 `AppServices.Instance.Motion.Enabled`。

## 6. 卡片悬浮与按下反馈

### 6.1 实现方式：附加属性 + Composition 显式动画

新增 `Views/CardHoverBehavior.cs`（`static class` + 附加属性）：

```csharp
public static class CardHoverBehavior
{
    public static readonly DependencyProperty IsEnabledProperty;   // attached
}
```

行为（`IsEnabled = true` 时接线）：

| 事件 | 效果 |
| --- | --- |
| `PointerEntered` | `Scale = 1.012` |
| `PointerExited` | `Scale = 1.0` |
| `PointerPressed` | `Scale = 0.985` |
| `PointerReleased` / `PointerCaptureLost` | 回到 hover 态（若仍悬浮）/ 常态 |

> 上浮（`OffsetY = -1`）**已放弃**：WinAppSDK 1.8 的 `Microsoft.UI.Composition.Visual` 投影（`net6.0-windows10.0.17763.0`）无 `Translation` 成员，而动画 `Offset` 会在列表滚动/虚拟化回收时覆盖 XAML 布局结果导致错位。只保留 `Scale`，观感已足够。

实现要点：
- 用 `ElementCompositionPreview.GetElementVisual(element)` 拿 `Visual`，通过 `Compositor.CreateVector3KeyFrameAnimation` 显式播放（**不用** `ImplicitAnimations`，避免与现有 `WindowBackgroundHost` 的子视觉冲突，也便于统一 duration/easing）。
- 中心缩放：元素需设 `Visual.CenterPoint = new Vector3(w/2, h/2, 0)`；在 `SizeChanged` 时更新 `Size` 与 `CenterPoint`。
- 时长 120ms，缓动 `CubicBezier`（WinUI 标准 `EaseOut`）。
- 动效关闭（`MotionService.Enabled == false`）时**完全不接线**（零开销）。
- 全程 `try/catch`：任一环节失败即放弃该元素（静默）。
- 附加属性在元素 `Unloaded` 时解绑事件，避免泄漏。

### 6.2 挂载方式（两处覆盖全站）

1. `App.xaml` 新增全局样式：

```xml
<Style x:Key="ScHoverCard" TargetType="Border">
    <Setter Property="local:CardHoverBehavior.IsEnabled" Value="True" />
    ...
</Style>
```

2. `SettingsPage.xaml` 的 `PclCard` 样式改为 `BasedOn="{StaticResource ScHoverCard}"` → **设置页 43 处卡片一次性获得反馈**。
3. `VersionsPage` / `SavesPage` / `AccountsPage` / `NovaPage` / `HomePage` 的行容器（`ListView.ItemContainerStyle` 或自绘 `Border`）挂 `local:CardHoverBehavior.IsEnabled="True"`。

`ListView` 的选中态由框架提供，附加属性只补 hover/按压的「质感」。

## 7. 空状态统一组件

新增 `Views/EmptyStateView.xaml(.cs)`（`UserControl`）：

```
        [FontIcon 28]
        标题（FontWeight SemiBold, 14）
        副标题（12, TextFillColorSecondaryBrush, 可换行）
        [操作 Button（可选，AccentButtonStyle）]
```

依赖属性：

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `Glyph` | `string` | 图标字形 |
| `Title` | `string` | 主文案 |
| `Subtitle` | `string` | 副文案（空则不占位） |
| `ActionText` | `string` | 按钮文案（空则隐藏按钮） |
| `ActionVisible` | 由 `ActionText` 推导并 `Visibility` 绑定 | — |

事件：`ActionClick`（`TypedEventHandler` 或普通 `RoutedEventHandler`）。

替换清单（保持各页原文案逻辑不变，只换外观容器）：

| 页面 | 位置 | 说明 |
| --- | --- | --- |
| `HomePage` | 版本列表空提示 | 补图标；按钮「前往下载页安装」→ `ActionText` |
| `VersionsPage` | `EmptyHint` | 已含图标文案，改为复用组件 |
| `SavesPage` | `EmptyHint` | 同上（含「无匹配」态文案改写） |
| `AccountsPage` | 账户列表空提示 | 补图标 + 操作按钮「添加账户」 |
| `NovaPage` | 模型列表 / 流水列表 | 补图标 |
| `DownloadPage` | 搜索结果空提示 | 改为复用组件 |

「暂无可用构建」（加载器抽屉内窄区域）与 `LogsPage`（复用日志文本框）**不替换**——空间与语义不适配。

## 8. 视觉一致性收敛

只处理离群值（低风险、肉眼可辨）：

| 位置 | 现值 | 改为 |
| --- | --- | --- |
| `DownloadPage.xaml` 徽章（2 处） | `CornerRadius="9"` | `4` |
| `NovaPage.xaml` / `VersionsPage.xaml` 小容器（3 处） | `6` | `8` |
| `VersionDetailPage.xaml` 图标容器 | `10` | `8` |
| `VersionDetailPage.xaml` 内存条（3 处） | `3.5` | `4` |
| `VersionDetailPage.xaml` 页面主标题 | `FontSize="24"` | `28`（与其余 7 页一致） |

不做：把 84 处硬编码圆角迁移到 `{StaticResource CardCornerRadius}`（机械改动、易引入回归）。

## 9. 涉及文件

**新增**
- `dotnet/src/Starcloud.App/Services/MotionService.cs`
- `dotnet/src/Starcloud.App/Views/CardHoverBehavior.cs`
- `dotnet/src/Starcloud.App/Views/EmptyStateView.xaml(.cs)`

**修改**
- `Starcloud.Core/Services/LauncherPaths.cs`（`UiAnimations` 字段 + 钳制）
- `Starcloud.App/Services/AppServices.cs`（注入 `Motion`）
- `Starcloud.App/App.xaml`（`ScHoverCard` 全局样式）
- `Starcloud.App/MainWindow.xaml.cs`（转场 + 动效开关缓存）
- `Starcloud.App/Views/SettingsPage.xaml(.cs)`（「界面动效」下拉；`PclCard` 继承 `ScHoverCard`）
- 上表各页 XAML / code-behind（空状态替换、hover 挂载、离群值收敛）

## 10. 验收标准

1. `dotnet build` 两个项目 **0 警告 0 错误**。
2. 顶层切页有淡入上移转场；进入版本详情为下钻转场；设置「界面动效 = 关闭」后两者均立即消失。
3. 设置页 / 版本 / 存档 / 账户 / Nova 页的卡片在鼠标悬浮时轻微放大、按下时轻微缩小；关闭动效后无变化。
4. 六个空状态外观一致（图标 + 标题 + 副标题 + 可选按钮）。
5. 关闭系统动画（或应用内关闭）后，全应用无任何自定义动效，功能不受影响。
