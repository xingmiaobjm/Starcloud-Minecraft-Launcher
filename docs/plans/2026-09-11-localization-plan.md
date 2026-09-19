# 多语言与无障碍（D3）实现计划

> 依赖设计：[2026-09-11-localization-design.md](../specs/2026-09-11-localization-design.md)
> 提交约定：整组完成后一次性提交。

## T1 基础设施

1. 新增 `dotnet/src/Starcloud.App/Localization/Loc.cs`：
   - `Initialize` / `SetLanguage` / `Current` / `LanguageChanged` / `T(string)` / `T(string, params object?[])`。
   - 词典从嵌入式资源 `Starcloud.App.Strings.en-US.json` 反序列化（`Dictionary<string,string>`，`Ordinal` 比较）。
   - 8 个附加属性 + 弱引用重放表（见设计 §3.3）。
2. 新增 `dotnet/src/Starcloud.App/Strings/en-US.json`（先放 `{}`，T7 填充）。
3. `Starcloud.App.csproj` 增加 `<EmbeddedResource Include="Strings\*.json" />`。
4. 构建 `Starcloud.App` → 0/0。

## T2 设置与入口

1. `LauncherSettings` 新增 `ui_language`（默认 `"zh-CN"`）。
2. `App` 构造函数最早期（`InitializeComponent()` 之前）`Loc.Initialize(...)`；`ConfigureAwait` 无关。
3. `SettingsPage`「主题外观」卡片新增「界面语言」`ComboBox`（简体中文 / English），回填 + `SelectionChanged` 即改即存 + `Loc.SetLanguage`。

## T3 XAML 转换：Shell / Home / Logs

- `MainWindow.xaml`（18）、`Views/HomePage.xaml`（37）、`Views/LogsPage.xaml`（4）→ 对应 `xaml.cs` 文案。
- 图标按钮补 `AutomationProperties.Name` / `ToolTipService.ToolTip`。

## T4 XAML 转换：Download / VersionDetail / Versions

- `Views/DownloadPage.xaml`（63）+ `DownloadPage.xaml.cs`（141）
- `Views/VersionDetailPage.xaml`（106）+ `VersionDetailPage.xaml.cs`（104）
- `Views/VersionsPage.xaml`（25）+ `VersionsPage.xaml.cs`（21）

## T5 XAML 转换：Saves / Accounts / Nova / Settings

- `Views/SavesPage.xaml`（14）+ `.cs`（51）
- `Views/AccountsPage.xaml`（21）+ `.cs`（53）
- `Views/NovaPage.xaml`（57）+ `.cs`（41）、`Views/NovaChatView.xaml`（20）、`Views/NovaOverlayWindow.xaml`（2）
- `Views/SettingsPage.xaml`（86）+ `.cs`（14）、`Views/VersionDetailPage` 交互文案
- `Views/EmptyStateView.xaml(.cs)`：`Title` / `Subtitle` / `ActionText` 由使用方传入，本身不硬编码文案（校验）。

## T6 C# 对话框 / 提示 / 服务文案

- `ModPackDialogs.cs`（94）、`CrashDialog.cs`（24）、`LaunchTips.cs`（24）、`InstanceActions.cs`（16）、`PageRows.cs`（14）、`EnvCheckDialogs.cs`（8）、`MrPackFlow.cs`（2）、`AppearanceService.cs`（6）、`MainWindow.xaml.cs`（6）、`LogsPage.xaml.cs`（3）、`AppServices.cs`（1）、`App.xaml.cs`（1）、`NovaRuntime.cs`（31）。

## T7 英文词典

- 从转换后的源码 grep 出全部词条键，生成 `Strings/en-US.json` 全量英文翻译。
- 校验：`en-US.json` 键集合 ⊇ 源码出现的所有键（可用脚本比对）。

## T8 无障碍 + 文档 + 验收

1. 无障碍补强（设计 §3.7）自检。
2. `docs/USER_MANUAL.md`：设置页「通用」行补「界面语言」。
3. `docs/ARCHITECTURE.md`：新增「本地化层」段落 + 目录树。
4. 双构建 0/0。
5. 一次性提交。
