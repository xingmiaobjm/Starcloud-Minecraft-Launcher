# 插件导航项（navItems）实现计划

日期：2026-09-29
设计文档：docs/designs/2026-09-29-plugin-nav-items-design.md
所属：插件 2.0 扩展点（一级导航入口）
状态：已完成（T1–T7 全部落地；Core 单测见 `dotnet/tests/Starcloud.Core.Tests/PluginNavItemsTests.cs`）

构建命令（每个任务完成后执行）：

```powershell
Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue
dotnet build dotnet\Starcloud.slnx -v q -nologo
```

最终验收命令：

```powershell
dotnet build dotnet\Starcloud.slnx -v m
dotnet test dotnet\tests\Starcloud.Core.Tests\Starcloud.Core.Tests.csproj -v q -nologo --filter "FullyQualifiedName!~单连接被限速时"
```

目标：0 错误 0 警告；测试全绿；实机截图复验通过。

> 说明：`ChunkedFileDownloaderTests.单连接被限速时_并行分片把整文件吞吐拉起来` 是墙钟吞吐量测，
> 机器上有其它重负载进程时会掉到 2 MB/s 以下而失败，与本次改动无关，验收时排除。

---

## 计划期微修（相对设计文档的实现决定，不改变设计与验收）

1. **图标表放 Core，不放 App**。设计 §4 原写「白名单在 App 层 `PluginNavIcons`」，
   但那样白名单、回落规则都无法被 `Starcloud.Core.Tests` 覆盖。改为：
   Core 的 `PluginExtensionPoints` 里放**数据**（名字 → 字形码位字符串 + 默认名 + `ResolveNavIcon`），
   App 只负责把字形挂到 `IconFont.Family` 上渲染。`PluginManifest.Validate` 仍**只校验 `icon` 非空**、
   不因名字未命中而拒绝（设计 §4 的 fail-soft 规则不变，回落发生在装配导航栏时）。
2. **路由 tag 的拼/解也放 Core**。设计 §7 要求覆盖「tag 解析（含非法 tag）」的测试，
   而测试工程只测 Core，所以 `PluginNavTag(pluginId, navId)` / `TryParseNavTag(tag, out …)`
   与 `NavTagPrefix` 一并放 `PluginExtensionPoints`（与既有 `SlotPoints` / `MenuPoints` 同处），
   `MainWindow` 只调用、不自己拼字符串，避免两处格式漂移。
3. **导航组重建收成一个方法** `RebuildPluginNavGroup()`，由 `ContributionsChanged` 触发，
   内部 `DispatcherQueue.TryEnqueue` 回 UI 线程（贡献变化可能来自插件后台线程）。
4. **`PluginNavPage` 没有返回按钮**（设计 §3），但**失败兜底里保留一个「去插件页」按钮** ——
   插件被卸载时页面留着，用户得有出路。
5. **「当前页被摘掉 → 回落」判定放在重建之后**：重建完比较
   `ContentFrame.Content is PluginNavPage p && p.PluginId == 被摘插件 id`，
   命中则 `NavigateTo("plugins")`。
6. **`try/catch` 边界**：整组重建与路由解析都包 `try/catch` 并写 `LauncherLog`，
   理由与 `Nav_SelectionChanged` 既有风格一致（导航层不能把异常抛进 XAML 事件）。

## 前置确认（已核实，实现时不再重复探索）

- **`PluginManifest.Validate`**（`dotnet/src/Starcloud.Core/Plugins/PluginManifest.cs`）：
  扩展点校验块 L190-221（`c is null` 早退 L190；`Panels` L192-196、`Slots` L197-202、
  `VersionSections` L203-207、`HomeCards` L208-212、`ContextMenu` L213-218、`Settings` L219-220）；
  `ValidateViewPath` L223-227；`IsSafeRelativePath` L243。
- **`PluginContributes`** L311-319（`Panels/Slots/VersionSections/HomeCards/ContextMenu/Settings`）；
  **`PluginViewRef`** L321-326（`Id`/`Title`/`View`）；
  **`PluginExtensionPoints`** L356-（`SlotPoints` / `MenuPoints`）。
