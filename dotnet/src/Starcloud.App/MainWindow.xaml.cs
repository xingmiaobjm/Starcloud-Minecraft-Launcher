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
        NotificationCenter.Instance.Changed += () => _taskDirty = true;
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
                var heading = string.IsNullOrWhiteSpace(title) ? pluginId : title;
                PluginNotifyTip.Title = heading;
                PluginNotifyTip.Message = message;
                PluginNotifyTip.IsOpen = true;
                pluginNotifyTimer.Stop();
                pluginNotifyTimer.Start();
                // 同一份通知也进历史流水（右下角提示会消失，历史通知留着可查）
                NotificationCenter.Instance.Info($"{heading}：{message}");
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

    // ── 调度中心（右侧任务抽屉） ──
    private readonly DispatcherQueueTimer _taskRefresh =
        DispatcherQueue.GetForCurrentThread().CreateTimer();
    private volatile bool _taskDirty;

    private void TaskCenterBtn_Click(object sender, RoutedEventArgs e) => ToggleTaskPanel();

    private void TaskCenterClose_Click(object sender, RoutedEventArgs e) => ToggleTaskPanel();

    private void TaskCenterScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        ToggleTaskPanel();

    /// <summary>Esc 关闭抽屉；用 KeyboardAccelerator 而非 KeyDown，焦点不在抽屉内时同样生效。</summary>
    private void TaskCenterEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (TaskCenterOverlay.Visibility == Visibility.Visible) ToggleTaskPanel();
    }

    /// <summary>清空历史通知（调度队列不受影响）</summary>
    private void NoticeClear_Click(object sender, RoutedEventArgs e)
    {
        NotificationCenter.Instance.Clear();
        _taskDirty = true;
        RefreshTaskRows();
    }

    private void ToggleTaskPanel()
    {
        var open = TaskCenterOverlay.Visibility == Visibility.Visible;
        if (open)
        {
            TaskCenterOverlay.Visibility = Visibility.Collapsed;
            TaskCenterBtn.Focus(FocusState.Programmatic);   // 焦点还给入口，键盘用户不迷路
        }
        else
        {
            ApplyTaskDrawerTransition();
            TaskCenterOverlay.Visibility = Visibility.Visible;
            TaskCenterCloseBtn.Focus(FocusState.Programmatic);
            // 建行推迟一帧：入场转场期间新增子元素会与动画同帧合成，留下淡淡的残影
            DispatcherQueue.TryEnqueue(() =>
            {
                _taskDirty = true;
                RefreshTaskRows();
            });
        }
        _taskDirty = true;
        RefreshTaskRows();
    }

    /// <summary>抽屉入场动效：跟随 MotionService 开关（关闭动效时不挂任何转场）。
    /// 用框架 EntranceThemeTransition，系统「显示动画」关闭时自动不播。</summary>
    private void ApplyTaskDrawerTransition()
    {
        TaskCenterDrawer.Transitions ??= [];
        TaskCenterDrawer.Transitions.Clear();
        if (!AppServices.Instance.Motion.Enabled) return;
        TaskCenterDrawer.Transitions.Add(new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition
        {
            FromHorizontalOffset = 32,
            FromVerticalOffset = 0,
        });
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
            var tc = TaskCenterService.Instance;
            var nc = NotificationCenter.Instance;

            var running = tc.RunningCount;
            TaskBadgeText.Text = running.ToString();
            TaskBadge.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (TaskCenterOverlay.Visibility != Visibility.Visible) return;
            var queue = tc.Snapshot().Where(t => t.State == AppTaskState.Running).ToList();
            var notices = nc.Snapshot().Take(MaxShownNotices).ToList();

            TaskSummaryText.Text = running > 0
                ? Loc.T("{0} 个任务进行中 · {1} 条通知", running, nc.Count)
                : Loc.T("{0} 条通知", nc.Count);

            SyncQueueRows(queue);
            SyncNoticeRows(notices);
            QueueEmptyHost.Visibility = queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoticeClearBtn.Visibility = nc.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { /* 面板刷新失败不影响主流程，下个 tick 重试 */ }
    }

    /// <summary>调度队列：按 Task.Id 复用任务卡（重建会打断不定态动画，内容就地更新）</summary>
    private void SyncQueueRows(List<AppTask> tasks)
    {
        var ids = tasks.Select(t => t.Id).ToHashSet();
        var index = 0;
        foreach (var t in tasks)
        {
            if (!_rows.TryGetValue(t.Id, out var r))
            {
                r = BuildTaskRow(t);
                _rows[t.Id] = r;
                TaskRunningHost.Children.Insert(Math.Min(index, TaskRunningHost.Children.Count), r.Root);
            }
            else
            {
                var pos = TaskRunningHost.Children.IndexOf(r.Root);
                if (pos >= 0 && pos != index)
                {
                    TaskRunningHost.Children.RemoveAt(pos);
                    TaskRunningHost.Children.Insert(index, r.Root);
                }
            }
            UpdateTaskRow(r, t);
            index++;
        }

        // 任务结束（转入历史通知）或超出保留条数：撤掉队列里的卡片
        foreach (var kv in _rows.Where(kv => !ids.Contains(kv.Key)).ToList())
        {
            TaskRunningHost.Children.Remove(kv.Value.Root);
            _rows.Remove(kv.Key);
        }
    }

    /// <summary>历史通知：按 Notice.Id 复用行控件（通知不可变，只在增删时动列表）</summary>
    private void SyncNoticeRows(List<AppNotice> notices)
    {
        var ids = notices.Select(n => n.Id).ToHashSet();
        var index = 0;
        foreach (var n in notices)
        {
            if (!_noticeRows.TryGetValue(n.Id, out var row))
            {
                row = BuildNoticeRow(n);
                _noticeRows[n.Id] = row;
                NoticeHost.Children.Insert(Math.Min(index, NoticeHost.Children.Count), row);
            }
            else
            {
                var pos = NoticeHost.Children.IndexOf(row);
                if (pos >= 0 && pos != index)
                {
                    NoticeHost.Children.RemoveAt(pos);
                    NoticeHost.Children.Insert(index, row);
                }
            }
            index++;
        }

        foreach (var kv in _noticeRows.Where(kv => !ids.Contains(kv.Key)).ToList())
        {
            NoticeHost.Children.Remove(kv.Value);
            _noticeRows.Remove(kv.Key);
        }
    }

    /// <summary>历史通知行：[时间] [级别] [文案]；级别只用系统语义色，文案超三行截断并给 ToolTip</summary>
    private Border BuildNoticeRow(AppNotice n)
    {
        var time = new TextBlock
        {
            Text = n.Time.ToString("HH:mm:ss"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
        };
        var levelText = Loc.T(n.Level switch
        {
            NoticeLevel.Warning => "警告",
            NoticeLevel.Error => "错误",
            _ => "信息",
        });
        var level = new TextBlock
        {
            Text = levelText, FontSize = 11, Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = LevelBrush(n.Level),
        };
        var message = Loc.TCore(n.Message);
        var text = new TextBlock
        {
            Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemedBrush("TextFillColorPrimaryBrush"),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(time, 0);
        Grid.SetColumn(level, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(time);
        grid.Children.Add(level);
        grid.Children.Add(text);

        ToolTipService.SetToolTip(grid, message);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{n.Time:HH:mm:ss} {levelText} {message}");
        return new Border { Padding = new Thickness(2, 1, 2, 1), Child = grid };
    }

    /// <summary>通知级别配色：仅作状态信号（信息 = 强调色、警告 = 警示色、错误 = 严重色）</summary>
    private Brush? LevelBrush(NoticeLevel level) => level switch
    {
        NoticeLevel.Warning => ThemedBrush("SystemFillColorCautionBrush"),
        NoticeLevel.Error => ThemedBrush("SystemFillColorCriticalBrush"),
        _ => ThemedBrush("SystemFillColorAttentionBrush"),
    };

    /// <summary>已构建任务行的可更新控件引用（按 Task.Id 缓存，跨刷新复用）。
    /// 进度条为自绘（Star 列宽 = 比例，直接布局无动画）——WinUI ProgressBar 的值变化自带
    /// 缓动动画，300ms 刷新节奏下动画永远在追赶，这就是进度条抽搐的根因。</summary>
    private sealed class TaskRowRefs
    {
        public Border Root = null!;               // 任务卡容器
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!;
        public TextBlock Value = null!;           // 右对齐的关键数字：百分比 / 速度 / 阶段
        public Border Track = null!;            // 主进度条底槽（圆角）
        public Microsoft.UI.Xaml.Shapes.Rectangle Fill = null!;
        public ColumnDefinition FillCol = null!; // 填充列（Star 宽 = 进度比例）
        public ColumnDefinition RestCol = null!;
        public ProgressBar Ind = null!;         // 不确定进度时显示（流动动画）
        public TextBlock Detail = null!;
        public TextBlock MirrorCaption = null!;
        public Grid Ribbon = null!;              // 多源贡献带（按字节占比分段）
        public StackPanel Legend = null!;        // 贡献带图例：源名 + 体积 + 占比
    }

    /// <summary>调度队列的行缓存（Task.Id → 行控件）与历史通知的行缓存（Notice.Id → 行控件）</summary>
    private readonly Dictionary<string, TaskRowRefs> _rows = [];
    private readonly Dictionary<long, Border> _noticeRows = [];

    /// <summary>历史通知最多渲染的条数（服务端保留 200 条，面板只展示最近这些）</summary>
    private const int MaxShownNotices = 50;

    /// <summary>构建任务卡骨架：状态点 + 标题 + 右对齐关键数字 + 自绘进度条 + 详情 + 多源贡献带。
    /// 只有「正在执行」的任务才会出现在调度队列里，因此这里只有一种形态。</summary>
    private TaskRowRefs BuildTaskRow(AppTask t)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = t.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // 右对齐的「关键数字」：这一行最该被看见的东西（百分比 / 速度 / 结果）
        var value = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 190,
        };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(value, 2);
        head.Children.Add(dot);
        head.Children.Add(title);
        head.Children.Add(value);

        var root = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
        };
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(head);

        var refs = new TaskRowRefs { Root = root, Dot = dot, Value = value };

        refs.FillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        refs.RestCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        refs.Fill = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Fill = ThemedBrush("AccentFillColorDefaultBrush"),
            RadiusX = 2, RadiusY = 2, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(refs.FillCol);
        barGrid.ColumnDefinitions.Add(refs.RestCol);
        barGrid.Children.Add(refs.Fill);
        refs.Track = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            Background = ThemedBrush("ControlStrongFillColorDefaultBrush"),
            Child = barGrid,
        };
        refs.Ind = new ProgressBar { MinHeight = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var barHost = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        barHost.Children.Add(refs.Track);
        barHost.Children.Add(refs.Ind);

        refs.Detail = new TextBlock
        {
            FontSize = 11, Visibility = Visibility.Collapsed,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        refs.MirrorCaption = new TextBlock
        {
            Text = Loc.T("各源贡献"), FontSize = 11, Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
        };
        refs.Ribbon = new Grid { Height = 6, ColumnSpacing = 3, Visibility = Visibility.Collapsed };
        refs.Legend = new StackPanel
        {
            Spacing = 3, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed,
        };

        body.Children.Add(barHost);
        body.Children.Add(refs.Detail);
        body.Children.Add(refs.MirrorCaption);
        body.Children.Add(refs.Ribbon);
        body.Children.Add(refs.Legend);

        // 队列里的任务卡带材质面：正在执行的工作「有实体」
        root.Background = ThemedBrush("LayerFillColorDefaultBrush");
        root.BorderBrush = ThemedBrush("CardStrokeColorDefaultBrush");
        root.BorderThickness = new Thickness(1);

        root.Child = body;
        return refs;
    }

    /// <summary>就地更新任务卡内容（不重建控件，保证 indeterminate 动画连续）。
    /// 队列里只有正在执行的任务，状态点与进度一律用品牌 accent（主题切换后自愈）。</summary>
    private void UpdateTaskRow(TaskRowRefs r, AppTask t)
    {
        r.Dot.Fill = ThemedBrush("AccentFillColorDefaultBrush");

        // 无障碍：整卡播报任务名，帮助文本给出当前进度 / 阶段
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(r.Root, t.Title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            r.Root, string.IsNullOrEmpty(t.Detail) ? "" : t.Detail);

        // 进行中：右上给出关键数字（确定进度 = 百分比；否则 = 速度 / 阶段文本）
        var pct = t.Progress is { } p ? (int)Math.Floor(Math.Clamp(p, 0, 1) * 100) : (int?)null;
        if (pct is not null)
        {
            r.Value.Text = $"{pct}%";
            r.Value.FontSize = 13;
            r.Value.FontWeight = FontWeights.SemiBold;
            r.Value.Foreground = ThemedBrush("TextFillColorPrimaryBrush");
        }
        else
        {
            r.Value.Text = t.Detail;
            r.Value.FontSize = 12;
            r.Value.FontWeight = FontWeights.Normal;
            r.Value.Foreground = ThemedBrush("TextFillColorSecondaryBrush");
        }
        r.Value.Visibility = string.IsNullOrEmpty(r.Value.Text)
            ? Visibility.Collapsed : Visibility.Visible;

        if (pct is not null)
        {
            r.Fill.Fill = ThemedBrush("AccentFillColorDefaultBrush");   // 主题切换后自愈
            SetBar(r.FillCol, r.RestCol, pct.Value / 100.0);
            r.Track.Visibility = Visibility.Visible;
            r.Ind.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 不确定进度：显示流动条（复用行，动画连续不抽搐）
            r.Track.Visibility = Visibility.Collapsed;
            r.Ind.Visibility = Visibility.Visible;
        }

        // 详情：确定进度时详情补第二行；不确定进度时 Detail 已在右上，避免重复
        var detail = pct is not null ? t.Detail : "";
        r.Detail.Text = detail;
        r.Detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        UpdateMirrorRibbon(r, t);
    }

    /// <summary>多源竞速的「贡献带」：一条按各源字节占比分段的横条 + 图例。
    /// 领先源用品牌 accent，其余按名次递减透明度——一眼看出这一票是谁在扛。
    /// 只有一个源时没有可比性，退回普通进度表达。</summary>
    private void UpdateMirrorRibbon(TaskRowRefs r, AppTask t)
    {
        var mirrors = t.Mirrors.Where(m => m.Bytes > 0)
            .OrderByDescending(m => m.Bytes).Take(6).ToList();
        r.Ribbon.Children.Clear();
        r.Ribbon.ColumnDefinitions.Clear();
        r.Legend.Children.Clear();

        var show = mirrors.Count > 1;
        r.MirrorCaption.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        r.Ribbon.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        r.Legend.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var total = Math.Max(1, mirrors.Sum(m => (double)m.Bytes));
        for (var i = 0; i < mirrors.Count; i++)
        {
            var m = mirrors[i];
            r.Ribbon.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, (double)m.Bytes), GridUnitType.Star),
            });
            var seg = new Border
            {
                CornerRadius = i == 0
                    ? new CornerRadius(3, 0, 0, 3)
                    : i == mirrors.Count - 1 ? new CornerRadius(0, 3, 3, 0) : new CornerRadius(0),
                Background = SourceBrush(i),
                Opacity = SourceOpacity(i),
            };
            Grid.SetColumn(seg, i);
            r.Ribbon.Children.Add(seg);
            r.Legend.Children.Add(BuildMirrorLegendRow(m, total, i));
        }
    }

    /// <summary>贡献带图例行：色块 + 源名 + 体积 · 占比</summary>
    private Grid BuildMirrorLegendRow(AppTaskMirror m, double total, int rank)
    {
        var chip = new Border
        {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = SourceBrush(rank),
            Opacity = SourceOpacity(rank),
        };
        var name = new TextBlock
        {
            Text = m.Name, FontSize = 11, Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
        };
        var share = new TextBlock
        {
            Text = $"{m.Bytes / 1048576.0:F1} MB · {m.Bytes / total * 100:F0}%",
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
        };
        var g = new Grid { ColumnSpacing = 0 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        Grid.SetColumn(chip, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(share, 2);
        g.Children.Add(chip);
        g.Children.Add(name);
        g.Children.Add(share);
        return g;
    }

    /// <summary>贡献带配色：名次 0 = 品牌 accent，其余中性色按名次递减透明度</summary>
    private Brush? SourceBrush(int rank) => rank == 0
        ? ThemedBrush("AccentFillColorDefaultBrush")
        : ThemedBrush("ControlStrongFillColorDefaultBrush");

    /// <summary>贡献带透明度：领先源实心，其余按名次递减（最深 0.35，保证可见）</summary>
    private static double SourceOpacity(int rank) => rank == 0 ? 1 : Math.Max(0.35, 0.85 - rank * 0.15);

    /// <summary>设置自绘条的填充比例（改 Star 列宽，布局直算、无动画）</summary>
    private static void SetBar(ColumnDefinition fillCol, ColumnDefinition restCol, double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        fillCol.Width = new GridLength(ratio, GridUnitType.Star);
        restCol.Width = new GridLength(1 - ratio, GridUnitType.Star);
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
