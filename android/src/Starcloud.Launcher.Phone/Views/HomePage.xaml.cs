// 首页 · 手机版：由平板版 HomePage 单列化重排而来（对照平板 Starcloud.Launcher.Uno 的 HomePage）
//
// 与平板版的差异：
//   · 布局单列纵向流：账户/启动卡置顶（启动 CTA 常驻可见）→ 通知/自检 → 插件区 → 版本库卡撑满余高；
//   · 删去两栏卡片的收起/展开补间与窄条 Dock（手机竖屏无多余宽度可收），
//     设置项 HomeAccountCollapsed / HomeVersionsCollapsed 随之不再读写（字段保留在共享 Core 层）；
//   · 账户区改横排紧凑（头像 44 + 切换按钮常显——触屏无悬停浮现的时机）。
// 与平板版一致的部分：启动链、启动页状态机、模式分段（合成动画指示器）、账户滑动切换、
//   版本行右键菜单、自检卡与通知区整块显隐。
//
// Uno 移植说明（对照桌面 HomePage.xaml.cs）：
//   · 启动链：桌面 GameLauncher（进程拉起 + 凭据预检 + 文件补全 + 环境体检 + 崩溃诊断 + 自动重试）
//     是桌面进程启动链专属；安卓壳改走 GameLauncherService（组件部署 → Java 桥 → JVM 拉起），
//     其 Log 事件回 UiThread 驱动启动页步骤 / 进度 / 实时日志；拉起成功即推入 GamePage 进游戏画面。
//   · 对话框：桌面 ThemedDialog（ContentDialog 流程）→ UiDialog（Ui.Root 作 XamlRoot）。
//   · 导航：桌面 MainWindow.NavigateTo / OpenNovaPanel → AppNavigation.Navigate 路由；
//     桌面 OnNavigatedTo / OnNavigatedFrom → IShellPage.OnShown + Unloaded。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.Android.Host;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Dialogs;
using Starcloud.Launcher.Phone.Views.Rows;
using Starcloud.Launcher.Phone.Views.Shared;
using System.Diagnostics;
using System.IO;
using Windows.Foundation;

namespace Starcloud.Launcher.Phone.Views;

public sealed partial class HomePage : Page, IShellPage
{
    private readonly AppServices _s = AppServices.Instance;
    private List<LocalVersionRow> _rows = [];
    private string? _pendingLaunchId; // 版本列表页跨页启动请求（HomePage.PendingLaunchVersionId）
    private int _modeIndex;           // 账户模式分段的当前段（0 正版 / 1 外置 / 2 离线），供尺寸变化时复位指示器