- **`PluginHost`**（`.../Plugins/PluginHost.cs`）：
  `PanelRegistration` L33、`SlotRegistration` L41、`HomeCardRegistration` L49；
  `_homeCards` L62；`event Action? ContributionsChanged` L115；`RaiseContributionsChanged()` L117；
  `GetHomeCards()` L164-166；`Start` 里 `RegisterContributedSlots` L227 / `RegisterContributedHomeCards` L228；
  `SupportedSlotPoints` L274；`RegisterContributedSlots` L287-（不支持的点 L304-306 跳过 + 记日志）；
  `RegisterContributedHomeCards` L342-368（同 id 覆盖 L363-364）；`Stop` L372-389
  （`removedAny` L385、`RaiseContributionsChanged` L389）；`StopAll` L403。
- **`PluginPanelPage`**（`dotnet/src/Starcloud.App/Views/PluginPanelPage.xaml(.cs)`）：
  XAML 结构 = `Grid RowSpacing="8"` 四行（页头 L24-28 / `SourceBar` L31-32 / 内容 `Border` L35-46
  内含 `Web` L39 与 `ScrollViewer DeclarativeHost` L40-44 → `StackPanel ViewRoot` L43 / `ActionBar` L49），
  `NavigationCacheMode="Disabled"` L13。
  code-behind：`OnNavigatedTo` L28-54、`ShowDeclarative` L56-64、`DrawView` L67-99、
  `ShowAction` L101-106、`Message` L108-114、`Back_Click` L116-119。
  **`PluginNavPage` 照它写，去掉返回按钮、去掉 WebView2 分支与 `SourceBar`。**
- **`MainWindow.xaml`**：`NavigationView.MenuItems` L410-471（`Nova` 项 L465-470，`</NavigationView.MenuItems>` L471）；
  `FooterMenuItems` L472-（`启动器日志` L473-477 / `设置` L478-）；
  图标写法 `<FontIcon FontFamily="{StaticResource ScIconFont}" Glyph="&#xEC3B;" />`。
- **`MainWindow.xaml.cs`**：`InitializeComponent()` L29；`ContentFrame.Navigate(HomePage)` L100；
  `StopBackgroundTimers` L219；`Nav_SelectionChanged` L254-284（`var tag = item.Tag?.ToString()` L261、
  `Type page = tag switch { … }` L262-275、`ContentFrame.Navigate(page, null, …)` L279）；
  `NavigateTo(string tag)` L287-294（在 `MenuItems.Concat(FooterMenuItems)` 里按 tag 找项、设 `SelectedItem`）；
  `_taskRefresh` L326 声明 / L61-68 起。
- **`IconFont`**（`dotnet/src/Starcloud.App/Services/IconFont.cs` L11）→ `FontFamily`。
- **字形**（取自 `MainWindow.xaml` 导航项，已在实机渲染）：
  首页 `EC77`、版本 `EA94`、下载 `E90C`、存档 `EA90`、联机 `EB91`、账户 `EB8F`、插件 `EABC`、日志 `EC64`。
- **测试工程**：`dotnet/tests/Starcloud.Core.Tests/`；xunit；方法名中文；
  插件用例的文件与临时目录写法见 `PluginSpecV2Tests.cs`（`Directory.CreateTempSubdirectory("mclplug-…-")`
  + `new PluginHost("0.5.0", Path.Combine(dir, "logs"), "")` + `try/finally Directory.Delete`）。
- **演示插件（用户数据目录，不入库）**：
  `%APPDATA%\starcloud-launcher\plugins\unpacked\com.starcloud.spec2demo\`，清单 2.3.0，
  已有 `panels/` `slots/` `sections/` `cards/` `views/`；`integrity.json` 需在改文件后重算。

---

## T1：Core · 清单模型

文件：`dotnet/src/Starcloud.Core/Plugins/PluginManifest.cs`（改）

1. `PluginContributes`（L311-319）加一行（放在 `homeCards` 之后）：

```csharp
[JsonPropertyName("navItems")] public PluginNavRef[] NavItems { get; init; } = [];
```

2. 新增条目类型（紧挨 `PluginViewRef` L321-326 之后）：

```csharp
/// <summary>一级导航项（规范 2.0 §6.4）：icon 是宿主内置图标名，不是路径或字形</summary>
public sealed class PluginNavRef
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("icon")] public string Icon { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = "";
}
```

3. `Validate` 的扩展点校验块（L213 之后、`ContextMenu` 之前，或紧随 `HomeCards` 之后，取与既有顺序一致的位置）加：

```csharp
if (c.NavItems.Length > 1)
    errors.Add($"contributes.navItems 最多 1 条（当前 {c.NavItems.Length} 条）—— 一级导航位置每个插件只能占一个");
