# D1 自定义背景与主题 实现计划

日期：2026-09-11
设计：[2026-09-11-custom-background-theme-design.md](../specs/2026-09-11-custom-background-theme-design.md)
范围：PCL2 平替 D 组 · 子项 1/3

## 任务清单

| # | 任务 | 产出 |
|---|---|---|
| T1 | 设置模型新增 6 个背景字段 + 钳制/校验辅助 | `LauncherPaths.cs` |
| T2 | 生成 6 张内置壁纸资源 + csproj 打包 | `Assets/Wallpapers/*.jpg`、`Starcloud.App.csproj` |
| T3 | `AppearanceService`（主题应用 + 壁纸清单 + 遮罩解析） | `Services/AppearanceService.cs`、`AppServices.cs` |
| T4 | `WindowBackgroundHost`（图片/模糊/遮罩三层） | `Views/WindowBackgroundHost.cs` |
| T5 | `MainWindow` 接入：背景层 XAML + 主题启动生效 + `RefreshAppearance()` + SizeChanged | `MainWindow.xaml(.cs)` |
| T6 | 设置页「背景与外观」卡片 UI | `SettingsPage.xaml` |
| T7 | 设置页逻辑：回填 / 即时预览 / 落盘 / 选择图片 / 模式联动 | `SettingsPage.xaml.cs` |
| T8 | 构建校验 0 警告 0 错误 + 文档更新（USER_MANUAL/ARCHITECTURE） | — |

## T1 设置字段

在 `LauncherSettings` 的 `show_game_log_on_launch` 之后追加 §4.1 的 7 个属性，
并新增两个只读辅助：

```csharp
/// <summary>背景图实际生效的文件路径；未配置 / 模式非法 / 文件不存在 → null。</summary>
public string? ResolvedBackgroundPath();   // 需要 App 侧内置目录，故仅处理 file 模式

public void ClampBackgroundValues();       // opacity/blur/mask 范围钳制
```

> `builtin` 模式的路径解析放 `AppearanceService`（Core 不应知道 App 的 Assets 目录）。
> Core 侧只做 `file` 模式的 `File.Exists` 校验。

## T2 内置壁纸

用 PowerShell + `System.Drawing`（GDI+）程序化生成 6 张 1920×1080 抽象渐变壁纸，
写入 `dotnet/src/Starcloud.App/Assets/Wallpapers/`，jpg 质量 90：

- 深色组：`aurora`（青紫极光）、`nebula`（紫红星云）、`midnight`（深蓝子夜）
- 浅色组：`dawn`（暖橙破晓）、`mist`（灰蓝晨雾）、`sand`（米色暖沙）

生成方式：线性渐变底 + 若干 `PathGradientBrush` 径向光斑（中心色 → 透明），保证平滑无硬边。

csproj 追加：

```xml
<ItemGroup>
  <Content Include="Assets\Wallpapers\*.jpg" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

## T3 `AppearanceService`

见设计 §5。挂到 `AppServices`：`public AppearanceService Appearance { get; }`。

## T4 `WindowBackgroundHost`

见设计 §6。关键点：

- `Apply(settings, theme)`：解析路径 → 无则整体折叠；有则设 `Image.Source`、`Opacity`、
  更新模糊 `BlurAmount` 与可见性、设遮罩颜色与 `Opacity`。
- 模糊用 `Compositor.CreateBackdropBrush()` + `GaussianBlurEffect`，
  挂在 `ElementCompositionPreview.SetElementChildVisual(blurHost, sprite)`；
  整段 `try/catch`，失败仅置 `_blurAvailable = false`。
- `Resize(w, h)` 同步 `sprite.Size`。

## T5 `MainWindow`

XAML：根 `Grid` 第一子元素插入

```xml
<Grid x:Name="BackgroundLayer" Grid.RowSpan="3" IsHitTestVisible="False" Visibility="Collapsed">
    <Image x:Name="BackgroundImage" Stretch="UniformToFill" Opacity="0.7" />
    <Border x:Name="BackgroundBlurHost" />
    <Border x:Name="BackgroundMask" />
</Grid>
```

代码：

- ctor 末尾 `_background = new WindowBackgroundHost(BackgroundImage, BackgroundBlurHost, BackgroundMask);`
  再 `ApplyTheme(...)` + `_background.Apply(...)`。
- `Content` 的 `SizeChanged` → `_background.Resize(...)`。
- `public static void RefreshAppearance()`（设计 §7）。

## T6/T7 设置页

见设计 §8。新增控件与处理器：

- `BackgroundModeCombo`（SelectionChanged）
- `BuiltinWallpaperGrid`（GridView，SelectionChanged）+ 缩略图在代码里按 `AppearanceService.Wallpapers` 生成
- `PickImageBtn` / `ClearImageBtn` / `ImagePathText`
- `BgBlurSlider` / `BgOpacitySlider` / `MaskOpacitySlider` + `MaskFollowThemeCheck`
- 回填统一放 `Loaded` 回调的 `_loaded = true` **之前**（与现有控件同批），
  再把 `_loaded = true`（放在主题回填之后，确保主题也正确下落）
- `Theme_Changed` 改为转调 `AppearanceService.ApplyTheme`

## T8 校验与文档

- `dotnet build dotnet\src\Starcloud.Core\Starcloud.Core.csproj` → 0/0
- `dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj` → 0/0
- `docs/USER_MANUAL.md`：设置页章节补「背景与外观」行 + 新增小节
- `docs/ARCHITECTURE.md`：外观层补 `AppearanceService` / `WindowBackgroundHost`

## 提交

整组完成后一次性提交：
`实现自定义背景与内置壁纸（D 组：背景与外观）`