    // ── 启动链（安卓：GameLauncherService 组件部署 → Java 桥 → JVM 拉起） ──
    private AppTask? _launchTask;               // 调度中心：游戏启动任务
    private CancellationTokenSource? _launchCts; // 取消令牌：JVM 拉起前可整体取消
    private bool _cancelRequested;               // 取消标记（ShowOverlay 复位）
    private double _climbTo = 100;               // 氛围进度爬升上限（到区间顶即停）
    private readonly DispatcherTimer _stageTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };  // 氛围进度爬升

    private const int LogMaxLines = 200;                                            // 实时日志展示上限
    private readonly List<string> _logFlushBuffer = [];                             // 输出行缓冲（UI 线程定时刷新）
    private int _logPending;                                                        // 待刷新行数（免无谓刷新）
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // ── 启动页状态机（launch-overlay-design.md §3.2 的安卓简化：JVM 拉起成功即收层进游戏页） ──
    private enum LaunchStage
    {
        Idle, Launching, Failed
    }

    private LaunchStage _stage;

    /// <summary>版本列表页跨页启动请求（桌面记在 App.PendingLaunchVersionId；安卓壳由发起页直接写本静态）。</summary>
    public static string? PendingLaunchVersionId;

    public HomePage()
    {
        InitializeComponent();
        _stageTimer.Tick += (_, _) => StageTimer_Tick();
        _logTimer.Tick += (_, _) => FlushLogBuffer();
        Loaded += (_, _) => Initialize();
        Unloaded += OnPageUnloaded;
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

    /// <summary>
    /// 左列通知区（三张通知条 + 启动自检卡）的整块显隐。
    /// 全不可见时整块收成 Collapsed —— Collapsed 的元素不参与布局，版本卡因此贴齐上方内容。
    /// 间距只能挂在这一层：外层 Grid 一旦用 RowSpacing，空行也照收一份，版本卡会被推下去。
    /// </summary>
    private void RefreshLeftNoticeArea()
    {
        bool any = GlobalErrorBar.IsOpen || LocalErrorBar.IsOpen || WarnBar.IsOpen
            || CheckCard.Visibility == Visibility.Visible;
        LeftNoticeArea.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        LeftNoticeArea.Margin = any ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    /// <summary>首次 Loaded 已跑完：页面启用导航缓存，构造与首次 Loaded 只执行一次，之后每次进入走 OnShown。</summary>
    private bool _initialized;

    private void Initialize()
    {
        // 页面已启用导航缓存：实例跨页面切换复用，本方法只在首次 Loaded 跑一次。缓存实例再次挂树时
        // Loaded 会重新触发，靠 _initialized 挡住，数据刷新走 OnShown。
        if (_initialized) return;
        _initialized = true;

        RefreshHomeData();
    }

    /// <summary>
    /// 主页数据刷新（首次 Loaded 与缓存后每次进入共用）：模式分段 / 版本列表 / 账户 / 启动自检 /
    /// 左列通知区，最后消费跨页启动请求。构造只在首次执行，缓存的实例靠这里保证数据新鲜。
    /// </summary>
    private void RefreshHomeData()
    {
        SetMode(LaunchAccountService.ModeOfLastUsername(_s.Settings.LastUsername));
        LoadVersions();
        RefreshAccounts();
        RefreshGuide();
        RefreshLeftNoticeArea(); // 兜底：无通知时把左列通知区收掉，版本卡才能与右栏卡上边缘齐平
        ConsumePendingLaunch();
    }

    /// <summary>页面被导航显示（对齐桌面 OnNavigatedTo）。首次进入列表还没加载，交给 Loaded → Initialize 消费。</summary>
    public void OnShown()
    {
        _pendingLaunchId = PendingLaunchVersionId;
        PendingLaunchVersionId = null;
        UpdateVersionMeta(); // 按钮态依赖实时运行集合（多开：可能在别的页启停了实例）
        if (!_initialized) return; // 首次进入：Loaded → Initialize 随后刷新并消费上面的启动请求
        RefreshHomeData();
    }

    /// <summary>消费版本列表页的跨页启动请求（从详情页/列表页发起）。</summary>
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
    private async Task LoadVersionsAsync()
    {
        var dotMc = _s.Settings.DotMinecraft;
        // 目录扫描 + 逐实例档案读取是磁盘 IO，放后台线程——同步扫盘会让切页瞬间掉帧
        var (rows, error) = await Task.Run(() =>
        {
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
                return (installed.Select(i =>
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
                }).ToList(), (string?)null);
            }
            catch (Exception ex)
            {
                return (new List<LocalVersionRow>(), (string?)ex.Message);
            }
        });
        if (error is { } err)
        {
            LocalErrorBar.Message = Loc.T("读取已安装版本失败：{0}", err);
            LocalErrorBar.IsOpen = true;
        }
        _rows = rows;
        VersionCountText.Text = $"({_rows.Count})";
        // 数据没变就不再重建 ItemsSource / 重演错峰：切页回来时列表原样呈现，零开销
        var sig = string.Join("|", _rows.Select(r => $"{r.Id}:{r.Category}:{r.LastPlayedText}:{r.SizeText}"));
        if (sig != _appliedListSig || VersionList.ItemsSource is null)
        {
            _appliedListSig = sig;
            VersionList.ItemsSource = _rows;
            MotionFx.StaggerListItems(VersionList);
        }
        EmptyVersionHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var preferred = _s.Settings.LastVersion is { } last && _rows.Any(r => r.Id == last) ? last
            : _rows.FirstOrDefault()?.Id;
        VersionList.SelectedItem = _rows.FirstOrDefault(r => r.Id == preferred);
    }

    /// <summary>上一次版本列表签名：内容没变就不重建列表 / 不重演错峰（切页闪烁来源之一）。</summary>
    private string? _appliedListSig;

    private void LoadVersions() => _ = LoadVersionsAsync();

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
            _s.SaveSettings();
        }
        UpdateVersionMeta();
    }

    private void UpdateVersionMeta()
    {
        var id = (VersionList.SelectedItem as LocalVersionRow)?.Id;
        var isRunning = RunningPidFor(id) != 0;
        LaunchTitleText.Text = isRunning ? Loc.T("停止游戏") : Loc.T("启动游戏");
        // 按钮第二行同步当前选中版本名（没选就用「未选择版本」兜底）
        LaunchBtnVersionText.Text = id ?? Loc.T("未选择版本");
        // 自绘轮廓：两个 Path 叠放，切可见性
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

    /// <summary>入口③「问 Nova」：选定实例上下文 → 跳转 Nova 页承接（桌面是展开悬浮面板，安卓壳以路由页承载）。</summary>
    private void AskNova_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        _s.NovaRuntime.SetCurrentInstance(id);
        _s.NovaRuntime.RequestInput($"帮我给 {id} 做一次游戏体检，有隐患直接说");
        AppNavigation.Navigate("ai");
    }

    /// <summary>跳转到该版本的详情/管理页（桌面跳独立详情页，安卓壳详情并入版本页路由）。</summary>
    private void VersionDetail_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        AppNavigation.Navigate("versions");
    }

    /// <summary>打开游戏目录：安卓无桌面式文件管理器直达，就地给出目录路径（PORTING：目录打开 → 提示）。</summary>
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
        _ = UiDialog.AlertAsync(Loc.T("打开游戏目录"), dir);
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        // 桌面 ThemedDialog（ContentDialog 流程）→ UiDialog（PORTING 公约）
        if (!await UiDialog.ConfirmAsync(
                Loc.T("删除版本"),
                Loc.T("确定要删除版本 {0} 吗？\n将删除其版本文件夹，此操作不可恢复。", id),
                Loc.T("删除")))
            return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, id);
            if (string.Equals(_s.Settings.LastVersion, id, StringComparison.OrdinalIgnoreCase))
            {
                _s.Settings.LastVersion = null;
                _s.SaveSettings();
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
    /// 微软设备码 / 第三方账密登录是桌面窗体流程，安卓壳暂不支持（启动链用内置离线账户开箱即玩）。
    /// </summary>
    private async void NoAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        NoAccountBtn.IsEnabled = false;
        try
        {
            switch (_s.LaunchAccount.Mode)
            {
                case "offline":
                    if (await AddOfflineAccountAsync() is null) return;
                    break;
                default:
                    await UiDialog.AlertAsync(Loc.T("登录"),
                        Loc.T("安卓端暂不支持该登录方式；启动将使用内置离线账户。"));
                    return;
            }
            _s.NovaRuntime.NotifyLoginChanged();
            RefreshAccounts();
        }
        finally
        {
            NoAccountBtn.IsEnabled = true;
        }
    }

    /// <summary>切换账号：去账户页（与右上角账户入口同一处）。手机版按钮常显，无悬停浮现逻辑。</summary>
    private void SwitchAccount_Click(object sender, RoutedEventArgs e) =>
        AppNavigation.Navigate("accounts");

    /// <summary>离线账户只差一个名字，就地填完即用（桌面 ThemedDialog 流程 → UiDialog.PromptAsync）</summary>
    private async Task<string?> AddOfflineAccountAsync()
    {
        var name = await UiDialog.PromptAsync(
            Loc.T("添加离线账户"),
            Loc.T("玩家名（3-16 位字母、数字或下划线）"),
            Loc.T("添加"), Loc.T("取消"));
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
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

    // ── 启动页状态机：ApplyStage 集中驱动（launch-overlay-design.md §3.2 的安卓简化版） ──

    /// <summary>统一驱动标题 / 步骤文字 / 进度条 / 按钮可见性 / 错误行，避免散落赋值。
    /// 同时把阶段同步到调度中心（悬浮任务面板可见启动进度）。
    /// 安卓链在拉起成功后立即收层进游戏页，所以只有 启动中 / 失败 / 空闲 三个阶段。</summary>
    private void ApplyStage(LaunchStage stage)
    {
        _stage = stage;
        _stageTimer.Stop();
        LaunchErrorText.Visibility = stage == LaunchStage.Failed ? Visibility.Visible : Visibility.Collapsed;

        // 调度中心同步：任务在 ShowOverlay 登记，贯穿整条启动链；
        // 只有启动失败 / 启动阶段取消两个终态在这里收尾，成功路径在 StartLaunchAsync 里 Complete。
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
                    // 收回启动页（取消启动 / 返回主页）要收尾，否则任务会一直挂在「运行中」。
                    tc.Cancel(_launchTask);
                    _launchTask = null;
                    break;
            }
        }

        switch (stage)
        {
            case LaunchStage.Launching:
                SetStageUi(Loc.T("正在启动"), Loc.T("准备启动环境…"), 0, 10);
                break;
            case LaunchStage.Failed:
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

        // 按钮：启动中只留「取消」；失败只留「返回主页」
        var launching = stage == LaunchStage.Launching;
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

    /// <summary>进入启动页（spec §3.4）：赋版本/登录方式 → 静态贴士 → 淡入 → 进入启动阶段。</summary>
    private void ShowOverlay(string versionId, string mode)
    {
        _cancelRequested = false;
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
        ClearLogBuffer();
        LaunchLogToggle.IsChecked = _s.Settings.ShowGameLogOnLaunch; // 触发 LaunchLogToggle_Changed 同步可见性
        _logTimer.Start();
        // 启动页进场：并入统一的 MotionFx（合成层淡入 + 轻微上移），不再自绘 Storyboard
        MotionFx.Reset(LaunchOverlay);
        LaunchOverlay.Visibility = Visibility.Visible;
        MotionFx.FadeSlideIn(LaunchOverlay, 14f);

        // 调度中心登记：这条任务贯穿整条启动链，退出 / 失败 / 取消时收尾
        _launchTask = Starcloud.Core.Services.TaskCenterService.Instance
            .Begin(Loc.T("启动游戏 {0}", versionId));
    }

    /// <summary>收起启动页回主页（spec §3.4）：状态机回 Idle，可选右栏状态文字。
    /// 不清 _cancelRequested：取消路径要靠它把异常当成静默收层（标记由 ShowOverlay 复位）。</summary>
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
            StatusText.Text = statusText;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    // ── 启动链日志：GameLauncherService 的进度事件 → 实时日志 + 关键节点同步步骤/进度 ──
    // Log 回调线程不定（启动链线程池 / Java 桥线程），文本一律回 UiThread（PORTING 公约）。

    private void OnLauncherLog(string line)
    {
        UiThread.Run(() =>
        {
            OnGameOutputLine(line);   // 全量进实时日志
            ApplyLaunchLogStep(line); // 关键节点同步步骤文字与氛围进度区间
        });
    }

    /// <summary>按启动链的关键日志节点推进启动页的步骤与进度（文案与 GameLauncherService 的输出对齐）。</summary>
    private void ApplyLaunchLogStep(string line)
    {
        if (_stage != LaunchStage.Launching) return;
        if (line.Contains("[flow] 组件部署完成", StringComparison.Ordinal))
            SetStageUi(Loc.T("正在启动"), Loc.T("初始化 Java 桥…"), 35, 45);
        else if (line.Contains("Java 桥已就绪", StringComparison.Ordinal))
            SetStageUi(Loc.T("正在启动"), Loc.T("加载版本清单…"), 50, 60);
        else if (line.Contains("[flow] 版本加载完成", StringComparison.Ordinal))
            SetStageUi(Loc.T("正在启动"), Loc.T("构建启动参数…"), 65, 75);
        else if (line.Contains("参数构建完成", StringComparison.Ordinal))
            SetStageUi(Loc.T("正在启动"), Loc.T("拉起游戏进程…"), 80, 90);
        else if (line.Contains("JVM 线程已拉起", StringComparison.Ordinal))
        {
            _stageTimer.Stop();
            LaunchProgress.IsIndeterminate = false;
            LaunchProgress.Value = 100;
            LaunchStageStepText.Text = Loc.T("进入游戏画面…");
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

    /// <summary>打开本局日志文件；安卓无桌面式文件查看器，就地给出日志目录路径（PORTING：文件打开 → 提示）。</summary>
    private void LaunchLogOpen_Click(object sender, RoutedEventArgs e)
    {
        _ = UiDialog.AlertAsync(Loc.T("打开日志文件"),
            Loc.T("安卓端请在游戏页查看实时日志。\n日志目录：{0}", LauncherPaths.GameLogsDir));
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
        // 安卓链：JVM 拉起前经取消令牌在阶段边界退出；拉起成功即收层进游戏页，无需在此杀进程
        try { _launchCts?.Cancel(); } catch { /* 已完成等情况忽略 */ }
    }

    private void LaunchBackBtn_Click(object sender, RoutedEventArgs e)
    {
        // 安卓链拉起成功即收层进游戏页，覆盖层只在失败态停留，返回即回主页
        HideOverlay(null);
    }

    /// <summary>导航离开（页被替换即 Unloaded，对齐桌面 OnNavigatedFrom 语义）：
    /// 停定时器、解绑日志；启动页还开着就按取消语义处理。</summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _stageTimer.Stop();
        _logTimer.Stop();
        GameLauncherService.Instance.Log -= OnLauncherLog;
        if (LaunchOverlay.Visibility != Visibility.Visible) return;
        if (_stage == LaunchStage.Launching)
        {
            _cancelRequested = true; // 未拉起：让启动链在取消令牌上自行退出（拉起成功时覆盖层已收）
            try { _launchCts?.Cancel(); } catch { /* 已完成等情况忽略 */ }
        }
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

    /// <summary>统一启动入口：主页按钮与跨页启动请求均走此流程。
    /// 安卓链：前台服务先行 → GameLauncherService（组件部署 → Java 桥 → JVM 拉起）→ 推入游戏页。
    /// 桌面版的凭据预检 / 文件补全 / 环境体检 / 崩溃诊断属于桌面进程启动链，安卓壳不适用。</summary>
    private async Task StartLaunchAsync(string versionId)
    {
        var context = global::Android.App.Application.Context;
        LaunchBtn.IsEnabled = false;
        GlobalErrorBar.IsOpen = false;
        ShowOverlay(versionId, _s.LaunchAccount.Mode);
        try
        {
            ApplyStage(LaunchStage.Launching);

            // 前台服务必须先于启动链请求：StartForegroundService 后系统只给 5 秒窗口执行
            // StartForeground，若在组件解压 + JVM 拉起（CPU 打满）之后才请求，主线程排队
            // 超时即遭 ForegroundServiceDidNotStartInTimeException 杀进程（GamePage 启动链同款实证）
            GameForegroundService.Start(context);
            await GameForegroundService.WaitForForegroundAsync(TimeSpan.FromSeconds(4));

            _launchCts = new CancellationTokenSource();
            GameLauncherService.Instance.Log += OnLauncherLog;
            // 启动链整体放线程池：组件解压为 CPU 密集长任务，留在主线程会阻塞 Looper（GamePage 同款）
            await Task.Run(() => GameLauncherService.Instance.LaunchAsync(versionId, _launchCts.Token));
            GameLauncherService.Instance.Log -= OnLauncherLog;

            // 拉起成功：与桌面一致落「上次启动版本」，随后进游戏画面
            _s.Settings.LastVersion = versionId;
            _s.SaveSettings();
            FinishLaunchTask(_launchTask, Loc.T("游戏已拉起"));
            AppNavigation.PushOverlay(new GamePage());
            HideOverlay(null);
        }
        catch (Exception ex)
        {
            GameLauncherService.Instance.Log -= OnLauncherLog;
            // 启动链失败：撤回已请求的前台服务，避免通知栏残留常驻通知（GamePage 同款）
            GameForegroundService.Stop(context);
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

    /// <summary>调度中心：「启动游戏 X」任务收尾（正常完成 / 失败 / 取消），收尾后本实例不再持有。</summary>
    private void FinishLaunchTask(AppTask? task, string detail,
        bool failed = false, bool cancelled = false)
    {
        if (task is null) return;
        var tc = TaskCenterService.Instance;
        if (cancelled) tc.Cancel(task);
        else if (failed) tc.Fail(task, detail);
        else tc.Complete(task, detail);
        if (ReferenceEquals(_launchTask, task)) _launchTask = null;
    }

    // ── 入口 ──
    private void PickVersion_Click(object s, RoutedEventArgs e) =>
        AppNavigation.Navigate("download");

    // ── 启动自检（版本 / 账户 / Java） ──
    private bool? _javaOk;   // null=检测中

    private void Recheck_Click(object s, RoutedEventArgs e) => RefreshGuide();

    private void CheckGoVersion_Click(object s, RoutedEventArgs e) =>
        AppNavigation.Navigate("download");
    private void CheckGoAccount_Click(object s, RoutedEventArgs e) =>
        AppNavigation.Navigate("accounts");
    private void CheckGoJava_Click(object s, RoutedEventArgs e) =>
        AppNavigation.Navigate("settings");

    private void RefreshGuide()
    {
        var hasInstalled = _rows.Count > 0;
        var hasAccount = _s.Accounts.Accounts.Count > 0;
        SetCheckRow(CheckIconVersion, CheckTextVersion, CheckGoVersion,
            hasInstalled, "游戏版本", hasInstalled ? "就绪" : "未安装", "前往下载");
        SetCheckRow(CheckIconAccount, CheckTextAccount, CheckGoAccount,
            hasAccount, "账户", hasAccount ? "就绪" : "未配置", "前往账户");
        // Java 检测较慢，异步进行
        _javaOk = null;
        CheckTextJava.Text = "Java：检测中…";
        _ = CheckJavaAsync();
        UpdateCheckCardVisibility();
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
        }
        catch
        {
            _javaOk = false;
            SetCheckRow(CheckIconJava, CheckTextJava, CheckGoJava, false, "Java", "检测失败", "前往设置");
        }
        UpdateCheckCardVisibility();
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
        icon.Foreground = (ok
            ? ThemeBrush("SystemFillColorSuccessBrush")
            : ThemeBrush("SystemFillColorCriticalBrush")) ?? icon.Foreground;
        text.Text = $"{label}：{detail}";
        go.Content = action;
        go.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
}