foreach (var nav in c.NavItems)
{
    if (string.IsNullOrWhiteSpace(nav.Id)) errors.Add("contributes.navItems 缺少 id");
    if (string.IsNullOrWhiteSpace(nav.Title)) errors.Add("contributes.navItems 缺少 title");
    // icon 只校验非空：白名单命中与否在装配导航栏时判定并回落默认图标（设计 §4 fail-soft）
    if (string.IsNullOrWhiteSpace(nav.Icon)) errors.Add("contributes.navItems 缺少 icon");
    ValidateViewPath(nav.View, "contributes.navItems.view", errors);
}
```

构建（`Starcloud.Core`）。

---

## T2：Core · 扩展点目录（tag 与图标表）

文件：`dotnet/src/Starcloud.Core/Plugins/PluginManifest.cs`（`PluginExtensionPoints`，L356-，改）

1. 导航 tag 前缀与拼解：

```csharp
/// <summary>插件导航项的 tag 前缀：plugin:&lt;插件 id&gt;:&lt;导航 id&gt;（与内置 tag 不可能撞）</summary>
public const string NavTagPrefix = "plugin:";

/// <summary>拼一个插件导航项的 tag</summary>
public static string PluginNavTag(string pluginId, string navId) =>
    $"{NavTagPrefix}{pluginId}:{navId}";

/// <summary>解一个 tag；不是插件 tag 或两段为空都返回 false</summary>
public static bool TryParseNavTag(string? tag, out string pluginId, out string navId)
{
    pluginId = ""; navId = "";
    if (string.IsNullOrEmpty(tag) || !tag.StartsWith(NavTagPrefix, StringComparison.Ordinal)) return false;
    var rest = tag[NavTagPrefix.Length..];
    var at = rest.IndexOf(':');
    if (at <= 0 || at == rest.Length - 1) return false;
    pluginId = rest[..at];
    navId = rest[(at + 1)..];
    return pluginId.Length > 0 && navId.Length > 0;
}
```

2. 图标表（名字 → 字形码位字符串；复导航栏已渲染过的字形，不新挑，避免缺字）：

```csharp
/// <summary>插件导航项的图标白名单：名字 → 字形。字形复用导航栏里已在用的那批 HarmonyOS 图标。</summary>
public static readonly IReadOnlyDictionary<string, string> NavIcons =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["home"] = "\uEC77",       // 首页 / 概览
        ["instance"] = "\uEA94",   // 版本 / 实例
        ["download"] = "\uE90C",   // 下载 / 资源
        ["saves"] = "\uEA90",      // 存档 / 世界
        ["server"] = "\uEB91",     // 联机 / 服务器
        ["account"] = "\uEB8F",    // 账户 / 身份
        ["extension"] = "\uEABC",  // 扩展 / 插件（默认）
        ["log"] = "\uEC64",        // 日志 / 记录
    };

/// <summary>默认图标名（未声明或未命中白名单时用它）</summary>
public const string DefaultNavIcon = "extension";

/// <summary>解析图标名；未命中白名单返回 false 并给出默认字形（调用方据此记一行日志）</summary>
public static bool TryResolveNavIcon(string? name, out string glyph)
{
    if (name is not null && NavIcons.TryGetValue(name, out var hit)) { glyph = hit; return true; }
    glyph = NavIcons[DefaultNavIcon];
    return false;
}
```

构建（`Starcloud.Core`）。

---

## T3：Core · 导航项注册表

文件：`dotnet/src/Starcloud.Core/Plugins/PluginHost.cs`（改）

1. 记录类型（紧挨 `HomeCardRegistration` L49 之后）：

```csharp
/// <summary>
/// 插件的一级导航项（2.0 contributes.navItems，规范 2.0 §6.4）。
/// 跨插件上限（最多 3 个）由宿主导航栏按加载顺序截断，注册表全量收下。
/// </summary>
public sealed record NavItemRegistration(
    string PluginId, string NavId, string Title, string IconGlyph, string EntryPath,
    bool IconFallback);
