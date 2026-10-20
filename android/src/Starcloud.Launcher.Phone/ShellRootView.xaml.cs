// 壳根视图 · 手机版（横屏 + 底部导航）：延续竖屏版的导航设计，用于横屏。
// 结构 = 44px 顶栏 + 内容区 + 56px 底部五格导航（首页/版本/下载/联机/我的），
// 次级入口（存档/运行状态/账户/插件/Nova/日志/设置/插件导航组）收进「我的」聚合页（MorePage）；
// 手机特有：挖孔屏（P40 Pro 前摄）安全边距——MainActivity 捕获 DisplayCutout 内边距，
// 壳按 px→DIP 应用到 RootGrid.Padding，转向换边时经 CutoutChanged 事件即时重排；
// 调度中心为全宽底部抽屉；Nova 面板为全宽底部面板。
// 共享范围：导航路由表 / 调度中心（队列 + 历史通知渲染）/ NovaOrb 五态 / Nova 面板 /
//           浮窗提醒 / 插件通知 / 运行状态圆点 / 个人中心入口。
// 桌面窗口专属（PORTING.md 公约，no-op 并注释）：AppWindow / 标题栏拖拽与自绘窗口按钮 /
//           WindowBackgroundHost 背景三层与亚克力强度 / 自更新重启 UI / 窗口居中与尺寸下限。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Android.Host;
using Starcloud.Android.Host.Runtime;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views;
using Windows.UI;

namespace Starcloud.Launcher.Phone;

public sealed partial class ShellRootView : UserControl
{
    // 底部导航的五个一级路由（tab 高亮只在这些路由间切换；
    // 其余路由（saves/running/accounts/…）从「我的」页或页面内跳转进入，不点亮任何 tab）
    private static readonly string[] TabRoutes = ["home", "versions", "download", "multiplayer", "more"];

    // 当前路由：多个路由可能映射同一页型（如 accounts/accountAdd/accountDetail 均为独立页，
    // 但 home 与未知路由都落 HomePage），不能用 CurrentSourcePageType 判重
    private string? _currentRoute;

    // 叠加式页面切换器：页面实例按路由缓存（等价 NavigationCacheMode=Enabled，自管不设限）；
    // 切换时新页直接叠在旧页上方，Loaded + 首帧渲染完成后才撤旧页——全程无黑屏空档
    private readonly Dictionary<string, Page> _pageCache = new();
    private Page? _currentPage;

