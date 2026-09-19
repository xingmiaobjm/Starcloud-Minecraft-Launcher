// 下载页：PCL2 式左侧分类（原版游戏 + 社区资源五类）
// 原版：版本列表（正式版按大版本抽屉折叠）→ 详情（Forge/NeoForge/Fabric 抽屉三选一 + 开始下载）
// 社区：Modrinth 搜索筛选安装（Mod/整合包/数据包/资源包/光影包）

using System.Globalization;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Models;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class DownloadPage : Page
{
    private const int PageSize = 20;

    private static readonly Brush InstalledBrush = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);

    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private readonly DispatcherQueueTimer _okTimer;

    // ── 原版 ──
    private List<ManifestVersion> _versions = [];
    private string? _detailId;
    private bool _busy;

    // ── 详情页加载器（Forge/NeoForge/Fabric/Quilt 抽屉互斥选装） ──
    private enum LoaderKind { Forge, NeoForge, Fabric, Quilt }

    private static readonly LoaderKind[] LoaderKeys =
        [LoaderKind.Forge, LoaderKind.NeoForge, LoaderKind.Fabric, LoaderKind.Quilt];

    private static readonly Dictionary<LoaderKind, (string Name, string CacheKind)> LoaderInfo = new()
    {
        [LoaderKind.Forge] = ("Forge", "forge"),
        [LoaderKind.NeoForge] = ("NeoForge", "neoforge"),
        [LoaderKind.Fabric] = ("Fabric", "fabric"),
        [LoaderKind.Quilt] = ("Quilt", "quilt"),
    };

    // 每个加载器的构建列表（null = 拉取中）与已选构建（null = 不安装）
    private readonly Dictionary<LoaderKind, List<LoaderBuild>?> _builds = [];
    private readonly Dictionary<LoaderKind, string?> _picked = [];

    // ── 社区资源 ──
    private static readonly (string Tag, string SearchTitle, string Folder, string Verb)[] Sections =
    {
        ("mod",          "搜索 Mod",    "mods",          "安装"),
        ("modpack",      "搜索整合包", "modpacks",      "下载"),
        ("datapack",     "搜索数据包", "datapacks",     "安装"),
        ("resourcepack", "搜索资源包", "resourcepacks", "安装"),
        ("shader",       "搜索光影包", "shaderpacks",   "安装"),
    };

    /// <summary>Modrinth 分类 slug → 中文（未映射的显示原 slug）</summary>
    private static readonly Dictionary<string, string> CategoryNames = new()
    {
        // mod / datapack
        ["adventure"] = "冒险", ["cursed"] = "猎奇", ["decoration"] = "装饰",
        ["economy"] = "经济", ["equipment"] = "装备", ["food"] = "食物与烹饪",
        ["game-mechanics"] = "游戏机制", ["library"] = "支持库", ["magic"] = "魔法",
        ["management"] = "管理", ["minigame"] = "小游戏", ["mobs"] = "生物",
        ["optimization"] = "性能优化", ["social"] = "社交", ["storage"] = "存储",
        ["technology"] = "科技", ["transportation"] = "运输", ["utility"] = "实用",
        ["worldgen"] = "世界元素",
        // modpack
        ["challenging"] = "硬核挑战", ["combat"] = "战斗", ["kitchen-sink"] = "大杂烩",
        ["lightweight"] = "轻量", ["multiplayer"] = "多人", ["quests"] = "任务",
        // resourcepack
        ["audio"] = "音频", ["blocks"] = "方块", ["core-shaders"] = "核心着色器",
        ["entities"] = "实体", ["environment"] = "环境", ["fonts"] = "字体",
        ["gui"] = "界面", ["items"] = "物品", ["locale"] = "本地化",
        ["modded"] = "模组支持", ["models"] = "模型", ["realistic"] = "写实",
        ["simplistic"] = "简洁", ["themed"] = "主题", ["tweaks"] = "微调",
        ["vanilla-like"] = "原版风格",
        // shader
        ["atmosphere"] = "大气", ["bloom"] = "泛光", ["cartoon"] = "卡通",
        ["colored-lighting"] = "彩色光照", ["fantasy"] = "奇幻", ["foliage"] = "植被",
        ["path-tracing"] = "路径追踪", ["pbr"] = "PBR", ["potato"] = "土豆画质",
        ["reflections"] = "反射", ["screenshot"] = "截图", ["semi-realistic"] = "半写实",
        ["shadows"] = "阴影",
    };

    private bool _communityInit;
    private Task? _communityInitTask;
    private bool _suppress;     // 初始化/重建筛选控件期间抑制 SelectionChanged 触发搜索
    private int _offset;
    private int _total;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<ModrinthCategory>? _categories;
    private List<ModrinthHit> _lastHits = [];   // 当前搜索结果原始数据（行点击时按 ProjectId 找回，传入下载窗直达详情）

    public DownloadPage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        _okTimer = _dq.CreateTimer();
        _okTimer.Interval = TimeSpan.FromSeconds(6);
        _okTimer.Tick += (_, _) => OkBar.IsOpen = false;
        Loaded += async (_, _) => { await LoadVersionsAsync(); await EnsurePluginSlotsAsync(); };
    }

    // ════════════ 插件挂载区（download.top） ════════════

    private bool _slotsLoaded;

    /// <summary>
    /// 已启用插件通过 ctx.ui.registerSlot("download.top", html, height) 注册的横幅，
    /// 以 WebView2 file:// 渲染（与插件面板同安全基线：不暴露宿主对象）。插件启停后重进页面生效。
    /// </summary>
    private async Task EnsurePluginSlotsAsync()
    {
        if (_slotsLoaded) return;
        _slotsLoaded = true;
        try
        {
            var regs = _s.Plugins.Host.GetSlots("download.top");
            if (regs.Count == 0) return;
            foreach (var reg in regs)
            {
                var frame = new Border
                {
                    Height = Math.Clamp(reg.Height, 48, 320),
                    CornerRadius = new CornerRadius(12),
                    Background = Application.Current.Resources["LayerFillColorDefaultBrush"] as Brush,
                    BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                    BorderThickness = new Thickness(1),
                    Child = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch },
                };
                PluginSlotHost.Children.Add(frame);
                if (frame.Child is WebView2 web)
                {
                    await web.EnsureCoreWebView2Async();
                    web.CoreWebView2.Navigate(reg.EntryPath.Replace('\\', '/').Insert(0, "file:///"));
                }
            }
            PluginSlotHost.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // 挂载区是锦上添花，失败静默不影响下载页
            System.Diagnostics.Debug.WriteLine($"插件挂载区加载失败：{ex.Message}");
        }
    }

    // ════════════ 分类切换 ════════════

    private string CurrentCategory =>
        (CategoryList.SelectedItem as FrameworkElement)?.Tag as string ?? "vanilla";

    private async void Category_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // XAML 中首个条目 IsSelected="True" 会在 InitializeComponent 解析期间同步触发本事件，
        // 此时右侧面板尚未创建，直接跳过（XAML 已自带正确的初始可见性）
        if (CommunityPanel is null) return;
        var isVanilla = CurrentCategory == "vanilla";
        var incoming = isVanilla ? VanillaPanel : CommunityPanel;
        var outgoing = isVanilla ? CommunityPanel : VanillaPanel;

        // 交叉过渡：两个面板叠在同一格，旧面板快速淡出、新面板从下方浮入（合成动画，不闪白）
        if (incoming.Visibility != Visibility.Visible)
        {
            MotionFx.Reset(incoming);
            incoming.Visibility = Visibility.Visible;
            MotionFx.FadeSlideIn(incoming, 18f, MotionFx.EnterMs);

            if (outgoing.Visibility == Visibility.Visible)
            {
                MotionFx.FadeOut(outgoing, MotionFx.ExitMs);
                await Task.Delay(MotionFx.ExitMs + 20);
                outgoing.Visibility = Visibility.Collapsed;
                MotionFx.Reset(outgoing);
            }
        }

        if (isVanilla)
        {
            // 切回原版：取消在途社区搜索，过期结果不再回填到已隐藏的面板
            _cts?.Cancel();
            return;
        }

        if (_communityInit)
        {
            // 分区切换（如 Mod → 整合包）：重建筛选控件并重新搜索，否则内容停留在上一个分区
            _suppress = true;
            try { RebuildFilterRows(); }
            finally { _suppress = false; }
            _ = SearchAsync(reset: true);
        }
        else
        {
            _ = EnsureCommunityAsync();
        }
    }

    // ════════════ 原版：清单与筛选 ════════════

    private async Task LoadVersionsAsync()
    {
        try
        {
            var manifest = await _s.Manifest.GetManifestAsync(false);
            _versions = manifest.Versions;
            ApplyVersionFilter();
            VersionCacheBadge.Visibility =
                _s.Manifest.LastLoadFromCache ? Visibility.Visible : Visibility.Collapsed;

            // 命中缓存 → 后台直连网络拉新内容并替换；失败保留缓存内容、不弹错
            if (_s.Manifest.LastLoadFromCache)
            {
                try
                {
                    var fresh = await _s.Manifest.GetManifestAsync(true);
                    _versions = fresh.Versions;
                    ApplyVersionFilter();
                    VersionCacheBadge.Visibility = Visibility.Collapsed;
                }
                catch { /* 保留缓存内容 */ }
            }
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    /// <summary>当前版本类型：取分段控件里被选中的那一段</summary>
    private string TypeTag =>
        TypeSnapshotBtn.IsChecked == true ? "snapshot"
        : TypeOldBtn.IsChecked == true ? "old"
        : "release";

    /// <summary>分段控件点击：单选语义 + 指示器平移到位 + 重新筛选</summary>
    private void TypeSegment_Click(object s, RoutedEventArgs e)
    {
        if (s is not ToggleButton clicked) return;
        var tag = clicked.Tag as string ?? "release";

        // 单选：其余两段取消勾选（ToggleButton 自身是开关语义，这里手动收束）
        foreach (var button in new[] { TypeReleaseBtn, TypeSnapshotBtn, TypeOldBtn })
            button.IsChecked = ReferenceEquals(button, clicked);

        AnimateTypeIndicator(tag switch { "snapshot" => 1, "old" => 2, _ => 0 });

        if (VersionList is null) return;
        ApplyVersionFilter();
    }

    /// <summary>分段指示器平移（合成动画）：第 index 段 × 段宽 88</summary>
    private void AnimateTypeIndicator(int index, bool animate = true)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TypeIndicator);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(TypeIndicator, true);
            var target = new System.Numerics.Vector3(index * 88f, 0f, 0f);

            if (!animate || !_s.Motion.Enabled)
            {
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", target);
                return;
            }

            var compositor = visual.Compositor;
            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(260);
            anim.InsertKeyFrame(1f, target, compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.16f, 1f), new System.Numerics.Vector2(0.3f, 1f)));
            visual.StartAnimation("Translation", anim);
        }
        catch
        {
            // 静默：动效失败不影响筛选
        }
    }

    private void SearchBox_TextChanged(object s, TextChangedEventArgs e) => ApplyVersionFilter();

    private void ApplyVersionFilter()
    {
        var installedIds = _s.Manifest.ListInstalled(_s.Settings.DotMinecraft).Select(i => i.Id).ToHashSet();
        var q = SearchBox.Text.Trim();
        IEnumerable<ManifestVersion> src = TypeTag switch
        {
            "snapshot" => _versions.Where(v => v.Type == "snapshot"),
            "old" => _versions.Where(v => v.Type is "old_beta" or "old_alpha"),
            _ => _versions.Where(v => v.Type == "release"),
        };
        if (q.Length > 0)
            src = src.Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase));

        var rows = src.Select(v =>
        {
            var installed = installedIds.Contains(v.Id);
            return new DownloadRow(v.Id, Describe(v),
                installed ? Loc.T("已安装") : "", installed ? InstalledBrush : null,
                VersionRowHelpers.VersionIcon(v.Type));
        }).ToList();

        // 最新版本瓦片：最新正式版 + 最新预览版各一枚（点进详情）
        var rel = NewestOf("release");
        var snap = NewestOf("snapshot");
        ApplyLatestTile(LatestReleaseTile, LatestReleaseIcon, LatestReleaseId, LatestReleaseMeta, rel);
        ApplyLatestTile(LatestSnapshotTile, LatestSnapshotIcon, LatestSnapshotId, LatestSnapshotMeta, snap);
        LatestCard.Visibility = rel is not null || snap is not null ? Visibility.Visible : Visibility.Collapsed;

        // 正式版且未搜索时按大版本抽屉折叠；搜索或快照/远古版平铺
        if (TypeTag == "release" && q.Length == 0)
        {
            VersionList.ItemTemplate = (DataTemplate)VersionList.Resources["VersionGroupTemplate"];
            VersionList.ItemsSource = rows
                .GroupBy(r => MajorOf(r.Id))
                .Select(g => new VersionGroupRow { Major = g.Key, Count = g.Count(), Items = g.ToList() })
                .ToList();
        }
        else
        {
            VersionList.ItemTemplate = (DataTemplate)VersionList.Resources["VersionFlatTemplate"];
            VersionList.ItemsSource = rows;
        }

        StaggerVersionRows();
    }

    /// <summary>填充一枚「最新版本」瓦片；无对应版本时整块隐藏</summary>
    private static void ApplyLatestTile(Button tile, Image icon, TextBlock idText, TextBlock metaText,
        ManifestVersion? version)
    {
        if (version is null)
        {
            tile.Visibility = Visibility.Collapsed;
            return;
        }
        tile.Visibility = Visibility.Visible;
        tile.Tag = version.Id;
        icon.Source = VersionRowHelpers.VersionIcon(version.Type);
        idText.Text = version.Id;
        metaText.Text = Loc.T("发布于 {0}", FormatReleaseTime(version.ReleaseTime));
    }

    /// <summary>列表回填后错峰入场（只对已实现的容器生效，前 12 行依次浮现）</summary>
    private void StaggerVersionRows()
    {
        if (!_s.Motion.Enabled) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                for (var i = 0; i < Math.Min(VersionList.Items.Count, 12); i++)
                    if (VersionList.ContainerFromIndex(i) is FrameworkElement container)
                        MotionFx.StaggerIn(container, i);
            }
            catch
            {
                // 静默：入场动效失败不影响列表
            }
        });
    }

    /// <summary>大版本分组键："1.21.1" → "1.21"（取前两段）</summary>
    private static string MajorOf(string id)
    {
        var parts = id.Split('.');
        return parts.Length <= 2 ? id : string.Join('.', parts[..2]);
    }

    /// <summary>指定类型里发布时间最新的一版（ReleaseTime 为 ISO 串，可直接按序比较）</summary>
    private ManifestVersion? NewestOf(string type) =>
        _versions.Where(v => v.Type == type)
                 .OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal)
                 .FirstOrDefault();

    private static string FormatReleaseTime(string releaseTime) =>
        DateTimeOffset.TryParse(releaseTime, out var t)
            ? t.ToString("yyyy/MM/dd HH:mm")
            : releaseTime;

    private static string Describe(ManifestVersion v) =>
        $"{v.ReleaseTime[..Math.Min(10, v.ReleaseTime.Length)]} · {TypeText(v.Type)}";

    private static string TypeText(string type) => type switch
    {
        "snapshot" => Loc.T("快照"),
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Loc.T("正式版"),
    };

    // ════════════ 原版：版本详情（加载器选择） ════════════

    private void VersionList_ItemClick(object s, ItemClickEventArgs e)
    {
        if (_busy || e.ClickedItem is not DownloadRow row) return;
        OpenDetail(row.Id);
    }

    private void VersionGroupItem_Click(object s, ItemClickEventArgs e)
    {
        if (_busy || e.ClickedItem is not DownloadRow row) return;
        OpenDetail(row.Id);
    }

    /// <summary>点「最新版本」瓦片：直接进该版本详情</summary>
    private void LatestTile_Click(object s, RoutedEventArgs e)
    {
        if (_busy || s is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        OpenDetail(id);
    }

    /// <summary>大版本抽屉组头：点击切换展开/收起（高度平滑过渡由 ExpandBehavior 负责，箭头同步旋转）</summary>
    private void VersionGroupHeader_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { DataContext: VersionGroupRow row } header) return;
        row.IsExpanded = !row.IsExpanded;
        if (FindDescendant<FontIcon>(header, "ChevronIcon") is { } chevron)
            SetChevronAngle(chevron, row.IsExpanded ? 90f : 0f, animate: true);
    }

    /// <summary>组头箭头首次进入可视树：按当前展开态摆好角度（收起 = 指向右侧）</summary>
    private void VersionChevron_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FontIcon chevron) return;
        var expanded = (chevron.DataContext as VersionGroupRow)?.IsExpanded == true;
        SetChevronAngle(chevron, expanded ? 90f : 0f, animate: false);
    }

    /// <summary>箭头旋转（合成动画，绕自身中心）</summary>
    private static void SetChevronAngle(FontIcon chevron, float angle, bool animate)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(chevron);
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)chevron.ActualWidth / 2f, (float)chevron.ActualHeight / 2f, 0f);

            if (!animate || !AppServices.Instance.Motion.Enabled)
            {
                visual.StopAnimation("RotationAngleInDegrees");
                visual.RotationAngleInDegrees = angle;
                return;
            }

            var compositor = visual.Compositor;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(220);
            anim.InsertKeyFrame(1f, angle, compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.16f, 1f), new System.Numerics.Vector2(0.3f, 1f)));
            visual.StartAnimation("RotationAngleInDegrees", anim);
        }
        catch
        {
            // 静默：动效失败不影响展开
        }
    }

    /// <summary>在可视子树里按 x:Name 查找元素（DataTemplate 内部控件）</summary>
    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && hit.Name == name) return hit;
            if (FindDescendant<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private void OpenDetail(string id)
    {
        _detailId = id;
        var v = _versions.FirstOrDefault(x => x.Id == id);
        DetailTitle.Text = id;
        DetailMeta.Text = v is null ? "" : Describe(v);
        DetailIcon.Source = VersionRowHelpers.VersionIcon(v?.Type);
        RefreshDetailState();
        ShowDetail(show: true);
        _ = LoadLoaderOptionsAsync(id);
    }

    private void Back_Click(object s, RoutedEventArgs e) => ShowDetail(show: false);

    private void ShowDetail(bool show)
    {
        // 列表 ↔ 详情互相切换时做一次轻微浮入：向前是从下方推进，返回是从上方落回
        if (show)
        {
            ListPanel.Visibility = Visibility.Collapsed;
            MotionFx.Reset(DetailPanel);
            DetailPanel.Visibility = Visibility.Visible;
            MotionFx.FadeSlideIn(DetailPanel, 22f, MotionFx.EnterMs);
        }
        else
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            MotionFx.Reset(ListPanel);
            ListPanel.Visibility = Visibility.Visible;
            MotionFx.FadeSlideIn(ListPanel, -14f, MotionFx.EnterMs);
            _detailId = null;
        }
    }

    private void RefreshDetailState()
    {
        if (_detailId is not { } id) return;
        DetailStateText.Text = _s.Installer.IsInstalled(_s.Settings.DotMinecraft, id)
            ? Loc.T("已安装（重新下载将覆盖）") : "";
    }

    private async Task LoadLoaderOptionsAsync(string id)
    {
        _builds.Clear();
        _picked.Clear();

        // 缓存优先：先渲染本地缓存（离线可用），后台再从下载源刷新
        var dotMc = _s.Settings.DotMinecraft;
        foreach (var kind in LoaderKeys)
        {
            _builds[kind] = LoaderBuildCache.Read(dotMc, LoaderInfo[kind].CacheKind, id);
            _picked[kind] = null;
        }
        if (_detailId == id) RefreshLoaderUI();

        // 并行拉取四家加载器的构建列表（失败时保留缓存或按无处理）
        var forgeTask = SafeBuildsAsync(() => _s.Forge.ListForgeBuildsAsync(id));
        var neoTask = SafeBuildsAsync(() => _s.Forge.ListNeoForgeBuildsAsync(id));
        var fabricTask = SafeFabricBuildsAsync(id);
        var quiltTask = SafeQuiltBuildsAsync(id);
        await Task.WhenAll(forgeTask, neoTask, fabricTask, quiltTask);

        // 详情可能已被切换（用户快速返回再进入），仅当仍是当前详情时才应用
        if (_detailId != id) return;
        ApplyBuilds(LoaderKind.Forge, forgeTask.Result, id);
        ApplyBuilds(LoaderKind.NeoForge, neoTask.Result, id);
        ApplyBuilds(LoaderKind.Fabric, fabricTask.Result, id);
        ApplyBuilds(LoaderKind.Quilt, quiltTask.Result, id);
    }

    /// <summary>应用拉取结果：成功则写缓存并覆盖，失败且无缓存按无处理</summary>
    private void ApplyBuilds(LoaderKind kind, List<LoaderBuild> fetched, string id)
    {
        if (fetched.Count > 0)
        {
            _builds[kind] = fetched;
            LoaderBuildCache.Write(_s.Settings.DotMinecraft, LoaderInfo[kind].CacheKind, id, fetched);
        }
        _builds[kind] ??= [];
        RefreshLoaderUI();
    }

    private static async Task<List<LoaderBuild>> SafeBuildsAsync(Func<Task<List<LoaderBuild>>> f)
    {
        try { return await f(); }
        catch { return []; }
    }

    private async Task<List<LoaderBuild>> SafeFabricBuildsAsync(string id)
    {
        try
        {
            var loaders = await _s.Fabric.ListLoadersAsync(id);
            return loaders
                .Select(l => new LoaderBuild(l.Version, l.Loader.Stable ? null : Loc.T("预览版"), l.Loader.Stable))
                .ToList();
        }
        catch { return []; }
    }

    private async Task<List<LoaderBuild>> SafeQuiltBuildsAsync(string id)
    {
        try
        {
            var loaders = await _s.Fabric.ListQuiltLoadersAsync(id);
            return loaders
                .Select(l => new LoaderBuild(l.Version, l.Loader.Stable ? null : Loc.T("预览版"), l.Loader.Stable))
                .ToList();
        }
        catch { return []; }
    }

    // ════════════ 加载器抽屉渲染与互斥（Forge / NeoForge / Fabric / Quilt 互斥选装） ════════════

    private (Button Rec, TextBlock RecVersion, TextBlock RecNote, Border RecBadge, TextBlock RecBadgeText,
             TextBlock Count, ListView List, TextBlock Empty, TextBlock Status) LoaderControls(
        LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => (ForgeRecBtn, ForgeRecVersion, ForgeRecNote, ForgeRecBadge, ForgeRecBadgeText,
                             ForgeCountText, ForgeBuildList, ForgeEmptyText, ForgeStatus),
        LoaderKind.NeoForge => (NeoForgeRecBtn, NeoForgeRecVersion, NeoForgeRecNote, NeoForgeRecBadge,
                                NeoForgeRecBadgeText, NeoForgeCountText, NeoForgeBuildList,
                                NeoForgeEmptyText, NeoForgeStatus),
        LoaderKind.Quilt => (QuiltRecBtn, QuiltRecVersion, QuiltRecNote, QuiltRecBadge, QuiltRecBadgeText,
                             QuiltCountText, QuiltBuildList, QuiltEmptyText, QuiltStatus),
        _ => (FabricRecBtn, FabricRecVersion, FabricRecNote, FabricRecBadge, FabricRecBadgeText,
              FabricCountText, FabricBuildList, FabricEmptyText, FabricStatus),
    };

    /// <summary>推荐构建：Forge 取推荐/最新标记，其余取最新一项；无则不显示推荐卡</summary>
    private static LoaderBuild? RecommendedOf(LoaderKind kind, List<LoaderBuild> builds) => kind switch
    {
        LoaderKind.Forge => builds.FirstOrDefault(b => b.Note == "推荐")
                         ?? builds.FirstOrDefault(b => b.Note == "最新"),
        _ => builds.FirstOrDefault(),
    };

    /// <summary>统一刷新三个抽屉：推荐卡、构建列表、锁定状态与头部状态文本</summary>
    private void RefreshLoaderUI()
    {
        foreach (var kind in LoaderKeys)
        {
            var (rec, recVersion, recNote, _, recBadgeText, count, list, empty, _) =
                LoaderControls(kind);
            var builds = _builds[kind];
            var picked = _picked[kind];

            if (builds is null)
            {
                rec.Visibility = Visibility.Collapsed;
                count.Text = Loc.T("全部版本");
                list.ItemsSource = null;
                empty.Visibility = Visibility.Collapsed;
                continue;
            }

            if (RecommendedOf(kind, builds) is not { } recBuild)
            {
                rec.Visibility = Visibility.Collapsed;
            }
            else
            {
                rec.Visibility = Visibility.Visible;
                recVersion.Text = recBuild.Version;
                // Note 为 Core 产出的中文标记（"推荐"/"最新"/"最新稳定版"），按「源语言即键」取词
                recNote.Text = recBuild.Note is { } note ? Loc.T(note) : Loc.T("稳定版");
                recBadgeText.Text = picked == recBuild.Version ? Loc.T("已选") : Loc.T("推荐");
            }
            count.Text = Loc.T("全部版本 ({0})", builds.Count);
            list.ItemsSource = builds.Select(b => new LoaderBuildRow(
                b.Version, b.Note is { } n ? Loc.T(n) : (b.Stable ? Loc.T("稳定版") : Loc.T("预览版")),
                picked == b.Version)).ToList();
            empty.Visibility = builds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        RefreshLoaderLocks();
        RefreshLoaderStatus();
    }

    /// <summary>任一加载器选中构建后锁定其余两个（按钮与列表点不动），点击已选项可取消选中解锁</summary>
    private void RefreshLoaderLocks()
    {
        var anyPicked = LoaderKeys.Any(k => _picked[k] is not null);
        foreach (var kind in LoaderKeys)
        {
            var enabled = !anyPicked || _picked[kind] is not null;
            var c = LoaderControls(kind);
            c.Rec.IsEnabled = enabled;
            c.List.IsEnabled = enabled;
        }
    }

    /// <summary>统一刷新三行头部状态文本：将安装 / 与所选加载器互斥 / 可以添加 / 获取中 / 无可用构建</summary>
    private void RefreshLoaderStatus()
    {
        foreach (var kind in LoaderKeys)
        {
            var status = LoaderControls(kind).Status;
            if (_picked[kind] is { } p)
                status.Text = kind == LoaderKind.Fabric ? Loc.T("{0} · 同装 Fabric API", p) : Loc.T("{0} · 将安装", p);
            else if (LoaderKeys.Any(k => _picked[k] is not null))
                status.Text = Loc.T("与所选加载器互斥");
            else if (_builds[kind] is null)
                status.Text = Loc.T("获取中…");
            else if (_builds[kind]!.Count == 0)
                status.Text = Loc.T("无可用构建");
            else
                status.Text = Loc.T("可以添加");
        }
    }

    // ── 抽屉交互 ──
    private void ForgeRec_Click(object s, RoutedEventArgs e) => PickRec(LoaderKind.Forge);
    private void NeoForgeRec_Click(object s, RoutedEventArgs e) => PickRec(LoaderKind.NeoForge);
    private void FabricRec_Click(object s, RoutedEventArgs e) => PickRec(LoaderKind.Fabric);
    private void QuiltRec_Click(object s, RoutedEventArgs e) => PickRec(LoaderKind.Quilt);

    private void PickRec(LoaderKind kind)
    {
        if (_builds[kind] is not { Count: > 0 } builds) return;
        if (RecommendedOf(kind, builds) is not { } rec) return;
        _picked[kind] = _picked[kind] == rec.Version ? null : rec.Version;
        RefreshLoaderUI();
    }

    private void ForgeBuild_Click(object s, ItemClickEventArgs e) =>
        PickBuildRow(LoaderKind.Forge, e.ClickedItem);

    private void NeoForgeBuild_Click(object s, ItemClickEventArgs e) =>
        PickBuildRow(LoaderKind.NeoForge, e.ClickedItem);

    private void FabricBuild_Click(object s, ItemClickEventArgs e) =>
        PickBuildRow(LoaderKind.Fabric, e.ClickedItem);

    private void QuiltBuild_Click(object s, ItemClickEventArgs e) =>
        PickBuildRow(LoaderKind.Quilt, e.ClickedItem);

    private void PickBuildRow(LoaderKind kind, object clicked)
    {
        if (clicked is not LoaderBuildRow row) return;
        _picked[kind] = _picked[kind] == row.Version ? null : row.Version;
        RefreshLoaderUI();
    }

    // ════════════ 原版：开始下载 ════════════

    private async void StartDownload_Click(object s, RoutedEventArgs e)
    {
        if (_detailId is not { } id || _busy) return;
        var url = _versions.FirstOrDefault(v => v.Id == id)?.Url;
        if (url is null)
        {
            ErrorBar.Message = Loc.T("清单中找不到该版本");
            ErrorBar.IsOpen = true;
            return;
        }
        var fabric = _picked.GetValueOrDefault(LoaderKind.Fabric);
        var forge = _picked.GetValueOrDefault(LoaderKind.Forge);
        var neo = _picked.GetValueOrDefault(LoaderKind.NeoForge);
        var quilt = _picked.GetValueOrDefault(LoaderKind.Quilt);

        await RunInstallAsync(async progress =>
        {
            // 同名版本并存：目录已存在时自动改为 -2、-3…，不再覆盖重装
            id = _s.Installer.UniqueVersionId(_s.Settings.DotMinecraft, id);
            progress.Report(new BatchProgress(Loc.T("下载原版 {0}", id), 0, 0, 0, 0, 0, null));
            await _s.Installer.InstallVanillaAsync(id, url, _s.Settings, progress);

            // 加载器所需的 Java 大版本来自版本 JSON（Forge/NeoForge 官方安装器需要）
            var javaMajor = 8;
            try
            {
                var version = await _s.Installer.LoadVersionAsync(_s.Settings.DotMinecraft, id);
                javaMajor = version.JavaVersion?.MajorVersion ?? 8;
            }
            catch { /* 读取失败时按 Java 8 兜底，EnsureJava 会自动补齐 */ }

            if (fabric is not null)
            {
                progress.Report(new BatchProgress(Loc.T("安装 Fabric {0}", fabric), 0, 0, 0, 0, 0, null));
                await _s.Installer.InstallFabricAsync(id, fabric, _s.Settings, progress);

                // 选 Fabric 后自动同装 Fabric API（Modrinth P7dR8mSH），失败不阻塞主安装
                try
                {
                    progress.Report(new BatchProgress(Loc.T("下载 Fabric API"), 0, 0, 0, 0, 0, null));
                    var apiVersions = await _s.Mods.ListVersionsAsync("P7dR8mSH", "fabric", id);
                    if (apiVersions.FirstOrDefault()?.PrimaryFile is { } api)
                    {
                        var apiDest = Path.Combine(_s.Settings.DotMinecraft, "mods", api.Filename);
                        await _s.Download.DownloadFileAsync(api.Url, apiDest, api.Sha1,
                            ct: CancellationToken.None);
                    }
                }
                catch { /* Fabric API 获取失败不影响 Fabric 本体安装 */ }
            }
            if (forge is not null)
            {
                await _s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.ForgeInstallerUrl(id, forge), id, javaMajor, _s.Settings, progress);
            }
            if (neo is not null)
            {
                await _s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.NeoForgeInstallerUrl(id, neo), id, javaMajor, _s.Settings, progress);
            }
            if (quilt is not null)
            {
                progress.Report(new BatchProgress(Loc.T("安装 Quilt {0}", quilt), 0, 0, 0, 0, 0, null));
                await _s.Installer.InstallQuiltAsync(id, quilt, _s.Settings, progress);
            }
        });
    }

    // ════════════ 安装执行（原版与社区共用） ════════════

    private async Task RunInstallAsync(Func<IProgress<BatchProgress>, Task> install)
    {
        _busy = true;
        StartBtn.IsEnabled = false;
        InstallProgress.IsIndeterminate = true;
        InstallProgress.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = false;
        OkBar.IsOpen = false;
        var progress = new Progress<BatchProgress>(p => _dq.TryEnqueue(() =>
        {
            if (p.TotalFiles > 0 || p.TotalBytes > 0)
            {
                InstallProgress.IsIndeterminate = false;
                InstallProgress.Value = p.Percentage;
                ProgressText.Text = Loc.T("{0} {1}% · {2}/{3} 文件 · {4:F0} KB/s",
                    p.Stage, p.Percentage, p.DoneFiles, p.TotalFiles, p.SpeedBytesPerSec / 1024);
            }
            else
            {
                ProgressText.Text = p.Stage;
            }
        }));
        try
        {
            await install(progress);
            OkBar.Message = Loc.T("安装完成");
            OkBar.IsOpen = true;
            _okTimer.Stop();
            _okTimer.Start();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            StartBtn.IsEnabled = true;
            InstallProgress.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            ApplyVersionFilter();
            RefreshDetailState();
        }
    }

    // ════════════ 社区资源 ════════════

    /// <summary>按分区标签取分区元数据；非社区分区（如 "vanilla"）返回全 null 元组。</summary>
    private static (string Tag, string SearchTitle, string Folder, string Verb) SectionInfoFor(string tag) =>
        Sections.FirstOrDefault(s => s.Tag == tag);

    private Task EnsureCommunityAsync() => _communityInitTask ??= InitCommunityAsync();

    private async Task InitCommunityAsync()
    {
        if (_communityInit) return;
        _communityInit = true;

        try { _categories = await _s.Mods.GetCategoriesAsync(); }
        catch { /* 分类获取失败时仅显示“全部” */ }

        _suppress = true;
        try
        {
            RebuildFilterRows();
            // 版本下拉 = 全部游戏大版本（正式版与远古版，不含快照），默认“全部”
            try
            {
                var manifest = await _s.Manifest.GetManifestAsync(false);
                foreach (var v in manifest.Versions.Where(v => v.Type != "snapshot"))
                    VersionCombo.Items.Add(v.Id);
            }
            catch { /* 清单读取失败时仍可自行输入版本 */ }
            VersionCombo.Text = Loc.T("全部");
        }
        finally { _suppress = false; }

        await SearchAsync(reset: true);
    }

    /// <summary>按当前分区重建分类/加载器行与标题（需在 _suppress 期间调用）</summary>
    private void RebuildFilterRows()
    {
        var section = CurrentCategory;
        var info = SectionInfoFor(section);
        // 初始化社区资源期间用户切回原版：社区筛选行保持隐藏，无需重建
        if (info.Tag is null) return;
        SearchTitle.Text = Loc.T(info.SearchTitle);
        LoaderRow.Visibility = section == "mod" ? Visibility.Visible : Visibility.Collapsed;

        CategoryCombo.Items.Clear();
        var cats = _categories?
            .Where(c => c.ProjectType == section)
            .Select(c => c.Name)
            .ToList();
        if (cats is { Count: > 0 })
        {
            CategoryRow.Visibility = Visibility.Visible;
            CategoryCombo.Items.Add(new ComboBoxItem { Content = Loc.T("全部") });
            foreach (var c in cats)
                CategoryCombo.Items.Add(new ComboBoxItem { Content = ChineseCategory(c), Tag = c });
            CategoryCombo.SelectedIndex = 0;
        }
        else
        {
            // 数据包等类型暂无官方分类
            CategoryRow.Visibility = Visibility.Collapsed;
        }
    }

    private string? ResolveGameVersion()
    {
        var text = VersionCombo.Text?.Trim();
        return string.IsNullOrEmpty(text) || text == Loc.T("全部") ? null : text;
    }

    private string? ResolveCategory()
    {
        if (CategoryRow.Visibility != Visibility.Visible) return null;
        return (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    /// <summary>生效的加载器筛选：仅 Mod 分区；“自动检测”按版本 JSON 判断；“全部”不筛选</summary>
    private string? ResolveLoader()
    {
        if (CurrentCategory != "mod") return null;
        var tag = (LoaderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        if (tag is "any") return null;
        if (tag != "auto") return tag;
        var versionId = ResolveGameVersion();
        return versionId is null ? null : DetectLoader(versionId);
    }

    private string? DetectLoader(string versionId)
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(
                _s.Settings.DotMinecraft,
                "versions", versionId, versionId + ".json"));
            if (json.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
            if (json.Contains("quilt_loader", StringComparison.OrdinalIgnoreCase)) return "quilt";
            if (json.Contains("fabricloader", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)) return "fabric";
            if (json.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "forge";
        }
        catch { /* 版本 JSON 缺失或无法识别时按未检测处理 */ }
        return null;
    }

    // ── 社区事件 ──
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = SearchAsync(reset: true);
    }

    private void VersionBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = SearchAsync(reset: true);
    }

    private void SearchBtn_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: true);

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_communityInit) return;
        _suppress = true;
        try
        {
            CommunitySearchBox.Text = "";
            VersionCombo.Text = Loc.T("全部");
            if (CategoryRow.Visibility == Visibility.Visible && CategoryCombo.Items.Count > 0)
                CategoryCombo.SelectedIndex = 0;
            LoaderCombo.SelectedIndex = 0;
            SortCombo.SelectedIndex = 0;
        }
        finally { _suppress = false; }
        _ = SearchAsync(reset: true);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_communityInit || _suppress) return;
        _ = SearchAsync(reset: true);
    }

    // ── 社区搜索 ──
    private async Task SearchAsync(bool reset, bool forceRefresh = false)
    {
        if (!_communityInit) return;
        if (reset) _offset = 0;
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var query = CommunitySearchBox.Text?.Trim() ?? "";
        var section = CurrentCategory;
        var info = SectionInfoFor(section);
        // 已切回原版等非社区分区：本次搜索作废（搜索期间用户随时可能切走，分区信息必须在 await 前定下）
        if (info.Tag is null) return;
        var loader = ResolveLoader();
        var gameVersion = ResolveGameVersion();
        var category = ResolveCategory();
        var index = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "relevance";

        SetBusy(true);
        if (!forceRefresh) CacheBadge.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _s.Mods.SearchAsync(query, section, loader, gameVersion,
                _offset, PageSize, index, category, ct, forceRefresh);
            _total = result.TotalHits;
            _lastHits = result.Hits.ToList();
            var rows = result.Hits.Select(h => new ModRow
            {
                ProjectId = h.ProjectId,
                Title = ModTitleHelper.Display(h.Title),   // 中文名（MC 百科，缓存优先；未命中显示原名）
                Description = h.Description,
                IconUrl = h.IconUrl ?? "",
                Author = h.Author,
                Tags = h.DisplayCategories.Take(3).Select(ChineseCategory).ToList(),
                LoadersText = string.Join(" / ", h.Loaders.Select(DisplayLoader)),
                DownloadsText = $"\u2193 {FormatCount(h.Downloads)}",
                UpdatedText = $"\u2191 {FormatRelative(h.DateModified)}",
                Icon = RemoteIconHelper.Resolve(h.IconUrl),
            }).ToList();
            ModList.ItemsSource = rows;
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultCountText.Text = _total > 0 ? Loc.T("共 {0} 个结果", FormatCount(_total)) : "";
            PrevBtn.IsEnabled = _offset > 0 && rows.Count > 0;
            NextBtn.IsEnabled = _offset + rows.Count < _total && rows.Count > 0;

            var fromCache = _s.Mods.LastSearchFromCache;
            CacheBadge.Visibility = fromCache ? Visibility.Visible : Visibility.Collapsed;

            // 命中缓存 → 后台直连网络拉新内容并替换；失败保留缓存内容、不弹错
            if (fromCache && !forceRefresh)
            {
                SetBusy(false);
                await SearchAsync(reset: false, forceRefresh: true);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!forceRefresh)
            {
                ErrorBar.Message = Loc.T("搜索失败：{0}", ex.Message);
                ErrorBar.IsOpen = true;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        InstallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SearchBtn.IsEnabled = !busy;
        PrevBtn.IsEnabled = !busy && PrevBtn.IsEnabled;
        NextBtn.IsEnabled = !busy && NextBtn.IsEnabled;
    }

    // ── 社区分页 ──
    private void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        _offset = Math.Max(0, _offset - PageSize);
        _ = SearchAsync(reset: false);
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        _offset += PageSize;
        _ = SearchAsync(reset: false);
    }

    // ── 社区行点击 → PCL2 式下载详情窗（点击资源卡片直接打开下载界面，无行内安装按钮） ──
    private void ModRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModRow row || _busy) return;
        var info = SectionInfoFor(CurrentCategory);   // 行只存在于社区分区
        if (info.Tag is null) return;
        var hit = _lastHits.FirstOrDefault(h => h.ProjectId == row.ProjectId);
        if (hit is null) return;

        // 下载目标 = 首页当前选中的版本：弹窗默认按该版本的 MC 版本与加载器筛出兼容文件，
        // 点「下载安装」直接装入其 mods 文件夹（整合包走新建版本流程，无需选中版本）
        var targetId = _s.Settings.LastVersion ?? "";
        var (mc, loader) = ResolveTargetFilters(info.Tag, targetId);
        var dialog = new ContentDownloadDialog(_s, info.Tag, loader, mc,
            targetId, null, hit, InstallMrPackFromDialogAsync);
        dialog.XamlRoot = XamlRoot;
        _ = dialog.ShowAsync();
    }

    /// <summary>弹窗默认筛选：下载页已手动筛选则尊重手动选择；否则跟随主页选中版本的兼容范围。
    /// 原版（vanilla）不筛加载器——Modrinth 没有 vanilla 标签，传入会滤空所有文件。</summary>
    private (string? Mc, string? Loader) ResolveTargetFilters(string kind, string targetId)
    {
        var mc = ResolveGameVersion();
        var loader = kind == "mod" ? ResolveLoader() : null;
        if (string.IsNullOrEmpty(targetId)) return (mc, loader);
        var versionLoader = ContentLibraryService.ResolveLoader(targetId, out _);
        return (mc ?? ContentLibraryService.ResolveMc(targetId),
                loader ?? (kind == "mod" && versionLoader != "vanilla" ? versionLoader : null));
    }

    /// <summary>下载窗内整合包安装入口：转 .mrpack 新建版本流程，完成后在下载页显示结果。</summary>
    private async Task InstallMrPackFromDialogAsync(string libPath)
    {
        var summary = await InstallMrPackAsync(libPath);
        ShowOk(Loc.T("整合包已安装。{0}", summary));
    }

    private void ShowOk(string message)
    {
        OkBar.Message = message;
        OkBar.IsOpen = true;
        _okTimer.Stop();
        _okTimer.Start();
    }

    // ════════════ 智能依赖提示 ════════════

    /// <summary>Mod 装载后检查缺失依赖，提示一键补全</summary>
    private async Task CheckDependenciesAfterInstallAsync(ModrinthVersion version, string versionId)
    {
        var report = await _s.ContentLibrary.CheckDependenciesAsync(version, versionId);
        if (report.Required.Count == 0 && report.IncompatiblePresent.Count == 0) return;

        var parts = new List<string>();
        if (report.Required.Count > 0)
            parts.Add(Loc.T("缺少 {0} 个前置：{1}", report.Required.Count,
                string.Join("、", report.Required.Select(d => d.Title))));
        if (report.IncompatiblePresent.Count > 0)
            parts.Add(Loc.T("与已装不兼容：{0}", string.Join("、", report.IncompatiblePresent)));
        ShowOk(string.Join("；", parts));

        if (report.Required.Count == 0) return;

        var panel = new StackPanel { Spacing = 6, MinWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("是否自动下载并装载以下前置到「{0}」？", versionId),
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var dep in report.Required)
            panel.Children.Add(new TextBlock { Text = "• " + dep.Title, TextWrapping = TextWrapping.Wrap });

        var dlg = new ThemedDialog
        {
            Title = Loc.T("补全前置依赖"),
            Content = panel,
            PrimaryButtonText = Loc.T("一键补全"),
            CloseButtonText = Loc.T("稍后"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var loader = ContentLibraryService.ResolveLoader(versionId, out _);
        var mc = ContentLibraryService.ResolveMc(versionId);
        await ResolveMissingAsync(report.Required, versionId, mc,
            loader == "vanilla" ? null : loader, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>递归下载缺失前置（按 ProjectId 去重，深度 ≤ 2）</summary>
    private async Task ResolveMissingAsync(
        IReadOnlyList<MissingDependency> missing, string versionId, string mc,
        string? loader, int depth, HashSet<string> seen)
    {
        if (depth > 2) return;
        foreach (var dep in missing)
        {
            if (string.IsNullOrEmpty(dep.ProjectId) || !seen.Add(dep.ProjectId)) continue;

            var versions = await _s.Mods.ListVersionsAsync(dep.ProjectId, loader, mc);
            var version = versions.FirstOrDefault(v =>
                !string.Equals(v.VersionType, "alpha", StringComparison.OrdinalIgnoreCase))
                ?? versions.FirstOrDefault();
            if (version?.PrimaryFile is not { } file) continue;

            var entry = await _s.ContentLibrary.DownloadAndAdoptAsync("mod", file.Url, file.Filename,
                file.Sha1, new LibraryAdoptInfo(dep.Title, null, null, dep.ProjectId,
                    version.Id, version.VersionNumber, version.GameVersions, version.Loaders, file.Url));
            await _s.ContentLibrary.InstallToVersionAsync(entry, versionId);

            var nested = await _s.ContentLibrary.CheckDependenciesAsync(version, versionId);
            if (nested.Required.Count > 0)
                await ResolveMissingAsync(nested.Required, versionId, mc, loader, depth + 1, seen);
        }
    }

    // ── 整合包 .mrpack 自动安装 ──
    private async Task<string> InstallMrPackAsync(string mrpackPath)
    {
        var dlg = new ThemedDialog
        {
            Title = Loc.T("安装整合包"),
            Content = CreateInstallPanel(out var statusText, out var bar),
            CloseButtonText = Loc.T("后台安装"),
            XamlRoot = Content.XamlRoot,
        };
        _ = dlg.ShowAsync();

        var progress = new Progress<BatchProgress>(p => _dq.TryEnqueue(() =>
        {
            bar.Value = p.Percentage;
            statusText.Text = p.Stage;
        }));
        var result = await _s.MrPack.InstallAsync(mrpackPath, _s.Settings, progress: progress);

        // 依赖联动：缺失的原版 + 加载器（fabric-loader / forge / neoforge）
        var summary = await MrPackFlow.EnsureGameAndLoaderAsync(result, _s, progress);

        try { dlg.Hide(); } catch { /* 用户已关闭弹窗时忽略 */ }
        return summary;
    }

    private static UIElement CreateInstallPanel(out TextBlock statusText, out ProgressBar bar)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
        statusText = new TextBlock { Text = Loc.T("准备中…"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        bar = new ProgressBar { Maximum = 100 };
        panel.Children.Add(statusText);
        panel.Children.Add(bar);
        return panel;
    }

    // ── 格式化 ──
    private static string FormatCount(long n) => n switch
    {
        >= 100_000_000 => $"{n / 100_000_000.0:0.#}{Loc.T("亿")}",
        >= 10_000 => $"{n / 10_000.0:0.#}{Loc.T("万")}",
        _ => n.ToString("N0", CultureInfo.InvariantCulture),
    };

    private static string FormatRelative(DateTime utc)
    {
        var t = DateTime.UtcNow - utc.ToUniversalTime();
        return t.TotalMinutes < 1 ? Loc.T("刚刚")
            : t.TotalMinutes < 60 ? Loc.T("{0} 分钟前", (int)t.TotalMinutes)
            : t.TotalHours < 24 ? Loc.T("{0} 小时前", (int)t.TotalHours)
            : t.TotalDays < 30 ? Loc.T("{0} 天前", (int)t.TotalDays)
            : t.TotalDays < 365 ? Loc.T("{0} 个月前", (int)(t.TotalDays / 30))
            : Loc.T("{0} 年前", (int)(t.TotalDays / 365));
    }

    private static string ChineseCategory(string slug) =>
        CategoryNames.TryGetValue(slug, out var name) ? Loc.T(name) : slug;

    private static string DisplayLoader(string loader) => loader switch
    {
        "" => "",
        "neoforge" => "NeoForge",
        _ => char.ToUpperInvariant(loader[0]) + loader[1..],
    };
}
