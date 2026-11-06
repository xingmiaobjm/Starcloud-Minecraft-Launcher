// 下载资源弹窗（参考 PCL2）：搜索 Modrinth → 项目详情 → 版本芯片筛选 → 分组文件直接安装。
// 两个入口：① 版本详情页的内容分类芯片（Mod / 资源包 / 光影包）——打开即展示搜索列表；
//          ② 下载页点击社区资源卡片——携带 initialHit 直达该项目详情。
// 安装目标：Mod / 资源包 / 光影 / 数据包装入首页选中的版本（数据包进存档）；整合包转新建版本流程。

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Behaviors;
using Starcloud.App.Views.Controls;
using Starcloud.App.Views.Shared;
using Starcloud.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Starcloud.App.Views.Dialogs;

public sealed partial class ContentDownloadDialog : ContentDialog
{
    private readonly AppServices _s;
    private readonly string _kind;        // mod / modpack / datapack / resourcepack / shader
    private readonly string? _loader;     // 搜索加载器筛选（原版为 null，仅 Mod 使用）
    private readonly Func<string, Task>? _installModpack; // 整合包 .mrpack → 新建版本流程（宿主页提供，含结果展示）
    private readonly ModrinthHit? _initialHit; // 下载页入口：携带项目直达详情（不展示搜索列表）
    private readonly string? _mc;         // 目标版本的 MC 版本（null = 不筛选）
    private readonly string _versionId;   // 安装目标版本（首页选中版本；整合包可空）
    private readonly Action? _onInstalled;

    /// <summary>当前资源来源（modrinth / curseforge）：搜索、详情、安装共用同一个来源</summary>
    private string _source = ContentSourceIds.Modrinth;
    private bool _busy;
    private ModrinthHit? _selected;
    private List<ModrinthVersion> _versions = [];
    private string? _chip; // 选中的 MC 版本（null = 全部）

    public ContentDownloadDialog(AppServices state, string kind, string? loader,
        string? mcVersion, string versionId, Action? onInstalled,
        ModrinthHit? initialHit = null, Func<string, Task>? installModpack = null,
        string source = ContentSourceIds.Modrinth)
    {
        InitializeComponent();
        // 底部白底按钮（关闭等）也带跟手阴影：本窗显式指定了 ContentDialog 样式，
        // 不会走 ThemedDialog 的那条路径，所以在这里自己挂一次（见 ThemedDialog.ApplyShadowButtons）
        ThemedDialog.ApplyShadowButtons(this);
        _s = state;
        _kind = kind;
        _loader = loader;
        // MC 版本筛选仅接受标准版本号（1.21.1 / 26.3）；整合包等自定义版本 id 推不出 MC 版本，
        // 传整串会把所有搜索结果/文件全部筛空（表现为「搜索用不了」）
        _mc = NormalizeMcFilter(mcVersion);
        // 副本实例（26.3-2 / 用户自起名）从 id 推不出 MC 版本 → _mc 为 null。
        // 自动补前置在 _mc 为空时会整段跳过（宁可少装不装错），于是「前置显示正常但一个都没装」。
        // 这里从实例 JSON（inheritsFrom 链）补一次，补不上才保持不筛。
        if (_mc is null && !string.IsNullOrWhiteSpace(versionId))
        {
            var detected = LoaderDetector.Detect(state.Settings.DotMinecraft, versionId);
            _mc = NormalizeMcFilter(detected.McVersion);
        }
        _versionId = versionId;
        _onInstalled = onInstalled;
        _initialHit = initialHit;
        _installModpack = installModpack;
        _source = source;

        // 主题钉到窗口实际主题（PopupRoot 不继承 RequestedTheme，见 ThemedDialog）
        Loading += (_, _) =>
        {
            if (XamlRoot?.Content is FrameworkElement root)
                RequestedTheme = root.ActualTheme;
        };

        Title = Loc.T(_kind switch
        {
            "resourcepack" => "下载 资源包",
            "shader" => "下载 光影包",
            "modpack" => "安装 整合包",
            "datapack" => "下载数据包",
            _ => "下载 Mod",
        });
        CloseButtonText = Loc.T("关闭");
        Resources["ContentDialogMaxWidth"] = 1360.0;
        Resources["ContentDialogMaxHeight"] = 980.0;
        // 压缩标题与内容的默认间距，内容区上移（默认 Title 底距 12 + 容器顶距 24 过大）
        Resources["ContentDialogPadding"] = new Thickness(20, 24, 20, 16);
        Resources["ContentDialogTitleMargin"] = new Thickness(0);

        // 下载页行点击入口：直达项目详情；版本详情页入口：打开即展示热门内容
        Loading += async (_, _) =>
        {
            if (_initialHit is not null) await OpenDetailAsync(_initialHit);
            else await RunDlSearchAsync();
        };

        // 安装目标常显：所见即所得（版本 → 具体内容子目录；整合包新建版本）
        if (_kind == "modpack")
        {
            DlTarget.Text = Loc.T("安装方式：直接创建新游戏版本（会自动拉取包内容）");
        }
        else if (string.IsNullOrEmpty(_versionId))
        {
            DlTarget.Text = Loc.T("未选择安装目标：请先在「首页」选择游戏版本");
            DlTarget.Foreground = ThemedBrush("SystemFillColorCautionBrush");
        }
        else
        {
            // 分类落到哪个子目录要写出来：不然用户会以为资源包/光影/数据包都塞进 mods
            var folder = InstanceLayout.ContentDirFor(_kind);
            DlTarget.Text = _kind == "datapack"
                ? Loc.T("安装目标：{0} → saves/<存档>/datapacks", _versionId)
                : Loc.T("安装目标：{0} → {1}/", _versionId, folder);
        }
    }

    // ── 搜索 ──

    /// <summary>按来源分发搜索（两个服务的方法同形，返回同一套 DTO）</summary>
    private Task<ModrinthSearchResult> SearchSourceAsync(string query, bool forceRefresh = false)
    {
        if (_source == ContentSourceIds.CurseForge)
            return _s.CurseForge.SearchAsync(query, _kind, _kind == "mod" ? _loader : null, _mc,
                limit: 24, forceRefresh: forceRefresh);
        return _s.Mods.SearchAsync(query, _kind, _loader, _mc, limit: 24, forceRefresh: forceRefresh);
    }

    /// <summary>按项目的兼容范围拉版本：**不按 MC 版本在服务端筛掉其它版本**——
    /// 全部版本都保留，只在下方分组里把目标 MC 版本的组排到最前。</summary>
    private Task<List<ModrinthVersion>> ListSourceVersionsAsync(ModrinthHit hit) =>
        _source == ContentSourceIds.CurseForge
            ? _s.CurseForge.ListVersionsAsync(hit.ProjectId, _kind == "mod" ? _loader : null, null)
            : _s.Mods.ListVersionsAsync(hit.ProjectId, _kind == "mod" ? _loader : null, null);

    /// <summary>MC 版本筛选规范化：仅标准三段/两段数字版本号有效，其余（整合包 id / 快照码）不筛</summary>
    private static string? NormalizeMcFilter(string? mc) =>
        System.Text.RegularExpressions.Regex.IsMatch(mc ?? "", @"^\d+\.\d+(\.\d+)?$") ? mc : null;