    public ShellRootView()
    {
        InitializeComponent();
        AppNavigation.Attach(this);

        // 挖孔屏安全边距（P40 Pro 前摄）：先取当前值，转向/首布局后经事件重排；
        // Unloaded 退订（壳只在进程退出时卸载，兜底而已）
        ApplyCutoutPadding();
        Droid.MainActivity.CutoutChanged += OnCutoutChanged;

        // 任务历史只保留本次会话：启动时清空调度中心（任务列表纯内存，无跨启动持久化）
        TaskCenterService.Instance.ClearAll();

        // 桌面：TransparentAcrylicBackdrop + WindowBackgroundHost 背景三层 + 毛玻璃强度。
        // 安卓无窗口亚克力概念：整块降级 no-op，仅保留主题应用（跟随 AppearanceService，
        // 默认值由设置 Theme 决定，"system" 时跟随系统深浅）。
        AppServices.Instance.Appearance.ApplyTheme(AppServices.Instance.Settings.Theme, this);

        // 调度中心：Changed 可能在后台线程触发，仅置脏标志，UI 由 300ms 定时器统一刷新
        TaskCenterService.Instance.Changed += () => _taskDirty = true;
        NotificationCenter.Instance.Changed += () => _taskDirty = true;
        // Interval 必须显式设置：DispatcherQueueTimer 默认间隔为 0，会「尽最快速度」自旋（见桌面注解）
        _taskRefresh.Interval = TimeSpan.FromMilliseconds(300);
        _taskRefresh.IsRepeating = true;
        _taskRefresh.Tick += (_, _) =>
        {
            RefreshTaskRows();
            WatchGameEvents();
        };
        _taskRefresh.Start();

        // 测试版标识：启动器自身版本号（取自程序集版本）→「测试版 V0.2.0」
        var ver = typeof(AppServices).Assembly.GetName().Version ?? new Version(0, 0, 0);
        VersionBadgeText.Text = Loc.T("测试版 V{0}", $"{ver.Major}.{ver.Minor}.{ver.Build}");

        // Nova 内嵌（NOVA_INTEGRATION.md 五入口）：顶栏 Orb 随全局状态机联动
        var rt = AppServices.Instance.NovaRuntime;
        rt.StateChanged += OnNovaStateChanged;
        rt.OverlayToast += OnOverlayToast; // 浮窗一句话提醒 → TeachingTip
        // 桌面还订阅 NovaGatewayClient.QuotaChanged（云端额度 → 面板标头一行）；
        // 安卓暂无该网关通道，额度一行保持隐藏（见 RefreshPanelQuota）。
        _toastResetTimer.IsRepeating = false;
        _toastResetTimer.Tick += (_, _) =>
        {
            _toastResetTimer.Stop();
            NovaToastTip.IsOpen = false;
        };
        NovaToastTip.Target = NovaOrb;
        // Nova 面板贴底定位：面板高度由内容决定，展开 / 内容变化时重定位（见 ShowNovaPopup）
        NovaPanelHost.SizeChanged += OnNovaPanelSizeChanged;
        ApplyNovaState(rt.State);
        RefreshAccountButton();

        Navigate("home"); // 初始路由：默认停在「首页」（首 tab 选中态由 XAML 给定）

        // 桌面在窗口 Closed 时停常驻定时器（StopBackgroundTimers）；Uno 单窗口无关闭事件，
        // 挂 Unloaded 兜底（壳只在进程退出时卸载，正常路径由系统回收）
        Unloaded += (_, _) =>
        {
            Droid.MainActivity.CutoutChanged -= OnCutoutChanged;
            StopBackgroundTimers();
        };

        // 插件通知（launcher:notification）：内容区底部 InfoBar，8 秒自动收起
        var pluginNotifyTimer = DispatcherQueue.CreateTimer();
        pluginNotifyTimer.IsRepeating = false;
        pluginNotifyTimer.Interval = TimeSpan.FromSeconds(8);
        pluginNotifyTimer.Tick += (_, _) => PluginNotifyTip.IsOpen = false;
        AppServices.Instance.PluginHost.NotifyRequested += (pluginId, title, message) =>
            UiThread.Run(() =>
            {
                var heading = string.IsNullOrWhiteSpace(title) ? pluginId : title;
                PluginNotifyTip.Title = heading;
                PluginNotifyTip.Message = message;
                PluginNotifyTip.IsOpen = true;
                pluginNotifyTimer.Stop();
                pluginNotifyTimer.Start();
                // 同一份通知也进历史流水（底部提示会消失，历史通知留着可查）
                NotificationCenter.Instance.Info($"{heading}：{message}");
            });

        // 插件导航组（contributes.navItems）：底部导航只留五个一级 tab，插件导航项收进
        // 「我的」聚合页，由 MorePage 自行订阅 ContributionsChanged 重建（见 Views/MorePage）。
        // Loaded 只做对话框宿主登记。
        Loaded += (_, _) =>
        {
            Ui.Root = XamlRoot; // 对话框宿主（UiDialog 的 XamlRoot）
        };
    }

    // ── 挖孔屏（摄像头）安全边距 ──

    /// <summary>把 MainActivity 捕获的 DisplayCutout 安全内边距（px）换算成 DIP 落到根网格。
    /// 锁定横屏下 P40 Pro 前摄随重力落在屏幕左或右缘，侧栏/内容因此整体让开摄像头。</summary>
    private void ApplyCutoutPadding()
    {
        var (l, t, r, b) = Droid.MainActivity.CutoutPaddingPx;
        var density = global::Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1f;
        RootGrid.Padding = new Thickness(l / density, t / density, r / density, b / density);
    }

