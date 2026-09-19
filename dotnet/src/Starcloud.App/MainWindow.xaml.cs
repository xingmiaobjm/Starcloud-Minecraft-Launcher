// 主窗口：Acrylic 毛玻璃背景（自定义透明度）+ NavigationView 导航

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views;
using Starcloud.Core.Services;
using Windows.UI;

namespace Starcloud.App;

public sealed partial class MainWindow : Window
{
    // 自定义背景宿主（D1）：背景图 / 模糊层 / 毛玻璃遮罩三层
    private readonly WindowBackgroundHost _background;

    public MainWindow()
    {
        InitializeComponent();

        // 任务历史只保留本次会话：启动时清空调度中心（任务列表纯内存，无跨启动持久化）
        Starcloud.Core.Services.TaskCenterService.Instance.ClearAll();

        SystemBackdrop = new TransparentAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 窗口按钮改为自绘（见 MainWindow.xaml）：隐藏系统 caption，保留边框（拖拽缩放仍可用），
        // 顶栏 AppTitleBar 依旧是拖拽区（ExtendsContentIntoTitleBar 已开启）。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(true, false);
        SizeChanged += (_, _) => UpdateMaximizeGlyph(); // 最大化 / 还原（含 Win+↑ 等外部操作）后换图标
        UpdateMaximizeGlyph();

        // 调度中心：Changed 可能在后台线程触发，仅置脏标志，UI 由 300ms 定时器统一刷新
        Starcloud.Core.Services.TaskCenterService.Instance.Changed += () => _taskDirty = true;
        _taskRefresh.Tick += (_, _) => RefreshTaskRows();
        _taskRefresh.Start();

        // 图标按 AppContext.BaseDirectory 解析：相对路径会跟随快捷方式的「起始位置」，从开始菜单启动时可能落空
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        Title = "Starcloud Minecraft Launcher";

        // 测试版标识：启动器自身版本号（取自 csproj <Version>）→「测试版 V0.2.1」
        var rawVer = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var meta = rawVer.IndexOf('+'); // 去掉 SourceLink 附加的 +<commit>
        if (meta >= 0) rawVer = rawVer[..meta];
        VersionBadgeText.Text = Loc.T("测试版 V{0}", rawVer);

        // Nova 内嵌（NOVA_INTEGRATION.md 五入口）：顶栏 Orb 随全局状态机联动
        var rt = AppServices.Instance.NovaRuntime;
        rt.StateChanged += OnNovaStateChanged;
        rt.OverlayToast += OnOverlayToast; // 浮窗一句话提醒 → TeachingTip（nova-overlay-plan.md T7）
        _toastResetTimer.IsRepeating = false;
        _toastResetTimer.Tick += (_, _) =>
        {
            _toastResetTimer.Stop();
            NovaToastTip.IsOpen = false;
        };
        NovaToastTip.Target = NovaOrb;
        ApplyNovaState(rt.State);
        RefreshAccountButton();
        Activated += (_, _) => RefreshAccountButton();

        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage), null, MotionService.TransitionFor(AppServices.Instance.Motion.Enabled));

        // 插件通知（launcher:notification）：右下角 InfoBar，8 秒自动收起
        var pluginNotifyTimer = DispatcherQueue.CreateTimer();
        pluginNotifyTimer.IsRepeating = false;
        pluginNotifyTimer.Interval = TimeSpan.FromSeconds(8);
        pluginNotifyTimer.Tick += (_, _) => PluginNotifyTip.IsOpen = false;
        AppServices.Instance.PluginHost.NotifyRequested += (pluginId, title, message) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                PluginNotifyTip.Title = string.IsNullOrWhiteSpace(title) ? pluginId : title;
                PluginNotifyTip.Message = message;
                PluginNotifyTip.IsOpen = true;
                pluginNotifyTimer.Stop();
                pluginNotifyTimer.Start();
            });

        // 外观（D1）：主题在启动时即生效（修历史 bug：设置页回填时 _loaded=false 导致主题被忽略）
        _background = new WindowBackgroundHost(BackgroundLayer, BackgroundImage, BackgroundBlurHost, BackgroundMask);
        var root = (FrameworkElement)Content;
        AppServices.Instance.Appearance.ApplyTheme(AppServices.Instance.Settings.Theme, root);
        _background.Apply(AppServices.Instance.Settings, root.ActualTheme);
        root.SizeChanged += (_, _) => _background.Resize(root.ActualWidth, root.ActualHeight);
    }

    /// <summary>外观变更即时生效入口（设置页在主题 / 背景改动后调用）。</summary>
    public static void RefreshAppearance()
    {
        if (App.MainWindow is not MainWindow w) return;
        var s = AppServices.Instance.Settings;
        var root = (FrameworkElement)w.Content;
        AppServices.Instance.Appearance.ApplyTheme(s.Theme, root);
        w._background.Apply(s, root.ActualTheme);
        w._background.Resize(root.ActualWidth, root.ActualHeight);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not { } item) return;
        var tag = item.Tag?.ToString();
        Type page = tag switch
        {
            "versions" => typeof(VersionsPage),
            "saves" => typeof(SavesPage),
            "download" => typeof(DownloadPage),
            "accounts" => typeof(AccountsPage),
            "ai" => typeof(NovaPage),
            "settings" => typeof(SettingsPage),
            "plugins" => typeof(PluginsPage),
            "logs" => typeof(LogsPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, MotionService.TransitionFor(AppServices.Instance.Motion.Enabled));
        // 选择完成后自动收起侧栏，保持内容区宽畅
        Nav.IsPaneOpen = false;
    }

    /// <summary>页面内跳转入口：通过设置导航栏选中项驱动 Frame 导航，保证左侧选中状态同步。</summary>
    public void NavigateTo(string tag)
    {
        var target = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == tag);
        if (target is not null && !ReferenceEquals(Nav.SelectedItem, target))
            Nav.SelectedItem = target;
    }

    /// <summary>版本详情子页：直接 Frame 导航（不占用侧栏选中项，返回即回来源页）。</summary>
    public void NavigateToVersionDetail(string versionId) =>
        ContentFrame.Navigate(typeof(VersionDetailPage), versionId,
            MotionService.TransitionFor(AppServices.Instance.Motion.Enabled, drillIn: true));

    // ── 自绘窗口按钮（最小化 / 最大化-还原 / 关闭，见 MainWindow.xaml） ──

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        (AppWindow.Presenter as OverlappedPresenter)?.Minimize();

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter p) return;
        if (p.State == OverlappedPresenterState.Maximized) p.Restore();
        else p.Maximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ── 调度中心（右下角悬浮面板） ──
    private readonly DispatcherQueueTimer _taskRefresh =
        DispatcherQueue.GetForCurrentThread().CreateTimer();
    private volatile bool _taskDirty;

    private void TaskCenterBtn_Click(object sender, RoutedEventArgs e) => ToggleTaskPanel();

    private void TaskCenterClose_Click(object sender, RoutedEventArgs e) => ToggleTaskPanel();

    private void TaskCenterScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        ToggleTaskPanel();

    private void ToggleTaskPanel()
    {
        var open = TaskCenterOverlay.Visibility == Visibility.Visible;
        TaskCenterOverlay.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        _taskDirty = true;
        if (!open) RefreshTaskRows();
    }

    /// <summary>定时器刷新（300ms）：仅脏时按 Task.Id 复用行更新（全量重建会让 indeterminate 动画
    /// 反复重启——这就是进度条抽搐的根因）；同时维护标题栏按钮徽标。
    /// 异常兜底吞掉（调度中心只是辅助显示，绝不能带崩主进程）。</summary>
    private void RefreshTaskRows()
    {
        try
        {
            if (!_taskDirty) return;
            _taskDirty = false;
            var tc = Starcloud.Core.Services.TaskCenterService.Instance;

            var running = tc.RunningCount;
            TaskBadgeText.Text = running.ToString();
            TaskBadge.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (TaskCenterOverlay.Visibility != Visibility.Visible) return;
            var tasks = tc.Snapshot().Take(20).ToList();
            TaskSummaryText.Text = running > 0
                ? Loc.T("{0} 个任务进行中，共 {1} 条", running, tasks.Count)
                : Loc.T("共 {0} 条记录", tasks.Count);

            var seen = new HashSet<string>();
            var idx = 0;
            foreach (var t in tasks)
            {
                seen.Add(t.Id);
                if (!_rows.TryGetValue(t.Id, out var r))
                {
                    r = BuildTaskRow(t);
                    _rows[t.Id] = r;
                    TaskListHost.Children.Insert(Math.Min(idx, TaskListHost.Children.Count), r.Root);
                }
                UpdateTaskRow(r, t);
                idx++;
            }
            foreach (var gone in _rows.Where(kv => !seen.Contains(kv.Key)).ToList())
            {
                TaskListHost.Children.Remove(gone.Value.Root);
                _rows.Remove(gone.Key);
            }

            if (_rows.Count == 0)
            {
                _emptyHint ??= new TextBlock
                {
                    Text = Loc.T("没有任务在运行"),
                    FontSize = 12, Opacity = 0.6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 24),
                };
                if (!TaskListHost.Children.Contains(_emptyHint)) TaskListHost.Children.Add(_emptyHint);
            }
            else if (_emptyHint is not null)
            {
                TaskListHost.Children.Remove(_emptyHint);
            }
        }
        catch { /* 面板刷新失败不影响主流程，下个 tick 重试 */ }
    }

    /// <summary>已构建任务行的可更新控件引用（按 Task.Id 缓存，跨刷新复用）。
    /// 进度条为自绘（Star 列宽 = 比例，直接布局无动画）——WinUI ProgressBar 的值变化自带
    /// 缓动动画，300ms 刷新节奏下动画永远在追赶，这就是进度条抽搐的根因。</summary>
    private sealed class TaskRowRefs
    {
        public StackPanel Root = null!;
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!;
        public Border Track = null!;            // 主进度条底槽（圆角）
        public ColumnDefinition FillCol = null!; // 填充列（Star 宽 = 进度比例）
        public ColumnDefinition RestCol = null!;
        public ProgressBar Ind = null!;         // 不确定进度时显示（流动动画）
        public TextBlock Detail = null!;
        public StackPanel Mirrors = null!;
    }

    private readonly Dictionary<string, TaskRowRefs> _rows = [];
    private TextBlock? _emptyHint;

    private static readonly Brush DotRunning = DotBrush("#FFD322");
    private static readonly Brush DotOk = DotBrush("#57BB75");
    private static readonly Brush DotFail = DotBrush("#FF6B6B");
    private static readonly Brush DotIdle = DotBrush("#80808080");

    /// <summary>单行任务卡骨架：状态点 + 标题 + 进度条 + 详情 + 每源条容器 + 底部分隔线</summary>
    private TaskRowRefs BuildTaskRow(Starcloud.Core.Services.AppTask t)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(dot);
        head.Children.Add(new TextBlock
        {
            Text = t.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var track = BuildBar(DotRunning, 6, out var fillCol, out var restCol);
        var ind = new ProgressBar { MinHeight = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var barHost = new Grid();
        barHost.Children.Add(track);
        barHost.Children.Add(ind);
        var detail = new TextBlock
        {
            FontSize = 11, Opacity = 0.7, Visibility = Visibility.Collapsed,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var mirrors = new StackPanel { Spacing = 4, Margin = new Thickness(16, 2, 0, 0) };

        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(head);
        row.Children.Add(barHost);
        row.Children.Add(detail);
        row.Children.Add(mirrors);

        var padded = new Grid { Padding = new Thickness(2, 10, 2, 10) };
        padded.Children.Add(row);
        var root = new StackPanel();
        root.Children.Add(padded);
        root.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill = ThemedBrush("DividerStrokeColorDefaultBrush"),
        });

        return new TaskRowRefs
        {
            Root = root, Dot = dot, Track = track, FillCol = fillCol, RestCol = restCol,
            Ind = ind, Detail = detail, Mirrors = mirrors,
        };
    }

    /// <summary>自绘进度条：圆角底槽 + 两列 Star 网格（填充列宽 = 比例，改列宽即时生效、无动画）</summary>
    private Border BuildBar(Brush fill, double height, out ColumnDefinition fillCol, out ColumnDefinition restCol)
    {
        fillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        restCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(fillCol);
        grid.ColumnDefinitions.Add(restCol);
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Fill = fill, RadiusX = height / 2, RadiusY = height / 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        Grid.SetColumn((Microsoft.UI.Xaml.Shapes.Rectangle)grid.Children[0], 0);
        return new Border
        {
            Height = height, CornerRadius = new CornerRadius(height / 2),
            Background = ThemedBrush("ControlStrongFillColorDefaultBrush"),
            Child = grid,
        };
    }

    /// <summary>就地更新行内容（不重建控件，保证 indeterminate 动画连续）</summary>
    private void UpdateTaskRow(TaskRowRefs r, Starcloud.Core.Services.AppTask t)
    {
        r.Dot.Fill = t.State switch
        {
            Starcloud.Core.Services.AppTaskState.Running => DotRunning,
            Starcloud.Core.Services.AppTaskState.Completed => DotOk,
            Starcloud.Core.Services.AppTaskState.Failed => DotFail,
            _ => DotIdle,
        };

        r.Track.Opacity = 1;
        r.Ind.Visibility = Visibility.Collapsed;
        r.Track.Visibility = Visibility.Visible;
        if (t.State == Starcloud.Core.Services.AppTaskState.Running)
        {
            if (t.Progress is { } p) SetBar(r.FillCol, r.RestCol, Math.Clamp(p, 0, 1));
            else
            {
                // 不确定进度：显示流动条（复用行，动画连续不抽搐）
                r.Track.Visibility = Visibility.Collapsed;
                r.Ind.Visibility = Visibility.Visible;
            }
        }
        else
        {
            SetBar(r.FillCol, r.RestCol,
                t.State == Starcloud.Core.Services.AppTaskState.Completed ? 1 : 0);
            r.Track.Opacity = 0.5;
        }

        var hasDetail = !string.IsNullOrEmpty(t.Detail);
        r.Detail.Text = t.Detail;
        r.Detail.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;

        // 每源进度条（贡献占比 = 该源字节 / 已下载总量；自绘条无动画不抽搐）
        r.Mirrors.Children.Clear();
        if (t.Mirrors.Count > 0 && t.State == Starcloud.Core.Services.AppTaskState.Running)
        {
            var done = Math.Max(1, t.Mirrors.Sum(m => m.Bytes));
            foreach (var m in t.Mirrors)
            {
                var nameT = new TextBlock
                {
                    Text = m.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
                };
                var srcBar = BuildBar(DotRunning, 4, out var sf, out var sr);
                SetBar(sf, sr, m.Bytes / (double)done);
                var sizeT = new TextBlock
                {
                    Text = m.Bytes >= 1048576 ? $"{m.Bytes / 1048576.0:F1} MB" : $"{m.Bytes / 1024.0:F0} KB",
                    FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
                };
                var g = new Grid { ColumnSpacing = 8 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                Grid.SetColumn(nameT, 0);
                Grid.SetColumn(srcBar, 1);
                Grid.SetColumn(sizeT, 2);
                g.Children.Add(nameT);
                g.Children.Add(srcBar);
                g.Children.Add(sizeT);
                r.Mirrors.Children.Add(g);
            }
            r.Mirrors.Visibility = Visibility.Visible;
        }
        else
        {
            r.Mirrors.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>设置自绘条的填充比例（改 Star 列宽，布局直算、无动画）</summary>
    private static void SetBar(ColumnDefinition fillCol, ColumnDefinition restCol, double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        fillCol.Width = new GridLength(ratio, GridUnitType.Star);
        restCol.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    /// <summary>hex → 画刷（支持 #RRGGBB 与 #AARRGGBB）</summary>
    private static SolidColorBrush DotBrush(string hex)
    {
        if (hex.Length == 7) hex = "#FF" + hex[1..]; // 6 位 RGB 补不透明 alpha
        return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
            Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16), Convert.ToByte(hex[7..9], 16)));
    }

    /// <summary>按窗口实际主题查画刷：索引器可能解析到 Dark/Default 字典（任务卡灰块根因），
    /// 显式沿 ActualTheme 在合并字典的 ThemeDictionaries 中查找。</summary>
    private Microsoft.UI.Xaml.Media.Brush? ThemedBrush(string key)
    {
        var want = ((Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light).ToString();
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (d.ThemeDictionaries.TryGetValue(want, out var v) &&
                v is ResourceDictionary rd && rd.TryGetValue(key, out var b) &&
                b is Microsoft.UI.Xaml.Media.Brush br)
                return br;
        return Application.Current.Resources.TryGetValue(key, out var f) &&
            f is Microsoft.UI.Xaml.Media.Brush fb ? fb : null;
    }

    /// <summary>最大化时显示「还原」图标，否则显示「最大化」图标（含 Win+↑、拖到屏幕顶端等外部操作）。</summary>
    private void UpdateMaximizeGlyph()
    {
        var maximized = (AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(MaximizeButton, Loc.T(maximized ? "还原" : "最大化"));
    }

    // ── Nova 内嵌（NOVA_INTEGRATION.md 五入口：① 顶栏 Orb） ──

    /// <summary>① NovaOrb：顶栏下方 8px 弹出面板，再点收回。</summary>
    private void NovaOrb_Click(object sender, RoutedEventArgs e)
    {
        if (NovaPopup.IsOpen) { NovaPopup.IsOpen = false; return; }
        ShowNovaPopup();
    }

    /// <summary>按 Orb 实际位置定位弹出面板（右边界贴齐，左不越界），并限高到窗口底部。</summary>
    private void ShowNovaPopup()
    {
        var root = Content as FrameworkElement;
        if (root is null) return;
        // WinUI 3 的 Window 不继承 FrameworkElement，宽度从 Content 根元素取
        var p = NovaOrb.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
        NovaPopup.HorizontalOffset = Math.Max(12, Math.Min(p.X, root.ActualWidth - NovaPanelHost.Width - 12));
        NovaPopup.VerticalOffset = 48 + 8; // 顶栏(48) 下 8px
        // 面板比窗口还高时会被 Popup 裁掉输入行：限高，超出部分交给面板内部滚动
        NovaPanelHost.MaxHeight = Math.Max(240, root.ActualHeight - NovaPopup.VerticalOffset - 12);
        NovaPopup.IsOpen = true;
    }

    /// <summary>入口③「问 Nova」承接：展开顶栏 Nova 面板接收上下文输入（不跳页，少折腾）。</summary>
    public void OpenNovaPanel() => ShowNovaPopup();

    /// <summary>协议激活（starcloud://join）：好友邀请直达 Nova 确认清单，并展开 Nova 面板。</summary>
    public async Task HandleJoinUrlAsync(string url)
    {
        ShowNovaPopup();
        await AppServices.Instance.NovaRuntime.HandleJoinUrlAsync(url);
    }

    /// <summary>面板标头齿轮：关面板 → 去 Nova 页（许可层 / 流水 / LLM 设置）。</summary>
    private void NovaPanelSettings_Click(object sender, RoutedEventArgs e)
    {
        NovaPopup.IsOpen = false;
        NavigateTo("ai");
    }

    /// <summary>面板未登录引导：关面板 → 去 Nova 页登录 Starcloud ID。</summary>
    private void NovaPanelLogin_Click(object sender, RoutedEventArgs e)
    {
        NovaPopup.IsOpen = false;
        NavigateTo("ai");
    }

    private void OnNovaStateChanged(NovaState state) =>
        DispatcherQueue.TryEnqueue(() => ApplyNovaState(state));

    // ── 浮窗提醒 → TeachingTip（8s 后自动收起） ──

    private readonly DispatcherQueueTimer _toastResetTimer = DispatcherQueue.GetForCurrentThread()
        .CreateTimer(); // 构造在 UI 线程，可直接取

    private void OnOverlayToast(NovaOverlayToast toast) => DispatcherQueue.TryEnqueue(() =>
    {
        NovaToastTip.Subtitle = toast.Text;
        NovaToastTip.IsOpen = false;
        NovaToastTip.IsOpen = true;
        _toastResetTimer.Stop();
        _toastResetTimer.Interval = TimeSpan.FromSeconds(8);
        _toastResetTimer.Start();
    });

    /// <summary>① Orb 五态着色：待命灰 / 思考紫 / 有提案黄(角标) / 观察绿 / 离线暗灰。</summary>
    private void ApplyNovaState(NovaState state)
    {
        var rt = AppServices.Instance.NovaRuntime;
        var brush = StateBrush(state);

        NovaOrbGlyph.Foreground = brush;
        NovaOrbDot.Visibility = state == NovaState.HasPlan ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(NovaOrb, $"Nova：{rt.StateText}");

        // 面板内容随登录态切换：离线显示引导，已登录显示对话
        var offline = state == NovaState.Offline;
        NovaPanelLogin.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        NovaPanelChat.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;

        RefreshAccountButton();
    }

    // ── 个人中心入口（右上角，bilibili 式悬浮卡） ──

    private void AccountButton_Click(object sender, RoutedEventArgs e) => NavigateTo("accounts");

    private void RefreshAccountButton()
    {
        var session = AppServices.Instance.StarcloudId.Current;
        var logged = session is not null;
        var name = logged ? session!.DisplayName : "";
        var initial = !string.IsNullOrEmpty(name) ? name[..1]
            : logged && !string.IsNullOrEmpty(session!.UserId) ? session.UserId[..1] : "";
        AccountAvatarText.Text = initial;
        AccountAvatarText.Visibility = initial.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountAvatarGlyph.Visibility = initial.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        var tip = new StackPanel { Spacing = 2 };
        tip.Children.Add(new TextBlock
        {
            Text = logged ? session!.DisplayName : Loc.T("未登录"),
            FontWeight = FontWeights.SemiBold,
        });
        if (logged)
        {
            tip.Children.Add(new TextBlock
            {
                Text = session!.UserId,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            tip.Children.Add(new TextBlock
            {
                Text = Loc.T("已登录 · {0}", session.Channel == StarcloudIdService.CloudChannel ? Loc.T("云端通道") : Loc.T("测试通道")),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        else
        {
            tip.Children.Add(new TextBlock
            {
                Text = Loc.T("点击前往账户页登录 Starcloud ID"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        ToolTipService.SetToolTip(AccountButton, tip);
    }

    private static Brush StateBrush(NovaState state) => state switch
    {
        NovaState.Thinking => Solid(0x7C, 0x3A, 0xED),   // 星云紫
        NovaState.HasPlan => Solid(0xF5, 0x9E, 0x0B),    // 琥珀黄（提案待确认）
        NovaState.Observing => Solid(0x3D, 0xD3, 0x22),  // 品牌深绿（观察中）
        NovaState.Offline => (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
        _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b) =>
        new(Color.FromArgb(0xFF, r, g, b));
}

/// <summary>自定义亚克力背景：比系统默认毛玻璃更通透，深浅主题自动跟随。</summary>
internal sealed class TransparentAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_controller is null)
        {
            _controller = new DesktopAcrylicController();
            _configuration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
            };
            if (xamlRoot.Content is FrameworkElement root)
            {
                ApplyAcrylicTheme(root.ActualTheme);
                _configuration.Theme = ToSystemBackdropTheme(root.ActualTheme);
                root.ActualThemeChanged += OnRootThemeChanged;
            }
        }
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveAllSystemBackdropTargets();
    }

    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        ApplyAcrylicTheme(sender.ActualTheme);
        if (_configuration is not null)
            _configuration.Theme = ToSystemBackdropTheme(sender.ActualTheme);
    }

    /// <summary>深浅主题使用不同的亚克力配方：浅色通透偏白，深色为近黑暗玻璃并保留可见的模糊背景。</summary>
    private void ApplyAcrylicTheme(ElementTheme theme)
    {
        if (_controller is null) return;
        if (theme == ElementTheme.Dark)
        {
            _controller.TintColor = Color.FromArgb(0xFF, 0x14, 0x14, 0x14);
            _controller.TintOpacity = 0.45f;
            _controller.LuminosityOpacity = 0.30f;
            _controller.FallbackColor = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);
        }
        else
        {
            _controller.TintColor = Color.FromArgb(0xFF, 0xFC, 0xFC, 0xFC);
            _controller.TintOpacity = 0.25f;
            _controller.LuminosityOpacity = 0.18f;
            _controller.FallbackColor = Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        }
    }

    private static SystemBackdropTheme ToSystemBackdropTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        _ => SystemBackdropTheme.Light,
    };
}
