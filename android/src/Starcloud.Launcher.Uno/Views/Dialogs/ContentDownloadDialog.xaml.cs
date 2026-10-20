// 下载资源弹窗（参考 PCL2）：搜索 Modrinth → 项目详情 → 版本芯片筛选 → 分组文件直接安装。
// 两个入口：① 版本详情页的内容分类芯片（Mod / 资源包 / 光影包）——打开即展示搜索列表；
//          ② 下载页点击社区资源卡片——携带 initialHit 直达该项目详情。
// 安装目标：Mod / 资源包 / 光影 / 数据包装入首页选中的版本（数据包进存档）；整合包转新建版本流程。
// 移植自桌面 Starcloud.App.Views.Dialogs.ContentDownloadDialog（仅做安卓侧适配）：
// - 「转到 Modrinth / MC 百科」：桌面 Process.Start → 安卓系统 Intent 打开浏览器
// - 子弹窗（装载到存档）挂到 Ui.Root（壳注入的对话框宿主）

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Behaviors;
using Starcloud.Launcher.Uno.Views.Controls;
using Starcloud.Launcher.Uno.Views.Shared;
using Starcloud.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Starcloud.Launcher.Uno.Views.Dialogs;

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
            "modpack" => "下载 整合包",
            "datapack" => "下载数据包",
            _ => "下载 Mod",
        });
        CloseButtonText = Loc.T("关闭");
        Resources["ContentDialogMaxWidth"] = 1200.0;
        Resources["ContentDialogMaxHeight"] = 1040.0;
        // 压缩标题与内容的默认间距，内容区上移（默认 Title 底距 12 + 容器顶距 24 过大）
        Resources["ContentDialogPadding"] = new Thickness(20, 24, 20, 16);
        Resources["ContentDialogTitleMargin"] = new Thickness(0);

        // 下载页行点击入口：直达项目详情；版本详情页入口：打开即展示热门内容
        Loading += async (_, _) =>
        {
            if (_initialHit is not null) await OpenDetailAsync(_initialHit);
            else await RunDlSearchAsync();
        };

        // 安装目标常显：所见即所得（主页选中的版本直接装入；整合包新建版本）
        if (_kind == "modpack")
        {
            DlTarget.Text = _source == ContentSourceIds.CurseForge
                ? Loc.T("安装方式：下载整合包后新建游戏版本")
                : Loc.T("安装方式：下载 .mrpack 后新建游戏版本");
        }
        else if (string.IsNullOrEmpty(_versionId))
        {
            DlTarget.Text = Loc.T("未选择安装目标：请先在「首页」选择游戏版本");
            DlTarget.Foreground = ThemedBrush("SystemFillColorCautionBrush");
        }
        else
        {
            DlTarget.Text = Loc.T("安装目标：{0}", _versionId);
        }
        StyleSourceButtons();
    }

    // ── 搜索 ──

    // ── 资源来源切换 ──

    private void SourceModrinth_Click(object sender, RoutedEventArgs e) => SwitchSource(ContentSourceIds.Modrinth);

    private void SourceCurseForge_Click(object sender, RoutedEventArgs e) => SwitchSource(ContentSourceIds.CurseForge);

    /// <summary>
    /// 换来源：清空当前结果与详情，按同一关键词重新搜一次。
    /// 两个源的 DTO 已经统一（<see cref="ModrinthHit"/> / <see cref="ModrinthVersion"/>），
    /// 所以下面所有渲染与安装代码都不需要分叉。
    /// </summary>
    private void SwitchSource(string source)
    {
        if (_source == source) return;
        _source = source;
        StyleSourceButtons();
        _selected = null;
        _versions = [];
        DownloadDetail.Visibility = Visibility.Collapsed;
        DownloadBack.Visibility = Visibility.Collapsed;
        _ = RunDlSearchAsync();
    }

    private void StyleSourceButtons()
    {
        StyleSelected(SourceModrinthBtn, _source == ContentSourceIds.Modrinth);
        StyleSelected(SourceCurseForgeBtn, _source == ContentSourceIds.CurseForge);
        SearchBox.PlaceholderText = Loc.T(_source == ContentSourceIds.CurseForge
            ? "搜索 CurseForge…"
            : "搜索 Modrinth…");
    }

    /// <summary>按来源分发搜索（两个服务的方法同形，返回同一套 DTO）</summary>
    private Task<ModrinthSearchResult> SearchSourceAsync(string query)
    {
        if (_source == ContentSourceIds.CurseForge)
            return _s.CurseForge.SearchAsync(query, _kind, _kind == "mod" ? _loader : null, _mc,
                limit: 24);
        return _s.Mods.SearchAsync(query, _kind, _loader, _mc, limit: 24);
    }

    private Task<List<ModrinthVersion>> ListSourceVersionsAsync(ModrinthHit hit) =>
        _source == ContentSourceIds.CurseForge
            ? _s.CurseForge.ListVersionsAsync(hit.ProjectId, _kind == "mod" ? _loader : null, null)
            : _s.Mods.ListVersionsAsync(hit.ProjectId, _kind == "mod" ? _loader : null, null);

    /// <summary>MC 版本筛选规范化：仅标准三段/两段数字版本号有效，其余（整合包 id / 快照码）不筛</summary>
    private static string? NormalizeMcFilter(string? mc) =>
        System.Text.RegularExpressions.Regex.IsMatch(mc ?? "", @"^\d+\.\d+(\.\d+)?$") ? mc : null;

    private void DownloadSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = RunDlSearchAsync();
    }

    private void DownloadSearch_Click(object sender, RoutedEventArgs e) => _ = RunDlSearchAsync();

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
            Info(Loc.T("正在搜索…"), InfoBarSeverity.Informational);
            // 中文搜索词先翻成英文（内置词典 → MC 百科 → 机器翻译）：搜索框里保留用户原话
            var translated = await _s.SearchTranslation.TranslateAsync(SearchBox.Text.Trim());
            var res = await SearchSourceAsync(translated.Effective);
            DlInfoBar.IsOpen = false;
            // 搜索框保留用户输入的中文，这里告诉用户实际拿去搜的英文词
            var hint = SearchTranslationHint.Describe(translated);
            if (hint.Length > 0) Info(hint, InfoBarSeverity.Informational);
            DownloadResults.Children.Clear();
            DownloadResults.Visibility = Visibility.Visible;
            DlEmpty.Visibility = res.Hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var hit in res.Hits)
                DownloadResults.Children.Add(BuildResultRow(hit));
        }
        catch (Exception ex)
        {
            Info(Loc.T("搜索失败：{0}", ex.Message), InfoBarSeverity.Error);
        }
        finally { _busy = false; }
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

        DlDeps.Visibility = Visibility.Collapsed;   // 依赖行重置（拉到后再显示）
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
    /// 详情卡显示前置依赖：取最近若干版本的必需依赖并集，一次批量查项目标题（附带翻译）。
    /// 结果回到时若用户已切走本项目则放弃；失败静默（依赖展示是尽力而为）。
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

            var titles = _source == ContentSourceIds.CurseForge
                ? (await _s.CurseForge.GetProjectTitlesAsync(ids)).Values.ToList()
                : (await _s.Mods.GetProjectsAsync(ids))
                    .Where(p => !string.IsNullOrWhiteSpace(p.Title))
                    .Select(p => ModTitleHelper.Display(p.Title))
                    .ToList();
            if (titles.Count == 0 || !ReferenceEquals(_selected, hit)) return;

            DlDeps.Text = Loc.T("前置依赖：{0}", string.Join("、", titles));
            DlDeps.Visibility = Visibility.Visible;
        }
        catch { /* 依赖展示失败不影响主流程 */ }
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
        _chip = _mc is not null && versions.Contains(_mc) ? _mc : null;

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
        // Uno 的 RadialGradientBrush 未实现 GradientOrigin，Center 即渐变原点
        var glowBrush = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.5, 0.5),
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

    /// <summary>按当前芯片重建版本分组列表（可展开，文件行内直接下载安装）。</summary>
    private void BuildVersionList()
    {
        DlVersionList.Children.Clear();
        if (_selected is null) return;
        var items = _versions
            .Where(v => _chip is null || v.GameVersions.Contains(_chip))
            .Take(30).ToList();
        if (items.Count == 0)
        {
            DlVersionList.Children.Add(new TextBlock
            {
                Text = Loc.T("该版本下没有可用文件"), FontSize = 12,
                Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            });
            return;
        }
        foreach (var version in items)
            DlVersionList.Children.Add(BuildVersionRow(version));
        // 换芯片重建列表：让分组行错峰入场，而不是"啪"地整片换掉
        for (var i = 0; i < DlVersionList.Children.Count; i++)
            if (DlVersionList.Children[i] is FrameworkElement row)
                MotionFx.StaggerIn(row, i, stepMs: 20, maxStagger: 6);
    }

    private Expander BuildVersionRow(ModrinthVersion version)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new TextBlock
        {
            Text = version.VersionNumber, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });
        // 加载器徽标：同版本号在不同加载器（Fabric/Forge/NeoForge/Quilt）下是不同文件，必须可区分
        foreach (var l in version.Loaders.Take(2))
        {
            head.Children.Add(new Border
            {
                Background = ThemedBrush("SubtleFillColorTertiaryBrush"),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = DisplayLoader(l), FontSize = 11,
                    Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
                },
            });
        }
        if (version.VersionType is "beta" or "alpha")
        {
            head.Children.Add(new Border
            {
                Background = ThemedBrush("SubtleFillColorTertiaryBrush"),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = version.VersionType == "beta" ? "Beta" : "Alpha", FontSize = 11,
                    Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
                },
            });
        }
        var primary = version.PrimaryFile;
        if (primary is not null)
            head.Children.Add(new TextBlock
            {
                Text = Size(primary.Size), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
            });

        var filesPanel = new StackPanel { Spacing = 6 };
        foreach (var f in version.Files)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = f.Filename, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = Size(f.Size), FontSize = 11,
                Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
            });
            var btn = new GlowButton
            {
                Content = Loc.T(_kind == "modpack" ? "下载" : "下载安装"),
                FontSize = 12, Padding = new Thickness(10, 2, 10, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, _) => _ = InstallAsync(_selected!, version, f, btn);
            Grid.SetColumn(info, 0);
            row.Children.Add(info);
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);
            // 文件行也是"卡"：悬停要有高亮抬升与跟手光斑（与版本页的行同一套语言）
            filesPanel.Children.Add(WrapHoverRow(row));
        }

        return new Expander
        {
            Header = head,
            Content = filesPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    // ── 安装与跳转 ──

    /// <summary>下载入库并安装：Mod/资源包/光影 → 首页选中版本；数据包 → 选存档装入；整合包 → 新建版本流程。</summary>
    private async Task InstallAsync(ModrinthHit hit, ModrinthVersion version, ModrinthFile file, Button btn)
    {
        // 目标版本校验（整合包 .mrpack 走新建版本流程，无需选中版本）
        var verb = Loc.T(_kind == "modpack" ? "下载" : "下载安装");
        if (_kind != "modpack" &&
            (string.IsNullOrEmpty(_versionId) || !_s.Installer.IsInstalled(_s.Settings.DotMinecraft, _versionId)))
        {
            Info(Loc.T("请先在「首页」选择要安装到的游戏版本。"), InfoBarSeverity.Warning);
            return;
        }
        if (_busy) return;
        _busy = true;
        btn.IsEnabled = false;
        btn.Content = Loc.T("下载中…");
        try
        {
            // CurseForge 的文件 id 不是 Modrinth 版本 id：记 null，避免更新检查拿它去问 Modrinth
            var info = new LibraryAdoptInfo(hit.Title, hit.IconUrl, hit.Author, hit.ProjectId,
                _source == ContentSourceIds.CurseForge ? null : version.Id,
                version.VersionNumber, version.GameVersions, version.Loaders, file.Url, _source);
            var entry = await _s.ContentLibrary.DownloadAndAdoptAsync(_kind, file.Url, file.Filename, file.Sha1, info);

            // 整合包（.mrpack / CurseForge .zip）：下载入库后关闭本窗，转宿主页新建版本流程
            // （两种格式由宿主页按扩展名分派：mrpack 走 Modrinth 安装器，zip 走 CurseForge 安装器）
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
                    btn.IsEnabled = true;
                    btn.Content = verb;
                    return;
                }
                await _s.ContentLibrary.InstallToSaveAsync(entry, _versionId, save);
            }
            else
            {
                await _s.ContentLibrary.InstallToVersionAsync(entry, _versionId);
            }

            var msg = Loc.T("{0} 已安装到 {1}。", file.Filename, _versionId);
            if (_kind == "mod")
            {
                try
                {
                    var dep = await _s.ContentLibrary.CheckDependenciesAsync(version, _versionId);
                    if (dep.Required.Count > 0)
                        msg += " " + Loc.T("缺少必需依赖：{0}，建议到下载页补齐。",
                            string.Join("、", dep.Required.Select(d => d.Title)));
                }
                catch { /* 依赖检查失败不阻断 */ }
            }
            Info(msg, InfoBarSeverity.Success);
            btn.IsEnabled = false;                       // 已装好，按钮定格为完成态防重复安装
            btn.Content = Loc.T("✓ 已安装");
            _onInstalled?.Invoke();
        }
        catch (Exception ex)
        {
            Info(Loc.T("下载失败：{0}", ex.Message), InfoBarSeverity.Error);
            btn.IsEnabled = true;
            btn.Content = verb;
        }
        finally { _busy = false; }
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
            XamlRoot = Ui.Root,   // 挂在壳对话框宿主的可视树上（此时下载窗仍打开）
        };
        var result = await dlg.ShowAsync();
        if (saves.Count == 0 || result != ContentDialogResult.Primary) return null;
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private void DlModrinth_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } hit) return;
        OpenInBrowser(hit.WebUrl is { Length: > 0 } url
            ? url
            : $"https://modrinth.com/project/{hit.Slug}");
    }

    private void DlWiki_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } hit) return;
        OpenInBrowser($"https://search.mcmod.cn/s?key={Uri.EscapeDataString(hit.Title)}");
    }

    /// <summary>安卓用系统 Intent 打开链接（桌面是 Process.Start + 默认浏览器）</summary>
    private static void OpenInBrowser(string url)
    {
        try
        {
            var intent = new global::Android.Content.Intent(
                global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse(url));
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch { /* 无浏览器应用或链接非法时静默 */ }
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

    /// <summary>按对话框实际主题取主题字典画刷：先查 App 覆盖（Light/Dark），再查默认
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
