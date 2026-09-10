// 下载页：PCL2 式左侧分类（原版游戏 + 社区资源五类）
// 原版：版本列表（正式版按大版本抽屉折叠）→ 详情（Forge/NeoForge/Fabric 抽屉三选一 + 开始下载）
// 社区：Modrinth 搜索筛选安装（Mod/整合包/数据包/资源包/光影包）

using System.Globalization;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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

    // ── 详情页加载器（Forge/NeoForge/Fabric 抽屉三选一） ──
    private enum LoaderKind { Forge, NeoForge, Fabric }

    private static readonly LoaderKind[] LoaderKeys =
        [LoaderKind.Forge, LoaderKind.NeoForge, LoaderKind.Fabric];

    private static readonly Dictionary<LoaderKind, (string Name, string CacheKind)> LoaderInfo = new()
    {
        [LoaderKind.Forge] = ("Forge", "forge"),
        [LoaderKind.NeoForge] = ("NeoForge", "neoforge"),
        [LoaderKind.Fabric] = ("Fabric", "fabric"),
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

    public DownloadPage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        _okTimer = _dq.CreateTimer();
        _okTimer.Interval = TimeSpan.FromSeconds(6);
        _okTimer.Tick += (_, _) => OkBar.IsOpen = false;
        Loaded += async (_, _) => await LoadVersionsAsync();
    }

    // ════════════ 分类切换 ════════════

    private string CurrentCategory =>
        (CategoryList.SelectedItem as FrameworkElement)?.Tag as string ?? "vanilla";

    private void Category_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // XAML 中首个条目 IsSelected="True" 会在 InitializeComponent 解析期间同步触发本事件，
        // 此时右侧面板尚未创建，直接跳过（XAML 已自带正确的初始可见性）
        if (CommunityPanel is null) return;
        var isVanilla = CurrentCategory == "vanilla";
        VanillaPanel.Visibility = isVanilla ? Visibility.Visible : Visibility.Collapsed;
        CommunityPanel.Visibility = isVanilla ? Visibility.Collapsed : Visibility.Visible;
        if (isVanilla) return;

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

    private string TypeTag => (TypeCombo.SelectedItem as FrameworkElement)?.Tag as string ?? "release";

    private void TypeCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (VersionList is null) return;
        ApplyVersionFilter();
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
                installed ? "已安装" : "", installed ? InstalledBrush : null);
        }).ToList();

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
    }

    /// <summary>大版本分组键："1.21.1" → "1.21"（取前两段）</summary>
    private static string MajorOf(string id)
    {
        var parts = id.Split('.');
        return parts.Length <= 2 ? id : string.Join('.', parts[..2]);
    }

    private static string Describe(ManifestVersion v) =>
        $"{v.ReleaseTime[..Math.Min(10, v.ReleaseTime.Length)]} · {TypeText(v.Type)}";

    private static string TypeText(string type) => type switch
    {
        "snapshot" => "快照",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => "正式版",
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

    private void OpenDetail(string id)
    {
        _detailId = id;
        DetailTitle.Text = id;
        DetailMeta.Text = _versions.FirstOrDefault(v => v.Id == id) is { } v ? Describe(v) : "";
        RefreshDetailState();
        ShowDetail(show: true);
        _ = LoadLoaderOptionsAsync(id);
    }

    private void Back_Click(object s, RoutedEventArgs e) => ShowDetail(show: false);

    private void ShowDetail(bool show)
    {
        ListPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) _detailId = null;
    }

    private void RefreshDetailState()
    {
        if (_detailId is not { } id) return;
        DetailStateText.Text = _s.Installer.IsInstalled(_s.Settings.DotMinecraft, id)
            ? "已安装（重新下载将覆盖）" : "";
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

        // 并行拉取三家加载器的构建列表（失败时保留缓存或按无处理）
        var forgeTask = SafeBuildsAsync(() => _s.Forge.ListForgeBuildsAsync(id));
        var neoTask = SafeBuildsAsync(() => _s.Forge.ListNeoForgeBuildsAsync(id));
        var fabricTask = SafeFabricBuildsAsync(id);
        await Task.WhenAll(forgeTask, neoTask, fabricTask);

        // 详情可能已被切换（用户快速返回再进入），仅当仍是当前详情时才应用
        if (_detailId != id) return;
        ApplyBuilds(LoaderKind.Forge, forgeTask.Result, id);
        ApplyBuilds(LoaderKind.NeoForge, neoTask.Result, id);
        ApplyBuilds(LoaderKind.Fabric, fabricTask.Result, id);
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
                .Select(l => new LoaderBuild(l.Version, l.Loader.Stable ? null : "预览版", l.Loader.Stable))
                .ToList();
        }
        catch { return []; }
    }

    // ════════════ 加载器抽屉渲染与互斥（Forge / NeoForge / Fabric 三选一） ════════════

    private (Button Rec, TextBlock RecVersion, TextBlock RecNote, Border RecBadge, TextBlock RecBadgeText,
             TextBlock Count, ListView List, TextBlock Empty, TextBlock Status) LoaderControls(
        LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => (ForgeRecBtn, ForgeRecVersion, ForgeRecNote, ForgeRecBadge, ForgeRecBadgeText,
                             ForgeCountText, ForgeBuildList, ForgeEmptyText, ForgeStatus),
        LoaderKind.NeoForge => (NeoForgeRecBtn, NeoForgeRecVersion, NeoForgeRecNote, NeoForgeRecBadge,
                                NeoForgeRecBadgeText, NeoForgeCountText, NeoForgeBuildList,
                                NeoForgeEmptyText, NeoForgeStatus),
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
                count.Text = "全部版本";
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
                recNote.Text = recBuild.Note ?? "稳定版";
                recBadgeText.Text = picked == recBuild.Version ? "已选" : "推荐";
            }
            count.Text = $"全部版本 ({builds.Count})";
            list.ItemsSource = builds.Select(b => new LoaderBuildRow(
                b.Version, b.Note ?? (b.Stable ? "稳定版" : "预览版"), picked == b.Version)).ToList();
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
                status.Text = kind == LoaderKind.Fabric ? $"{p} · 同装 Fabric API" : $"{p} · 将安装";
            else if (LoaderKeys.Any(k => _picked[k] is not null))
                status.Text = "与所选加载器互斥";
            else if (_builds[kind] is null)
                status.Text = "获取中…";
            else if (_builds[kind]!.Count == 0)
                status.Text = "无可用构建";
            else
                status.Text = "可以添加";
        }
    }

    // ── 抽屉交互 ──
    private void ForgeRec_Click(object s, RoutedEventArgs e) => PickRec(LoaderKind.Forge);
    private void NeoForgeRec_Click(object s, RoutedEventArgs e) => PickRec(LoaderKind.NeoForge);
    private void FabricRec_Click(object s, RoutedEventArgs e) => PickRec(LoaderKind.Fabric);

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
            ErrorBar.Message = "清单中找不到该版本";
            ErrorBar.IsOpen = true;
            return;
        }
        var fabric = _picked.GetValueOrDefault(LoaderKind.Fabric);
        var forge = _picked.GetValueOrDefault(LoaderKind.Forge);
        var neo = _picked.GetValueOrDefault(LoaderKind.NeoForge);

        await RunInstallAsync(async progress =>
        {
            progress.Report(new BatchProgress($"下载原版 {id}", 0, 0, 0, 0, 0, null));
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
                progress.Report(new BatchProgress($"安装 Fabric {fabric}", 0, 0, 0, 0, 0, null));
                await _s.Installer.InstallFabricAsync(id, fabric, _s.Settings, progress);

                // 选 Fabric 后自动同装 Fabric API（Modrinth P7dR8mSH），失败不阻塞主安装
                try
                {
                    progress.Report(new BatchProgress("下载 Fabric API", 0, 0, 0, 0, 0, null));
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
                    ForgeInstallerService.NeoForgeInstallerUrl(neo), id, javaMajor, _s.Settings, progress);
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
                ProgressText.Text = $"{p.Stage} {p.Percentage}% · {p.DoneFiles}/{p.TotalFiles} 文件 · " +
                                    $"{p.SpeedBytesPerSec / 1024:F0} KB/s";
            }
            else
            {
                ProgressText.Text = p.Stage;
            }
        }));
        try
        {
            await install(progress);
            OkBar.Message = "安装完成";
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

    private (string Tag, string SearchTitle, string Folder, string Verb) SectionInfo =>
        Sections.FirstOrDefault(s => s.Tag == CurrentCategory);

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
            VersionCombo.Text = "全部";
        }
        finally { _suppress = false; }

        await SearchAsync(reset: true);
    }

    /// <summary>按当前分区重建分类/加载器行与标题（需在 _suppress 期间调用）</summary>
    private void RebuildFilterRows()
    {
        var section = CurrentCategory;
        SearchTitle.Text = SectionInfo.SearchTitle;
        LoaderRow.Visibility = section == "mod" ? Visibility.Visible : Visibility.Collapsed;

        CategoryCombo.Items.Clear();
        var cats = _categories?
            .Where(c => c.ProjectType == section)
            .Select(c => c.Name)
            .ToList();
        if (cats is { Count: > 0 })
        {
            CategoryRow.Visibility = Visibility.Visible;
            CategoryCombo.Items.Add(new ComboBoxItem { Content = "全部" });
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
        return string.IsNullOrEmpty(text) || text == "全部" ? null : text;
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
            VersionCombo.Text = "全部";
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
            var verb = SectionInfo.Verb;
            var rows = result.Hits.Select(h => new ModRow
            {
                ProjectId = h.ProjectId,
                Title = h.Title,
                Description = h.Description,
                Tags = h.DisplayCategories.Take(3).Select(ChineseCategory).ToList(),
                LoadersText = string.Join(" / ", h.Loaders.Select(DisplayLoader)),
                DownloadsText = $"\u2193 {FormatCount(h.Downloads)}",
                UpdatedText = $"\u2191 {FormatRelative(h.DateModified)}",
                Icon = string.IsNullOrEmpty(h.IconUrl) ? null : new BitmapImage(new Uri(h.IconUrl)),
                ButtonText = verb,
            }).ToList();
            ModList.ItemsSource = rows;
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultCountText.Text = _total > 0 ? $"共 {FormatCount(_total)} 个结果" : "";
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
                ErrorBar.Message = $"搜索失败：{ex.Message}";
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

    // ── 社区安装 ──
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ModRow row || _busy) return;
        var info = SectionInfo;
        var versionId = ResolveGameVersion();   // 可为 null（全部）
        var loader = ResolveLoader();

        _busy = true;
        btn.IsEnabled = false;
        InstallProgress.IsIndeterminate = true;
        InstallProgress.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = false;
        try
        {
            var versions = await _s.Mods.ListVersionsAsync(row.ProjectId, loader, versionId);
            var version = versions.FirstOrDefault();
            if (version?.PrimaryFile is not { } file)
                throw new InvalidOperationException(
                    versionId is null ? "没有可用文件版本"
                        : loader is null ? $"没有兼容 {versionId} 的文件版本"
                        : $"没有兼容 {versionId}（{loader}）的文件版本");

            var dest = Path.Combine(_s.Settings.DotMinecraft, info.Folder, file.Filename);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await _s.Download.DownloadFileAsync(file.Url, dest, file.Sha1, ct: CancellationToken.None);

            if (info.Tag == "modpack" && file.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            {
                var summary = await InstallMrPackAsync(dest);
                OkBar.Message = $"{file.Filename} 已安装。{summary}";
            }
            else
            {
                OkBar.Message = $"{file.Filename} 已安装到 {info.Folder}" +
                                (versionId is null ? "" : $"（{versionId}）");
            }
            OkBar.IsOpen = true;
            _okTimer.Stop();
            _okTimer.Start();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"安装失败：{ex.Message}";
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            btn.IsEnabled = true;
            InstallProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ── 整合包 .mrpack 自动安装 ──
    private async Task<string> InstallMrPackAsync(string mrpackPath)
    {
        var dlg = new ContentDialog
        {
            Title = "安装整合包",
            Content = CreateInstallPanel(out var statusText, out var bar),
            CloseButtonText = "后台安装",
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
        var game = result.GameVersion;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(game))
        {
            if (!_s.Installer.IsInstalled(_s.Settings.DotMinecraft, game))
            {
                var entry = (await _s.Manifest.GetManifestAsync(false)).Versions
                    .FirstOrDefault(v => v.Id == game);
                if (entry is not null)
                    await _s.Installer.InstallVanillaAsync(game, entry.Url, _s.Settings, progress);
            }
            parts.Add($"Minecraft {game}");

            var javaMajor = 8;
            try
            {
                var v = await _s.Installer.LoadVersionAsync(_s.Settings.DotMinecraft, game);
                javaMajor = v.JavaVersion?.MajorVersion ?? 8;
            }
            catch { /* 读取失败按 Java 8 兜底，安装器会自动补齐运行时 */ }

            if (result.Dependencies.TryGetValue("fabric-loader", out var fab))
            {
                await _s.Installer.InstallFabricAsync(game, fab, _s.Settings, progress);
                parts.Add($"Fabric {fab}");
            }
            if (result.Dependencies.TryGetValue("forge", out var forge))
            {
                await _s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.ForgeInstallerUrl(game, forge), game, javaMajor, _s.Settings, progress);
                parts.Add($"Forge {forge}");
            }
            if (result.Dependencies.TryGetValue("neoforge", out var neo))
            {
                await _s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.NeoForgeInstallerUrl(neo), game, javaMajor, _s.Settings, progress);
                parts.Add($"NeoForge {neo}");
            }
        }

        try { dlg.Hide(); } catch { /* 用户已关闭弹窗时忽略 */ }
        return parts.Count > 0
            ? "已联动安装 " + string.Join(" + ", parts)
            : $"共下载 {result.DownloadedFiles} 个文件";
    }

    private static UIElement CreateInstallPanel(out TextBlock statusText, out ProgressBar bar)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
        statusText = new TextBlock { Text = "准备中…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        bar = new ProgressBar { Maximum = 100 };
        panel.Children.Add(statusText);
        panel.Children.Add(bar);
        return panel;
    }

    // ── 格式化 ──
    private static string FormatCount(long n) => n switch
    {
        >= 100_000_000 => $"{n / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{n / 10_000.0:0.#}万",
        _ => n.ToString("N0", CultureInfo.InvariantCulture),
    };

    private static string FormatRelative(DateTime utc)
    {
        var t = DateTime.UtcNow - utc.ToUniversalTime();
        return t.TotalMinutes < 1 ? "刚刚"
            : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} 分钟前"
            : t.TotalHours < 24 ? $"{(int)t.TotalHours} 小时前"
            : t.TotalDays < 30 ? $"{(int)t.TotalDays} 天前"
            : t.TotalDays < 365 ? $"{(int)(t.TotalDays / 30)} 个月前"
            : $"{(int)(t.TotalDays / 365)} 年前";
    }

    private static string ChineseCategory(string slug) =>
        CategoryNames.TryGetValue(slug, out var name) ? name : slug;

    private static string DisplayLoader(string loader) => loader switch
    {
        "" => "",
        "neoforge" => "NeoForge",
        _ => char.ToUpperInvariant(loader[0]) + loader[1..],
    };
}