    /// <summary>挖孔内边距变化（正反横切换 / 首次布局）：回 UI 线程重排。</summary>
    private void OnCutoutChanged() => UiThread.Run(ApplyCutoutPadding);

    // ── 游戏起停联动（插件事件 + 运行状态圆点） ──

    /// <summary>上一次广播的「正在运行的版本」；多开时同版本只算一条（安卓同屏仅一局，无多开）</summary>
    private readonly HashSet<string> _pluginEventGameIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 游戏起停广播：桌面用 GameSessionGuard 探测会话锁文件 + java 进程；安卓游戏经 JVM 宿主
    /// 在应用进程内跑，运行态直接看 JvmHost，版本号取启动链选定的版本（GameLauncherService.SelectedVersion）。
    /// 启动广播 game.launched、退出广播 game.exited；钩子在线程池跑，插件回调可做慢操作。
    /// </summary>
    private void WatchGameEvents()
    {
        try
        {
            var host = AppServices.Instance.PluginHost;
            var running = JvmHost.IsRunning;
            UpdateRunningDot(running);

            var id = GameLauncherService.Instance.SelectedVersion ?? "";
            if (running && !string.IsNullOrEmpty(id) && _pluginEventGameIds.Add(id))
            {
                var versionId = id;
                _ = Task.Run(() => host.RaiseEvent("game.launched", new { versionId }));
            }

            // 已无运行实例：把登记过的版本逐一广播 game.exited（安卓单局，等价于桌面多开收敛）
            if (!running && _pluginEventGameIds.Count > 0)
            {
                foreach (var v in _pluginEventGameIds.ToList())
                    _ = Task.Run(() => host.RaiseEvent("game.exited", new { versionId = v }));
                _pluginEventGameIds.Clear();
            }
        }
        catch
        {
            // 广播失败不影响界面（事件是增强能力，不是主流程）
        }
    }

    /// <summary>导航项「运行状态」圆点：有游戏在跑就点亮（横屏延续竖屏版语义：点亮「我的」格）</summary>
    private void UpdateRunningDot(bool running) => TabMore.SetRunningDot(running);

    /// <summary>外观变更即时生效入口（设置页在主题 / 背景改动后调用）。</summary>
    public static void RefreshAppearance()
    {
        // 桌面还重涂 WindowBackgroundHost 三层与亚克力强度；安卓无窗口背景，只重放主题
        AppServices.Instance.Appearance.ApplyTheme(AppServices.Instance.Settings.Theme,
            App.MainWindow?.Content as FrameworkElement);
    }

    /// <summary>
    /// 收尾：停掉常驻的循环 UI 定时器。
    /// 桌面挂在窗口 Closed（循环 DispatcherQueueTimer 会拖住退出流程）；Uno 单窗口无关闭事件，
    /// 由 Unloaded 兜底调用。
    /// </summary>
    private void StopBackgroundTimers()
    {
        try
        {
            _taskRefresh.Stop();
            AppServices.Instance.NovaRuntime.StopTimers();
        }
        catch
        {
            // 收尾失败不阻塞退出
        }
    }

    // ── 导航（底部导航 Tapped → AppNavigation 路由表） ──

    private void BottomTab_Tapped(object sender, TappedRoutedEventArgs e) =>
        Navigate(sender is Views.Controls.BottomTabButton { Tag2: { } tag } ? tag : "home");

    /// <summary>页面内跳转入口：一级路由同步底部导航高亮；次级路由（saves/running/…）
    /// 不点亮任何 tab（从「我的」页进入的功能页回到主页时首 tab 复位）。</summary>
    public void NavigateTo(string tag)
    {
        if (TabRoutes.Contains(tag))
        {
            SetTabSelection(tag);
        }
        Navigate(tag);
    }