    /// <summary>
    /// 打开弹窗即展示默认（推荐）列表。
    /// <para>**缓存优先、命中就完全不联网**：精确筛选键没有时退到同类资源的通用推荐键
    /// （换新版本/新加载器时精确键必然未命中，不能让用户对着空列表等）。</para>
    /// <para>只有一条缓存都没有（首次安装 / 清了缓存）才发请求。</para>
    /// </summary>
    private async Task RunDlSearchAsync()
    {
        if (_busy) return;
        _busy = true;
        DlEmpty.Visibility = Visibility.Collapsed;
        DownloadDetail.Visibility = Visibility.Collapsed;
        DownloadBack.Visibility = Visibility.Collapsed;   // 返回按钮已移至顶行，随视图切换显隐
        _selected = null;
        try
        {
            var kindFilter = _kind == "mod" ? _loader : null;
            if (TryReadCached(kindFilter, out var cached) && cached is not null)
            {
                RenderResults(cached);
                return;
            }

            Info(Loc.T("正在加载…"), InfoBarSeverity.Informational);
            var res = await SearchSourceAsync("");
            DlInfoBar.IsOpen = false;
            RenderResults(res);
        }
        catch (Exception ex)
        {
            Info(Loc.T("加载失败：{0}", ex.Message), InfoBarSeverity.Error);
        }
        finally { _busy = false; }
    }

    /// <summary>只读缓存，绝不联网</summary>
    private bool TryReadCached(string? loader, out ModrinthSearchResult? result)
    {
        if (_source == ContentSourceIds.CurseForge)
            return _s.CurseForge.TryReadCachedSearch(_kind, loader, _mc, out result);
        return _s.Mods.TryReadCachedSearch(_kind, loader, _mc, out result);
    }

    /// <summary>渲染结果列表：逐条添加 + 从左往右羽化入场</summary>
    private void RenderResults(ModrinthSearchResult res)
    {
        DownloadResults.Children.Clear();
        DownloadResults.Visibility = Visibility.Visible;
        DlEmpty.Visibility = res.Hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var index = 0;
        foreach (var hit in res.Hits)
        {
            var row = BuildResultRow(hit);
            DownloadResults.Children.Add(row);
            // 与好友列表 / 版本列表同一条「从左往右羽化」入场
            MotionFx.FeatherIn(row, index++);
        }
    }

