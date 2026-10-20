// 下载页：PCL2 式左侧分类（原版游戏 + 社区资源五类）
// 原版：版本列表（正式版按大版本抽屉折叠）→ 详情（Forge/NeoForge/Fabric 抽屉三选一 + 开始下载）
// 社区：Modrinth 搜索筛选安装（Mod/整合包/数据包/资源包/光影包）
// 移植自桌面 Starcloud.App.Views.DownloadPage（仅做安卓侧适配）：
// - 桌面 DispatcherQueueTimer（6s 收起完成条）→ DispatcherTimer；_dq.TryEnqueue → UiThread.Queue
// - 桌面 ConnectedAnimationService 连通动画在 Uno 侧无可用实现，略去（列表↔详情仍保留 MotionFx 浮入过渡）
// - 版本筛选下拉：Uno ComboBox 无 IsEditable，改用首枚「全部」占位项 + SelectedItem 读取（触屏无自由输入语义）
// - 资源下载窗挂到 Ui.Root（壳注入的对话框宿主）

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Dialogs;
using Starcloud.Launcher.Phone.Views.Rows;
using Starcloud.Launcher.Phone.Views.Shared;
using Starcloud.Core.Models;
using Starcloud.Core.Services;
using System.Globalization;
using System.IO;

namespace Starcloud.Launcher.Phone.Views;

public sealed partial class DownloadPage : Page, IShellPage
{
    private const int PageSize = 20;