```

2. 字段与查询（`_homeCards` L62 之后 + `GetHomeCards()` L164-166 之后）：

```csharp
private readonly List<NavItemRegistration> _navItems = [];

/// <summary>一级导航项（按插件加载顺序、插件内的声明顺序）</summary>
public IReadOnlyList<NavItemRegistration> GetNavItems()
{
    lock (_gate) return [.. _navItems];
}
```

3. 注册（照 `RegisterContributedHomeCards` L342-368 同构，放在它之后）：

```csharp
/// <summary>2.0 一级导航项：contributes.navItems 里写了就注册；同样是 UI 能力，需要 ui:panel。</summary>
private void RegisterContributedNavItems(PluginManifest manifest, string dir, PluginLogger logger)
{
    var items = manifest.Contributes?.NavItems;
    if (items is null || items.Length == 0) return;
    if (manifest.IsApi2 &&
        !PluginPermissions.BridgeApi2(manifest.Permissions).Capabilities.Contains(PluginPermissions.UiPanel))
    {
        logger.Warn("声明了 contributes.navItems，但 ui:panel 能力未生效，导航项不会注册");
        return;
    }

    // 清单校验已限制 ≤1；这里仍防御性只取第一条（防手改清单）
    var item = items[0];
    if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.View)) return;
    var iconOk = PluginExtensionPoints.TryResolveNavIcon(item.Icon, out var glyph);
    if (!iconOk) logger.Warn($"导航项 {item.Id} 的图标名 {item.Icon} 不在宿主白名单里，已回落默认图标");

    var reg = new NavItemRegistration(manifest.Id, item.Id,
        string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title,
        glyph,
        Path.Combine(dir, item.View.Replace('/', Path.DirectorySeparatorChar)),
        IconFallback: !iconOk);
    lock (_gate)
    {
        _navItems.RemoveAll(n => n.PluginId == manifest.Id && n.NavId == item.Id);
        _navItems.Add(reg);
    }
    logger.Info($"已注册导航项：{item.Id}（{reg.Title}）");
    RaiseContributionsChanged();
}
```
4. `Start` 里注册链（L227-228 之后）加 `RegisterContributedNavItems(manifest, dir, logger);`。
5. `Stop`（L372-389）里加摘除并计入 `removedAny`：

```csharp
var removedNavs = _navItems.RemoveAll(n => n.PluginId == pluginId) > 0;
removedAny = removed.Count > 0 || removedSlots || removedCards || removedNavs;
```

构建（`Starcloud.Core`）。

---

## T4：App · 导航页

文件：`dotnet/src/Starcloud.App/Views/PluginNavPage.xaml`（新建）、`PluginNavPage.xaml.cs`（新建）

XAML 照 `PluginPanelPage.xaml` 的骨架减料：`NavigationCacheMode="Disabled"`；
`Grid RowSpacing="8"` 三行 —— 页头（`TextBlock x:Name="NavTitle" FontSize="24" FontWeight="Medium"`，
**无返回按钮**）/ 内容 `Border`（同 `GlassCardBrush` + `CornerRadius="16"` + `Padding="4"`，
内含 `ScrollViewer x:Name="ViewHost"` → `StackPanel x:Name="ViewRoot" Margin="16,14,16,14" Spacing="10"`）/
`InfoBar x:Name="ActionBar"`。不引 `WebView2`、不引 `SourceBar`。

code-behind：

```csharp
// 插件一级导航页（contributes.navItems）：宿主按 (插件 id, 导航 id) 渲染该插件的声明式视图。
// 与「插件」页里的面板同源 —— 同一份解析、同一套能力层、同一套失败兜底；
// 区别只是这里没有返回按钮（导航栏本身就是返回），并多一个「去插件页」出路。

public sealed partial class PluginNavPage : Page
{
    private readonly AppServices _s = AppServices.Instance;

