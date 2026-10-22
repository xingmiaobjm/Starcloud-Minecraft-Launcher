// 首页：PCL2 式左卡（版本列表 → 启动 → 账户），版本条目支持右键菜单

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Dialogs;
using Starcloud.App.Views.Rows;
using Starcloud.App.Views.Shared;
using Starcloud.Core.Services;
using System.Diagnostics;
using System.IO;
using Windows.Foundation;

namespace Starcloud.App.Views;

public sealed partial class HomePage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private List<LocalVersionRow> _rows = [];
    private GameSession? _gameProcess;          // 当前启动页 / 收尾流程对应的那一局（多开时=最近一次启动的）
    private string? _pendingLaunchId; // 版本列表页跨页启动请求（App.PendingLaunchVersionId）
    private int _modeIndex;           // 账户模式分段的当前段（0 正版 / 1 外置 / 2 离线），供尺寸变化时复位指示器

    // ── 启动可靠性（设计：docs/designs/2026-09-11-launch-reliability-design.md） ──
    private LaunchOptions? _activeOptions;      // 本局启动参数（退出收尾命令复用）
    private Func<Task>? _relaunch;              // 启动闭包：预检 + 拉起（自动重试复用）
    private bool _crashDialogOpen;              // 崩溃弹窗重入保护
    private int _retryCount;                    // 本局已自动重试次数（ShowOverlay 归零）

    /// <summary>本页启动的一局（多开：一局一条，各自独立收尾，不串到当前启动页）</summary>
    private sealed class HomeRun
    {
        public GameSession Session = null!;
        public LaunchOptions? Options;
        public Starcloud.Core.Services.AppTask? Task;
    }

    /// <summary>本页启动且尚未收尾的局（pid → 局）；多开时用来分辨「哪一局退出了」</summary>
    private readonly Dictionary<int, HomeRun> _runs = [];

    private const int LogMaxLines = 200;                                            // 实时日志展示上限
    private readonly List<string> _logFlushBuffer = [];                             // 输出行缓冲（UI 线程定时刷新）
    private int _logPending;                                                        // 待刷新行数（免无谓刷新）
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // ── 启动页状态机（设计：docs/designs/2026-09-09-launch-overlay-design.md §3.2） ──
    private enum LaunchStage
    {
        Idle, VerifyingCredentials, EnvChecking, Retrying, CheckingFiles, RepairingFiles,
        StartingProcess, WaitingWindow, Running, Exited, Failed
    }

    private LaunchStage _stage;
    private Starcloud.Core.Services.AppTask? _launchTask;   // 调度中心：游戏启动任务
    private bool _cancelRequested;  // 取消标记：未拉起→阶段边界退出；已拉起→Exited 回调收层
    private double _climbTo = 100;  // 氛围进度爬升上限（到区间顶即停）
    private long _runningStart;     // 进程拉起时刻（算本局时长）
    private long _windowDeadline;   // 窗口检测超时时刻
    private readonly DispatcherTimer _stageTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };  // 氛围进度爬升
    private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromMilliseconds(500) }; // 游戏窗口出现轮询

    public HomePage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        _stageTimer.Tick += (_, _) => StageTimer_Tick();
        _windowTimer.Tick += (_, _) => WindowTimer_Tick();
        _logTimer.Tick += (_, _) => FlushLogBuffer();
        Loaded += (_, _) =>
        {
            AvatarHelper.AvatarUpdated += OnAvatarUpdated;
            Initialize();
        };
        Unloaded += (_, _) => AvatarHelper.AvatarUpdated -= OnAvatarUpdated;
        SizeChanged += OnPageSizeChanged;
        // 三张通知条的开关与自检卡的显隐都汇到一处（左列通知区整块收起 / 展开，版本卡才能与右栏卡上边缘齐平）。
        // 用属性回调而不是在每个赋值点插一句：通知条自己那颗关闭按钮也会改 IsOpen，这样一并覆盖到。
        GlobalErrorBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnLeftNoticeChanged);
        LocalErrorBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnLeftNoticeChanged);
        WarnBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnLeftNoticeChanged);
        CheckCard.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnLeftNoticeChanged);
        // 插件首页卡片里的动作结果没有地方就地显示（卡片带高度是固定的），
        // 汇到左列通知区：成功走提示条、失败走本地错误条（与挂载区那套结果条同语义）。
        HomeCards.Notify = (message, ok) =>
        {
            var bar = ok ? WarnBar : LocalErrorBar;
            bar.Message = message;
            bar.IsOpen = true;
        };
    }

    private void OnLeftNoticeChanged(DependencyObject sender, DependencyProperty dp) => RefreshLeftNoticeArea();

    /// <summary>外部账户皮肤头像下载完成后刷新右侧账户卡（事件来自后台线程）。</summary>
    private void OnAvatarUpdated() => _dq.TryEnqueue(RefreshAccounts);

    /// <summary>
    /// 左列通知区（三张通知条 + 启动自检卡）的整块显隐，以及它到版本卡之间的那点间距。
    /// 全不可见时整块收成 Collapsed —— Collapsed 的元素不参与布局，版本卡因此贴齐左列顶端，
    /// 与右栏账户卡的上边缘齐平。间距只能挂在这一层：左列 Grid 一旦用 RowSpacing，
    /// 空行也照收一份，卡片照样会被推下去（这正是两边上边缘差出约 20px 的原因）。
    /// </summary>
    private void RefreshLeftNoticeArea()
    {
        bool any = GlobalErrorBar.IsOpen || LocalErrorBar.IsOpen || WarnBar.IsOpen
            || CheckCard.Visibility == Visibility.Visible;
        LeftNoticeArea.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        LeftNoticeArea.Margin = any ? new Thickness(0, 0, 0, 16) : new Thickness(0);
    }

    /// <summary>
    /// 窗口尺寸变了：展开态版本卡是撑满左列的，撑满高跟着窗口走，
    /// 收起那一拍记下的那份缓存就作废，下次展开重新量。
    /// </summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _expandedMetricsValid = false;
    }

    private void Initialize()
    {
        // 两栏卡片的收起 / 展开状态跨页面切换保留：页面是 NavigationCacheMode=Disabled，每次回来都是新实例，
        // 状态只能存设置里。先把两个字段按存值填好（必须早于任何 setter —— LoadVersions 里那句
        // 「没版本就强制摊开」的兜底会写设置，若此刻字段还是默认值，就会把存着的收起态反写成展开）。
        _cardExpanded = !_s.Settings.HomeAccountCollapsed;
        _versionsCollapsed = _s.Settings.HomeVersionsCollapsed;

        SetMode(LaunchAccountService.ModeOfLastUsername(_s.Settings.LastUsername));
        LoadVersions();
        RefreshAccounts();
        RefreshGuide();
        RefreshLeftNoticeArea(); // 兜底：初始无通知时把左列通知区收掉，版本卡才能与右栏卡上边缘齐平
        ConsumePendingLaunch();
        SetCardExpanded(_cardExpanded, animate: false); // 落存下来的终态（含 tooltip / 刻度朝向），不播动画
        SetVersionsCollapsed(_versionsCollapsed, animate: false); // 版本卡同理，顺带把两颗刻度的提示语对齐
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingLaunchId = App.PendingLaunchVersionId;
        App.PendingLaunchVersionId = null;
        if (_rows.Count > 0) ConsumePendingLaunch(); // 缓存实例回到主页立即消费；新实例等 Loaded
        UpdateVersionMeta(); // 按钮态依赖实时运行集合（多开：可能在别的页启停了实例）
    }

    /// <summary>消费版本列表页的跨页启动请求（T4 起从详情页/列表页发起）。</summary>
    private void ConsumePendingLaunch()
    {
        var id = _pendingLaunchId;
        _pendingLaunchId = null;
        if (id is null) return;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            LocalErrorBar.Message = Loc.T("版本 {0} 不存在，可能已被删除", id);
            LocalErrorBar.IsOpen = true;
            return;
        }
        VersionList.SelectedItem = row;
        _ = StartLaunchAsync(id); // 多开：不再拦截「已有游戏在运行」
    }

    private void SetMode(string mode)
    {
        // 模式确实换了才做整块滑出 / 滑入；点的是当前这一段就只是刷一下内容，不动画面。
        var changed = !string.Equals(_s.LaunchAccount.Mode, mode, StringComparison.OrdinalIgnoreCase);
        var prevIndex = _modeIndex;
        _s.LaunchAccount.Mode = mode;
        // 三段单选：只勾选对上的那段（ToggleButton 自身是开关语义，这里手动收束），
        // 再把品牌色药丸指示器平移到该段——与下载页版本类型分段同一套动作与配色。
        _modeIndex = mode switch { "external" => 1, "offline" => 2, _ => 0 };
        foreach (var btn in new[] { ModeMsBtn, ModeExternalBtn, ModeOfflineBtn })
            btn.IsChecked = (string?)btn.Tag == mode;
        AnimateModeIndicator(_modeIndex);

        if (changed)
        {
            // 内容与指示器反向：药丸往右移，底下这块往左滑 —— 像翻页一样，
            // 新那块从药丸移过去的那一侧补位上来。
            SlideAccountArea(_modeIndex - prevIndex > 0 ? -1 : 1);
        }
        else
        {
            RefreshAccounts();
        }
    }

    /// <summary>分段容器实测宽度变化：重排段宽，指示器不做动画直接归位</summary>
    private void ModeGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => AnimateModeIndicator(_modeIndex, animate: false);

    /// <summary>分段指示器平移（合成动画）：第 index 段 × 实测段宽。
    /// 段宽 = 容器实测宽 / 3，不写死像素——通栏后宽度随卡片走，窗口缩放 / 文案变长都跟得上。</summary>
    private void AnimateModeIndicator(int index, bool animate = true)
    {
        var segment = ModeGrid.ActualWidth / 3.0;
        if (segment <= 0) return; // 还没布局，等 SizeChanged 再来

        try
        {
            // 指示器宽 = 一段宽（跨三列，靠自身宽度收成一段的视觉）。仅在实际有变时赋值，避免自身触发布局循环。
            // 注意 Width 初始为 NaN（Auto），NaN 的比较恒为 false，必须先判 NaN 再比差值，否则宽度永远不被设置。
            if (double.IsNaN(ModeIndicator.Width) || Math.Abs(ModeIndicator.Width - segment) > 0.5)
                ModeIndicator.Width = segment;

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ModeIndicator);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(ModeIndicator, true);
            var target = new System.Numerics.Vector3(index * (float)segment, 0f, 0f);

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
            // 静默：动效失败不影响模式切换
        }
    }

    private void ModeMs_Click(object s, RoutedEventArgs e) => SetMode("microsoft");
    private void ModeExternal_Click(object s, RoutedEventArgs e) => SetMode("external");
    private void ModeOffline_Click(object s, RoutedEventArgs e) => SetMode("offline");

    // ── 版本列表 ──
    private void LoadVersions()
    {
        var dotMc = _s.Settings.DotMinecraft;
        var store = new InstanceSettingsStore(dotMc);
        try
        {
            var installed = _s.Manifest.ListInstalled(dotMc).ToList();
            installed.Sort((a, b) =>
            {
                // 上次使用的版本置顶，其余按版本号倒序
                var aLast = string.Equals(a.Id, _s.Settings.LastVersion, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                var bLast = string.Equals(b.Id, _s.Settings.LastVersion, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (aLast != bLast) return bLast - aLast;
                return string.Compare(b.Id, a.Id, StringComparison.OrdinalIgnoreCase);
            });
            // 与版本页同一套行模型：图标 + 类型 + 上次启动 + 大小，主页不再是"光秃秃的名字 + 大小"
            _rows = installed.Select(i =>
            {
                var profile = store.Get(i.Id);
                var kind = VersionRowHelpers.DetectKind(dotMc, i.Id);
                return new LocalVersionRow(
                    i.Id, kind, FormatSize(i.SizeBytes),
                    VersionRowHelpers.DisplayIconPath(dotMc, i.Id, profile.IconPath),
                    profile.Category ?? "",
                    InstanceLayout.IsIsolated(profile, _s.Settings),
                    VersionRowHelpers.FormatLastPlayed(profile.LastPlayedAt),
                    (i.Id + " " + kind).ToLowerInvariant());
            }).ToList();
        }
        catch (Exception ex)
        {
            _rows = [];
            LocalErrorBar.Message = Loc.T("读取已安装版本失败：{0}", ex.Message);
            LocalErrorBar.IsOpen = true;
        }
        VersionCountText.Text = $"({_rows.Count})";
        // 行数变了，展开态的自然高也跟着变，缓存作废。
        _expandedMetricsValid = false;
        VersionList.ItemsSource = _rows;
        MotionFx.StaggerListItems(VersionList);
        EmptyVersionHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var preferred = _s.Settings.LastVersion is { } last && _rows.Any(r => r.Id == last) ? last
            : _rows.FirstOrDefault()?.Id;
        VersionList.SelectedItem = _rows.FirstOrDefault(r => r.Id == preferred);
        SyncCompactRow();
        RefreshVersionCollapseAvailability();
    }

    private void RefreshVersionsBtn_Click(object s, RoutedEventArgs e)
    {
        LoadVersions();
        RefreshGuide();
    }

    private void VersionList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // 记录主界面当前选中的版本：下载页的 Mod / 资源包 / 光影 / 数据包直接装入该版本
        if (VersionList.SelectedItem is LocalVersionRow row && _s.Settings.LastVersion != row.Id)
        {
            _s.Settings.LastVersion = row.Id;
            _s.Settings.Save();
        }
        SyncCompactRow(); // 折叠态那一条要跟着换人
        UpdateVersionMeta();
    }

    private void UpdateVersionMeta()
    {
        var id = (VersionList.SelectedItem as LocalVersionRow)?.Id;
        var isRunning = RunningPidFor(id) != 0;
        LaunchTitleText.Text = isRunning ? Loc.T("停止游戏") : Loc.T("启动游戏");
        // 按钮第二行同步当前选中版本名（没选就用「未选择版本」兜底）
        LaunchBtnVersionText.Text = id ?? Loc.T("未选择版本");
        // 自绘轮廓：两个 Path 叠放，切可见性（线宽在 XAML 里统一为 3.2）
        LaunchPlayIcon.Visibility = isRunning ? Visibility.Collapsed : Visibility.Visible;
        LaunchStopIcon.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        LaunchBtn.IsEnabled = isRunning || id is not null;
    }

    /// <summary>
    /// 选中版本当前正在运行的实例 pid（同版本多开时取最近启动的一个；没有则 0）。
    /// 多开下按钮只反映「选中版本」的运行情况：没在跑就是「启动游戏」，在跑才是「停止游戏」。
    /// </summary>
    private int RunningPidFor(string? versionId)
    {
        if (string.IsNullOrEmpty(versionId)) return 0;
        var hit = _s.Runtime.Instances.Where(i => i.VersionId == versionId).ToList();
        return hit.Count == 0 ? 0 : hit[^1].Pid; // Instances 按 StartedAt 升序
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => Loc.T("未完成"),
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    // ── 版本右键菜单 ──
    private void VersionRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalVersionRow row })
            VersionList.SelectedItem = row;
    }

    /// <summary>入口③「问 Nova」：选定实例上下文 → 展开 Nova 面板承接，不跳页。</summary>
    private void AskNova_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        _s.NovaRuntime.SetCurrentInstance(id);
        _s.NovaRuntime.RequestInput($"帮我给 {id} 做一次游戏体检，有隐患直接说");
        (App.MainWindow as MainWindow)?.OpenNovaPanel();
    }

    /// <summary>跳转到该版本的详情/管理页。</summary>
    private void VersionDetail_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        (App.MainWindow as MainWindow)?.NavigateToVersionDetail(id);
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        var dir = Path.Combine(_s.Settings.DotMinecraft, "versions", id);
        if (!Directory.Exists(dir))
        {
            LocalErrorBar.Message = Loc.T("版本目录不存在");
            LocalErrorBar.IsOpen = true;
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        var dlg = new ThemedDialog
        {
            Title = Loc.T("删除版本"),
            Content = Loc.T("确定要删除版本 {0} 吗？\n将删除其版本文件夹，此操作不可恢复。", id),
            PrimaryButtonText = Loc.T("删除"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, id);
            if (string.Equals(_s.Settings.LastVersion, id, StringComparison.OrdinalIgnoreCase))
            {
                _s.Settings.LastVersion = null;
                _s.Settings.Save();
            }
            LoadVersions();
            RefreshGuide();
        }
        catch (Exception ex)
        {
            LocalErrorBar.Message = Loc.T("删除失败：{0}", ex.Message);
            LocalErrorBar.IsOpen = true;
        }
    }

    // ── 账户 ──

    /// <summary>账号区切换登录方式时的横向滑动量（DIP）：退场与进场各取反向，同一个数两条腿才对称。</summary>
    private const float AccountSlideOffset = 56f;

    private EventHandler<object>? _accountFrame;   // 盯着退场跑完、好换内容的那一拍

    /// <summary>
    /// 切换登录方式时整块换向滑动：旧方式那块（头像 + 账号名 + 登录入口）向一侧滑出并淡出，
    /// 新方式那块从另一侧补位滑入并淡入。<paramref name="direction"/> 取 +1 / −1，
    /// 与上方模式分段指示器的平移方向反号 —— 药丸往右走，内容就往左滑（翻页式）。
    /// <para>尺寸类属性上不了合成层，但位移与透明度可以，所以两段各跑一段合成动画；
    /// 两段之间用逐帧时钟盯着退场时长来交接（先换内容、再进场，绝不让两块同时在场）。</para>
    /// </summary>
    private void SlideAccountArea(int direction)
    {
        // 动效关闭、卡片收起（账号区不在台面上）、或还没布局量不出宽度时：直接换内容，不起动画。
        if (!_s.Motion.Enabled || AccountArea.ActualWidth <= 1
            || AccountArea.Visibility != Visibility.Visible)
        {
            RefreshAccounts();
            return;
        }

        StopAccountSlide();

        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(AccountArea);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(AccountArea, true);
        var compositor = visual.Compositor;
        var exitEase = MotionFx.ExitEase(compositor);

        // 退场：旧方式那块顺着切换方向滑出、同时淡出。没有 0 帧关键帧时合成动画从当前值起手，
        // 所以中途连点打断也能接住半路的位置，不会跳回起点。
        var slideOut = compositor.CreateVector3KeyFrameAnimation();
        slideOut.Duration = TimeSpan.FromMilliseconds(MotionFx.ExitMs);
        slideOut.InsertKeyFrame(1f, new System.Numerics.Vector3(direction * AccountSlideOffset, 0f, 0f), exitEase);
        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = TimeSpan.FromMilliseconds(MotionFx.ExitMs);
        fadeOut.InsertKeyFrame(1f, 0f, exitEase);
        visual.StartAnimation("Translation", slideOut);
        visual.StartAnimation("Opacity", fadeOut);

        // 交换内容前先把卡片当裁剪框挂上：滑动量比两侧留白大，无账户时那颗通栏的
        // 「登录 / 添加」会探出卡片边缘糊到页面上；进场跑完再撤掉。
        AccountCard.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, AccountCard.ActualWidth, AccountCard.ActualHeight),
        };

        var clock = Stopwatch.StartNew();
        bool entered = false;
        void OnFrame(object? sender, object args)
        {
            var ms = clock.Elapsed.TotalMilliseconds;
            if (!entered)
            {
                if (ms < MotionFx.ExitMs) return;
                entered = true;
                // 交接点在退场末帧：此刻整块还透明、且停在来向那一侧，换掉内容不会露馅。
                RefreshAccounts();
                EnterAccountArea(visual, compositor, direction);
                return;
            }

            if (ms < MotionFx.ExitMs + MotionFx.EnterMs) return;
            // 进场也跑完，新内容已归位在卡片里，撤掉裁剪。
            StopAccountSlide();
            AccountCard.Clip = null;
        }

        _accountFrame = OnFrame;
        CompositionTarget.Rendering += _accountFrame;
    }

    /// <summary>进场：新方式那块从退场的反方向补位滑入并淡入（曲线与退场同族，合成动画，不触发布局）。</summary>
    private static void EnterAccountArea(Microsoft.UI.Composition.Visual visual,
        Microsoft.UI.Composition.Compositor compositor, int direction)
    {
        var ease = MotionFx.EnterEase(compositor);

        var slideIn = compositor.CreateVector3KeyFrameAnimation();
        slideIn.Duration = TimeSpan.FromMilliseconds(MotionFx.EnterMs);
        slideIn.InsertKeyFrame(0f, new System.Numerics.Vector3(-direction * AccountSlideOffset, 0f, 0f));
        slideIn.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = TimeSpan.FromMilliseconds(MotionFx.EnterMs);
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, ease);

        visual.StartAnimation("Translation", slideIn);
        visual.StartAnimation("Opacity", fadeIn);
    }

    /// <summary>卸掉等交接的那一拍（切换重入时要先停，免得两拍都在改同一个 visual）。</summary>
    private void StopAccountSlide()
    {
        if (_accountFrame is null) return;
        CompositionTarget.Rendering -= _accountFrame;
        _accountFrame = null;
    }

    private void RefreshAccounts()
    {
        // 卡片只显示当前模式的首选账户；首选按类型各记一个（见 AccountStore.SetDefault），
        // 所以这里取 DefaultOf(Mode)，切模式时不会串到别的类型上。
        var acc = _s.Accounts.DefaultOf(_s.LaunchAccount.Mode);
        var hasAny = acc is not null;

        // 该模式一个账户都没有：不给一句死文案，给一个能就地登录的按钮（名字按当前模式走）。
        // 空态由按钮说话，所以名字行收起；头像位留「?」占位。
        NoAccountBtn.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        NoAccountBtn.Content = Loc.T(_s.LaunchAccount.Mode switch
        {
            "external" => "登录第三方账户",
            "offline" => "添加离线账户",
            _ => "登录微软账户",
        });
        AccountNameText.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;

        var name = acc?.Username ?? "";
        AccountNameText.Text = string.IsNullOrWhiteSpace(name) ? Loc.T("未选择账户") : name;
        AvatarText.Text = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
        AvatarImage.Source = ResolveAvatar(acc, name);
    }

    /// <summary>
    /// 空态那颗按钮：就地走对应登录方式，成功即刷新卡片（不必再去账户页兜一圈）。
    /// 登录框是异步弹出的，期间禁用按钮防连点叠窗。
    /// </summary>
    private async void NoAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        var root = Content.XamlRoot;
        NoAccountBtn.IsEnabled = false;
        try
        {
            switch (_s.LaunchAccount.Mode)
            {
                case "offline":
                    if (await AddOfflineAccountAsync(root) is null) return;
                    break;
                case "external":
                    if (await AuthDialogs.ExternalPasswordAsync(root) is null) return;
                    break;
                default:
                    if (await AuthDialogs.MicrosoftDeviceCodeAsync(
                            root, _s.MicrosoftAuth, _s.Accounts, ShowAccountError) is null) return;
                    break;
            }
            _s.NovaRuntime.NotifyLoginChanged();
            RefreshAccounts();
        }
        finally
        {
            NoAccountBtn.IsEnabled = true;
        }
    }

    // ── 切换账号：平时隐形，指针进这一条才浮出来（对齐 PCL2 那颗「藏着」的按钮） ──
    private void SwitchAccountArea_Entered(object sender, PointerRoutedEventArgs e) =>
        ShowSwitchAccount(true);

    private void SwitchAccountArea_Exited(object sender, PointerRoutedEventArgs e) =>
        ShowSwitchAccount(false);

    /// <summary>切换账号按钮的显隐：基态完全隐藏，指针进这一条才浮出来。
    /// 动效关闭时直接切透明度，不留动画尾巴。</summary>
    private void ShowSwitchAccount(bool show)
    {
        if (!_s.Motion.Enabled)
        {
            SwitchAccountBtn.Opacity = show ? 1 : 0;
            return;
        }
        if (show) MotionFx.FadeIn(SwitchAccountBtn, MotionFx.HoverFadeInMs);
        else MotionFx.FadeTo(SwitchAccountBtn, 0, MotionFx.HoverFadeOutMs);
    }

    /// <summary>切换账号：去账户页（与右上角账户入口同一处）</summary>
    private void SwitchAccount_Click(object sender, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("accounts");

    /// <summary>离线账户只差一个名字，就地填完即用（离线身份无凭证可存，所以没有"登录"这一步）</summary>
    private async Task<string?> AddOfflineAccountAsync(XamlRoot root)
    {
        var box = new TextBox
        {
            PlaceholderText = Loc.T("玩家名（3-16 位字母、数字或下划线）"),
            MaxLength = 16,
            Width = 320,
        };
        var dlg = new ThemedDialog
        {
            Title = Loc.T("添加离线账户"),
            Content = box,
            PrimaryButtonText = Loc.T("添加"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

        var name = box.Text.Trim();
        if (!AccountStore.IsValidOfflineName(name))
        {
            ShowAccountError(Loc.T("用户名不合法：需为 3-16 位字母、数字或下划线"));
            return null;
        }
        try
        {
            return _s.Accounts.AddOrUpdateOffline(name).Uuid;
        }
        catch (Exception ex)
        {
            ShowAccountError(Loc.T("添加失败：{0}", ex.Message));
            return null;
        }
    }

    private void ShowAccountError(string message)
    {
        LocalErrorBar.Message = message;
        LocalErrorBar.IsOpen = true;
    }

    /// <summary>
    /// 皮肤头像：覆盖首字母色块；未选账户 / 无网络 / 名字不存在时返回 null（露出色块）。
    /// </summary>
    private static ImageSource? ResolveAvatar(StoredAccount? acc, string name)
    {
        if (acc is null) return null;
        return AvatarHelper.Resolve(acc.Type, acc.Uuid, name, acc.ServerBase,
            AvatarHelper.LocalSkinPath(acc));
    }

    // ── 右栏卡片：收起 / 展开（收起后只剩启动按钮那一行，像一条紧凑 Dock） ──
    // 折叠是「顶边落下来」：卡片底边固定，高度从整列高收到 2 + 24 + 62 = 88
    // （上下边框 + 上下内边距 + 启动行 62），宽度同时从整列宽收到 280 ——
    // 与左栏版本窄条同尺寸（两角各一颗同宽同高的 Dock），宽度收起时右对齐，
    // 右边缘始终贴着列右沿，与左栏窄条贴列左沿正好镜像。
    // 收起态底边就落在内容区底沿上，与展开态一致 —— 两态的页边距处处相等。
    // 高度与宽度都是依赖型属性、上不了合成层，只能逐帧补间（见 StartCardTween）；卡片与分段条
    // 必须同一时长、同样条，代数上才能保证每一帧「内容区 − 行合计」都 ≥ 0：
    //   内容区(t) − 行合计(t) = (H − 128)·(1 − p) ≥ 0
    //   （128 = 边框 2 + 内边距 24 + 两条 Auto 行 102：分段条 40 + 启动行 62）
    // 所以中途不会有半截内容被挤出卡片，末帧（p = 1）恰好为 0 —— 紧凑态内容区正好装下启动行。
    // 卡宽与卡高必须同时补：只等高度收完了再收宽，会先看到一条满宽的空条。

    /// <summary>收起态卡片高度：上下各 1 边框 + 各 12 内边距 + 启动行 62（与左栏版本窄条同为 88）</summary>
    private const double CardCompactHeight = 88;

    /// <summary>收起态卡片宽度：与左栏版本窄条同宽</summary>
    private const double CardCompactWidth = 280;

    /// <summary>分段条展开高度（1 边框 + 2 内缩 + 34 按钮 + 2 + 1），收起时被压到 0</summary>
    private const double CardModeBarHeight = 40;

    private bool _cardExpanded = true;
    private EventHandler<object>? _cardFrame;   // 逐帧补间的帧回调（非空即动画进行中）
    private Stopwatch? _cardClock;              // 该回调配的时钟，用来识别已被替换掉的旧回调

    private void CollapseToggle_Click(object sender, RoutedEventArgs e)
        => SetCardExpanded(!_cardExpanded);

    /// <summary>展开 / 收起右栏卡片。<paramref name="animate"/> 为 false 时直接落终态（初始化用）。</summary>
    private void SetCardExpanded(bool expanded, bool animate = true)
    {
        // 先读起点再停补间：中途重入（动画没跑完又点一下）时当前高度就是停在半路的那一帧，
        // 从它接着往反方向补，才不会跳回起点重新长一遍。
        double cardFrom = CurrentHeight(AccountCard);
        double widthFrom = AccountCard.ActualWidth;
        double modeFrom = CurrentHeight(ModeBar);
        StopCardTween();

        _cardExpanded = expanded;
        PersistHomeLayout(); // 收起 / 展开是用户的持久选择：写回设置，换页回来照旧
        double cardTo = expanded ? ResolveExpandedHeight() : CardCompactHeight;
        double widthTo = expanded ? ResolveExpandedWidth() : CardCompactWidth;
        double modeTo = expanded ? CardModeBarHeight : 0;

        // 还没布局（初始化那一帧）拿不到可插值的起点、或起终点已经重合时没有动画可言，直接落终态。
        bool motion = animate && _s.Motion.Enabled
            && cardFrom > 1
            && !double.IsNaN(cardTo) && cardTo > 1 && !double.IsNaN(widthTo) && widthTo > 1
            && (Math.Abs(cardTo - cardFrom) > 0.5 || Math.Abs(widthTo - widthFrom) > 0.5);

        // 状态文字（Row3）带 Margin 0,14，不参与高度动画：先移出布局，展开时等收尾再按需放回，
        // 否则它那份上边距会把启动行顶出卡片下沿（见 FinishCardAnimation）。
        StatusText.Visibility = Visibility.Collapsed;

        UpdateCollapseChrome(expanded);
        AnimateChevron(expanded, motion);
        FadeCardContent(expanded, motion);

        if (!motion)
        {
            AccountCard.HorizontalAlignment = expanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            AccountCard.VerticalAlignment = expanded ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
            AccountCard.Width = expanded ? double.NaN : CardCompactWidth;
            AccountCard.Height = expanded ? double.NaN : CardCompactHeight;
            AccountCard.Margin = new Thickness(0);
            ModeBar.Height = expanded ? double.NaN : 0;
            CardBody.Clip = null;
            RestoreStatusText();
            return;
        }

        // 动画期间一律「右／底边对齐 + 显式宽高」：展开态满宽满高时与 Stretch 视觉等价、尺寸变化不跳，
        // 终态再交回 Stretch / NaN（见 FinishCardAnimation）。宽度收起时右对齐 ——
        // 显式宽配 Stretch 会被居中，收起时会看到卡片从两边一起往里缩，而不是右边缘钉住。
        AccountCard.HorizontalAlignment = HorizontalAlignment.Right;
        AccountCard.VerticalAlignment = VerticalAlignment.Bottom;
        AccountCard.Width = widthFrom;
        AccountCard.Height = cardFrom;
        AccountCard.Margin = new Thickness(0);
        ModeBar.Height = modeFrom;

        // 折叠的中间帧里账户区所在的 * 行被压向 0，而它内容实高不变，会短暂溢出卡片下沿；
        // 动画期间在 CardBody 上挂一层裁剪，Rect 由 CardBody_SizeChanged 逐帧跟随，稳态撤掉。
        CardBody.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, CardBody.ActualWidth, CardBody.ActualHeight),
        };

        StartCardTween(
            cardFrom, cardTo, widthFrom, widthTo, modeFrom, modeTo,
            expanded ? MotionFx.DrawerExpandMs : MotionFx.DrawerCollapseMs);
    }

    /// <summary>
    /// 把两栏卡片的收起 / 展开状态写回设置，下次进主页（新实例）按它还原。
    /// <para>与存值相同就不落盘——初始化还原、以及「没版本强制摊开」这类兜底都会经过 setter，
    /// 逐个判断能免掉每次进页面的一次无谓写文件。</para>
    /// </summary>
    private void PersistHomeLayout()
    {
        var s = _s.Settings;
        bool versionsCollapsed = _versionsCollapsed;
        bool accountCollapsed = !_cardExpanded;
        if (s.HomeVersionsCollapsed == versionsCollapsed && s.HomeAccountCollapsed == accountCollapsed) return;
        s.HomeVersionsCollapsed = versionsCollapsed;
        s.HomeAccountCollapsed = accountCollapsed;
        _s.SaveSettings();
    }

    /// <summary>
    /// 高度与宽度两段的补间。两者都是依赖型属性、上不了合成层，而 <c>Storyboard</c> 在本进程里对它们的支持
    /// 也不可用（<c>Storyboard.SetTarget</c> 直接抛 E_INVALIDARG），所以这里自己逐帧解算：
    /// 每帧按同一条进场曲线把两段一起落位，末帧交给 <see cref="FinishCardAnimation"/>。
    /// </summary>
    private void StartCardTween(
        double cardFrom, double cardTo, double widthFrom, double widthTo,
        double modeFrom, double modeTo, int ms)
    {
        StopCardTween();
        var clock = Stopwatch.StartNew();
        _cardClock = clock;

        // 曲线与合成层动画共用 MotionFx.CreateEnterSpline()，两条腿的节奏才一致。
        var spline = MotionFx.CreateEnterSpline();

        void OnFrame(object? sender, object args)
        {
            // 中途重入时旧回调可能还排在队列里，时钟对不上就直接出局。
            if (!ReferenceEquals(_cardClock, clock)) return;

            double p = Math.Clamp(clock.Elapsed.TotalMilliseconds / ms, 0d, 1d);
            double t = MotionFx.EaseProgress(spline, p);
            AccountCard.Height = cardFrom + (cardTo - cardFrom) * t;
            AccountCard.Width = widthFrom + (widthTo - widthFrom) * t;
            ModeBar.Height = modeFrom + (modeTo - modeFrom) * t;
            // 收起态的版本窄条紧挨启动卡站（见 VersionCollapsedShift）：启动卡这一帧有多宽，
            // 它左边的空档就有多大。逐帧把窄条对到当前卡宽上，两卡才会一起动。
            SyncCollapsedVersionBar(AccountCard.Width);
            if (p >= 1d) FinishCardAnimation();
        }

        _cardFrame = OnFrame;
        CompositionTarget.Rendering += _cardFrame;
    }

    /// <summary>停下进行中的高度补间（收尾 / 重入都要）。</summary>
    private void StopCardTween()
    {
        if (_cardFrame is null) return;
        CompositionTarget.Rendering -= _cardFrame;
        _cardFrame = null;
        _cardClock = null;
    }

    /// <summary>动画收尾：展开态交回 Stretch / Auto 并撤掉裁剪；收起态保持显式紧凑尺寸、把两块内容移出布局。</summary>
    private void FinishCardAnimation()
    {
        StopCardTween();
        CardBody.Clip = null;

        if (_cardExpanded)
        {
            AccountCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            AccountCard.VerticalAlignment = VerticalAlignment.Stretch;
            AccountCard.Width = double.NaN;
            AccountCard.Height = double.NaN;
            AccountCard.Margin = new Thickness(0);
            ModeBar.Height = double.NaN;
            RestoreStatusText();
            SyncCollapsedVersionBar();
            return;
        }
        // 淡出（DrawerFadeMs）先于高度归零跑完，这时才真正折叠，收起过程看得见尾迹。
        ModeBar.Visibility = Visibility.Collapsed;
        AccountArea.Visibility = Visibility.Collapsed;
        // 收起态是贴右的窄条（与左栏贴左的窄条镜像）；交回 Stretch 会被拉回整列宽。
        AccountCard.HorizontalAlignment = HorizontalAlignment.Right;
        AccountCard.VerticalAlignment = VerticalAlignment.Bottom;
        AccountCard.Width = CardCompactWidth;
        AccountCard.Height = CardCompactHeight;
        AccountCard.Margin = new Thickness(0);
        ModeBar.Height = 0;
        SyncCollapsedVersionBar();
    }

    /// <summary>
    /// 右列这一格宽窄一变，收起态的版本窄条得跟着挪 —— 它的站位是「紧挨启动卡左边」，
    /// 右移量正是启动卡让出来的空档 = 右列宽 − 启动卡当前宽（见 <see cref="VersionCollapsedShift"/>）。
    /// 启动卡收起来（空档出现）或重新展开（空档消失）都要重新对位，
    /// 否则窄条会停在按上一格宽度算出的位置上，与启动卡之间隔着一段说不清的缝。
    /// <paramref name="cardWidth"/> 传具体值表示「按这一帧的卡宽算」（补间途中逐帧调用，两卡一起动）；
    /// 传 <c>NaN</c> 表示按稳态卡宽算（终态收尾用）。
    /// 只在没有版本补间进行时才动 —— 补间正跑着，它自己每帧都在算这个位置。
    /// </summary>
    private void SyncCollapsedVersionBar(double cardWidth = double.NaN)
    {
        if (!_versionsCollapsed || _versionFrame is not null) return;
        if (HomeContent.ColumnDefinitions.Count < 2) return;
        double colW = HomeContent.ColumnDefinitions[1].ActualWidth;
        if (colW < 1) return;

        double card = double.IsNaN(cardWidth)
            ? (_cardExpanded ? colW : CardCompactWidth)
            : cardWidth;
        double shift = colW - card;
        if (shift < 0) shift = 0;

        VersionCard.Margin = new Thickness(0, 0, -shift, 0);
        VersionCardShadow.Margin = VersionCard.Margin;
    }

    /// <summary>两段高度的当前值：显式值优先（含动画停在中途的那一帧），展开态交还 Auto 时才退回实测。</summary>
    private static double CurrentHeight(FrameworkElement element)
    {
        var h = element.Height;
        return double.IsNaN(h) ? element.ActualHeight : h;
    }

    /// <summary>把状态文字放回原位（仅当有内容；没有内容时保持收起，卡片底边才干净）。</summary>
    private void RestoreStatusText()
    {
        if (!string.IsNullOrEmpty(StatusText.Text)) StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>展开态卡片高度 = 右列高度（卡片与窗口同高）；布局未定时退回卡片实测值。</summary>
    private double ResolveExpandedHeight()
    {
        var h = HomeContent.ActualHeight;
        return h > 0 ? h : AccountCard.ActualHeight;
    }

    /// <summary>展开态卡片宽度 = 右列那一格的宽（卡片撑满整列）；布局未定时退回卡片实测值。</summary>
    private double ResolveExpandedWidth()
    {
        var w = HomeContent.ColumnDefinitions[1].ActualWidth;
        return w > 1 ? w : AccountCard.ActualWidth;
    }

    /// <summary>分段条与账户区的进出场：收起是淡出（高度归零后再折叠），展开是淡入。</summary>
    private void FadeCardContent(bool expanded, bool motion)
    {
        if (expanded)
        {
            ModeBar.Visibility = Visibility.Visible;
            AccountArea.Visibility = Visibility.Visible;
        }

        if (!motion)
        {
            // 动效关闭时不能交给 MotionFx.FadeOut —— 它把合成透明度抬回 1，
            // 对「收起后就该消失」的两块内容正好是反的；这里复位到可见态，再用 Visibility 说话。
            MotionFx.Reset(ModeBar);
            MotionFx.Reset(AccountArea);
            if (!expanded)
            {
                ModeBar.Visibility = Visibility.Collapsed;
                AccountArea.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (expanded)
        {
            MotionFx.FadeIn(ModeBar, MotionFx.DrawerFadeMs);
            MotionFx.FadeIn(AccountArea, MotionFx.DrawerFadeMs);
        }
        else
        {
            MotionFx.FadeOut(ModeBar, MotionFx.DrawerFadeMs);
            MotionFx.FadeOut(AccountArea, MotionFx.DrawerFadeMs);
        }
    }

    /// <summary>收起 / 展开刻度：展开态朝下（按一下把卡片折下去），收起态旋到朝上（按一下向上展开）。</summary>
    private void AnimateChevron(bool expanded, bool motion)
        => MotionFx.RotateChevron(CollapseChevron, expanded ? 0f : 180f, motion);

    /// <summary>刻度按钮的提示与无障碍名跟着状态走；代码赋值同样会重新 Track，切语言也能重放。</summary>
    private void UpdateCollapseChrome(bool expanded)
    {
        var key = expanded ? "收起卡片" : "展开卡片";
        Loc.SetTip(CollapseBtn, key);
        Loc.SetName(CollapseBtn, key);
    }

    /// <summary>折叠中间帧里跟着卡片实高刷新裁剪框；稳态没挂 Clip，什么都不做。</summary>
    private void CardBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CardBody.Clip is RectangleGeometry clip)
            clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
    }

    // ── 左栏版本卡：收起 / 展开（收起后只剩当前选中那一版的图标与版本名，卡收成 280 宽的窄条） ──
    // 展开态卡片撑满左列余下的整格（上边缘贴齐右栏账户卡、下边缘贴列底），列表在卡内滚动；
    // 收起是「卡头 + 列表区一起让位」，窄条站到左列右沿、再整体右移一格右列空档，紧挨到启动卡左边
    // （间距回到两列间的 24），底边落在内容区底沿、与右栏启动卡齐平（两态页边距相等），
    // 卡高落到 2 边框 + 24 内边距 + 62 紧凑行 = 88，卡宽落到 280，与右栏卡片是同一种紧凑 Dock 语汇。
    // 与右栏一样，高度上不了合成层，六段（卡高 / 卡宽 / 卡头高 / 卡头下间距 / 紧凑行高 / 右移量）
    // 只能逐帧补间。高度那四段共用一条曲线、同一个进度，代数上每一帧都恒等：
    //   卡头(t) + 间距(t) + 列表(t) + 紧凑(t) = (卡高0 − 26)(1 − t) + 62t = 卡高(t) − 26
    // 其中列表区不再显式补 —— 它是卡片内那一行 *，上面几段补到哪、剩下的空间自动落到它头上，
    // 恒等式由布局自己成立，末帧也不会算出负数高度。
    // 中途没有半截内容被挤出卡片（紧凑行那 60 高的行模板会短暂超出该行，
    // 但它锚在行顶、行底又始终贴着卡片内沿，超出的部分正好落到卡片外，被 VersionCardBody 的裁剪吃掉）。
    // 卡宽与卡高必须同时补：只等高度收完了再收宽，会先看到一条满宽的空条。
    // 底边留白与右移量也跟着同一条曲线 —— 收起态窄条要贴到启动卡左边、底边与启动卡齐平，
    // 这两段都不在高度里，跟不上就会在末帧跳一下（与右栏 SetCardExpanded 同理）。
    // 收起态与补间途中一律「右／底边对齐 + 显式宽高」：显式尺寸配 Stretch 会被居中，
    // Left 对齐又让负右边距失效；只有展开稳态才交回 Stretch + Auto（撑满整格）。

    /// <summary>收起态版本卡高度：上下各 1 边框 + 各 12 内边距 + 紧凑行 62</summary>
    private const double VersionCardCompactHeight = 88;

    /// <summary>收起态版本卡宽度：只裹住图标 + 版本名 + 行右端那颗刻度按钮</summary>
    private const double VersionCardCompactWidth = 280;

    /// <summary>折叠态紧凑行高度（行模板 60 + 上下各 1 外边距）</summary>
    private const double VersionCompactRowHeight = 62;

    /// <summary>卡头与列表区之间的间距（收起时与卡头一并归零）</summary>
    private const double VersionHeaderGap = 8;

    /// <summary>版本卡的固定开销：上下边框 2 + 上下内边距 24</summary>
    private const double VersionCardChrome = 26;

    private bool _versionsCollapsed;
    private EventHandler<object>? _versionFrame;   // 逐帧补间的帧回调（非空即动画进行中）
    private Stopwatch? _versionClock;              // 该回调配的时钟，用来识别已被替换掉的旧回调
    private RectangleGeometry? _versionClip;       // 补间途中挂在 VersionCardBody 上的裁剪框，逐帧跟着卡片尺寸走

    // 展开态尺寸的缓存：收起那一拍趁卡片正处在展开稳态，把起点值顺手记下来，
    // 下次展开直接用 —— 就不必再把可视树摊成满高满宽去量（那次改树会被渲染出一帧，就是「展开时闪一下」）。
    private double _expandedCardHeight = double.NaN;
    private double _expandedCardWidth = double.NaN;
    private double _expandedHeader = double.NaN;
    private bool _expandedMetricsValid;

    private void VersionCollapseToggle_Click(object sender, RoutedEventArgs e)
        => SetVersionsCollapsed(!_versionsCollapsed);

    /// <summary>折叠态那一行点一下就展开（刻度按钮自己的点击不会冒泡到这里，见 XAML）。</summary>
    private void VersionCompactRow_Tapped(object sender, TappedRoutedEventArgs e)
        => SetVersionsCollapsed(false);

    /// <summary>收起 / 展开左栏版本卡。<paramref name="animate"/> 为 false 时直接落终态（初始化用）。</summary>
    private void SetVersionsCollapsed(bool collapsed, bool animate = true)
    {
        // 先读起点再停补间：中途重入（动画没跑完又点一下）时当前高度就是停在半路的那一帧，
        // 从它接着往反方向补，才不会跳回起点重新长一遍。
        double cardFrom = CurrentHeight(VersionCard);
        double widthFrom = VersionCard.ActualWidth;
        double headerFrom = CurrentHeight(VersionHeader);
        double gapFrom = VersionHeader.Margin.Bottom;
        double compactFrom = CurrentHeight(VersionCompactRow);
        double shiftFrom = -VersionCard.Margin.Right;     // 窄条额外右移的量（收起态挨着启动卡、展开态 0）

        // 收起这一拍的起点正是展开稳态的撑满尺寸，顺手记下来给下次展开用。
        // 只在「当前确实是展开稳态」时记：动画没跑完时的高度是半路的，不能当撑满高。
        if (!_versionsCollapsed && _versionFrame is null && cardFrom > 1 && widthFrom > 1)
        {
            _expandedCardHeight = cardFrom;
            _expandedCardWidth = widthFrom;
            _expandedHeader = headerFrom;
            _expandedMetricsValid = true;
        }

        StopVersionTween();

        _versionsCollapsed = collapsed;
        PersistHomeLayout(); // 同上：版本卡的收起态也跨页面切换保留
        // 收起态左列顶部本来是死区：把状态摘要条放出来；展开态由版本卡占满整列，收回去
        SummaryBar.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;

        double cardTo, widthTo, headerTo, gapTo, compactTo, shiftTo;
        if (collapsed)
        {
            cardTo = VersionCardCompactHeight;
            widthTo = VersionCardCompactWidth;
            headerTo = 0;
            gapTo = 0;
            compactTo = VersionCompactRowHeight;
            // 再右移一格空档，从「左列靠右」挪到「启动卡左边」。
            shiftTo = VersionCollapsedShift;
        }
        else
        {
            // 展开要落在「撑满左列」的高上：收起时卡头 / 列表区高度是 0，量不出东西。
            if (_expandedMetricsValid)
            {
                // 缓存命中：直接用收起那一拍记下的尺寸，完全不碰可视树 ——
                // 探高那次「摊成满高满宽」会被渲染出一帧，正是「展开时闪一下」的来源。
                cardTo = _expandedCardHeight;
                widthTo = _expandedCardWidth;
                headerTo = _expandedHeader;
            }
            else
            {
                var (card, header) = MeasureVersionExpanded();
                cardTo = card;
                widthTo = VersionCardExpandedWidth;
                headerTo = header;
                _expandedCardHeight = cardTo;
                _expandedCardWidth = widthTo;
                _expandedHeader = headerTo;
                // 宿主格还没量出宽度（页面尚未布局）时这份数据不可用，别缓存，下次重新量。
                _expandedMetricsValid = widthTo > 1;

                // 量完立刻把树写回起点：UpdateLayout 只重排、不渲染，同一个 UI 回合里写回去，
                // 中间那帧满高满宽的状态不会被看见（补间第一帧照样从起点起手）。
                VersionCard.Height = cardFrom;
                VersionCard.Margin = new Thickness(0);
                VersionHeader.Height = headerFrom;
                VersionHeader.Margin = new Thickness(0, 0, 0, gapFrom);
            }

            // 展开途中这两块要在台面上（每帧给它们 0 → 撑满）；此刻它们还停在收起态的 Collapsed。
            VersionHeader.Visibility = Visibility.Visible;
            VersionListArea.Visibility = Visibility.Visible;
            // 列表区高度交回 Auto：它是卡内那一行 *，卡高、卡头、间距、紧凑行补到哪，
            // 剩下的空间就是它 —— 与上面那条恒等式等价，不必再逐帧写它的高度。
            VersionListArea.Height = double.NaN;
            gapTo = VersionHeaderGap;
            compactTo = 0;
            shiftTo = 0;     // 展开态卡片本就撑满整列，没有额外位移可言
        }

        // 还没布局（初始化那一帧）拿不到可插值的起点、或起终点已经重合时没有动画可言，直接落终态。
        bool motion = animate && _s.Motion.Enabled
            && cardFrom > 1 && cardTo > 1 && widthTo > 1
            && (Math.Abs(cardTo - cardFrom) > 0.5 || Math.Abs(widthTo - widthFrom) > 0.5
                || Math.Abs(shiftTo - shiftFrom) > 0.5);

        UpdateVersionCollapseChrome(collapsed);
        // 这一列的内容是往上收，刻度方向与右栏那颗正好反向：展开态朝上（按一下卷起列表），收起态朝下。
        MotionFx.RotateChevron(VersionHeaderChevron, collapsed ? 0f : 180f, motion);
        MotionFx.RotateChevron(VersionCompactChevron, 0f, motion);
        FadeVersionCardContent(collapsed, motion);
        FadeVersionShadow(collapsed, motion);

        if (!motion)
        {
            ApplyVersionTerminal(collapsed);
            return;
        }

        // 卡宽是显式值时对齐不能是 Stretch：Stretch 配显式宽会变成居中，收起时会看到卡片从两边一起往里缩。
        // 收起态靠右站（右边缘钉住），再靠负的右边距整体右移一格空档，挪到启动卡左边；
        // 展开态满宽时与 Stretch 视觉等价、尺寸变化不跳。
        // 竖向一律 Bottom：这一格是 *，Stretch 配显式高会把卡片在整列里居中，Top 又会让窄条飘到列顶；
        // 底边对齐 + 收起态不含额外底部留白，窄条才会稳稳落在列底、与右栏启动卡底边齐平。
        // 这里写的正是起点，视觉上不动一根头发。
        VersionCard.HorizontalAlignment = HorizontalAlignment.Right;
        VersionCard.VerticalAlignment = VerticalAlignment.Bottom;
        VersionCard.Width = widthFrom;
        VersionCard.Margin = new Thickness(0, 0, -shiftFrom, 0);
        // 投影层自己不参与布局（负边距环带、期望尺寸为 0），尺寸、留白与位移只能手工跟着卡片走，先落到起点。
        VersionCardShadow.HorizontalAlignment = HorizontalAlignment.Right;
        VersionCardShadow.Width = widthFrom;
        VersionCardShadow.Height = cardFrom;
        VersionCardShadow.VerticalAlignment = VerticalAlignment.Bottom;
        VersionCardShadow.Margin = new Thickness(0, 0, -shiftFrom, 0);

        // 补间途中紧凑行里的行模板会短暂超出卡片内沿，挂一层裁剪逐帧跟随。
        // 裁剪框直接由补间值算（见 StartVersionTween），不等 SizeChanged —— 布局回调晚于补间帧，
        // 那一拍里溢出的紧凑行内容会画到卡片下沿之外，看着就是「收起时往下抖一下」。
        _versionClip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, widthFrom - VersionCardChrome, cardFrom - VersionCardChrome),
        };
        VersionCardBody.Clip = _versionClip;

        StartVersionTween(
            cardFrom, cardTo, widthFrom, widthTo, headerFrom, headerTo, gapFrom, gapTo,
            compactFrom, compactTo, shiftFrom, shiftTo,
            collapsed ? MotionFx.DrawerCollapseMs : MotionFx.DrawerExpandMs);
    }

    /// <summary>展开态卡宽：卡片撑满左列那一格，所以取宿主格子的实际宽。</summary>
    private double VersionCardExpandedWidth
        => VersionCard.Parent is FrameworkElement host && host.ActualWidth > 1
            ? host.ActualWidth
            : double.NaN;

    /// <summary>
    /// 收起态版本窄条的额外右移量：正好是右列（启动卡那一列）窄卡自己让出来的空档。
    /// 版本窄条在左列里靠右站之后，两卡之间还隔着「两列间的 24 + 这道空档」；
    /// 把这道空档整份右移补掉，窄条就紧贴到启动卡左边，间距回到与两列间距一致的 24。
    /// 启动卡还撑着整列时返回 0 —— 否则窄条会钻到它底下（右列是先声明的，卡片要叠在上面）。
    /// 卡宽取的是稳态该有的值，不读 <c>ActualWidth</c>：布局回调晚于补间末帧，
    /// 末帧那一拍量到的还是补到一半的宽，会把空档算歪。
    /// </summary>
    private double VersionCollapsedShift
    {
        get
        {
            if (HomeContent.ColumnDefinitions.Count < 2) return 0;
            double colW = HomeContent.ColumnDefinitions[1].ActualWidth;
            if (colW < 1) return 0;   // 布局未就绪：宁可不挪，也别算出一个歪位置
            double slack = colW - (_cardExpanded ? colW : CardCompactWidth);
            return slack > 1 ? slack : 0;
        }
    }

    /// <summary>
    /// 量出展开态的「撑满高」与卡头自然高（会把两块放回布局，调用方随即用补间把它们压回起点）。
    /// 卡高不再由内容决定：卡片是左列那一格（*）的子元素，Height=NaN + Stretch 就撑满整格，
    /// 列表区（卡内那一行 *）随之吃掉余下的空间、在卡内滚动。
    /// </summary>
    private (double Card, double Header) MeasureVersionExpanded()
    {
        VersionHeader.Visibility = Visibility.Visible;
        VersionListArea.Visibility = Visibility.Visible;
        VersionHeader.Height = double.NaN;
        VersionListArea.Height = double.NaN;
        VersionHeader.Margin = new Thickness(0, 0, 0, VersionHeaderGap);
        VersionCard.Height = double.NaN;
        VersionCard.VerticalAlignment = VerticalAlignment.Stretch;
        VersionCard.Margin = new Thickness(0);
        // 卡宽也一并放回整列再量：折叠态是 280 的窄卡，列表里的行（名称列写死 260）在窄卡里
        // 是横向挤着的，先摊开再量才与展开后真正落的高度一致。
        VersionCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        VersionCard.Width = double.NaN;
        VersionCardBody.UpdateLayout();
        return (VersionCard.ActualHeight, VersionHeader.ActualHeight);
    }

    /// <summary>
    /// 六段尺寸（卡高 / 卡宽 / 卡头高 / 卡头下间距 / 紧凑行高 / 右移量）的补间。
    /// 与右栏卡片同一套理由：高度是依赖型属性、上不了合成层，
    /// <c>Storyboard</c> 在本进程里对它的支持也不可用，所以逐帧解算，
    /// 每帧按同一条进场曲线把六段一起落位，末帧交给 <see cref="FinishVersionAnimation"/>。
    /// 列表区高度不在这里 —— 它由卡片内那一行 * 自动吸收（见该类顶部注释）。
    /// 右移量走同一条曲线：宽在收、位置也要同步挪到启动卡左边，分两条曲线就会看出「先缩再挪」两步。
    /// </summary>
    private void StartVersionTween(
        double cardFrom, double cardTo, double widthFrom, double widthTo,
        double headerFrom, double headerTo, double gapFrom, double gapTo,
        double compactFrom, double compactTo, double shiftFrom, double shiftTo, int ms)
    {
        StopVersionTween();
        var clock = Stopwatch.StartNew();
        _versionClock = clock;

        // 曲线与合成层动画共用 MotionFx.CreateEnterSpline()，两条腿的节奏才一致。
        var spline = MotionFx.CreateEnterSpline();

        void OnFrame(object? sender, object args)
        {
            // 中途重入时旧回调可能还排在队列里，时钟对不上就直接出局。
            if (!ReferenceEquals(_versionClock, clock)) return;

            double p = Math.Clamp(clock.Elapsed.TotalMilliseconds / ms, 0d, 1d);
            double t = MotionFx.EaseProgress(spline, p);
            VersionCard.Height = cardFrom + (cardTo - cardFrom) * t;
            VersionCard.Width = widthFrom + (widthTo - widthFrom) * t;
            // 右移量也走同一条曲线：它不在尺寸里，跟不上末帧会跳一下（与右栏 StartCardTween 同理）。
            double shift = shiftFrom + (shiftTo - shiftFrom) * t;
            VersionCard.Margin = new Thickness(0, 0, -shift, 0);
            VersionHeader.Height = headerFrom + (headerTo - headerFrom) * t;
            VersionHeader.Margin = new Thickness(0, 0, 0, gapFrom + (gapTo - gapFrom) * t);
            VersionCompactRow.Height = compactFrom + (compactTo - compactFrom) * t;
            VersionCardShadow.Width = VersionCard.Width;
            VersionCardShadow.Height = VersionCard.Height;
            VersionCardShadow.Margin = VersionCard.Margin;
            // 裁剪框与各段高度同一帧落值，紧凑行溢出的部分当帧就被切掉（等 SizeChanged 会晚一拍）。
            if (_versionClip is not null)
                _versionClip.Rect = new Rect(0, 0,
                    VersionCard.Width - VersionCardChrome, VersionCard.Height - VersionCardChrome);
            if (p >= 1d) FinishVersionAnimation();
        }

        _versionFrame = OnFrame;
        CompositionTarget.Rendering += _versionFrame;
    }

    /// <summary>停下进行中的高度补间（收尾 / 重入都要）。</summary>
    private void StopVersionTween()
    {
        if (_versionFrame is null) return;
        CompositionTarget.Rendering -= _versionFrame;
        _versionFrame = null;
        _versionClock = null;
    }

    /// <summary>动画收尾：展开态交回 Auto 并撤掉裁剪；收起态落显式紧凑高度、把卡头与列表区移出布局。</summary>
    private void FinishVersionAnimation()
    {
        StopVersionTween();
        ApplyVersionTerminal(_versionsCollapsed);
    }

    /// <summary>落稳态。展开态交回 Auto（撑满左列那一格），收起态写死紧凑尺寸并只留紧凑行。</summary>
    private void ApplyVersionTerminal(bool collapsed)
    {
        VersionCard.Height = collapsed ? VersionCardCompactHeight : double.NaN;
        // 收起态窄条靠右站、再右移一格右列空档，紧挨到启动卡左边（见 VersionCollapsedShift）；
        // 展开态交回 Stretch 撑满整列（Width 落 Auto，不再吃显式值）。
        VersionCard.HorizontalAlignment = collapsed ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        // 竖向：展开态必须 Stretch —— 这一格是 *，只有 Stretch + Height=NaN 才撑满；
        // 收起态 Bottom，窄条才会落在列底、与右栏启动卡底边齐平，而不是贴在列顶或整列居中。
        VersionCard.VerticalAlignment = collapsed ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        VersionCard.Width = collapsed ? VersionCardCompactWidth : double.NaN;
        // 底部不留额外留白：收起态芯片的底边与展开态卡片的底边落在同一条线上，两态页边距相等。
        double shift = collapsed ? VersionCollapsedShift : 0;
        VersionCard.Margin = new Thickness(0, 0, -shift, 0);
        VersionHeader.Height = collapsed ? 0 : double.NaN;
        VersionHeader.Margin = new Thickness(0, 0, 0, collapsed ? 0 : VersionHeaderGap);
        VersionListArea.Height = collapsed ? 0 : double.NaN;
        VersionCompactRow.Height = collapsed ? VersionCompactRowHeight : 0;
        VersionHeader.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        VersionListArea.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        VersionCompactRow.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        // 投影层只在收起态露面，尺寸、留白与位移跟着窄条走（展开态交回 Auto，撤下时不留一个 280 宽的空层）。
        VersionCardShadow.HorizontalAlignment = collapsed ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        VersionCardShadow.Width = collapsed ? VersionCardCompactWidth : double.NaN;
        VersionCardShadow.Height = collapsed ? VersionCardCompactHeight : double.NaN;
        VersionCardShadow.VerticalAlignment = VerticalAlignment.Bottom;
        VersionCardShadow.Margin = VersionCard.Margin;
        VersionCardShadow.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        VersionCardBody.Clip = null;
        _versionClip = null;

        // 淡入淡出（DrawerFadeMs）先于高度补间跑完，这里复位只是为了把合成层上的透明度动画撤干净。
        MotionFx.Reset(VersionHeader);
        MotionFx.Reset(VersionListArea);
        MotionFx.Reset(VersionCompactRow);
        MotionFx.Reset(VersionCardShadow);
    }

    /// <summary>
    /// 投影层的进退场（只在收起态露面）：收起时随窄条一起淡进，展开时先淡出、等补间收尾再撤下。
    /// <para>宽度不在这里定 —— 补间途中每帧跟着卡片走（见 <see cref="StartVersionTween"/>），
    /// 稳态由 <see cref="ApplyVersionTerminal"/> 落值。</para>
    /// </summary>
    private void FadeVersionShadow(bool collapsed, bool motion)
    {
        if (!motion)
        {
            // 动效关闭时靠 Visibility 说话，透明度必须先复位（否则会带着上一轮的 0 露面）。
            if (collapsed) VersionCardShadow.Visibility = Visibility.Visible;
            MotionFx.Reset(VersionCardShadow);
            return;
        }

        if (collapsed)
        {
            VersionCardShadow.Visibility = Visibility.Visible;
            MotionFx.FadeIn(VersionCardShadow, MotionFx.DrawerFadeMs);
        }
        else
        {
            // DrawerFadeMs 短于 DrawerExpandMs：淡完才轮到终态收回，不会半路断在半透明上。
            MotionFx.FadeOut(VersionCardShadow, MotionFx.DrawerFadeMs);
        }
    }

    /// <summary>卡头与列表区 / 紧凑行的进出场：收起是两块淡出（高度归零后再折叠），展开是淡入。</summary>
    private void FadeVersionCardContent(bool collapsed, bool motion)
    {
        if (!motion)
        {
            // 动效关闭时不能交给 MotionFx.FadeOut —— 它把合成透明度抬回 1，
            // 对「收起后就不该看见」的两块正好是反的；这里复位到可见态，再用 Visibility 说话。
            MotionFx.Reset(VersionHeader);
            MotionFx.Reset(VersionListArea);
            MotionFx.Reset(VersionCompactRow);
            return;
        }

        if (collapsed)
        {
            // 紧凑行先上台面再淡进：它此刻还停在收起态的 Collapsed（上一次展开的终态），
            // 淡在一个不可见元素上等于没淡，直到补间末帧才整块蹦出来 —— 就是「收起时闪一下」。
            // 提前露出来不会和上面正在退场的列表打架：行底始终贴着卡片内沿、内容锚在行顶，
            // 溢出行外的部分被 VersionCardBody 的裁剪吃掉（见该类顶部注释）。
            VersionCompactRow.Visibility = Visibility.Visible;
            MotionFx.FadeOut(VersionHeader, MotionFx.DrawerFadeMs);
            MotionFx.FadeOut(VersionListArea, MotionFx.DrawerFadeMs);
            MotionFx.FadeIn(VersionCompactRow, MotionFx.DrawerFadeMs);
        }
        else
        {
            MotionFx.FadeIn(VersionHeader, MotionFx.DrawerFadeMs);
            MotionFx.FadeIn(VersionListArea, MotionFx.DrawerFadeMs);
            MotionFx.FadeOut(VersionCompactRow, MotionFx.DrawerFadeMs);
        }
    }

    /// <summary>紧凑行显示当前选中的那一版（只取图标与版本名，见 VersionCompactTemplate）。</summary>
    private void SyncCompactRow() => VersionCompactPresenter.Content = VersionList.SelectedItem;

    /// <summary>没有已安装版本时不给收起（收起态会剩下一张空卡），并强制摊开。</summary>
    private void RefreshVersionCollapseAvailability()
    {
        bool can = _rows.Count > 0;
        VersionCollapseBtn.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        if (!can && _versionsCollapsed) SetVersionsCollapsed(false, animate: false);
    }

    /// <summary>两颗刻度按钮的提示与无障碍名跟着状态走（同一时刻只有一颗在台面上，赋值相同即可）。</summary>
    private void UpdateVersionCollapseChrome(bool collapsed)
    {
        var key = collapsed ? "展开版本列表" : "收起版本列表";
        Loc.SetTip(VersionCollapseBtn, key);
        Loc.SetName(VersionCollapseBtn, key);
        Loc.SetTip(VersionCompactBtn, key);
        Loc.SetName(VersionCompactBtn, key);
    }

    // ── 启动页状态机：ApplyStage 集中驱动（launch-overlay-design.md §3.2 表） ──

    /// <summary>统一驱动标题 / 步骤文字 / 进度条 / 按钮可见性 / 错误行，避免散落赋值。
    /// 同时把阶段同步到调度中心（悬浮任务面板可见启动进度）。</summary>
    private void ApplyStage(LaunchStage stage)
    {
        _stage = stage;
        _stageTimer.Stop();
        LaunchErrorText.Visibility = stage == LaunchStage.Failed ? Visibility.Visible : Visibility.Collapsed;

        // 调度中心同步：任务在 ShowOverlay 登记，贯穿整局（运行期间保持「进行中·游戏运行中」），
        // 只有启动失败 / 启动阶段取消两个终态在这里收尾；游戏退出的收尾在 HandleGameExitedAsync。
        var tc = Starcloud.Core.Services.TaskCenterService.Instance;
        if (_launchTask is not null)
        {
            switch (stage)
            {
                case LaunchStage.Failed:
                    tc.Fail(_launchTask, LaunchErrorText.Text is { Length: > 0 } m ? m : Loc.T("启动失败"));
                    _launchTask = null;
                    break;
                case LaunchStage.Idle:
                    // 收回启动页（取消登录 / 取消启动 / 返回主页）要收尾，否则任务会一直挂在「运行中」，
                    // 还停在最后一步的文案上（例如「验证账户凭据…」）。但游戏已拉起时除外：
                    // 此时任务要贯穿整局，由退出回调负责收尾（例如「运行中点返回主页」）。
                    if (_gameProcess is { HasExited: false }) break;
                    tc.Cancel(_launchTask);
                    _launchTask = null;
                    break;
            }
        }

        switch (stage)
        {
            case LaunchStage.VerifyingCredentials:
                SetStageUi(Loc.T("正在启动"), Loc.T("验证账户凭据…"), 0, 10);
                break;
            case LaunchStage.EnvChecking:
                SetStageUi(Loc.T("正在启动"), Loc.T("检查运行环境…"), 0, 10);
                break;
            case LaunchStage.Retrying:
                SetStageUi(Loc.T("正在重试"), Loc.T("启动失败，正在重试…"), 0, 25);
                break;
            case LaunchStage.CheckingFiles:
                SetStageUi(Loc.T("正在启动"), Loc.T("检查游戏文件完整性…"), 10, 25);
                break;
            case LaunchStage.RepairingFiles:
                // 真进度阶段：Progress 回调映射 25→75，这里不爬升
                SetStageUi(Loc.T("正在启动"), Loc.T("补全缺失文件…"), 25, null);
                break;
            case LaunchStage.StartingProcess:
                SetStageUi(Loc.T("正在启动"), Loc.T("构建启动命令…"), 75, 85);
                break;
            case LaunchStage.WaitingWindow:
                SetStageUi(Loc.T("已启动游戏"), Loc.T("等待游戏窗口出现…"), 85, null);
                LaunchProgress.IsIndeterminate = true; // 不确定态闪烁
                _windowDeadline = Environment.TickCount64 + 120_000;
                _windowTimer.Start();
                break;
            case LaunchStage.Running:
                _windowTimer.Stop();
                LaunchStageTitle.Text = Loc.T("已启动游戏");
                LaunchStageTitle.Foreground = null;
                LaunchStageStepText.Text = Loc.T("游戏运行中");
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 100;
                break;
            case LaunchStage.Exited:
                _windowTimer.Stop();
                LaunchStageTitle.Text = Loc.T("游戏已退出");
                LaunchStageTitle.Foreground = null;
                LaunchStageStepText.Text = SessionDurationText(
                    Math.Max(0, Environment.TickCount64 - _runningStart));
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 0;
                break;
            case LaunchStage.Failed:
                _windowTimer.Stop();
                LaunchStageTitle.Text = Loc.T("启动失败");
                LaunchStageTitle.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 0;
                break;
            default: // Idle
                LaunchStageTitle.Text = Loc.T("正在启动");
                LaunchStageTitle.Foreground = null;
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 0;
                break;
        }

        // 调度中心：阶段文本/进度刷新后同步（此时 UI 值已是新阶段）
        if (_launchTask is not null)
            tc.Update(_launchTask,
                progress: LaunchProgress.IsIndeterminate ? null : LaunchProgress.Value / 100.0,
                detail: LaunchStageStepText.Text);

        // 按钮：启动中只留「取消」；Running/Exited/Failed 只留「返回主页」
        var launching = stage is >= LaunchStage.VerifyingCredentials and <= LaunchStage.WaitingWindow;
        LaunchCancelBtn.Visibility = launching ? Visibility.Visible : Visibility.Collapsed;
        LaunchBackBtn.Visibility = !launching && stage != LaunchStage.Idle
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>统一设置标题/步骤/进度；climbTo 非空时从 from 起爬升到区间顶（250ms +0.5%）。</summary>
    private void SetStageUi(string title, string step, double from, double? climbTo)
    {
        LaunchStageTitle.Text = title;
        LaunchStageTitle.Foreground = null;
        LaunchStageStepText.Text = step;
        LaunchProgress.IsIndeterminate = false;
        LaunchProgress.Value = from;
        _climbTo = climbTo ?? double.MaxValue;
        if (climbTo > from) _stageTimer.Start();
    }

    private void StageTimer_Tick()
    {
        // 氛围进度：区间内每 250ms +0.5%，到顶即停（不承诺精确，PCL2 同款）
        LaunchProgress.Value = Math.Min(_climbTo, LaunchProgress.Value + 0.5);
        if (LaunchProgress.Value >= _climbTo) _stageTimer.Stop();
    }

    private void WindowTimer_Tick()
    {
        if (_stage != LaunchStage.WaitingWindow) { _windowTimer.Stop(); return; }
        if (Environment.TickCount64 >= _windowDeadline)
        {
            ApplyStage(LaunchStage.Running); // 超时兜底：不阻塞用户（可能检测失效）
            return;
        }
        try
        {
            _gameProcess?.Refresh();
            if (_gameProcess is { HasExited: false } p && p.MainWindowHandle != IntPtr.Zero)
                ApplyStage(LaunchStage.Running);
        }
        catch { /* 句柄异常继续轮询，超时兜底 */ }
    }

    /// <summary>进入启动页（spec §3.4）：赋版本/登录方式 → 静态贴士 → 淡入 → 进入凭据阶段。</summary>
    private void ShowOverlay(string versionId, string mode)
    {
        _cancelRequested = false;
        _gameProcess = null; // 本次启动页尚未拉起进程；多开时不要误指上一次启动的那一局
        LaunchCancelBtn.Content = Loc.T("取消");
        LaunchCancelBtn.IsEnabled = true;
        LaunchVersionText.Text = versionId;
        LaunchLoginModeText.Text = mode switch
        {
            "external" => Loc.T("第三方登录"),
            "offline" => Loc.T("离线登录"),
            _ => Loc.T("正版登录"),
        };
        LaunchTipText.Text = LaunchTips.Next();
        LaunchErrorText.Text = "";
        _retryCount = 0;
        _crashDialogOpen = false;
        ClearLogBuffer();
        LaunchLogToggle.IsChecked = _s.Settings.ShowGameLogOnLaunch; // 触发 LaunchLogToggle_Changed 同步可见性
        _logTimer.Start();
        // 启动页进场：并入统一的 MotionFx（合成层淡入 + 轻微上移），不再自绘 Storyboard
        MotionFx.Reset(LaunchOverlay);
        LaunchOverlay.Visibility = Visibility.Visible;
        MotionFx.FadeSlideIn(LaunchOverlay, 14f);

        // 调度中心登记：这条任务贯穿整局，运行期间保持「进行中·游戏运行中」，退出时由退出回调收尾
        _launchTask = Starcloud.Core.Services.TaskCenterService.Instance
            .Begin(Loc.T("启动游戏 {0}", versionId));

        ApplyStage(LaunchStage.VerifyingCredentials);

        // Nova 动态贴士（克制版，spec §3.5）：已登录 Starcloud ID 且 30% 概率；
        // 结果入队尾，下一次启动才显示；失败/超时静默，不碰 UI
        if (_s.StarcloudId.Current is not null && Random.Shared.Next(10) < 3)
            _ = FetchNovaTipAsync(versionId);
    }

    /// <summary>收起启动页回主页（spec §3.4）：状态机回 Idle，可选右栏状态文字。
    /// 不清 _cancelRequested：取消杀进程后 Exited 回调仍需感知（标记由 ShowOverlay 复位）。</summary>
    private async void HideOverlay(string? statusText)
    {
        _logTimer.Stop();
        ApplyStage(LaunchStage.Idle);
        // 退场也走同一套：先淡出再收起（不直接消失）
        if (_s.Motion.Enabled)
        {
            MotionFx.FadeOut(LaunchOverlay, 140);
            await Task.Delay(160);
        }
        LaunchOverlay.Visibility = Visibility.Collapsed;
        MotionFx.Reset(LaunchOverlay);
        if (statusText is not null)
        {
            // 收起态里没地方摆状态文字（只剩启动行那一排），先把卡片放出来再写。
            if (!_cardExpanded) SetCardExpanded(true);
            StatusText.Text = statusText;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    // ── 实时游戏日志（spec §5）：后台线程只塞缓冲，UI 线程 100ms 批量刷新（上限 200 行） ──

    private void OnGameOutputLine(string line)
    {
        lock (_logFlushBuffer)
        {
            _logFlushBuffer.Add(line);
            if (_logFlushBuffer.Count > LogMaxLines)
                _logFlushBuffer.RemoveRange(0, _logFlushBuffer.Count - LogMaxLines);
            _logPending++;
        }
    }

    private void FlushLogBuffer()
    {
        string[] lines;
        lock (_logFlushBuffer)
        {
            if (_logPending == 0) return;
            _logPending = 0;
            lines = [.. _logFlushBuffer];
        }
        LaunchLogText.Text = string.Join('\n', lines);
        if (LaunchLogText.Visibility == Visibility.Visible)
        {
            LaunchLogText.SelectionStart = LaunchLogText.Text.Length;
            LaunchLogText.SelectionLength = 0;
        }
    }

    private void ClearLogBuffer()
    {
        lock (_logFlushBuffer)
        {
            _logFlushBuffer.Clear();
            _logPending = 0;
        }
        LaunchLogText.Text = "";
    }

    private void LaunchLogToggle_Changed(object sender, RoutedEventArgs e)
    {
        var show = LaunchLogToggle.IsChecked == true;
        LaunchLogToggle.Content = show ? Loc.T("隐藏日志") : Loc.T("显示日志");
        LaunchLogText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LaunchLogOpenBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) FlushLogBuffer();
    }

    /// <summary>打开本局日志文件；尚未生成时打开日志目录</summary>
    private void LaunchLogOpen_Click(object sender, RoutedEventArgs e)
    {
        var path = _gameProcess?.LogFile;
        try
        {
            if (path is not null && File.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo { FileName = LauncherPaths.GameLogsDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LocalErrorBar.Message = Loc.T("打开日志失败：{0}", ex.Message);
            LocalErrorBar.IsOpen = true;
        }
    }

    /// <summary>Nova 生成一条动态贴士入队尾（下一次启动显示；8 秒超时，失败静默，spec §3.5）。</summary>
    private async Task FetchNovaTipAsync(string versionId)
    {
        try
        {
            using var cts = new CancellationTokenSource(8_000);
            var reply = await _s.Nova.ChatAsync(
                $"用一句话（≤40字）生成一条面向「{versionId}」玩家的启动小贴士，不要解释。",
                _s.Settings, null, cts.Token);
            LaunchTips.Enqueue(reply.Text);
        }
        catch { /* 静默：动态贴士失败无任何 UI 提示 */ }
    }

    // ── 启动页按钮：取消 / 返回主页（spec §3.4） ──
    private void LaunchCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cancelRequested = true;
        LaunchCancelBtn.Content = Loc.T("正在取消…");
        LaunchCancelBtn.IsEnabled = false;
        if ((_stage is LaunchStage.WaitingWindow or LaunchStage.Running) && _gameProcess is { } session)
        {
            // 已拉起：只结束本局进程（按 pid 精确匹配，多开时不影响其它实例），
            // Exited 回调感知取消标记 → 直接收层回主页
            try { _s.Runtime.Kill(session.Pid); } catch { /* 已退出等情况忽略 */ }
        }
        // 未拉起：等当前 await 完成后的阶段边界检查退出（LaunchBtn_Click）
    }

    private void LaunchBackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == LaunchStage.Running)
        {
            // 游戏仍在运行：只收层，主页复用「停止游戏」第二击路径（不杀进程）
            HideOverlay(Loc.T("游戏运行中"));
            UpdateVersionMeta();
        }
        else
        {
            HideOverlay(null);
        }
    }

    /// <summary>覆盖层显示期间导航离开：按取消语义处理（spec §3.4）。</summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (LaunchOverlay.Visibility != Visibility.Visible) return;
        _stageTimer.Stop();
        _windowTimer.Stop();
        if (_stage is LaunchStage.WaitingWindow or LaunchStage.Running)
            return; // 已拉起：不杀游戏只放弃视图，Exited 回调收尾
        _cancelRequested = true; // 未拉起：让后台启动流程在阶段边界自行退出
    }

    // ── 启动 / 停止 ──
    private void LaunchBtn_Click(object s, RoutedEventArgs e)
    {
        var versionId = (VersionList.SelectedItem as LocalVersionRow)?.Id;
        if (string.IsNullOrEmpty(versionId)) return;

        // 多开：按钮只反映「选中版本」的运行情况。
        // 选中的版本正在跑 → 结束它（按 pid 精确结束，其它实例不受影响）；没在跑 → 再开一局。
        var pid = RunningPidFor(versionId);
        if (pid != 0)
        {
            try { _s.Runtime.Kill(pid); } catch { /* ignore */ }
            return;
        }

        _ = StartLaunchAsync(versionId);
    }

    /// <summary>统一启动入口：主页按钮与版本列表页「启动」均走此流程。</summary>
    private async Task StartLaunchAsync(string versionId)
    {
        LaunchBtn.IsEnabled = false;
        GlobalErrorBar.IsOpen = false;
        ShowOverlay(versionId, _s.LaunchAccount.Mode);
        try
        {
            // 共享就地登录流程：会话内无凭证（token 不落盘）时弹登录框，登录后自动重试
            var player = await AuthDialogs.EnsurePlayableAccountAsync(XamlRoot, _s);
            if (player is null) { HideOverlay(null); return; } // 用户取消登录
            if (_cancelRequested) { HideOverlay(null); return; }

            // 层叠合并（版本管理设计）：全局设置 + 版本档案覆盖 → 生效启动参数
            var dotMc = _s.Settings.DotMinecraft;
            var store = new InstanceSettingsStore(dotMc);
            var profile = store.Get(versionId);
            var gameRoot = InstanceLayout.GameRoot(dotMc, versionId,
                InstanceLayout.IsIsolated(profile, _s.Settings));
            var eff = InstanceLayout.Resolve(dotMc, versionId, profile, _s.Settings,
                MemoryAdvisor.Probe(gameRoot)); // auto 内存档位按实际模组数推荐（spec §8.2）

            // 第三方账户：注入 authlib-injector（Assets 内置 jar + 用户自己的认证服务器）
            string? agentJar = null;
            if (player.ExternalBase is not null)
            {
                agentJar = AppServices.AgentJarPath()
                    ?? throw new InvalidOperationException(
                        "authlib-injector 组件缺失，无法启动第三方账户，请重新安装启动器");
            }

            var options = new LaunchOptions(
                versionId, player.Name, eff.GameRoot, dotMc,
                eff.MinMem, eff.MaxMem, eff.Java, eff.ExtraJvmArgs,
                _s.Settings.UseBuiltinJvmArgs, player.Credentials,
                eff.ServerAddress, eff.Language, eff.LanguageOverride,
                eff.WindowW, eff.WindowH, eff.Fullscreen, eff.EnvVars,
                eff.PreCmd, eff.PostCmd,
                AgentJarPath: agentJar, AgentBase: player.ExternalBase,
                PlayerSkinUrl: player.SkinUrl,
                PluginHook: _s.PluginHost);
            _activeOptions = options;

            // 启动前环境体检（spec §6）：Block 弹窗由用户决定是否继续，Warn 只提示不阻断
            ApplyStage(LaunchStage.EnvChecking);
            var report = await _s.EnvCheck.CheckAsync(dotMc, versionId, options, _s.Settings);
            if (_cancelRequested) { HideOverlay(null); return; }
            if (report.NeedsAttention)
            {
                LauncherLog.Write("launch", $"环境体检 {versionId}：" +
                    string.Join("、", report.Issues.Select(i => $"{i.Code}({i.Level})")));
                if (report.Worst == EnvLevel.Block)
                {
                    var go = await EnvCheckDialogs.ShowAsync(XamlRoot, report,
                        issue => FixEnvIssueAsync(issue, versionId, options));
                    if (!go) { HideOverlay(null); return; }
                }
                else
                {
                    LaunchStageStepText.Text = Loc.T("环境提示：{0}", Loc.TCore(report.Issues[0].Title));
                }
            }
            if (_cancelRequested) { HideOverlay(null); return; }

            // 闭包从 _activeOptions 取参数：体检一键修复后能立刻用上新内存档位
            _relaunch = () => AcquireAndLaunchAsync(dotMc, versionId, _activeOptions ?? options);
            await _relaunch();
        }
        catch (Exception ex)
        {
            if (_cancelRequested) { HideOverlay(null); return; }
            // 启动期间的异常显示在启动页（spec §3.4），不再弹 LocalErrorBar
            if (LaunchOverlay.Visibility == Visibility.Visible)
            {
                LaunchErrorText.Text = ex.Message;
                ApplyStage(LaunchStage.Failed);
            }
            else
            {
                LocalErrorBar.Message = ex.Message;
                LocalErrorBar.IsOpen = true;
            }
        }
        finally
        {
            LaunchBtn.IsEnabled = true;
            UpdateVersionMeta();
        }
    }

    /// <summary>预检补全 → 拉起进程（首次启动与自动重试共用）。多开：同版本也允许再开一局。</summary>
    private async Task AcquireAndLaunchAsync(string dotMc, string versionId, LaunchOptions options)
    {
        // 启动前预检：缺失/损坏文件全自动补全，进度显示在启动页，补完直接启动
        ApplyStage(LaunchStage.CheckingFiles);
        var preflight = await _s.Preflight.CheckAsync(dotMc, versionId);
        if (_cancelRequested) { HideOverlay(null); return; }
        if (preflight.NeedsRepair)
        {
            if (preflight.VersionJsonBroken && !preflight.CanAutoRestoreJson)
            {
                HideOverlay(null);
                LocalErrorBar.Message = Loc.T("版本 JSON 缺失或损坏，无法自动恢复，请到下载页重新安装");
                LocalErrorBar.IsOpen = true;
                return;
            }
            ApplyStage(LaunchStage.RepairingFiles);
            // 唯一真进度：BatchProgress 线性映射到 25→75%（spec §3.2）
            var progress = new Progress<BatchProgress>(p =>
            {
                LaunchProgress.Value = 25 + p.Percentage * 0.5;
                LaunchStageStepText.Text = Loc.T("补全缺失文件 {0}/{1}（{2}%）…", p.DoneFiles, p.TotalFiles, p.Percentage);
            });
            try
            {
                await _s.Preflight.EnsureReadyAsync(dotMc, versionId, preflight,
                    _s.Settings, progress);
            }
            catch (PreflightException ex)
            {
                var listed = string.Join("\n", ex.FailedFiles.Take(5)
                    .Select(f => "• " + f.Path));
                if (ex.FailedFiles.Count > 5)
                    listed += Loc.T("\n等 {0} 个文件", ex.FailedFiles.Count);
                var dialog = new ThemedDialog
                {
                    Title = "补全失败",
                    Content = listed,
                    PrimaryButtonText = "仍然启动",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                // 取消则不启动（覆盖层一并收起）；「仍然启动」带缺失文件继续
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    HideOverlay(null);
                    return;
                }
            }
            if (_cancelRequested) { HideOverlay(null); return; }
        }

        ApplyStage(LaunchStage.StartingProcess);

        // 版本级启动前命令：非零退出码时询问是否继续（仍可强制启动）
        if (!string.IsNullOrWhiteSpace(options.PreLaunchCommand))
        {
            var pre = await _s.Launcher.RunPreLaunchAsync(options);
            if (pre.ExitCode != 0)
            {
                var tail = pre.Output.Length > 600 ? "…" + pre.Output[^600..] : pre.Output;
                var dialog = new ThemedDialog
                {
                    Title = "启动前命令失败",
                    Content = $"退出码 {pre.ExitCode}\n{tail}",
                    PrimaryButtonText = "仍然启动",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    HideOverlay(null);
                    return;
                }
            }
            if (_cancelRequested) { HideOverlay(null); return; }
        }

        var session = await _s.Launcher.LaunchVersionAsync(dotMc, versionId, options);
        _gameProcess = session;
        // 运行状态监控：本方法拿不到上游解析的 eff（重试路径的 options 可变），就地 Resolve 一次
        var ctxEff = InstanceLayout.Resolve(dotMc, versionId,
            new InstanceSettingsStore(dotMc).Get(versionId), _s.Settings,
            MemoryAdvisor.Probe(options.GameDir));
        _s.Runtime.Attach(session, GameLaunchContextStore.Capture(versionId, ctxEff,
            GameLaunchContextStore.CountFiles(Path.Combine(options.GameDir, "mods")),
            GameLaunchContextStore.CountFiles(Path.Combine(options.GameDir, "shaderpacks")),
            GameLaunchContextStore.CountFiles(Path.Combine(options.GameDir, "resourcepacks"))),
            session.CommandLine);
        // 多开：一局一条记录，各自独立收尾（闭包带上 session，退出回调才知道是哪一局）
        _runs[session.Pid] = new HomeRun { Session = session, Options = options, Task = _launchTask };
        session.OutputLine += OnGameOutputLine;  // 实时日志（spec §5）
        session.Exited += code => OnGameExited(session, code); // 退出收尾 / 崩溃诊断 / 自动重试（spec §7、§8.1）
        if (_cancelRequested)
        {
            // 极短窗口内取消：杀掉刚拉起的进程回主页，Exited 回调续收尾
            try { session.Kill(); } catch { /* ignore */ }
            HideOverlay(null);
            return;
        }
        _runningStart = Environment.TickCount64;
        ApplyStage(LaunchStage.WaitingWindow); // 窗口出现由 _windowTimer 轮询（§3.3）

        // Nova 时刻：游戏启动即开观察会话（未授权录屏则静默）；浮窗监测需要 pid
        _s.NovaRuntime.OnGameLaunched(versionId, session.Id);
        new InstanceSettingsStore(dotMc).TouchPlayed(versionId); // 记录版本档案「上次启动」
        _s.Settings.LastUsername = LaunchAccountService.PrefixFor(options.Credentials?.UserType switch
        {
            "mojang" => "external",
            "legacy" => "offline",
            _ => "microsoft",
        }) + options.PlayerName;
        _s.Settings.LastVersion = versionId;
        _s.Settings.Save();
        LauncherLog.Write("launch", $"已拉起 {versionId}（pid {session.Pid}，退出码待定）");
    }

    /// <summary>体检项一键修复（spec §6）：写版本内存档位；返回是否修复成功。</summary>
    private Task<bool> FixEnvIssueAsync(EnvIssue issue, string versionId, LaunchOptions options)
    {
        try
        {
            var dotMc = _s.Settings.DotMinecraft;
            var (modCount, hasShaders) = MemoryAdvisor.Probe(options.GameDir);
            var totalMb = SystemMemory.Snapshot().TotalMb;
            var suggest = MemoryAdvisor.SuggestMaxMb(totalMb, modCount, hasShaders);

            var store = new InstanceSettingsStore(dotMc);
            var profile = store.Get(versionId);
            profile.MemoryMode = "manual";
            profile.MemoryMinMb = 512;
            profile.MemoryMaxMb = suggest;
            store.Set(versionId, profile);
            store.Save();

            // 本次启动立即生效：_relaunch 每次从 _activeOptions 取参数
            _activeOptions = (_activeOptions ?? options) with { MinMemoryMb = 512, MaxMemoryMb = suggest };
            LauncherLog.Write("launch", $"体检修复 {versionId}（{issue.Code}）→ 内存 512-{suggest} MB，" +
                MemoryAdvisor.Explain(totalMb, modCount, hasShaders));
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("launch", $"体检修复 {versionId} 失败（{issue.Code}）", ex);
            return Task.FromResult(false);
        }
    }

    /// <summary>调度中心：「启动游戏 X」任务收尾（正常完成 / 失败 / 取消），收尾后本实例不再持有。</summary>
    private void FinishLaunchTask(Starcloud.Core.Services.AppTask? task, string detail,
        bool failed = false, bool cancelled = false)
    {
        if (task is null) return;
        var tc = TaskCenterService.Instance;
        if (cancelled) tc.Cancel(task);
        else if (failed) tc.Fail(task, detail);
        else tc.Complete(task, detail);
        if (ReferenceEquals(_launchTask, task)) _launchTask = null;
    }

    /// <summary>本局时长文案（启动页与调度中心收尾共用）</summary>
    private static string SessionDurationText(long uptimeMs)
    {
        var sec = uptimeMs / 1000;
        return sec >= 60
            ? Loc.T("本局时长 {0} 分 {1} 秒", sec / 60, sec % 60)
            : Loc.T("本局时长 {0} 秒", sec);
    }

    /// <summary>游戏进程退出（后台线程）→ 切回 UI 线程收尾。闭包带上 session：多开时分辨是哪一局。</summary>
    private void OnGameExited(GameSession session, int exitCode) =>
        _dq.TryEnqueue(() => _ = HandleGameExitedAsync(session, exitCode));

    /// <summary>退出收尾（spec §7、§8.1）：自动重试 / 崩溃诊断 / 状态展示，最后统一跑收尾命令与 Nova 收尾。
    /// 多开：只有「当前启动页对应的那一局」走完整收尾，后台局只收自己的调度中心任务与收尾命令。</summary>
    private async Task HandleGameExitedAsync(GameSession session, int exitCode)
    {
        _runs.Remove(session.Pid, out var run);
        var options = run?.Options;
        var versionId = session.VersionId;
        var uptimeMs = (long)Math.Max(0, session.Uptime.TotalMilliseconds);
        // 用户主动结束（运行状态页「结束进程」/ 主页「停止游戏」）必须在此刻读：
        // 下面的自动重试会 _relaunch → Attach，把该实例的标记复位。
        // 读 session.KilledByUser 而非 Monitor.KilledByUser：后者按「选中实例」解析，本局收口后就不代表本局了。
        var userStopped = session.KilledByUser;

        // 后台局（本页启动过、但启动页已经转向另一局）：轻量收尾，不碰启动页 / 不重试 / 不弹崩溃框
        if (!ReferenceEquals(session, _gameProcess))
        {
            LauncherLog.Write("launch",
                $"{versionId}（pid {session.Pid}）已退出（退出码 {exitCode}，运行 {uptimeMs / 1000} 秒）");
            FinishLaunchTask(run?.Task, Loc.T("游戏已退出，{0}", SessionDurationText(uptimeMs)));
            await RunExitCleanupAsync(options, novaCleanup: false);
            return;
        }

        var overlayVisible = LaunchOverlay.Visibility == Visibility.Visible && IsLoaded;

        // 自动重试（spec §8.1）：启动即崩且本局未重试过 → 静默重跑一次（不重跑体检）
        if (!_cancelRequested && !userStopped && exitCode != 0 && _retryCount == 0
            && _s.Settings.LaunchAutoRetry && uptimeMs < 8000
            && _relaunch is not null && overlayVisible)
        {
            _retryCount++;
            LauncherLog.Write("launch", $"{versionId} 启动即退出（退出码 {exitCode}，{uptimeMs:F0} ms），自动重试一次");
            ApplyStage(LaunchStage.Retrying);
            await Task.Delay(600); // 让用户看清「正在重试」（spec §8.1 文案）
            try
            {
                await _relaunch();
            }
            catch (Exception ex)
            {
                LauncherLog.Error("launch", $"自动重试启动 {versionId} 失败", ex);
                LaunchErrorText.Text = ex.Message;
                ApplyStage(LaunchStage.Failed);
            }
            return; // 重试期间调度中心任务保持「进行中」，由下一次退出回调收尾
        }

        if (_cancelRequested)
        {
            FinishLaunchTask(run?.Task, "", cancelled: true);
            HideOverlay(null); // 取消路径：静默收层回主页（spec §3.4）
        }
        else if (exitCode == 0)
        {
            LauncherLog.Write("launch", $"{versionId} 正常退出（运行 {uptimeMs / 1000} 秒）");
            FinishLaunchTask(run?.Task, Loc.T("游戏已退出，{0}", SessionDurationText(uptimeMs)));
            if (overlayVisible)
                ApplyStage(LaunchStage.Exited); // 覆盖层显示：停在启动页展示本局时长
        }
        else if (userStopped)
        {
            // 用户在界面里主动结束：进程被强杀必然是非零退出码，但这不是崩溃，安静收尾即可
            LauncherLog.Write("launch", $"{versionId} 已由用户结束（运行 {uptimeMs / 1000} 秒）");
            FinishLaunchTask(run?.Task, Loc.T("已结束游戏，{0}", SessionDurationText(uptimeMs)));
            if (overlayVisible)
                ApplyStage(LaunchStage.Exited);
        }
        else
        {
            LauncherLog.Write("launch", $"{versionId} 异常退出（退出码 {exitCode}，运行 {uptimeMs / 1000} 秒）");
            FinishLaunchTask(run?.Task, Loc.T("游戏异常退出（退出码 {0}）", exitCode), failed: true);
            if (overlayVisible)
            {
                LaunchStageTitle.Text = "游戏异常退出";
                LaunchStageTitle.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
                LaunchStageStepText.Text = "正在分析崩溃原因…";
                await ShowCrashDialogAsync(session, options, versionId, exitCode);
            }
        }

        await RunExitCleanupAsync(options, novaCleanup: true);
    }

    /// <summary>异常退出：本地规则诊断 → 崩溃弹窗（spec §7）。</summary>
    private async Task ShowCrashDialogAsync(GameSession? session, LaunchOptions? options,
        string versionId, int exitCode)
    {
        if (_crashDialogOpen) return;
        _crashDialogOpen = true;
        try
        {
            var dotMc = _s.Settings.DotMinecraft;
            var store = new InstanceSettingsStore(dotMc);
            var gameRoot = options?.GameDir ?? InstanceLayout.GameRoot(dotMc, versionId,
                InstanceLayout.IsIsolated(store.Get(versionId), _s.Settings));

            CrashDiagnosis diag;
            try
            {
                diag = await _s.CrashAnalyzer.AnalyzeAsync(dotMc, versionId, exitCode,
                    session?.StartedAt, session?.Tail(60), gameRoot);
            }
            catch (Exception ex)
            {
                LauncherLog.Error("crash", $"崩溃诊断 {versionId} 失败", ex);
                diag = new CrashDiagnosis("游戏异常退出", $"退出码 {exitCode}，本地诊断失败：{ex.Message}",
                    "", "", [], CrashFixKind.None, null, null);
            }
            LauncherLog.Write("crash", $"{versionId} 诊断结论：{diag.Title}（修复动作 {diag.Fix}）");

            await CrashDialog.ShowAsync(XamlRoot, diag, versionId, exitCode,
                restartAsync: async () =>
                {
                    if (_relaunch is null) return;
                    ShowOverlay(versionId, _s.LaunchAccount.Mode); // 复位状态机
                    try { await _relaunch(); }
                    catch (Exception ex)
                    {
                        LaunchErrorText.Text = ex.Message;
                        ApplyStage(LaunchStage.Failed);
                    }
                },
                askNovaAsync: () =>
                {
                    _s.NovaRuntime.SetCurrentInstance(versionId);
                    _s.NovaRuntime.RequestInput(
                        $"游戏 {versionId} 刚刚异常退出（退出码 {exitCode}），帮我看看是什么原因");
                    (App.MainWindow as MainWindow)?.OpenNovaPanel();
                    return Task.CompletedTask;
                });
        }
        finally
        {
            _crashDialogOpen = false;
        }
    }

    /// <summary>退出后统一收尾：刷新版本元数据 → 启动后命令 → Nova 战报（失败不阻断退出流程）。
    /// 多开：Nova 是单会话的，只有启动页对应的那一局退出才收尾，避免误停其它实例的录屏/浮窗。</summary>
    private async Task RunExitCleanupAsync(LaunchOptions? options, bool novaCleanup)
    {
        UpdateVersionMeta();
        if (options is not null)
        {
            try { _s.Launcher.RunPostLaunch(options); }
            catch (Exception ex) { LauncherLog.Error("launch", "启动后命令执行失败", ex); }
        }
        if (!novaCleanup) return;
        try { await _s.NovaRuntime.OnGameExitedAsync(); }
        catch (Exception ex) { LauncherLog.Error("launch", "Nova 收尾失败", ex); }
    }

    // ── 入口 ──
    private void PickVersion_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");

    // ── 启动自检（版本 / 账户 / Java） ──
    private bool? _javaOk;   // null=检测中

    private void Recheck_Click(object s, RoutedEventArgs e) => RefreshGuide();

    private void CheckGoVersion_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");
    private void CheckGoAccount_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("accounts");
    private void CheckGoJava_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("settings");

    private void RefreshGuide()
    {
        var hasInstalled = _rows.Count > 0;
        var hasAccount = _s.Accounts.Accounts.Count > 0;
        SetCheckRow(CheckIconVersion, CheckTextVersion, CheckGoVersion,
            hasInstalled, "游戏版本", hasInstalled ? "就绪" : "未安装", "前往下载");
        SetCheckRow(CheckIconAccount, CheckTextAccount, CheckGoAccount,
            hasAccount, "账户", hasAccount ? "就绪" : "未配置", "前往账户");
        // Java 检测需要拉起 java -version，较慢，异步进行
        _javaOk = null;
        CheckTextJava.Text = "Java：检测中…";
        _ = CheckJavaAsync();
        UpdateCheckCardVisibility();
        RefreshSummaryBar();
    }

    private async Task CheckJavaAsync()
    {
        try
        {
            var runtimes = await _s.Java.ListRuntimesAsync();
            var ok = runtimes.Count > 0 || !string.IsNullOrWhiteSpace(_s.Settings.JavaExecutable);
            _javaOk = ok;
            SetCheckRow(CheckIconJava, CheckTextJava, CheckGoJava, ok, "Java",
                ok ? (runtimes.Count > 0 ? $"就绪（检测到 {runtimes.Count} 个）" : "就绪（使用手动指定路径）") : "未检测到",
                "前往设置");
            RefreshSummaryBar();
        }
        catch
        {
            _javaOk = false;
            SetCheckRow(CheckIconJava, CheckTextJava, CheckGoJava, false, "Java", "检测失败", "前往设置");
        }
        UpdateCheckCardVisibility();
    }

    /// <summary>收起态下左列顶部的状态摘要：版本数 / 账户 / Java，一眼看清缺什么</summary>
    private void RefreshSummaryBar()
    {
        var hasInstalled = _rows.Count > 0;
        var accounts = _s.Accounts.Accounts.Count;
        SummaryVersionText.Text = hasInstalled ? _rows.Count.ToString() : "0";
        SummaryAccountText.Text = accounts > 0 ? accounts.ToString() : "0";
        SummaryJavaText.Text = _javaOk switch { true => "就绪", false => "缺失", _ => "检测中" };
    }

    private void UpdateCheckCardVisibility()
    {
        var hasInstalled = _rows.Count > 0;
        var hasAccount = _s.Accounts.Accounts.Count > 0;
        // 全部就绪后收起；Java 检测中保持显示以免闪动
        CheckCard.Visibility = !hasInstalled || !hasAccount || _javaOk == false
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetCheckRow(FontIcon icon, TextBlock text, HyperlinkButton go,
        bool ok, string label, string detail, string action)
    {
        icon.Glyph = ok ? "\uEA7C" : "\uEBA6";
        icon.Foreground = ok
            ? ThemeBrush("SystemFillColorSuccessBrush")
            : ThemeBrush("SystemFillColorCriticalBrush") ?? icon.Foreground;
        text.Text = $"{label}：{detail}";
        go.Content = action;
        go.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
}