    /// <summary>搜索结果行：图标 + 名称 + 简介 + 下载量/更新时间（点击进详情）。</summary>
    private Border BuildResultRow(ModrinthHit hit)
    {
        var iconBorder = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8),
            Background = ThemedBrush("SubtleFillColorTertiaryBrush"),
        };
        var icon = RemoteIconHelper.Resolve(hit.IconUrl);
        iconBorder.Child = icon is null
            ? new FontIcon { FontFamily = IconFont.Family,  Glyph = "\uEC62", FontSize = 18, Foreground = ThemedBrush("TextFillColorSecondaryBrush") }
            : new Image { Source = icon, Width = 40, Height = 40, Stretch = Stretch.Uniform };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = hit.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        var summary = new TextBlock
        {
            Text = hit.Description, FontSize = 12, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
        };
        text.Children.Add(summary);
        // 英文简介先上屏，MCIM 的中文译文异步补上（仅中文界面；失败保留英文）
        _ = FillSummaryAsync(hit, summary);
        text.Children.Add(new TextBlock
        {
            Text = Loc.T("{0} 下载 · 更新 {1:yyyy-MM-dd}", Count(hit.Downloads), hit.DateModified),
            FontSize = 11,
            Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
        });

        var grid = new Grid { ColumnSpacing = 10 };
        grid.Children.Add(iconBorder);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var card = new Border
        {
            Background = ThemedBrush("SubtleFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Child = grid,
        };
        card.Tapped += (_, _) => _ = OpenDetailAsync(hit);
        return card;
    }

    /// <summary>列表行简介的中文译文（MCIM；仅中文界面启用，失败保留英文）</summary>
    private async Task FillSummaryAsync(ModrinthHit hit, TextBlock target)
    {
        var zh = await _s.ModSummaries.TranslateAsync(hit.Source, hit.ProjectId, hit.Description);
        if (!string.IsNullOrEmpty(zh)) target.Text = zh!;
    }

    /// <summary>详情面板简介的中文译文；期间用户切到别的项目就丢弃这次结果</summary>
    private async Task FillDetailSummaryAsync(ModrinthHit hit)
    {
        var zh = await _s.ModSummaries.TranslateAsync(hit.Source, hit.ProjectId, hit.Description);
        if (string.IsNullOrEmpty(zh) || !ReferenceEquals(_selected, hit)) return;
        DlDesc.Text = zh!;
    }

    /// <summary>打开项目详情：填项目卡 + 拉取版本列表构建芯片与分组。</summary>
    private async Task OpenDetailAsync(ModrinthHit hit)
    {
        _selected = hit;
        DlTitle.Text = ModTitleHelper.Display(hit.Title);   // 中文名（未命中显示原名，后台补翻译）
        DlDesc.Text = hit.Description;
        // 详情简介的中文译文异步补上；期间用户可能已切到别的项目，故只在自己仍被选中时才写回
        _ = FillDetailSummaryAsync(hit);
        var icon = RemoteIconHelper.Resolve(hit.IconUrl);
        if (icon is null)
        {
            DlIcon.Source = null;
            DlIcon.Visibility = Visibility.Collapsed;
            DlIconFallback.Visibility = Visibility.Visible;
        }
        else
        {
            DlIcon.Source = icon;
            DlIcon.Visibility = Visibility.Visible;
            DlIconFallback.Visibility = Visibility.Collapsed;
        }
        DlMeta1.Text = hit.Loaders.Count > 0 ? Loc.T("支持 {0}", string.Join(" / ", hit.Loaders)) : Loc.T("通用");
        DlMeta2.Text = Loc.T("{0} 下载", Count(hit.Downloads));
        DlMeta3.Text = Loc.T("更新 {0:yyyy-MM-dd}", hit.DateModified);
        DlMeta4.Text = Loc.T("来源 {0}", ContentSourceIds.Display(_source));

        DownloadResults.Visibility = Visibility.Collapsed;
        DlEmpty.Visibility = Visibility.Collapsed;
        DownloadDetail.Visibility = Visibility.Visible;
        DownloadBack.Visibility = Visibility.Visible;

        DlDepsSection.Visibility = Visibility.Collapsed;   // 前置资源重置（拉到后再显示）
        DlDepsList.Children.Clear();
        DlVersionChips.Children.Clear();
        DlVersionList.Children.Clear();
        DlVersionList.Children.Add(new TextBlock
        {
            Text = Loc.T("正在加载版本…"), FontSize = 12,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
        });
        try
        {
            _versions = await ListSourceVersionsAsync(hit);
        }
        catch { _versions = []; }
        BuildChips();
        if (_kind == "mod") _ = LoadDependenciesAsync(hit);
        _ = UpdateTitleWhenTranslatedAsync(hit);
    }

    /// <summary>标题翻译解析完成后即时更新（用户停留期间由英文名切换为中文名）。</summary>
    private async Task UpdateTitleWhenTranslatedAsync(ModrinthHit hit)
    {
        var zh = await ModTitleHelper.GetAsync(hit.Title);
        if (zh is null || !ReferenceEquals(_selected, hit)) return;
        if (string.Equals(DlTitle.Text, hit.Title, StringComparison.Ordinal))
            DlTitle.Text = zh;
    }

    /// <summary>
    /// 详情页「前置资源」：取最近若干版本的必需依赖并集，按 PCL 样式画成一列卡片
    /// （图标 + 名称 + 简介 + 下载量/更新时间/来源）。无论自动安装开关是否开启都展示。
    /// 结果回到时若用户已切走本项目则放弃；失败静默。
    /// </summary>
    private async Task LoadDependenciesAsync(ModrinthHit hit)
    {
        try
        {
            var ids = _versions.Take(8)
                .SelectMany(v => v.Dependencies)
                .Where(d => d.DependencyType == "required" && d.ProjectId is not null)
                .Select(d => d.ProjectId!)
                .Where(id => id != hit.ProjectId)
                .Distinct().Take(5).ToList();
            if (ids.Count == 0) return;

            if (_source == ContentSourceIds.CurseForge)
            {
                var titles = await _s.CurseForge.GetProjectTitlesAsync(ids);
                if (!ReferenceEquals(_selected, hit)) return;
                foreach (var id in ids)
                    if (titles.TryGetValue(id, out var title))
                        DlDepsList.Children.Add(BuildDependencyCard(title, null, null, 0, null, "CurseForge"));
            }
            else
            {
                var projects = await _s.Mods.GetProjectsAsync(ids);
                if (!ReferenceEquals(_selected, hit)) return;
                foreach (var p in projects.OrderBy(p => ids.IndexOf(p.Id)))
                    DlDepsList.Children.Add(BuildDependencyCard(
                        ModTitleHelper.Display(p.Title), p.Description, p.IconUrl,
                        p.Downloads, p.Updated, "Modrinth"));
            }
            DlDepsSection.Visibility = DlDepsList.Children.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { /* 依赖展示失败不影响主流程 */ }
    }

    /// <summary>一张前置资源卡（PCL 同款信息密度：图标、名称、简介、下载量/更新时间/来源）</summary>
    private Border BuildDependencyCard(string title, string? description, string? iconUrl,
        long downloads, DateTimeOffset? updated, string source)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBox = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8),
            Background = ThemedBrush("SubtleFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconGrid = new Grid();
        var fallback = new FontIcon
        {
            FontFamily = IconFont.Family, Glyph = "\uEC62", FontSize = 18,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconGrid.Children.Add(fallback);
        if (RemoteIconHelper.Resolve(iconUrl) is { } imageSource)
        {
            var image = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform, Source = imageSource };
            iconGrid.Children.Add(image);
            fallback.Visibility = Visibility.Collapsed;
        }
        iconBox.Child = iconGrid;
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(description))
            text.Children.Add(new TextBlock
            {
                Text = description, FontSize = 11, MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            });
        var meta = new List<string>();
        if (downloads > 0) meta.Add(Loc.T("{0} 下载", Count(downloads)));
        if (updated is not null) meta.Add(Loc.T("更新 {0:yyyy-MM-dd}", updated.Value));
        meta.Add(source);
        text.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", meta), FontSize = 11,
            Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Background = ThemedBrush("SubtleFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Child = grid,
        };
    }

    private void DownloadBack_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        DownloadDetail.Visibility = Visibility.Collapsed;
        DownloadResults.Visibility = Visibility.Visible;
    }

    // ── 版本芯片与分组 ──

    /// <summary>构建版本芯片行：全部 + 各 MC 版本（默认选中当前版本的 MC 版本）。</summary>
    private void BuildChips()
    {
        var versions = _versions.SelectMany(v => v.GameVersions).Distinct()
            .Where(g => g.Split('.').All(s => int.TryParse(s, out _)))
            .OrderByDescending(VersionKey).Take(14).ToList();
        // 默认「全部」：目标版本的组靠排序置顶，而不是把其它版本筛掉
        _chip = null;

        DlVersionChips.Children.Clear();
        AddChip(Loc.T("全部"), null);
        foreach (var v in versions) AddChip(v, v);
        SelectChip(_chip);
    }

    private void AddChip(string label, string? gameVersion)
    {
        var chip = new Button
        {
            Style = (Style)Resources["ChipStyle"],
            Tag = gameVersion ?? "",
            Content = new TextBlock { Text = label },
        };
        chip.Click += (_, _) => SelectChip(gameVersion);
        // 芯片是透明底（ChipStyle）：自动光斑会被"纯透明底不铺深色渐变"的规则跳过，
        // 所以只给它悬停缩放的反馈，与其它卡片同一套手感。
        CardHoverBehavior.SetIsEnabled(chip, true);
        DlVersionChips.Children.Add(chip);
    }

    /// <summary>
    /// 给代码构建的行套上页面同款的悬停语言：圆角底 + 悬停高亮层 + 跟手光斑。
    /// <para>弹窗里的文件行是代码拼出来的，够不到 XAML 里那套附加属性，所以按同一形态手工挂一次；
    /// 图层顺序与版本页的行一致：底色 → 高亮层 → 跟手光斑 → 内容。</para>
    /// </summary>
    private Grid WrapHoverRow(UIElement content)
    {
        var host = new Grid
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemedBrush("ScRowShadeBrush"),
        };
        var hover = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemedBrush("ControlFillColorDefaultBrush"),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        // 跟手光斑：内联径向渐变（与页面同款六档），底色由 HoverShadowBehavior 按主题重写
        var glow = new Border { CornerRadius = new CornerRadius(8), Opacity = 0, IsHitTestVisible = false };
        var glowBrush = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.5, 0.5),
            GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RadiusX = 0.9,
            RadiusY = 0.9,
        };
        var glowStops = new (byte A, double Offset)[]
        {
            (0x28, 0), (0x1F, 0.18), (0x16, 0.40), (0x0D, 0.62), (0x05, 0.82), (0x00, 1),
        };
        foreach (var (a, offset) in glowStops)
            glowBrush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(a, 0, 0, 0), Offset = offset });
        glow.Background = glowBrush;
        host.Children.Add(hover);
        host.Children.Add(glow);
        // 内容层：内边距放在这一层，让内容内缩而 hover/glow 铺满整行；
        // 若内边距直接加在 host 上，hover/glow 会被排布到内边距里、比行小一圈。
        var padded = new Grid { Padding = new Thickness(10, 6, 10, 6) };
        padded.Children.Add(content);
        host.Children.Add(padded);

        HoverShadowBehavior.SetIsEnabled(host, true);
        HoverShadowBehavior.SetCarrier(host, glow);
        RowHoverBehavior.SetIsEnabled(host, true);
        RowHoverBehavior.SetOverlay(host, hover);
        return host;
    }

    private void SelectChip(string? gameVersion)
    {
        _chip = gameVersion;
        foreach (var chip in DlVersionChips.Children.OfType<Button>())
            StyleSelected(chip, string.Equals((string)chip.Tag, gameVersion ?? "", StringComparison.Ordinal));
        BuildVersionList();
    }

    /// <summary>
    /// 按「加载器 + MC 版本」分组（PCL 口径）：组标题如 `NeoForge 1.21.1`，组内每个文件一行，
    /// 点整行直接下载安装。目标 MC 版本的组排最前，其余按 MC 版本降序——不是只显示目标版本。
    /// </summary>
    private void BuildVersionList()
    {
        DlVersionList.Children.Clear();
        if (_selected is null) return;

        var items = _versions
            .Where(v => _chip is null || v.GameVersions.Contains(_chip))
            .ToList();
        if (items.Count == 0)
        {
            DlVersionList.Children.Add(new TextBlock
            {
                Text = Loc.T("该版本下没有可用文件"), FontSize = 12,
                Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            });
            return;
        }

        var groups = items
            .SelectMany(v => GroupKeysOf(v).Select(k => (Key: k, Version: v)))
            .GroupBy(x => x.Key)
            .Select(g => (Key: g.Key, Versions: g.Select(x => x.Version)
                .DistinctBy(v => v.Id).ToList()))
            .OrderByDescending(g => string.Equals(g.Key.Mc, _mc, StringComparison.Ordinal))
            .ThenByDescending(g => VersionKey(g.Key.Mc))
            .ThenBy(g => LoaderRank(g.Key.Loader))
            .ToList();

        var first = true;
        foreach (var (key, versions) in groups)
        {
            var expander = BuildLoaderGroup(key, versions);
            if (first)
            {
                expander.IsExpanded = true;   // 第一组（最新兼容）默认展开
                first = false;
            }
            DlVersionList.Children.Add(expander);
        }
        for (var i = 0; i < DlVersionList.Children.Count; i++)
            if (DlVersionList.Children[i] is FrameworkElement row)
                MotionFx.StaggerIn(row, i, stepMs: 20, maxStagger: 6);
    }

    private readonly record struct LoaderMc(string Loader, string Mc);

    /// <summary>一个文件支持的（加载器, MC 版本）组合；无元数据时给 unknown / ? 保证仍能列出来。</summary>
    private static IEnumerable<LoaderMc> GroupKeysOf(ModrinthVersion v)
    {
        var loaders = v.Loaders.Count > 0 ? v.Loaders : ["unknown"];
        var mcs = v.GameVersions.Count > 0 ? v.GameVersions : ["?"];
        foreach (var l in loaders)
            foreach (var mc in mcs)
                yield return new LoaderMc(l, mc);
    }

    private static int LoaderRank(string loader) => loader.ToLowerInvariant() switch
    {
        "fabric" => 0,
        "neoforge" => 1,
        "forge" => 2,
        "quilt" => 3,
        _ => 4,
    };

    /// <summary>一个「加载器 + MC 版本」分组：组标题 + 组内每个版本一行（点整行下载）。</summary>
    private Expander BuildLoaderGroup(LoaderMc key, List<ModrinthVersion> versions)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(new TextBlock
        {
            Text = $"{DisplayLoader(key.Loader)} {key.Mc}",
            FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var ordered = versions
            .OrderByDescending(v => v.VersionType == "release")
            .ThenByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue)
            .ToList();
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("{0} 个文件", ordered.Count), FontSize = 11,
            Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var panel = new StackPanel { Spacing = 4 };
        foreach (var version in ordered)
            panel.Children.Add(BuildVersionLine(version, key));

        return new Expander
        {
            Header = header,
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    /// <summary>
    /// 一个文件一行：标题 `<项目名> <版本号> for <加载器> <MC版本>`（规范命名），
    /// 副行给出加载器 / 文件名 / 大小 / 更新时间；**整行点击即下载安装**（不再放按钮）。
    /// </summary>
    private FrameworkElement BuildVersionLine(ModrinthVersion version, LoaderMc key)
    {
        var primary = version.PrimaryFile;
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = $"{_selected!.Title} {version.VersionNumber} for {DisplayLoader(key.Loader)} {key.Mc}",
            FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var meta = new List<string> { DisplayLoader(key.Loader) };
        if (primary is not null)
        {
            meta.Add(primary.Filename);
            meta.Add(Size(primary.Size));
        }
        if (version.VersionType is "beta" or "alpha")
            meta.Add(version.VersionType == "beta" ? Loc.T("测试版") : Loc.T("早期测试版"));
        if (version.DatePublished is { } published)
            meta.Add(Loc.T("更新于 {0:yyyy-MM-dd}", published));
        info.Children.Add(new TextBlock
        {
            Text = string.Join(", ", meta), FontSize = 11,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // 行内状态文本：安装/保存进行中就地把结果写在这行，不弹额外对话框
        var stateText = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
            Visibility = Visibility.Collapsed,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stateText, 1);
        grid.Children.Add(stateText);

        var host = WrapHoverRow(grid);
        host.Tapped += (_, e) =>
        {
            e.Handled = true;
            if (_busy || primary is null) return;
            // 点击整行 = 直接下载安装到目标版本（不进保存对话框：那是「另存为」，走右键菜单）
            void SetState(string text, bool interactive)
            {
                stateText.Text = text;
                stateText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                host.Opacity = interactive ? 1 : 0.6;
            }
            _ = InstallAsync(_selected!, version, primary, SetState);
        };

        // 另存为：右键菜单（需要把文件落到自己指定位置时才用）
        var saveAs = new MenuFlyoutItem { Text = Loc.T("另存为…") };
        saveAs.Click += (_, _) =>
        {
            if (_busy || primary is null) return;
            _ = SaveAsAsync(primary, (_, _) => { }, version);
        };
        var menu = new MenuFlyout();
        menu.Items.Add(saveAs);
        host.ContextFlyout = menu;
        return host;
    }

    // ── 安装与跳转 ──

    /// <summary>下载入库并安装：Mod/资源包/光影 → 首页选中版本；数据包 → 选存档装入；整合包 → 新建版本流程。</summary>
    private async Task InstallAsync(ModrinthHit hit, ModrinthVersion version, ModrinthFile file,
        Action<string, bool> setState)
    {
        // 目标版本校验（整合包 .mrpack 走新建版本流程，无需选中版本）
        var verb = Loc.T(_kind == "modpack" ? "安装" : "下载安装");
        if (_kind != "modpack" &&
            (string.IsNullOrEmpty(_versionId) || !_s.Installer.IsInstalled(_s.Settings.DotMinecraft, _versionId)))
        {
            Info(Loc.T("请先在「首页」选择要安装到的游戏版本。"), InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;
        _busy = true;
        setState(Loc.T("下载中…"), false);
        try
        {
            // 装之前先查前置：缺必需依赖时明确列出「缺哪些」，由用户决定要不要一起装。
            // 之前是装完才在结果条里一句「缺少必需依赖」，用户已经切走了，等于没提示。
            var installDeps = false;
            List<MissingDependency> silentDeps = [];   // 加载器自带的基础 API：不问，直接补
            DependencyReport? depReport = null;
            if (_kind == "mod")
            {
                // 上游漏登记兜底：同项目最近几个版本声明过的必需前置，这个版本没写时也要算进来。
                // 实测案例：createaddition fabric-1.20.1-1.3.4 的 dependencies 是空数组（1.3.3 有建 Create），
                // 只按它自己的清单判 → 缺 Create 却报「安装成功」。左侧「前置资源」卡片取的是并集，
                // 两边口径必须一致，否则用户看到卡片有依赖、结果条却说没问题。
                var extraRequired = _versions.Take(8)
                    .SelectMany(v => v.Dependencies)
                    .Where(d => d.DependencyType == "required" && d.ProjectId is not null)
                    .Select(d => d.ProjectId!)
                    .Where(id => !string.Equals(id, version.ProjectId, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                try
                {
                    depReport = await _s.ContentLibrary.CheckDependenciesAsync(
                        version, _versionId, extraRequired: extraRequired);
                }
                catch { /* 依赖检查失败按无前置处理，不阻断安装 */ }

                if (depReport is { Required.Count: > 0 })
                {
                    // 加载器自带的基础 API（Fabric API）不参与询问：装加载器时已经一并装过，
                    // 玩家眼里它不是"前置"，是环境的一部分。万一真缺就静默补上，不打扰。
                    var baseApis = depReport.Required.Where(d => IsLoaderProvided(d.ProjectId)).ToList();
                    var askList = depReport.Required.Where(d => !IsLoaderProvided(d.ProjectId)).ToList();
                    if (baseApis.Count > 0) silentDeps.AddRange(baseApis);

                    if (askList.Count > 0)
                    {
                        var choice = await AskDependenciesAsync(askList);
                        if (choice is null)
                        {
                            setState(verb, true);   // 用户取消：什么都不装
                            return;
                        }
                        installDeps = choice.Value;
                    }
                }
            }

            // CurseForge 的文件 id 不是 Modrinth 版本 id：记 null，避免更新检查拿它去问 Modrinth
            var info = new LibraryAdoptInfo(hit.Title, hit.IconUrl, hit.Author, hit.ProjectId,
                _source == ContentSourceIds.CurseForge ? null : version.Id,
                version.VersionNumber, version.GameVersions, version.Loaders, file.Url, _source);
            var entry = await _s.ContentLibrary.DownloadAndAdoptAsync(_kind, file.Url, file.Filename, file.Sha1, info);

            // 整合包（.mrpack / CurseForge .zip）：点「安装」直接建版本，不再让用户在
            // 导入对话框里再确认一次（下载入库只是安装的输入，对用户是「安装」一个动作）
            if (_kind == "modpack" &&
                (file.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
                 file.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                Hide();
                var libPath = Path.Combine(_s.ContentLibrary.DirFor("modpack"), entry.FileName);
                await _installModpack!(libPath);
                return;
            }

            // 数据包 → 首页选中版本的存档；其余类别 → 直接装入选中版本
            if (_kind == "datapack")
            {
                var save = await PickSaveAsync(_versionId);
                if (save is null)
                {
                    setState(verb, true);
                    return;
                }
                await _s.ContentLibrary.InstallToSaveAsync(entry, _versionId, save);
            }
            else
            {
                await _s.ContentLibrary.InstallToVersionAsync(entry, _versionId);
            }

            var msg = Loc.T("{0} 已安装到 {1}。", file.Filename, _versionId);

            // 光影需要光影加载器（Iris / OptiFine）：目标版本没有就明确提示，
            // 否则用户挂了光影、进游戏发现没效果（版本详情页早就有这个提示，下载页补齐口径）
            if (_kind == "shader" && !_s.ContentLibrary.HasShaderLoader(_versionId))
            {
                msg += " " + Loc.T("⚠ 该版本未检测到光影加载器（Iris / OptiFine），"
                    + "光影包不会生效——请先给这个版本安装 Iris 或 OptiFine。");
            }
            if (_kind == "mod")
            {
                // 版本对不上要先说清楚：把 1.20.1 的 mod 装进 26.3 的实例，进游戏只会报错，
                // 而「前置装不上」往往也是这个原因（前置按主文件的版本去找）
                if (_mc is { Length: > 0 } targetMc && version.GameVersions.Count > 0 &&
                    !version.GameVersions.Contains(targetMc, StringComparer.OrdinalIgnoreCase))
                {
                    var fileMc = version.GameVersions.FirstOrDefault(v => v.Count(c => c == '.') >= 1 && !v.Contains('-'))
                                 ?? version.GameVersions[0];
                    msg += " " + Loc.T("⚠ 这个文件是给 {0} 的，当前实例是 {1}——装了不会生效，建议换匹配版本或另建实例。",
                        fileMc, targetMc);
                }
                // 前置：用户在上面选了「自动安装」才装；否则把缺的列在结果条里
                List<string> autoInstalled = [];
                // 基础 API（Fabric API）不询问、直接补：它是加载器环境的一部分
                if (silentDeps.Count > 0)
                    autoInstalled.AddRange(await InstallExplicitDependenciesAsync(silentDeps));
                if (installDeps)
                {
                    autoInstalled = await AutoInstallDependenciesAsync(version, _versionId);
                    if (LastDependencyFailures.Count > 0)
                        msg += " " + Loc.T("⚠ 以下前置没找到匹配版本，需要手动安装：{0}（多半是版本或加载器对不上）。",
                            string.Join("、", LastDependencyFailures.Distinct(StringComparer.OrdinalIgnoreCase)));
                }
                if (autoInstalled.Count > 0)
                    msg += " " + Loc.T("已自动安装前置：{0}。", string.Join("、", autoInstalled));
                // 装完再核一遍：上面只报了「找不到版本」，这里兜住「装了但没被识别」
                //（哈希查不到身份 / 落到别的目录 / 文件名冲突被跳过），别让用户以为齐了
                if (installDeps)
                {
                    try
                    {
                        var after = await _s.ContentLibrary.CheckDependenciesAsync(version, _versionId);
                        var still = after.Required.Select(d => d.Title)
                            .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
                        if (still.Count > 0)
                            msg += " " + Loc.T("⚠ 仍有前置未就位：{0}。", string.Join("、", still));
                    }
                    catch { /* 复核失败不改变安装结果 */ }
                }
                if (!installDeps && depReport is { Required.Count: > 0 })
                {
                    var stillMissing = depReport.Required
                        .Where(d => !IsLoaderProvided(d.ProjectId))
                        .Select(d => d.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    if (stillMissing.Count > 0)
                        msg += " " + Loc.T("缺少必需依赖：{0}。", string.Join("、", stillMissing));
                }
            }
            Info(msg, InfoBarSeverity.Success);
            setState(Loc.T("✓ 已安装"), false);   // 已装好，定格为完成态防重复安装
            _onInstalled?.Invoke();
        }
        catch (Exception ex)
        {
            Info(Loc.T("下载失败：{0}", ex.Message), InfoBarSeverity.Error);
            setState(verb, true);
        }
        finally { _busy = false; }
    }

    // ── 前置自动安装 ──

    private TaskCompletionSource<bool?>? _depChoice;

    /// <summary>
    /// 列出缺失的必需前置，问用户要不要一起装。
    /// 返回 true = 自动安装前置，false = 只装本体，null = 取消整个安装。
    /// <para>为什么用同窗遮罩而不是 ContentDialog：本窗自己就是 ContentDialog，
    /// 同一个 XamlRoot 不能同时开两个，第二个 ShowAsync 会直接抛异常。</para>
    /// </summary>
    private async Task<bool?> AskDependenciesAsync(IReadOnlyList<MissingDependency> required)
    {
        _depChoice = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 逐项一行：图标 + 名称 + 「最低要求 / 将安装」 + 右侧状态（缺失 / 已安装）。
        // 与参考稿一致：玩家在按按钮之前就能看清会装哪些、装哪个版本。
        DepPromptList.Children.Clear();
        var ids = required.Select(r => r.ProjectId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<ModrinthProject> projects = [];
        try { projects = await _s.Mods.GetProjectsAsync(ids); } catch { /* 图标/简介取不到不影响清单 */ }

        foreach (var dep in required)
        {
            if (string.IsNullOrWhiteSpace(dep.Title)) continue;
            var project = projects.FirstOrDefault(p =>
                string.Equals(p.Id, dep.ProjectId, StringComparison.OrdinalIgnoreCase));

            // 「将安装」= 点自动安装时真正会落地的那个版本（与补前置同一套挑选逻辑）
            string? willInstall = null;
            if (!string.IsNullOrEmpty(dep.ProjectId))
            {
                try
                {
                    var v = await FindLatestCompatibleAsync(dep.ProjectId, DepFallbackMc());
                    willInstall = v is null ? null : $"{dep.Title} {v.VersionNumber}";
                }
                catch { /* 解析失败时只显示最低要求 */ }
            }

            DepPromptList.Children.Add(BuildDependencyPromptRow(
                ModTitleHelper.Display(project?.Title ?? dep.Title),
                project?.IconUrl,
                dep.VersionId is { Length: > 0 } ? dep.Title : null,
                willInstall));
        }

        DepPrompt.Visibility = Visibility.Visible;
        try { return await _depChoice.Task; }
        finally
        {
            DepPrompt.Visibility = Visibility.Collapsed;
            DepPromptList.Children.Clear();
            _depChoice = null;
        }
    }

    /// <summary>补前置用的 MC 版本：主文件声明的版本优先（实例版本可能与之不符）</summary>
    private string? DepFallbackMc() => _mc;

    /// <summary>
    /// 加载器自带的基础 API：安装加载器时启动器已经一并装过（下载页装完 Fabric 会自动补 Fabric API），
    /// 所以它们不该出现在「缺少前置」询问里；万一真缺就静默补上，不打扰玩家。
    /// </summary>
    private static readonly HashSet<string> LoaderProvidedProjects =
        new(StringComparer.OrdinalIgnoreCase) { "P7dR8mSH" };   // Fabric API

    private static bool IsLoaderProvided(string? projectId) =>
        projectId is { Length: > 0 } id && LoaderProvidedProjects.Contains(id);

    /// <summary>按清单装指定项目（基础 API 用；失败留痕但不打扰用户）</summary>
    private async Task<List<string>> InstallExplicitDependenciesAsync(IReadOnlyList<MissingDependency> deps)
    {
        var done = new List<string>();
        foreach (var dep in deps)
        {
            if (string.IsNullOrEmpty(dep.ProjectId)) continue;
            try
            {
                var v = await FindLatestCompatibleAsync(dep.ProjectId, DepFallbackMc());
                if (v?.PrimaryFile is not { } f) continue;
                var info = new LibraryAdoptInfo(dep.Title, null, null, dep.ProjectId,
                    _source == ContentSourceIds.CurseForge ? null : v.Id,
                    v.VersionNumber, v.GameVersions, v.Loaders, f.Url, _source);
                var entry = await _s.ContentLibrary.DownloadAndAdoptAsync("mod", f.Url, f.Filename, f.Sha1, info);
                await _s.ContentLibrary.InstallToVersionAsync(entry, _versionId);
                done.Add(dep.Title);
            }
            catch (Exception ex)
            {
                LauncherLog.Write("deps", $"基础 API 安装失败：{dep.Title} — {ex.Message}");
            }
        }
        return done;
    }

    /// <summary>依赖清单的一行（参考稿样式）：图标 / 名称 / 最低要求 / 将安装 / 状态</summary>
    private Border BuildDependencyPromptRow(string title, string? iconUrl,
        string? requiredVersion, string? willInstall)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
            Background = ThemedBrush("SubtleFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var resolved = RemoteIconHelper.Resolve(iconUrl);
        iconBox.Child = resolved is null
            ? new FontIcon
            {
                FontFamily = IconFont.Family, Glyph = "\uEC62", FontSize = 16,
                Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            }
            : new Image { Source = resolved, Width = 36, Height = 36, Stretch = Stretch.UniformToFill };
        grid.Children.Add(iconBox);

        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(requiredVersion))
            texts.Children.Add(new TextBlock
            {
                Text = Loc.T("最低要求：{0}", requiredVersion), FontSize = 12,
                Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        texts.Children.Add(new TextBlock
        {
            Text = willInstall is null ? Loc.T("将安装：未找到匹配版本") : Loc.T("将安装：{0}", willInstall),
            FontSize = 12,
            Foreground = willInstall is null
                ? ThemedBrush("SystemFillColorCautionBrush")
                : ThemedBrush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var status = new TextBlock
        {
            Text = willInstall is null ? Loc.T("需手动") : Loc.T("缺失"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = willInstall is null
                ? ThemedBrush("SystemFillColorCautionBrush")
                : ThemedBrush("TextFillColorTertiaryBrush"),
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        return new Border { Child = grid };
    }

    private void DepPromptAuto_Click(object sender, RoutedEventArgs e) => _depChoice?.TrySetResult(true);

    private void DepPromptSkip_Click(object sender, RoutedEventArgs e) => _depChoice?.TrySetResult(false);

    private void DepPromptCancel_Click(object sender, RoutedEventArgs e) => _depChoice?.TrySetResult(null);

    /// <summary>
    /// 自动补齐必需前置：从主文件开始 BFS，缺什么装什么，每个都挑与目标 MC 版本 / 加载器
    /// 匹配的最新版本（release 优先、再按发布时间倒序）。最多装 12 个，防病态依赖链。
    /// 失败静默 —— 自动补依赖不该把主安装标成失败。
    /// </summary>
    private async Task<List<string>> AutoInstallDependenciesAsync(
        ModrinthVersion version, string versionId)
    {
        var installed = new List<string>();
        // 不知道目标 MC 版本时不自动装：宁可少装，也不要把 1.20 的前置装进 1.21 的版本
        if (string.IsNullOrWhiteSpace(_mc)) return installed;
        // 前置按「主文件自己的 MC 版本」找，而不是一律按目标实例的版本：
        // 实例 26.3 里装了 1.20.1 的 mod 时（版本不匹配），按 26.3 去找 Create 的构建
        // 一个都找不到 → 前置永远装不上、还不报错。用主文件的版本至少能和主文件配套。
        var depMc = version.GameVersions.Contains(_mc, StringComparer.OrdinalIgnoreCase)
            ? _mc
            : version.GameVersions.FirstOrDefault(v =>
                System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+\.\d+(\.\d+)?$")) ?? _mc;
        var failed = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ModrinthVersion>();
        queue.Enqueue(version);
        while (queue.Count > 0 && installed.Count < 12)
        {
            var current = queue.Dequeue();
            DependencyReport report;
            try { report = await _s.ContentLibrary.CheckDependenciesAsync(current, versionId); }
            catch { continue; }

            foreach (var missing in report.Required)
            {
                if (string.IsNullOrEmpty(missing.ProjectId) || !visited.Add(missing.ProjectId))
                    continue;
                try
                {
                    var depVersion = await FindLatestCompatibleAsync(missing.ProjectId, depMc);
                    var depFile = depVersion?.PrimaryFile;
                    if (depVersion is null || depFile is null)
                    {
                        // 找不到「该 MC 版本 + 该加载器」的前置版本：多半是版本不匹配，
                        // 以前这里直接 continue，用户只看到「前置没装上」却没有任何线索
                        LauncherLog.Write("deps",
                            $"前置未找到可比配版本：{missing.Title}（{missing.ProjectId}），" +
                            $"目标 MC {depMc ?? "任意"}，加载器 {_loader ?? "任意"}");
                        failed.Add(missing.Title);
                        continue;
                    }

                    var info = new LibraryAdoptInfo(
                        missing.Title, null, null, missing.ProjectId,
                        _source == ContentSourceIds.CurseForge ? null : depVersion.Id,
                        depVersion.VersionNumber, depVersion.GameVersions, depVersion.Loaders,
                        depFile.Url, _source);
                    var entry = await _s.ContentLibrary.DownloadAndAdoptAsync(
                        "mod", depFile.Url, depFile.Filename, depFile.Sha1, info);
                    await _s.ContentLibrary.InstallToVersionAsync(entry, versionId);
                    installed.Add(missing.Title);
                    queue.Enqueue(depVersion);   // 前置自己也可能有前置
                }
                catch (Exception ex)
                {
                    // 单个前置失败不影响其它，但要留痕：静默失败会让「前置没装上」变成玄学
                    LauncherLog.Write("deps", $"前置安装失败：{missing.Title} — {ex.Message}");
                    failed.Add(missing.Title);
                }
            }
        }
        LastDependencyFailures = failed;
        return installed;
    }

    /// <summary>上一轮自动补前置里没装上的（供结果条如实告知，别报「已自动安装」却缺件）</summary>
    private List<string> LastDependencyFailures { get; set; } = [];

    /// <summary>挑与目标版本匹配的最新文件：先用服务端筛选，落地前再本地核对一遍。</summary>
    private async Task<ModrinthVersion?> FindLatestCompatibleAsync(string projectId, string? mcOverride = null)
    {
        var mc = mcOverride ?? _mc;
        List<ModrinthVersion> versions;
        try
        {
            versions = _source == ContentSourceIds.CurseForge
                ? await _s.CurseForge.ListVersionsAsync(projectId, _loader, mc)
                : await _s.Mods.ListVersionsAsync(projectId, _loader, mc);
        }
        catch { return null; }

        return versions
            .Where(v => v.PrimaryFile is not null)
            .Where(v => mc is null || v.GameVersions.Count == 0 || v.GameVersions.Contains(mc))
            .Where(v => _loader is null || v.Loaders.Count == 0 ||
                        v.Loaders.Contains(_loader, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    // ── 另存为 ──

    /// <summary>目标版本的 mods 目录（与「下载安装」同一套隔离判定，所以落点一致）。</summary>
    private string ResolveTargetModsDir(string versionId)
    {
        var dotMc = _s.Settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _s.Settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        return Path.Combine(gameRoot, "mods");
    }

    /// <summary>
    /// 另存为：弹系统保存对话框（初始目录定位到目标版本的 mods 文件夹），把文件下到用户选的位置。
    /// 与「下载安装」的区别：不进内容库、不写版本目录 —— 用户自己决定放哪。
    /// </summary>
    private async Task SaveAsAsync(ModrinthFile file, Action<string, bool> setState,
        ModrinthVersion? version = null)
    {
        const string idle = "另存为…";
        try
        {
            // 目标版本以首页选中为准；_versionId 由宿主页在打开弹窗时传入。
            // 为空时再兜底读一次设置，避免把 mods 建到当前工作目录下。
            var versionId = string.IsNullOrWhiteSpace(_versionId)
                ? (_s.Settings.LastVersion ?? "") : _versionId;
            if (string.IsNullOrWhiteSpace(versionId))
                throw new InvalidOperationException("还没有选择游戏版本：请先在首页选择一个版本，再另存 Mod。");

            var modsDir = ResolveTargetModsDir(versionId);
            Directory.CreateDirectory(modsDir);
            LauncherLog.Write("download", $"另存 Mod 初始目录：{modsDir}（版本 {versionId}）");

            // 初始目录定位到目标版本的 mods 文件夹；系统缺 FileSaveDialog 组件时
            // 内部自动退回 comdlg32 旧式对话框（初始目录同样生效）
            var ext = Path.GetExtension(file.Filename);
            var target = SystemSaveFileDialog.Show(
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow),
                Loc.T("另存 Mod"), file.Filename,
                string.IsNullOrEmpty(ext) ? "jar" : ext.TrimStart('.'),
                modsDir);
            if (target is null) return;

            // 选完位置再核对一次兼容性：用户可能手动切到了别的版本的 mods 目录，
            // 能用路径认出版本就按那个版本判断，认不出按首页选中的版本判断。
            if (version is not null)
            {
                var targetVersionId = VersionIdFromModsPath(target) ?? versionId;
                if (CompatibilityWarning(version, targetVersionId) is { } warning &&
                    !await ConfirmIncompatibleAsync(warning))
                    return;
            }

            // 重复安装拦截（一）：目标目录已有同名文件
            if (File.Exists(target))
            {
                Info(Loc.T("同名文件已存在，已阻止重复下载：{0}", Path.GetFileName(target)),
                    InfoBarSeverity.Warning);
                return;
            }

            setState(Loc.T("下载中…"), false);
            // 先下到临时文件：解析出 jar 里的 modId，再决定放不放行
            var temp = Path.Combine(Path.GetTempPath(),
                "sc-mod-" + Guid.NewGuid().ToString("N")[..8] + ".jar");
            try
            {
                await _s.Download.DownloadFileAsync(file.Url, temp,
                    string.IsNullOrEmpty(file.Sha1) ? null : file.Sha1);

                // 重复安装拦截（二）：同一 smods 目录已有同一个 modId（不同版本也算重复）
                var incoming = Starcloud.Core.Services.ModConflictService.Read(temp);
                var targetDir = Path.GetDirectoryName(target) ?? modsDir;
                if (!string.IsNullOrWhiteSpace(incoming.ModId) && Directory.Exists(targetDir))
                {
                    foreach (var existing in Directory.EnumerateFiles(targetDir))
                    {
                        if (!existing.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                        var info = Starcloud.Core.Services.ModConflictService.Read(existing);
                        if (!string.Equals(info.ModId, incoming.ModId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        Info(Loc.T("这个 Mod 已经装过了（{0}），同一实例不重复下载、也不再装不同版本。",
                            Path.GetFileName(existing)), InfoBarSeverity.Warning);
                        return;
                    }
                }

                File.Move(temp, target, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 清理失败不影响 */ }
            }
            Info(Loc.T("已保存到 {0}。", target), InfoBarSeverity.Success);
            NotificationCenter.Instance.Info(Loc.T("Mod 已另存：{0}", Path.GetFileName(target)));
        }
        catch (Exception ex)
        {
            Info(Loc.T("另存失败：{0}", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            setState(Loc.T(idle), true);
        }
    }

    /// <summary>从 …\versions\&lt;id&gt;\mods\&lt;file&gt; 反推版本 id；不是这个形状返回 null。</summary>
    private static string? VersionIdFromModsPath(string filePath)
    {
        var mods = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (mods is null || !string.Equals(Path.GetFileName(mods), "mods", StringComparison.OrdinalIgnoreCase))
            return null;
        var versionDir = Path.GetDirectoryName(mods);
        var versionsDir = versionDir is null ? null : Path.GetDirectoryName(versionDir);
        if (versionsDir is null ||
            !string.Equals(Path.GetFileName(versionsDir), "versions", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetFileName(versionDir);
    }

    /// <summary>文件与目标版本不匹配时返回一句人话说明；匹配或推不出目标版本返回 null。</summary>
    private string? CompatibilityWarning(ModrinthVersion version, string versionId)
    {
        var mc = _mc ?? ContentLibraryService.ResolveMc(versionId);
        var loader = _loader;
        if (string.IsNullOrWhiteSpace(loader))
        {
            var resolved = ContentLibraryService.ResolveLoader(versionId, out _);
            loader = string.Equals(resolved, "vanilla", StringComparison.OrdinalIgnoreCase) ? null : resolved;
        }

        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(mc) && version.GameVersions.Count > 0 &&
            !version.GameVersions.Contains(mc))
            problems.Add(Loc.T("游戏版本 {0}", mc));
        if (!string.IsNullOrWhiteSpace(loader) && version.Loaders.Count > 0 &&
            !version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
            problems.Add(Loc.T("加载器 {0}", loader));
        if (problems.Count == 0) return null;

        return Loc.T("这个文件不适用于当前版本（{0}）：它支持 {1} / {2}。仍要保存吗？",
            string.Join(" + ", problems),
            version.GameVersions.Count > 0 ? string.Join(", ", version.GameVersions) : Loc.T("未知"),
            version.Loaders.Count > 0 ? string.Join(", ", version.Loaders) : Loc.T("通用"));
    }

    private async Task<bool> ConfirmIncompatibleAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.T("版本不匹配"),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = Loc.T("仍要保存"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>弹窗选择数据包的装载存档；无存档或取消返回 null</summary>
    private async Task<string?> PickSaveAsync(string versionId)
    {
        var dotMc = _s.Settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _s.Settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var saves = _s.Saves.ListSaves(gameRoot, versionId, isolated);

        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("把数据包装载到「{0}」的哪个存档？", versionId),
            TextWrapping = TextWrapping.Wrap,
        });

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        if (saves.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("该版本没有存档，请先创建世界。"),
                Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var s in saves)
                combo.Items.Add(new ComboBoxItem { Content = s.LevelName, Tag = s.FolderName });
            combo.SelectedIndex = 0;
            panel.Children.Add(combo);
        }

        var dlg = new ThemedDialog
        {
            Title = Loc.T("装载到存档"),
            Content = panel,
            PrimaryButtonText = Loc.T("装载"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,   // 挂在下载窗自身所在的可视树上（此时下载窗仍打开）
        };
        var result = await dlg.ShowAsync();
        if (saves.Count == 0 || result != ContentDialogResult.Primary) return null;
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private void DlModrinth_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } hit) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = hit.WebUrl is { Length: > 0 } url
                ? url
                : $"https://modrinth.com/project/{hit.Slug}",
            UseShellExecute = true,
        });
    }

    private void DlWiki_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } hit) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(hit.Title)}",
            UseShellExecute = true,
        });
    }

    private void DlCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } hit) return;
        var pkg = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        pkg.SetText(hit.Title);
        Clipboard.SetContent(pkg);
        Info(Loc.T("已复制名称。"), InfoBarSeverity.Success);
    }

    // ── 工具 ──

    private void Info(string msg, InfoBarSeverity severity)
    {
        DlInfoBar.Message = msg;
        DlInfoBar.Severity = severity;
        DlInfoBar.IsOpen = true;
    }

    /// <summary>芯片选中态：accent 实底 + 黑字加粗；未选中交回 ChipStyle 默认配色。</summary>
    private void StyleSelected(Button b, bool selected)
    {
        if (selected)
        {
            b.Background = ThemedBrush("AccentFillColorDefaultBrush");
            b.Foreground = ThemedBrush("TextOnAccentFillColorPrimaryBrush");
            b.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        }
        else
        {
            b.ClearValue(Button.BackgroundProperty);
            b.ClearValue(Button.ForegroundProperty);
            b.ClearValue(Button.FontWeightProperty);
        }
    }

    /// <summary>按对话框实际主题取主题字典画刷：先查 App 覆盖（Light/Dark），再查 WinUI 默认
    /// （XamlControlsResources 的 Default=Dark 惯例），最后回退 Application 索引器。</summary>
    private Brush ThemedBrush(string key)
    {
        var dictKeys = ActualTheme == ElementTheme.Dark ? new[] { "Dark", "Default" } : new[] { "Light" };
        var root = Application.Current.Resources;
        foreach (var dictKey in dictKeys)
        {
            if (root.ThemeDictionaries.TryGetValue(dictKey, out var d) && d is ResourceDictionary rd &&
                rd.TryGetValue(key, out var v) && v is Brush br)
                return br;
            foreach (var md in root.MergedDictionaries)
                if (md.ThemeDictionaries.TryGetValue(dictKey, out var d2) && d2 is ResourceDictionary rd2 &&
                    rd2.TryGetValue(key, out var v2) && v2 is Brush br2)
                    return br2;
        }
        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>加载器显示名（slug → 标题式大小写）。</summary>
    private static string DisplayLoader(string l) => l switch
    {
        "neoforge" => "NeoForge",
        "" => l,
        _ => char.ToUpperInvariant(l[0]) + l[1..],
    };

    /// <summary>下载量格式化：1.2 亿 / 3.5 万。</summary>
    private static string Count(long n) => n switch
    {
        >= 100_000_000 => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##} 亿", n / 100_000_000.0),
        >= 10_000 => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} 万", n / 10_000.0),
        _ => n.ToString(),
    };

    /// <summary>文件大小格式化。</summary>
    private static string Size(long bytes) => bytes switch
    {
        >= 1 << 20 => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} MB", bytes / 1048576.0),
        >= 1024 => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} KB", bytes / 1024.0),
        _ => $"{bytes} B",
    };

    /// <summary>MC 版本排序键（如 26.2 / 1.21.4），数字段逐位比较。</summary>
    private static (int, int, int) VersionKey(string v)
    {
        var parts = v.Split('.');
        int P(int i) => parts.Length > i && int.TryParse(parts[i], out var n) ? n : 0;
        return (P(0), P(1), P(2));
    }
}