    /// <summary>本页对应的插件 id（回落判定用：插件被停用时要离开这一页）</summary>
    public string PluginId { get; private set; } = "";

    private string _navId = "";

    public PluginNavPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not (string pluginId, string navId)) return;
        PluginId = pluginId;
        _navId = navId;
        var item = _s.PluginHost.GetNavItems()
            .FirstOrDefault(n => n.PluginId == pluginId && n.NavId == navId);
        if (item is null) { ShowGone(); return; }
        NavTitle.Text = item.Title;
        DrawView(item.EntryPath);
    }

    /// <summary>解析 + 渲染；插件已卸载、解析失败、渲染异常都在页面里说明</summary>
    private void DrawView(string entryPath)
    {
        ViewRoot.Children.Clear();
        var installed = _s.Plugins.Scan().FirstOrDefault(p => p.Manifest.Id == PluginId);
        if (installed is null) { ShowGone(); return; }
        var element = PluginViewRenderer.RenderFile(PluginId, entryPath, ShowAction, () => DrawView(entryPath));
        ViewRoot.Children.Add(element);
    }

    /// <summary>插件没了：说明 + 回「插件」页的出路（不留死页面）</summary>
    private void ShowGone()
    {
        ViewRoot.Children.Clear();
        ViewRoot.Children.Add(new TextBlock
        {
            Text = "插件已不在已安装列表中，这一页无法渲染。",
            FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
        });
        var back = new Starcloud.App.Views.Controls.GlowButton
        {
            Content = Loc.T("去插件页"), Padding = new Thickness(12, 5),
        };
        back.Click += (_, _) => (App.MainWindow as MainWindow)?.NavigateTo("plugins");
        ViewRoot.Children.Add(back);
    }

    private void ShowAction(string message, bool ok)
    {
        ActionBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }
}
```

新增本地化键 `去插件页`（en-US.json 里按拼音序插到「去」附近）。

构建（`Starcloud.App`）。

---

## T5：App · 导航栏接入

文件：`dotnet/src/Starcloud.App/MainWindow.xaml`（改）、`MainWindow.xaml.cs`（改）

1. XAML：在 `Nova` 项（L465-470）与 `</NavigationView.MenuItems>`（L471）之间**不写任何东西** ——
   插件组是运行时插入的（插件后台加载，XAML 里写死会先闪一组空项）。
   **无插件导航项时连分隔线都不加**，由代码统一控制。

2. `MainWindow.xaml.cs`：

   a. 字段：`private readonly List<NavigationViewItem> _pluginNavItems = [];`、
      `private NavigationViewItemSeparator? _pluginNavSeparator;`、`private bool _pluginNavHooked;`

   b. 在 ctor 末尾（`_taskRefresh` 起完之后）挂一次（用 `Loaded` 保证控件树就绪）：

```csharp
// 插件导航组：插件后台加载，加载/停用都要重建；首次 Loaded 时也重建一次（可能已经有贡献）
Loaded += (_, _) =>
{
    if (_pluginNavHooked) return;
    _pluginNavHooked = true;
    AppServices.Instance.PluginHost.ContributionsChanged += OnPluginContributionsChanged;
    RebuildPluginNavGroup();
};
```

   c. 重建方法（插在 `MenuItems` 末尾、`FooterMenuItems` 之前）：

```csharp
/// <summary>重建「插件导航组」：Nova 之后一条分隔线 + 最多 3 项。
/// 上限是宿主展示策略：按插件加载顺序取前 3，其余不显示并写一行日志（与首页卡片带同口径）。</summary>
private void RebuildPluginNavGroup()
{
    try
    {
        foreach (var item in _pluginNavItems)
        {
            if (ReferenceEquals(Nav.SelectedItem, item)) Nav.SelectedItem = Nav.MenuItems[0];
            Nav.MenuItems.Remove(item);
        }
        _pluginNavItems.Clear();
        if (_pluginNavSeparator is not null) { Nav.MenuItems.Remove(_pluginNavSeparator); _pluginNavSeparator = null; }

        var all = AppServices.Instance.PluginHost.GetNavItems();
        if (all.Count == 0) return;   // 没插件导航项：连分隔线都不出现，导航栏与从前一致
        var shown = all.Count > MaxPluginNavItems ? all.Take(MaxPluginNavItems).ToList() : [.. all];
        if (all.Count > MaxPluginNavItems)
            LauncherLog.Write("plugin", $"插件导航项共 {all.Count} 个，超出上限，只展示前 {MaxPluginNavItems} 个（按插件加载顺序）");

        _pluginNavSeparator = new NavigationViewItemSeparator();
        Nav.MenuItems.Add(_pluginNavSeparator);
        foreach (var reg in shown)
        {
            var item = new NavigationViewItem
            {
                Tag = PluginExtensionPoints.PluginNavTag(reg.PluginId, reg.NavId),
                Content = new TextBlock { Text = reg.Title, VerticalAlignment = VerticalAlignment.Center },
                Icon = new FontIcon { FontFamily = IconFont.Family, Glyph = reg.IconGlyph },
            };
            Nav.MenuItems.Add(item);
            _pluginNavItems.Add(item);
        }
        LauncherLog.Write("plugin", $"插件导航组：{shown.Count} 项（共 {all.Count} 个注册）");
    }
    catch (Exception ex)
    {
        LauncherLog.Error("plugin", "重建插件导航组失败", ex);
    }
}