    /// <summary>按路由同步五个 tab 的选中态（重复设置无副作用，IsSelected 是普通 DP）。</summary>
    private void SetTabSelection(string tag)
    {
        TabHome.IsSelected = tag == "home";
        TabVersions.IsSelected = tag == "versions";
        TabDownload.IsSelected = tag == "download";
        TabMultiplayer.IsSelected = tag == "multiplayer";
        TabMore.IsSelected = tag == "more";
    }

    // ── 路由切换（AppNavigation 门面回调；路由表对齐桌面 MainWindow 的 tag switch） ──

    internal void Navigate(string route)
    {
        // 插件导航项：tag 是 plugin:<插件 id>:<导航 id>，走独立一级页（对齐桌面带参数导航；
        // 壳路由无导航参数，(pluginId, navId) 经静态属性带入）
        if (PluginExtensionPoints.TryParseNavTag(route, out var pluginId, out var navId))
        {
            if (_currentRoute == route) return;
            _currentRoute = route;
            ShowPage(route, typeof(PluginNavPage), onNew: () =>
            {
                PluginNavPage.PendingPluginId = pluginId;
                PluginNavPage.PendingNavId = navId;
            });
            return;
        }

        // 同路由重复进入不再导航（同页型路由，用路由字段判重）
        if (_currentRoute == route) return;
        _currentRoute = route;
        // 一级路由同步底部导航高亮（次级路由不点亮任何 tab）
        if (TabRoutes.Contains(route))
            SetTabSelection(route);
        var page = route switch
        {
            "versions" => typeof(VersionsPage),
            "versionDetail" => typeof(VersionDetailPage),
            "saves" => typeof(SavesPage),
            "multiplayer" => typeof(MultiplayerPage),
            "running" => typeof(RunningStatusPage),
            "download" => typeof(DownloadPage),
            "accounts" => typeof(AccountsPage),
            "accountAdd" => typeof(AccountAddPage),
            "accountDetail" => typeof(AccountDetailPage),
            "ai" => typeof(NovaPage),
            "settings" => typeof(SettingsPage),
            "plugins" => typeof(PluginsPage),
            "pluginPanel" => typeof(PluginPanelPage),
            "logs" => typeof(LogsPage),
            "more" => typeof(MorePage),
            _ => typeof(HomePage),
        };
        ShowPage(route, page);
    }

    // ── 叠加式页面切换器（对齐桌面"新页叠旧页"的观感） ──
    // 时序：新页入树（旧页保持可见）→ Loaded（布局完成，触发 OnShown，语义同 Frame.Navigated）
    //   → 首帧渲染（CompositionTarget.Rendering 下一拍）→ 撤旧页、开放新页输入。
    // 首次进入某路由时同步实例化页面（XAML 膨胀期间 UI 线程占用，旧页画面持续显示，无空档）。

    // 推挤转场进行中的两页（非 null = 动画进行中）；新导航开始时先瞬时落定上一段
    private Page? _animOldPage;
    private Page? _animNewPage;