    private static readonly Brush InstalledBrush = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);

    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherTimer _okTimer;

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
    /// <summary>各加载器已生成的行（选中状态原地更新，列表不重建）</summary>
    private readonly Dictionary<LoaderKind, List<LoaderBuildRow>> _buildRows = [];

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

    // 中文搜索词翻译：记住「输入原文 → 实际搜索词」，翻页时不重复联网翻译
    private string? _queryOriginal;
    private string _queryEffective = "";

    // 已安装版本集合（LoadVersionsAsync 后台扫描后缓存，ApplyVersionFilter 只读）
    private HashSet<string> _installedIds = new();

    // 上一次平铺列表签名：内容没变就不重建 ItemsSource / 不重演错峰
    private string? _lastListSig;

    public DownloadPage()
    {
        InitializeComponent();
        _okTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _okTimer.Tick += (_, _) => OkBar.IsOpen = false;   // 只在"下载完成"后亮 6 秒就收起
        Loaded += OnLoaded;
    }

    /// <summary>首次 Loaded 已跑完：页面启用导航缓存，之后每次进入由 OnShown 负责刷新。</summary>
    private bool _firstLoadDone;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_firstLoadDone) return; // 缓存实例再次挂树：刷新由 OnShown 负责，避免双份拉清单
        _firstLoadDone = true;
        await LoadVersionsAsync();
    }

    /// <summary>页面被导航显示（实例已缓存复用）：首次进入交给 Loaded，之后的每次进入重拉版本清单
    ///（本安装版本会增减，安装状态徽章随清单一起刷新）。</summary>
    public void OnShown()
    {
        if (!_firstLoadDone) return; // 首次进入交给 Loaded（此刻页面尚未挂树）
        _ = LoadVersionsAsync();
    }

    // ════════════ 分类切换 ════════════

    private string CurrentCategory =>
        (CategoryList.SelectedItem as FrameworkElement)?.Tag as string ?? "vanilla";

    private async void Category_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // XAML 中首个条目 IsSelected="True" 会在 InitializeComponent 解析期间同步触发本事件，
        // 此时右侧面板尚未创建，直接跳过（XAML 已自带正确的初始可见性）
        if (CommunityPanel is null) return;
        var category = CurrentCategory;
        var isVanilla = category == "vanilla";

        // 选中态落到各行控件自己的 IsActive 上：选中底、跟手光斑、圆角都由 CategoryNavItem
        // 的模板提供，三者同源同形，不再出现「阴影框与按钮框错位」
        CatVanilla.IsActive = category == "vanilla";
        CatMod.IsActive = category == "mod";
        CatModpack.IsActive = category == "modpack";
        CatDatapack.IsActive = category == "datapack";
        CatResourcepack.IsActive = category == "resourcepack";
        CatShader.IsActive = category == "shader";
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
            // 已安装集合是磁盘扫描，放后台；ApplyVersionFilter 只读缓存字段
            _installedIds = await Task.Run(() =>
                _s.Manifest.ListInstalled(_s.Settings.DotMinecraft)
                    .Select(i => i.Id).ToHashSet());
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
            anim.Duration = TimeSpan.FromMilliseconds(MotionFx.Scale(260));
            anim.InsertKeyFrame(1f, target, MotionFx.ControlEase(compositor));
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
        var installedIds = _installedIds;
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

        // 全部版本统一平铺（含正式版）：行外观与主页版本行一致
        // 内容没变就不再重建 ItemsSource / 重演错峰：切页回来时列表原样呈现，零开销
        var sig = q + "#" + TypeTag + "#" + string.Join(",", rows.Select(r => r.Id + (r.Badge.Length > 0 ? "*" : "")));
        if (sig != _lastListSig || VersionList.ItemsSource is null)
        {
            _lastListSig = sig;
            VersionList.ItemTemplate = (DataTemplate)VersionList.Resources["VersionFlatTemplate"];
            VersionList.ItemsSource = rows;
            MotionFx.StaggerListItems(VersionList);
        }
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
        PrepareDetailAnimation(VersionList.ContainerFromItem(e.ClickedItem) is FrameworkElement c
            ? FindDescendant<FrameworkElement>(c, "RowIconHost") : null);
        OpenDetail(row.Id);
    }

    /// <summary>点「最新版本」瓦片：直接进该版本详情</summary>
    private void LatestTile_Click(object s, RoutedEventArgs e)
    {
        if (_busy || s is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        PrepareDetailAnimation(s as FrameworkElement);   // 瓦片自身即图标宿主
        OpenDetail(id);
    }

    /// <summary>
    /// 列表 ↔ 详情的连通动画（桌面用框架原生的 ConnectedAnimationService 把图标"飞"到详情头图）。
    /// Uno 侧无对应实现，此处保留调用点、不做动画：两次界面仍由详情面板的 MotionFx 浮入过渡连接，
    /// 动效开关关闭时表现一致。
    /// </summary>
    private void PrepareDetailAnimation(FrameworkElement? source)
    {
        // 安卓侧：连通动画 no-op（见方法注释），导航与浮入过渡不受影响
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
            var rows = _buildRows.TryGetValue(kind, out var cached) ? cached : (_buildRows[kind] = []);
            // 只有"构建列表本身变了"（首次拉取 / 换了 MC 版本）才重建集合；
            // 单纯切换选中项时原地改这一行的状态——列表不重建，徽标即时切换，也不丢滚动位置。
            if (!rows.Select(r => r.Version).SequenceEqual(builds.Select(b => b.Version)))
            {
                rows.Clear();
                rows.AddRange(builds.Select(b => new LoaderBuildRow(
                    b.Version, b.Note is { } n ? Loc.T(n) : (b.Stable ? Loc.T("稳定版") : Loc.T("预览版")),
                    picked == b.Version)));
                list.ItemsSource = null;
                list.ItemsSource = rows;
                MotionFx.StaggerListItems(list, maxItems: 6); // 首次铺开时错峰入场，不做硬切
            }
            else
            {
                for (var i = 0; i < rows.Count; i++) rows[i].Selected = picked == builds[i].Version;
            }
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
        var progress = new Progress<BatchProgress>(p => UiThread.Run(() =>
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
            // 版本下拉 = 全部游戏大版本（正式版与远古版，不含快照），默认“全部”。
            // Uno ComboBox 不可编辑（见 XAML 注释）：先补一枚「全部」占位项承担桌面 Text 直写的语义
            VersionCombo.Items.Add(Loc.T("全部"));
            try
            {
                var manifest = await _s.Manifest.GetManifestAsync(false);
                foreach (var v in manifest.Versions.Where(v => v.Type != "snapshot"))
                    VersionCombo.Items.Add(v.Id);
            }
            catch { /* 清单读取失败时仅剩「全部」= 不筛选 */ }
            VersionCombo.SelectedIndex = 0;
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
        // Uno ComboBox 不可编辑：从选中项取版本，「全部」占位项 = 不筛选
        var text = VersionCombo.SelectedItem as string;
        return string.IsNullOrEmpty(text) || text == Loc.T("全部") ? null : text;
    }

    private string? ResolveCategory()
    {
        // CurseForge 的分类是另一套（名称而非 slug），不参与 Modrinth 的 facets 筛选
        if (CurrentSource == ContentSourceIds.CurseForge) return null;
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
            if (VersionCombo.Items.Count > 0) VersionCombo.SelectedIndex = 0;
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
        UpdateSourceUi();
        _ = SearchAsync(reset: true);
    }

    /// <summary>当前资源来源（modrinth / curseforge）：决定列表搜索与下载窗的默认来源</summary>
    private string CurrentSource =>
        (SourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? ContentSourceIds.Modrinth;

    /// <summary>
    /// 换来源后的界面适配：CurseForge 的分类体系与 Modrinth 的 slug 不同名，分类筛选直接灰显
    /// （比"选了没效果"诚实）；缓存角标只属于 Modrinth 的磁盘缓存。
    /// </summary>
    private void UpdateSourceUi()
    {
        var curseForge = CurrentSource == ContentSourceIds.CurseForge;
        CategoryCombo.IsEnabled = !curseForge;
        CategoryCombo.Opacity = curseForge ? 0.5 : 1;
        if (curseForge) CacheBadge.Visibility = Visibility.Collapsed;
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
            // 中文搜索词先翻成英文：Modrinth / CurseForge 的项目名都是英文，直接搜中文基本搜不到东西。
            // 只有新搜索（reset）或用户改了关键词才翻；翻页沿用上一次的结果，不重复联网。
            if (reset || !string.Equals(_queryOriginal, query, StringComparison.Ordinal))
            {
                var translated = await _s.SearchTranslation.TranslateAsync(query, ct);
                _queryOriginal = query;
                _queryEffective = translated.Effective;
                ShowTranslatedHint(SearchTranslationHint.Describe(translated));
            }
            var searchQuery = _queryEffective;

            // 来源分派：CurseForge 的分类体系与 Modrinth slug 不同名，分类筛选只在 Modrinth 生效
            var fromCurseForge = CurrentSource == ContentSourceIds.CurseForge;
            var result = fromCurseForge
                ? await _s.CurseForge.SearchAsync(searchQuery, section, loader, gameVersion,
                    _offset, PageSize, index, ct)
                : await _s.Mods.SearchAsync(searchQuery, section, loader, gameVersion,
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
            // 英文简介先上屏，MCIM 的中文译文异步补上（仅中文界面；失败保留英文）
            _ = FillSummariesAsync(
                rows, fromCurseForge ? ContentSourceIds.CurseForge : ContentSourceIds.Modrinth, ct);
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultCountText.Text = _total > 0 ? Loc.T("共 {0} 个结果", FormatCount(_total)) : "";
            PrevBtn.IsEnabled = _offset > 0 && rows.Count > 0;
            NextBtn.IsEnabled = _offset + rows.Count < _total && rows.Count > 0;

            var fromCache = !fromCurseForge && _s.Mods.LastSearchFromCache;
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

    /// <summary>把「实际拿去搜索的英文词」与来源贴到结果计数旁边；没翻译就收起</summary>
    private void ShowTranslatedHint(string hint)
    {
        TranslatedHint.Text = hint;
        TranslatedHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// 把搜索结果的英文简介换成 MCIM 的中文译文（仅中文界面启用，见 ModSummaryTranslationService）。
    /// 逐行并行，译文到达即就地替换（ModRow 是 OneWay 绑定）；任何失败都静默保留英文——
    /// 翻译是旁路能力，不该影响搜索结果本身。
    /// </summary>
    private async Task FillSummariesAsync(List<ModRow> rows, string source, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                var zh = await _s.ModSummaries.TranslateAsync(source, row.ProjectId, row.Description, ct);
                if (!string.IsNullOrEmpty(zh)) row.Description = zh!;
            }));
        }
        catch (OperationCanceledException) { }
        catch
        {
            // 翻译失败保留原文
        }
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
            targetId, null, hit, InstallMrPackFromDialogAsync, CurrentSource);
        dialog.XamlRoot = Ui.Root;
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

    /// <summary>
    /// 下载窗内整合包安装入口：按格式分派——.mrpack 走 Modrinth 安装器，
    /// CurseForge 的 .zip（内含 manifest.json）走 CurseForge 安装器；两者共用同一套
    /// 「缺原版/加载器就补装」的联动（<see cref="MrPackFlow.EnsureGameAndLoaderAsync"/>）。
    /// </summary>
    private async Task InstallMrPackFromDialogAsync(string libPath)
    {
        var summary = libPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? await InstallCurseForgePackAsync(libPath)
            : await InstallMrPackAsync(libPath);
        ShowOk(Loc.T("整合包已安装。{0}", summary));
    }

    /// <summary>CurseForge 整合包：解析 manifest.json 逐文件下载，再补装原版与加载器</summary>
    private async Task<string> InstallCurseForgePackAsync(string zipPath)
    {
        var dlg = new ThemedDialog
        {
            Title = Loc.T("安装整合包"),
            Content = CreateInstallPanel(out var statusText, out var bar),
            CloseButtonText = Loc.T("后台安装"),
            XamlRoot = Ui.Root,
        };
        _ = dlg.ShowAsync();

        var status = new Progress<string>(s => UiThread.Run(() => statusText.Text = s));
        var progress = new Progress<BatchProgress>(p => UiThread.Run(() =>
        {
            bar.Value = p.Percentage;
            statusText.Text = p.Stage;
        }));
        var result = await _s.CurseForgePacks.InstallAsync(zipPath, _s.Settings,
            status: status, progress: progress);
        var summary = await MrPackFlow.EnsureGameAndLoaderAsync(result, _s, progress);

        try { dlg.Hide(); } catch { /* 用户已关闭弹窗时忽略 */ }
        return summary;
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

        // 手机版：MinWidth 320 在竖屏弹窗内容区（约 312 宽）里会撑破裁切，改 MaxWidth 交给弹窗自适应
        var panel = new StackPanel { Spacing = 6, MaxWidth = 320 };
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
            XamlRoot = Ui.Root,
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
            XamlRoot = Ui.Root,
        };
        _ = dlg.ShowAsync();

        var progress = new Progress<BatchProgress>(p => UiThread.Run(() =>
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