/// <summary>贡献变化可能来自插件后台线程，必须回 UI 线程重建，并处理当前页被摘掉的情况</summary>
private void OnPluginContributionsChanged() => DispatcherQueue.TryEnqueue(() =>
{
    var current = (ContentFrame.Content as PluginNavPage)?.PluginId;
    RebuildPluginNavGroup();
    // 正在看的插件被停用：那一页已经没人维护了，回落「插件」页（不留死页面）
    if (current is not null &&
        !AppServices.Instance.PluginHost.GetNavItems().Any(n => n.PluginId == current))
        NavigateTo("plugins");
});
```

   常量 `private const int MaxPluginNavItems = 3;` 放字段区。

   d. 导航路由（改 `Nav_SelectionChanged` L254-284）：在 `var tag = item.Tag?.ToString();`（L261）之后插：

```csharp
// 插件导航项：tag 是 plugin:<插件 id>:<导航 id>，走独立一级页（带参数导航）
if (PluginExtensionPoints.TryParseNavTag(tag, out var pluginId, out var navId))
{
    var args = (pluginId, navId);
    if (ContentFrame.CurrentSourcePageType != typeof(PluginNavPage) ||
        (ContentFrame.Content as PluginNavPage)?.PluginId != pluginId)
    {
        ContentFrame.Navigate(typeof(PluginNavPage), args,
            MotionService.TransitionFor(AppServices.Instance.Motion.Enabled));
    }
    Nav.IsPaneOpen = false;
    return;
}
```

   内置分支（L262-284）保持不动。

   e. 退订：在既有 `Closed`（L103-）里加
      `AppServices.Instance.PluginHost.ContributionsChanged -= OnPluginContributionsChanged;`
      （**不要**用到 `StopBackgroundTimers` 里，那个在窗口关闭前也会被调用）。

   f. `NavigateTo(string tag)`（L287-294）**零改动** —— 它已经在
      `MenuItems.Concat(FooterMenuItems)` 里按 tag 找项，插件项在 `MenuItems` 里，天然能选中。

构建（整仓）。

---

## T6：Core 测试

文件：`dotnet/tests/Starcloud.Core.Tests/PluginNavItemsTests.cs`（新建）

| # | 用例 | 断言 |
| --- | --- | --- |
| 1 | 导航项随清单注册 | `navItems` 一条 → `GetNavItems().Count == 1`，`NavId`/`Title`（缺 title 回落成 id）/`IconGlyph`（命中白名单）/`EntryPath`（拼在插件目录下、`/` 已归一）正确 |
| 2 | 每插件最多 1 条 | `navItems` 两条 → `Validate` 的 errors 含 `contributes.navItems 最多 1 条` |
| 3 | 必填字段 | 缺 `id` / 缺 `title` / 缺 `icon` / `view` 写成 `../x.json` → 各自产生对应 error |
| 4 | 无 `ui:panel` 不注册 | 权限只给 `storage:kv` → `GetNavItems()` 为空 |
| 5 | 停用摘除 | `Stop(pluginId)` → `GetNavItems()` 为空 |
| 6 | 重复注册不重复条目 | 同一插件连续 `Start` 两次 → `GetNavItems().Count == 1`（`RemoveAll` 后重加的防御分支） |
| 7 | 图标白名单 | `TryResolveNavIcon("server")` → true 且字形为 `\uEB91`；`TryResolveNavIcon("nope")` → false 且字形等于默认（`\uEABC`）；`TryResolveNavIcon(null)` → false + 默认 |
| 8 | 注册项带回落标记 | 图标名写 `nope` → `NavItemRegistration.IconFallback == true` 且字形是默认 |
| 9 | tag 拼 | `PluginNavTag("com.a", "servers") == "plugin:com.a:servers"` |
| 10 | tag 解 | `TryParseNavTag("plugin:com.a:servers")` → true + 两段正确；`"home"` / `"plugin:"` / `"plugin:a"` / `"plugin::b"` / `"plugin:a:"` / `""` / `null` → 全 false 且 out 为空串 |
| 11 | 跨插件共存 | 两个插件各 1 条 → 2 条，顺序 = 注册顺序 |

写法：照 `PluginSpecV2Tests.cs` 的临时目录 + `new PluginHost("0.5.0", …, "")` 模式；
`Validate` 用例直接 `new PluginManifest { … }.Validate()` 取 errors（与既有用例同构）。

构建 + 测试。

---

## T7：文档 + 演示插件 + 全量验证

1. `docs/plugin-spec-v2.md`：
   - §6.2 表格加一行：`| navItems | 左侧导航栏一级入口（Nova 之后的独立分组） | 声明式视图 | ✅ 最多 3 项、每插件 1 项（见 §6.4） |`；
   - 新增 `### 6.4 一级导航项（navItems）✅`：清单示例、位置示意（代码块画导航结构）、
     上限口径、图标白名单表（8 个名字 + 字形 + 默认）、权限、标题不做本地化、
     路由 tag 格式、停用回落行为、不做清单；
   - §11 实现映射表加两行（`PluginNavPage`、`PluginExtensionPoints` 的 tag/图标表）。
