# 自定义背景与主题（D1 · 背景图 / 内置壁纸 / 毛玻璃强度）设计

日期：2026-09-11
状态：待用户确认
所属：PCL2 平替第 D 组（体验与外观）· 子项 1/3

## 1. 背景与问题

D 组拆为三个独立子系统，本设计只覆盖第一个：

| 子项 | 主题 | 状态 |
|---|---|---|
| **D1** | **自定义背景与主题** | 本文档 |
| D2 | 界面打磨与动效 | 后续单独设计 |
| D3 | 多语言与无障碍 | 后续单独设计 |

用户决策记录（本轮问答）：

| 决策点 | 结论 |
|---|---|
| 背景形态 | **本地图片 + 内置壁纸库**（不做纯色/渐变，不做在线壁纸源） |
| 毛玻璃配合 | **叠加 + 强度可调**：背景图之上叠一层可调强度的毛玻璃遮罩；「模糊强度」与「背景不透明度」分别可调 |
| 生效范围 | **全局统一**（整个启动器共用一套背景设置，不分页面/实例） |

现状问题（均已核实到行）：

1. **窗口背景是硬编码的亚克力配方，外部不可调**。
   [TransparentAcrylicBackdrop](../../../dotnet/src/Starcloud.App/MainWindow.xaml.cs#L234-L299)
   三个关键参数（`TintColor` / `TintOpacity` / `LuminosityOpacity`）在
   `ApplyAcrylicTheme`（L274–291）里按深浅主题写死，`_controller` 为 `private`，
   **没有任何 public/internal API**，用户无法调毛玻璃强度。
2. **完全没有背景图能力**。全仓搜索 `壁纸 / Wallpaper / BackgroundImage / ImageBrush`
   零命中；根 `Grid`（[MainWindow.xaml](../../../dotnet/src/Starcloud.App/MainWindow.xaml#L9-L14)）
   无 `Background`、`ContentFrame` 也是 `Background="Transparent"`，
   窗口底层只透出 SystemBackdrop 的桌面亚克力。
3. **没有内置壁纸资源**。项目无 `Assets` 目录、csproj 无任何 `Content` ItemGroup；
   [MainWindow.xaml.cs L27](../../../dotnet/src/Starcloud.App/MainWindow.xaml.cs#L27)
   引用的 `Assets/AppIcon.ico` 实际不存在。
4. **主题设置在启动时根本不生效**。
   [SettingsPage.xaml.cs L74-L79](../../../dotnet/src/Starcloud.App/Views/SettingsPage.xaml.cs#L74-L79)
   回填 `ThemeCombo.SelectedIndex` 时 `_loaded` 仍为 false（L83 才置 true），
   于是 `Theme_Changed`（L334–339）在 L336 提前 return；
   而 `RequestedTheme` 全仓只有 [L350-L361 `ApplyTheme`](../../../dotnet/src/Starcloud.App/Views/SettingsPage.xaml.cs#L350-L361)
   一处赋值 —— **保存的深浅色主题重启后丢失，必须手动进设置页再切一次**。
5. **设置页「通用」只有两张卡**。
   [SettingsPage.xaml L306-L339](../../../dotnet/src/Starcloud.App/Views/SettingsPage.xaml#L306-L339)
   = 主题外观（`ThemeCombo`）+ Nova 浮窗（`OverlaySwitch`），无外观自定义入口。
6. **设置变更无广播机制**。`AppServices`（[Services/AppServices.cs](../../../dotnet/src/Starcloud.App/Services/AppServices.cs)）
   无任何事件；跨页/窗口级外观刷新目前靠 `App.MainWindow?.Content` 直接强改，脆弱。

## 2. 设计目标

- 用户可选**内置壁纸**或**本地图片**作为整个启动器窗口的背景，未选则保持现有纯亚克力外观。
- 背景之上叠一层**毛玻璃遮罩**，其强度可调；背景图本身可单独调**模糊强度**与**不透明度**。
- 所有外观设置**全局统一**、即时预览、持久化，且**重启后正确还原**（顺带修掉现状问题 4）。
- 不改动游戏内浮窗（`NovaOverlayWindow`）的既有外观 —— 它复用 `TransparentAcrylicBackdrop` 但不承载背景图。
- **不做**：纯色/渐变背景、在线壁纸源、强调色（主色）自定义、按页面/实例区分背景。

> 强调色自定义被刻意排除：WinUI 3 运行时覆盖 `SystemAccentColor` 无法可靠传播到
> `XamlControlsResources` 已实例化的画刷，收益低风险高；本子项聚焦「背景」这一真正缺口。

## 3. 设计总览

### 3.1 新增文件

| 层 | 文件 | 职责 |
|---|---|---|
| App | `Views/WindowBackgroundHost.cs` | 窗口背景渲染宿主：图片层 / 模糊层 / 毛玻璃遮罩层三件套，全部由 `LauncherSettings` 驱动 |
| App | `Services/AppearanceService.cs` | 外观应用中心：主题应用、主题默认遮罩强度、内置壁纸清单与解析 |
| 资源 | `Assets/Wallpapers/*.jpg` | 6 张内置壁纸（深色组 3 + 浅色组 3） |

### 3.2 修改文件

| 文件 | 改动 |
|---|---|
| `Starcloud.Core/Services/LauncherPaths.cs` | `LauncherSettings` 新增 6 个背景字段（§4.1） |
| `Starcloud.App/MainWindow.xaml` | 根 `Grid` 内新增 `BackgroundLayer`（`Grid.RowSpan="3"`，`IsHitTestVisible="False"`） |
| `Starcloud.App/MainWindow.xaml.cs` | 构造时应用主题 + 注入背景宿主；暴露 `RefreshAppearance()` |
| `Starcloud.App/Views/SettingsPage.xaml` | 「通用」新增「背景与外观」卡片 |
| `Starcloud.App/Views/SettingsPage.xaml.cs` | 背景设置回填/即时预览/落盘；`Theme_Changed` 改为走 `AppearanceService` |
| `Starcloud.App/Starcloud.App.csproj` | 新增 `Content` ItemGroup 打包 `Assets/Wallpapers/*.jpg` |

### 3.3 渲染层结构（自下而上）

```
[SystemBackdrop: TransparentAcrylicBackdrop]   ← 桌面毛玻璃（始终存在，作兜底与纹理）
┌─ BackgroundLayer (Grid.RowSpan=3, 不吃命中测试) ────────────┐
│  1. Image   BackgroundImage    Stretch=UniformToFill         │  背景图（清晰）
│     · Opacity = BackgroundImageOpacity                       │
│  2. Border  BackgroundBlurHost 宿主元素，挂 composition 子视觉 │  背景图的模糊副本
│     · SpriteVisual.Brush = GaussianBlur(BackdropBrush)       │
│     · 仅当 BackgroundBlurRadius > 0 时可见                    │
│  3. Border  BackgroundMask      纯色 Tint                     │  毛玻璃遮罩
│     · Color 跟随主题（深 #141414 / 浅 #FCFCFC）               │
│     · Opacity = BackgroundMaskOpacity                        │
└──────────────────────────────────────────────────────────────┘
[NavigationView / 标题栏 / 状态栏 / Nova 面板]                  ← 常规 XAML 内容
```

要点：

- **模糊实现**用 composition 的 `GaussianBlurEffect` + `Compositor.CreateBackdropBrush()` 挂在
  `BackgroundBlurHost` 的子视觉上，模糊它**下方**的内容（即背景图）。
  这是 WinUI 3 里对「已有 XAML 内容做背景模糊」的标准做法，无需重新解码图片、无需额外 URI。
  整段构造包在 `try/catch` 里：失败则 `BackgroundBlurRadius` 静默失效（退化为清晰背景图），
  **绝不因外观功能导致窗口起不来**。
- **模糊层与清晰图的关系**：`BlurRadius > 0` 时模糊层可见（覆盖清晰图），`= 0` 时模糊层隐藏、清晰图可见。
  二者共用同一个「背景不透明度」语义 —— 因为模糊是对 `Opacity` 之后的图像采样的。
- `BackgroundBlurHost` 的 `SpriteVisual` 需要显式尺寸，窗口 `SizeChanged` 时同步更新。
- 未配置背景（`BackgroundMode == "none"`）时整个 `BackgroundLayer` 折叠，回到**现有纯亚克力外观**。

## 4. 数据模型

### 4.1 `LauncherSettings` 新增字段

追加在现有 `show_game_log_on_launch` 之后
（[LauncherPaths.cs L177-L184](../../../dotnet/src/Starcloud.Core/Services/LauncherPaths.cs#L177-L184)）：

| C# 属性 | 类型 | JsonPropertyName | 默认值 | 说明 |
|---|---|---|---|---|
| `BackgroundMode` | `string` | `background_mode` | `"none"` | `none` / `builtin` / `file` |
| `BackgroundBuiltinId` | `string?` | `background_builtin_id` | `null` | 内置壁纸 id |
| `BackgroundImagePath` | `string?` | `background_image_path` | `null` | 本地图片绝对路径 |
| `BackgroundImageOpacity` | `double` | `background_image_opacity` | `0.7` | 背景图不透明度 0.2–1.0 |
| `BackgroundBlurRadius` | `double` | `background_blur_radius` | `24` | 背景图模糊半径 0–64（px） |
| `BackgroundMaskOpacity` | `double?` | `background_mask_opacity` | `null` | 毛玻璃遮罩不透明度 0–1；**null = 跟随主题** |
| `BackgroundFollowTheme` | `bool` | `background_follow_theme` | `true` | 遮罩强度是否跟随主题默认值 |

- **无版本号迁移框架**，沿用现有「默认值 + 未知字段静默忽略」策略即可：
  旧配置文件缺这些字段 → 反序列化后即默认值；新配置文件被旧版本读 → 多余字段被忽略。
- `BackgroundMaskOpacity` 用 `double?`，`null` 表示跟随主题（深 0.45 / 浅 0.25，与现有亚克力配方一致）。
  `BackgroundFollowTheme == true` 时等价于 null。
- 读取时做一次**范围钳制**（`Clamp`）与**路径存在性校验**（图片不存在则视为 `none`，不弹错）。

### 4.2 内置壁纸清单

`AppearanceService` 内静态定义，图片以 `AppContext.BaseDirectory/Assets/Wallpapers/<file>` 解析：

| id | 显示名 | 文件 | 推荐主题 |
|---|---|---|---|
| `aurora` | 极光 | `aurora.jpg` | 深色 |
| `nebula` | 星云 | `nebula.jpg` | 深色 |
| `midnight` | 子夜 | `midnight.jpg` | 深色 |
| `dawn` | 破晓 | `dawn.jpg` | 浅色 |
| `mist` | 晨雾 | `mist.jpg` | 浅色 |
| `sand` | 暖沙 | `sand.jpg` | 浅色 |

- 6 张图随包发布（`Content` + `CopyToOutputDirectory=PreserveNewest`），jpg 质量 90、1920×1080，总量约 1 MB 级。
- 「推荐主题」仅用于设置页缩略图分组标题与首次选中时的提示，**不强制**切换主题。

## 5. `AppearanceService`

`Starcloud.App/Services/AppearanceService.cs`，挂在 `AppServices.Appearance` 上（与其它服务同构）：

```csharp
public sealed class AppearanceService
{
    /// <summary>把 "system" | "light" | "dark" 应用到窗口根元素。</summary>
    public void ApplyTheme(string theme, FrameworkElement root);

    /// <summary>主题默认遮罩不透明度（深 0.45 / 浅 0.25），与 TransparentAcrylicBackdrop 配方一致。</summary>
    public static double DefaultMaskOpacity(ElementTheme theme);

    /// <summary>按当前主题推算生效的遮罩不透明度（跟随主题时取默认值）。</summary>
    public static double ResolveMaskOpacity(LauncherSettings s, ElementTheme theme);

    /// <summary>内置壁纸清单。</summary>
    public static IReadOnlyList<BuiltinWallpaper> Wallpapers { get; }

    /// <summary>把设置里的背景解析成绝对文件路径；无效/未配置返回 null。</summary>
    public static string? ResolveBackgroundPath(LauncherSettings s);
}

public sealed record BuiltinWallpaper(string Id, string Name, string FileName, bool PreferDark);
```

## 6. `WindowBackgroundHost`

`Starcloud.App/Views/WindowBackgroundHost.cs`，`internal sealed`：

```csharp
internal sealed class WindowBackgroundHost
{
    public WindowBackgroundHost(Image image, Border blurHost, Border mask);

    /// <summary>按最新设置重建视觉（图片源/透明度/模糊半径/遮罩）。</summary>
    public void Apply(LauncherSettings settings, ElementTheme theme);

    /// <summary>窗口尺寸变化时同步模糊 SpriteVisual 尺寸。</summary>
    public void Resize(double width, double height);

    /// <summary>图片或设置失效时折叠整个 BackgroundLayer。</summary>
    public bool IsActive { get; }
}
```

实现要点：

- 模糊只在需要时构建一次 `GaussianBlurEffect` + `EffectFactory` + `SpriteVisual`，
  之后仅改 `BlurAmount` / `SpriteVisual.Opacity` / `Size`，避免反复重建。
- 图片加载：`BitmapImage { UriSource = new Uri(path) }`；
  为降低大图内存占用，`DecodePixelWidth` 取窗口宽（或 2560 上限）。
- 遮罩颜色跟随 `theme`：`ElementTheme.Dark → #141414`，否则 `#FCFCFC`。
- 每次 `Apply` 都先 `Visibility.Collapsed` 再按需恢复，确保模式切换干净。

## 7. 主题启动生效（修掉现状问题 4）

- `MainWindow` 构造末尾（`Nav.SelectedItem = ...` 之后）调用：

```csharp
AppServices.Instance.Appearance.ApplyTheme(AppServices.Instance.Settings.Theme, (FrameworkElement)Content);
```

- `SettingsPage.ApplyTheme`（现有 L350–361）改为转调 `AppearanceService.ApplyTheme`，消除重复实现。
- `MainWindow` 暴露：

```csharp
/// <summary>设置页改完外观后调用：重新应用主题 + 重建背景。</summary>
public static void RefreshAppearance()
{
    if (App.MainWindow is not MainWindow w) return;
    var s = AppServices.Instance.Settings;
    w._appearance.ApplyTheme(s.Theme, (FrameworkElement)w.Content);
    w._background.Apply(s, ((FrameworkElement)w.Content).ActualTheme);
}
```

- 设置页在 `Theme_Changed`、背景相关控件变更、`Save_Click` 后调用 `MainWindow.RefreshAppearance()`。

## 8. 设置页 UI

「通用」分组内、`主题外观` 卡片之后插入新卡片（沿用现有 `PclCard` / `PclSectionTitle` / `PclLabel` 样式约定）：

```
┌ 背景与外观 ─────────────────────────────────────────┐
│ 背景来源   [ ComboBox: 无（仅毛玻璃）/ 内置壁纸 / 本地图片 ] │
│                                                     │
│ ── 仅「内置壁纸」时显示 ──                          │
│ 壁纸       [ GridView 6 项缩略图，单列选中，高亮当前 ] │
│                                                     │
│ ── 仅「本地图片」时显示 ──                          │
│ 图片       [ 选择图片… ]  <当前文件名 / 未选择>  [ 清除 ]│
│                                                     │
│ ── 任意非「无」时显示 ──                            │
│ 模糊强度   [ Slider 0–64 ]  24                      │
│ 背景不透明度 [ Slider 0.2–1.0 ] 0.7                 │
│ 毛玻璃强度 [ Slider 0–1 ] 0.45   [x] 跟随主题       │
└─────────────────────────────────────────────────────┘
```

交互约定：

- **即时预览**：所有控件变更立刻 `RefreshAppearance()` 预览，并 `SavedBar.IsOpen = true`（沿用现有惯例）；
  同时写回 `_s.Settings` 但**只在 `Save_Click` 或不涉及主题时立即落盘** —— 与现有 `Theme_Changed`（只预览不落盘）保持一致，
  背景项则采用「即改即存」（与 `OverlaySwitch_Toggled` 等现有开关一致，见 §5 惯例）。
  统一口径：**背景三项 + 模糊/透明度滑块 = 即改即存；主题 = 仍由「保存设置」落盘**（不改现有行为）。
- `BackgroundMode` 切换时联动两张子面板的 `Visibility`。
- 本地图片用 `FileOpenPicker`（`Windows.Storage.Pickers`，非打包应用需
  `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)`），过滤器 `.jpg/.jpeg/.png/.bmp/.webp`。
- 选中的图片路径不存在时不弹错误，静默回落 `none` 并刷新 UI。
- 所有新控件补 `AutomationProperties.Name`（为 D3 无障碍打基础）。

## 9. 边界与降级

| 场景 | 行为 |
|---|---|
| composition 模糊构建失败 | 模糊静默失效，背景图仍清晰显示；其余功能不受影响 |
| 本地图片被删除/移动 | 视为 `none`，折叠背景层，UI 显示「未选择」 |
| 图片解码失败（损坏/超大） | 同上，静默回落 |
| 未配置背景 | 完全保持现有纯亚克力外观（`BackgroundLayer` 折叠） |
| 切换深浅主题 | 遮罩色 + 跟随主题的遮罩强度自动重算；亚克力配方沿用现有 `OnRootThemeChanged` |
| Nova 游戏内浮窗 | **不继承背景图**，仅继续使用现有亚克力 |
| 设置文件无新增字段 | 全部取默认值 = 无背景，行为与升级前完全一致 |

## 10. 验收标准

1. 设置页「通用」可见「背景与外观」卡片；三种背景来源可切换，子面板显示正确。
2. 选任一内置壁纸 → 窗口立即出现该背景；重启启动器后背景仍在。
3. 选本地图片 → 窗口立即出现该图片；重启后仍在。
4. 「背景不透明度」「模糊强度」滑块实时生效；「毛玻璃强度」实时改变磨砂感；
   勾选「跟随主题」时滑块禁用且值随深浅主题变化。
5. 深浅主题切换时，保存的主题在**重启后**依然生效（修掉现状问题 4）。
6. 背景来源选「无」→ 完全恢复升级前外观。
7. 背景图设置为无效路径 → 不弹错误、回落到「无」。
8. 整仓 `dotnet build` **0 警告 0 错误**。

## 11. 自检

- [x] 范围与用户决策一致（本地图片 + 内置壁纸、叠加可调、全局统一）
- [x] 明确排除项（纯色/渐变、在线壁纸、强调色、按实例背景）已写清
- [x] 覆盖现状 6 个问题，其中问题 4（主题启动不生效）顺带修复
- [x] 数据模型给出字段名/类型/默认值/迁移策略
- [x] 降级路径完备，外观功能不会导致窗口启动失败
- [x] 验收标准可逐条验证