    private void ShowPage(string route, Type pageType, Action? onNew = null)
    {
        if (!_pageCache.TryGetValue(route, out var newPage))
        {
            onNew?.Invoke();
            newPage = (Page)Activator.CreateInstance(pageType)!;
            _pageCache[route] = newPage;
            LauncherLog.Write("nav", $"实例化 {pageType.Name}");
        }
        if (ReferenceEquals(_currentPage, newPage)) return;

        // 上一段推挤动画还在进行：瞬时落定（撤旧页、复位位移），避免两段动画叠加
        if (_animNewPage is not null) FinishPushAnimation();

        var oldPage = _currentPage;
        _currentPage = newPage;
        newPage.IsHitTestVisible = false;   // 动画期间不响应输入，落定后开放
        ContentHost.Children.Add(newPage);
        // 初始位置：画面下方一屏（首帧渲染前就位，避免闪现终态）。
        // 壳 ActualHeight 在首次导航（构造期）可能还是 0——首启走无旧页落定路径，不受影响。
        var initialOffset = (float)(ActualHeight > 0 ? ActualHeight : 0);
        if (initialOffset > 0)
        {
            var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(newPage);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(newPage, true);
            v.Properties.InsertVector3("Translation",
                new System.Numerics.Vector3(0f, initialOffset, 0f));
        }

        void OnNewLoaded(object sender, RoutedEventArgs e)
        {
            newPage.Loaded -= OnNewLoaded;
            (newPage as IShellPage)?.OnShown();

            void OnFirstRender(object? s, object args)
            {
                CompositionTarget.Rendering -= OnFirstRender;
                // 撤掉新页以外的历史层（快速连切多重入时可能叠了多层）
                for (var i = ContentHost.Children.Count - 1; i >= 0; i--)
                    if (!ReferenceEquals(ContentHost.Children[i], newPage))
                        ContentHost.Children.RemoveAt(i);

                // 推挤转场：新页自下方上滑，把旧页向上顶出画面；动效关闭/无旧页直接落定
                if (oldPage is null || !AppServices.Instance.Motion.Enabled)
                {
                    var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(newPage);
                    v.StopAnimation("Translation");
                    v.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
                    newPage.IsHitTestVisible = true;
                    return;
                }
                var height = (float)(ActualHeight > 0 ? ActualHeight : newPage.ActualHeight);
                var vNew = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(newPage);
                var vOld = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(oldPage);
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(newPage, true);
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(oldPage, true);
                var compositor = vNew.Compositor;
                var ease = MotionFx.EnterEase(compositor);
                var duration = TimeSpan.FromMilliseconds(MotionFx.EnterMs);

                // 新页：从画面下方一屏滑到原位
                var slideNew = compositor.CreateVector3KeyFrameAnimation();
                slideNew.Duration = duration;
                slideNew.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, height, 0f));
                slideNew.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
                vNew.StartAnimation("Translation", slideNew);

                // 旧页：被顶着向上滑出一屏（与新页同曲线同节奏，画面连续）
                var slideOld = compositor.CreateVector3KeyFrameAnimation();
                slideOld.Duration = duration;
                slideOld.InsertKeyFrame(0f, System.Numerics.Vector3.Zero);
                slideOld.InsertKeyFrame(1f, new System.Numerics.Vector3(0f, -height, 0f), ease);
                vOld.StartAnimation("Translation", slideOld);

                _animOldPage = oldPage;
                _animNewPage = newPage;

                // 收尾：动画结束后撤旧页、复位位移、开放输入（缓冲 60ms 防掉帧尾）
                var done = ContentHost.DispatcherQueue!.CreateTimer();
                done.IsRepeating = false;
                done.Interval = duration + TimeSpan.FromMilliseconds(60);
                done.Tick += (_, _) =>
                {
                    if (ReferenceEquals(_animOldPage, oldPage) && ReferenceEquals(_animNewPage, newPage))
                        FinishPushAnimation();
                };
                done.Start();
            }
            CompositionTarget.Rendering += OnFirstRender;
        }
        newPage.Loaded += OnNewLoaded;
    }

    /// <summary>把新页以外的历史层移出内容宿主。</summary>
    private void RemoveOldPagesExcept(Page newPage)
    {
        for (var i = ContentHost.Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(ContentHost.Children[i], newPage))
                ContentHost.Children.RemoveAt(i);
    }

    /// <summary>瞬时落定进行中的推挤动画：撤旧页、两页位移归零、新页开放输入。</summary>
    private void FinishPushAnimation()
    {
        if (_animNewPage is null) return;
        foreach (var p in new[] { _animOldPage, _animNewPage })
        {
            if (p is null) continue;
            var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(p);
            v.StopAnimation("Translation");
            v.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
        }
        if (_animOldPage is not null)
            ContentHost.Children.Remove(_animOldPage);
        _animNewPage.IsHitTestVisible = true;
        _animOldPage = null;
        _animNewPage = null;
    }

    /// <summary>推入全窗覆盖页（游戏页等，覆盖在 NavigationView 之上）</summary>
    internal void PushOverlay(FrameworkElement content)
    {
        OverlayHost.Content = content;
        OverlayHost.Visibility = Visibility.Visible;
    }

    /// <summary>弹出全窗覆盖页</summary>
    internal void PopOverlay()
    {
        OverlayHost.Content = null;
        OverlayHost.Visibility = Visibility.Collapsed;
    }

    // ── 调度中心（手机版：全宽底部抽屉） ──
    private readonly DispatcherQueueTimer _taskRefresh =
        DispatcherQueue.GetForCurrentThread().CreateTimer();
    private volatile bool _taskDirty;

    private void TaskCenterBtn_Click(object sender, RoutedEventArgs e) => ToggleTaskPanel();

    private void TaskCenterClose_Click(object sender, RoutedEventArgs e) => ToggleTaskPanel();

    private void TaskCenterScrim_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleTaskPanel();

    /// <summary>Esc 关闭抽屉；用 KeyboardAccelerator 而非 KeyDown，焦点不在抽屉内时同样生效
    /// （Uno 触屏无硬件键盘时自然不触发）。</summary>
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
            UiThread.Run(() =>
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
        TaskCenterDrawer.Transitions ??= new TransitionCollection();
        TaskCenterDrawer.Transitions.Clear();
        if (!AppServices.Instance.Motion.Enabled) return;
        TaskCenterDrawer.Transitions.Add(new EntranceThemeTransition
        {
            // 手机版抽屉从底部滑入（平板版从右侧水平滑入）
            FromHorizontalOffset = 0,
            FromVerticalOffset = 32,
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
    /// 进度条为自绘（Star 列宽 = 比例，直接布局无动画）——ProgressBar 的值变化自带
    /// 缓动动画，300ms 刷新节奏下动画永远在追赶，这就是进度条抽搐的根因。</summary>
    private sealed class TaskRowRefs
    {
        public Border Root = null!;               // 任务卡容器
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!;
        public TextBlock Value = null!;           // 右对齐的关键数字：百分比 / 速度 / 阶段
        public Border Track = null!;              // 主进度条底槽（圆角）
        public Microsoft.UI.Xaml.Shapes.Rectangle Fill = null!;
        public ColumnDefinition FillCol = null!;  // 填充列（Star 宽 = 进度比例）
        public ColumnDefinition RestCol = null!;
        public ProgressBar Ind = null!;           // 不确定进度时显示（流动动画）
        public TextBlock Detail = null!;
        public TextBlock MirrorCaption = null!;
        public Grid Ribbon = null!;               // 多源贡献带（按字节占比分段）
        public StackPanel Legend = null!;         // 贡献带图例：源名 + 体积 + 占比
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
            Text = t.Title, FontSize = 13, FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // 右对齐的「关键数字」：这一行最该被看见的东西（百分比 / 速度 / 结果）
        var value = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(10, 0, 0, 0),
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
            r.Value.FontWeight = FontWeights.Bold;
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

    /// <summary>按当前主题查画刷：索引器可能解析到 Dark/Default 字典（任务卡灰块根因），
    /// 显式沿 ActualTheme 在合并字典的 ThemeDictionaries 中查找。</summary>
    private Brush? ThemedBrush(string key)
    {
        var want = ActualTheme.ToString();
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (d.ThemeDictionaries.TryGetValue(want, out var v) &&
                v is ResourceDictionary rd && rd.TryGetValue(key, out var b) &&
                b is Brush br)
                return br;
        return Application.Current.Resources.TryGetValue(key, out var f) &&
            f is Brush fb ? fb : null;
    }

    // ── Nova 内嵌（NOVA_INTEGRATION.md 五入口：① 顶栏 Orb） ──

    /// <summary>① NovaOrb：顶栏下方 8px 弹出面板，再点收回。</summary>
    /// <summary>NovaOrb：展开全宽底部面板，再点收回。</summary>
    private void NovaOrb_Click(object sender, RoutedEventArgs e)
    {
        if (NovaPopup.IsOpen) { NovaPopup.IsOpen = false; return; }
        ShowNovaPopup();
    }

    /// <summary>手机版：全宽底部面板。Popup 的 child 不自动拉伸（Uno 按左上角排布），
    /// 宽度按视口 − 左右边距显式给，高度由内容决定（限到窗口内），贴底位置经
    /// SizeChanged 跟踪（面板展开 / 内容变化都会重定位）。</summary>
    private void ShowNovaPopup()
    {
        NovaPanelHost.Width = Math.Max(240, ActualWidth - 24);
        NovaPanelHost.MaxHeight = Math.Max(240, ActualHeight - 24);
        NovaPopup.HorizontalOffset = 12;
        NovaPopup.VerticalOffset = Math.Max(12, ActualHeight - NovaPanelHost.ActualHeight - 12);
        NovaPopup.IsOpen = true;
        RefreshPanelQuota();
        MaybeRefreshPanelQuota();
        // 面板进场：与全应用同一套合成层淡入 + 轻微上移
        MotionFx.Reset(NovaPanelHost);
        MotionFx.FadeSlideIn(NovaPanelHost, 10f, MotionFx.EnterMs);
    }

    /// <summary>Nova 面板尺寸变化（首次布局 / 内容增减）：保持贴底（上下各留 12）。</summary>
    private void OnNovaPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NovaPopup.IsOpen)
            NovaPopup.VerticalOffset = Math.Max(12, ActualHeight - e.NewSize.Height - 12);
    }

    /// <summary>入口③「问 Nova」承接：展开顶栏 Nova 面板接收上下文输入（不跳页，少折腾）。</summary>
    public void OpenNovaPanel() => ShowNovaPopup();

    /// <summary>协议激活（starcloud://join）：好友邀请直达 Nova 确认清单，并展开 Nova 面板。</summary>
    public async Task HandleJoinUrlAsync(string url)
    {
        ShowNovaPopup();
        await AppServices.Instance.NovaRuntime.HandleJoinUrlAsync(url);
    }

    /// <summary>面板标头齿轮：关面板 → 去 Nova 页（许可层 / 操作日志 / LLM 设置）。</summary>
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
        UiThread.Run(() => ApplyNovaState(state));

    // ── 浮窗提醒 → TeachingTip（8s 后自动收起） ──

    private readonly DispatcherQueueTimer _toastResetTimer = DispatcherQueue.GetForCurrentThread()
        .CreateTimer(); // 构造在 UI 线程，可直接取

    private void OnOverlayToast(NovaOverlayToast toast) => UiThread.Run(() =>
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

        // Nova 图标换成了品牌标记（多色图片），五态着色改画在外圈描边上
        NovaOrbRing.BorderBrush = brush;
        NovaOrbDot.Visibility = state == NovaState.HasPlan ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(NovaOrb, $"Nova：{rt.StateText}");

        // 面板内容随登录态切换：离线显示引导，已登录显示对话
        var offline = state == NovaState.Offline;
        NovaPanelLogin.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        NovaPanelChat.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;

        RefreshPanelQuota();
        RefreshAccountButton();
    }

    // ── 面板标头的云端额度一行 ──

    /// <summary>
    /// 桌面从 NovaGatewayClient 拉云端额度（响应头 / 缓存）；安卓暂无该网关通道，
    /// 额度一行保持隐藏（no-op）。接入后在这里补取值与展示。
    /// </summary>
    private void RefreshPanelQuota()
    {
        NovaPanelQuota.Visibility = Visibility.Collapsed;
    }

    /// <summary>打开面板时顺带刷新额度：安卓无网关通道，no-op。</summary>
    private void MaybeRefreshPanelQuota()
    {
        // no-op（见 RefreshPanelQuota 注释）
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
            FontWeight = FontWeights.Medium,
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