2. `docs/plugin-sdk.md`：`contributes` 示例加 `navItems`，并补一段「一级导航项怎么用 + 图标只能填这 8 个名字」。
3. 演示插件（`%APPDATA%\starcloud-launcher\plugins\unpacked\com.starcloud.spec2demo\`）：
   - 新增 `views/servers.json`（一条 `text` + 两个 `badge` + 一个 `button` 的声明式视图，别只放一行字，
     要能验证动作结果条）；
   - 清单 `contributes.navItems` 加一条 `{ id: "servers", title: "服务器浏览", icon: "server", view: "views/servers.json" }`，
     版本升到 2.4.0；
   - **重算 `integrity.json`**（用上一轮那段 PowerShell：按路径排序、`path\0sha256\n` 拼接再 sha256
     得 package，排除 `integrity.json` / `data/*` / `*.mclplug`），并自检逐文件摘要与整包摘要一致。
4. 全量验证：
   - `dotnet build dotnet\Starcloud.slnx -v m` → 0 错 0 警；
   - `dotnet test … --filter "FullyQualifiedName!~单连接被限速时"` → 全绿；
   - 实机截图（`PrintWindow` + `PW_RENDERFULLCONTENT`）三张：
     ① 有导航项：Nova 之后出现分隔线 + 「服务器浏览」，点开渲染正常、动作按钮出结果条；
     ② 停用演示插件后：项与分隔线一起消失、当前页回落「插件」页；
     ③ 无任何插件导航项时：导航栏与改动前逐像素一致（与既有基线截图比对）。

## 依赖顺序

```
T1 → T2 → T3 ─┬→ T6（测试）
              └→ T4 → T5 → T7
```

- T1/T2 独立；T3 依赖 T1（清单模型）与 T2（图标解析）；T4 依赖 T3（`GetNavItems`）；
  T5 依赖 T4（要导航到 `PluginNavPage`）；T6 只依赖 T1–T3（纯 Core）。
- 全程不引入第三方依赖；导航项图标复用既有 HarmonyOS 字体，不新增资源。
