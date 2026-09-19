// 首页：PCL2 式左卡（版本列表 → 启动 → 账户），版本条目支持右键菜单

using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class HomePage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private List<VersionRow> _rows = [];
    private GameSession? _gameProcess;
    private string? _pendingLaunchId; // 版本列表页跨页启动请求（App.PendingLaunchVersionId）

    // ── 启动可靠性（设计：docs/designs/2026-09-11-launch-reliability-design.md） ──
    private LaunchOptions? _activeOptions;      // 本局启动参数（退出收尾命令复用）
    private Func<Task>? _relaunch;              // 启动闭包：占会话锁 + 预检 + 拉起（自动重试复用）
    private GameSessionHandle? _sessionHandle;  // 防双开会话锁（退出/失败/取消都要释放）
    private bool _crashDialogOpen;              // 崩溃弹窗重入保护
    private int _retryCount;                    // 本局已自动重试次数（ShowOverlay 归零）
    private long _launchStartedAt;              // 进程拉起时刻（判「启动即崩」）

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
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        SetMode(_s.Settings.LastUsername is { } u && u.StartsWith("ext:") ? "external" : "microsoft");
        LoadVersions();
        RefreshAccounts();
        RefreshGuide();
        ConsumePendingLaunch();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingLaunchId = App.PendingLaunchVersionId;
        App.PendingLaunchVersionId = null;
        if (_rows.Count > 0) ConsumePendingLaunch(); // 缓存实例回到主页立即消费；新实例等 Loaded
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
        if (_gameProcess is { HasExited: false }) return; // 已有游戏在运行，不叠加启动
        _ = StartLaunchAsync(id);
    }

    private void SetMode(string mode)
    {
        _s.LaunchAccount.Mode = mode;
        ModeMsBtn.IsChecked = mode == "microsoft";
        ModeExternalBtn.IsChecked = mode == "external";
        // 选中项黄底加粗，未选中恢复常规
        ModeMsBtn.FontWeight = mode == "microsoft" ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        ModeExternalBtn.FontWeight = mode == "external" ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        RefreshAccounts();
    }

    private void ModeMs_Click(object s, RoutedEventArgs e) => SetMode("microsoft");
    private void ModeExternal_Click(object s, RoutedEventArgs e) => SetMode("external");

    // ── 版本列表 ──
    private void LoadVersions()
    {
        try
        {
            var installed = _s.Manifest.ListInstalled(_s.Settings.DotMinecraft).ToList();
            installed.Sort((a, b) =>
            {
                // 上次使用的版本置顶，其余按版本号倒序
                var aLast = string.Equals(a.Id, _s.Settings.LastVersion, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                var bLast = string.Equals(b.Id, _s.Settings.LastVersion, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (aLast != bLast) return bLast - aLast;
                return string.Compare(b.Id, a.Id, StringComparison.OrdinalIgnoreCase);
            });
            _rows = installed.Select(i => new VersionRow(i.Id, FormatSize(i.SizeBytes))).ToList();
        }
        catch (Exception ex)
        {
            _rows = [];
            LocalErrorBar.Message = Loc.T("读取已安装版本失败：{0}", ex.Message);
            LocalErrorBar.IsOpen = true;
        }
        VersionList.ItemsSource = _rows;
        MotionFx.StaggerListItems(VersionList);
        EmptyVersionHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var preferred = _s.Settings.LastVersion is { } last && _rows.Any(r => r.Id == last) ? last
            : _rows.FirstOrDefault()?.Id;
        VersionList.SelectedItem = _rows.FirstOrDefault(r => r.Id == preferred);
    }

    private void RefreshVersionsBtn_Click(object s, RoutedEventArgs e)
    {
        LoadVersions();
        RefreshGuide();
    }

    private void VersionList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // 记录主界面当前选中的版本：下载页的 Mod / 资源包 / 光影 / 数据包直接装入该版本
        if (VersionList.SelectedItem is VersionRow row && _s.Settings.LastVersion != row.Id)
        {
            _s.Settings.LastVersion = row.Id;
            _s.Settings.Save();
        }
        UpdateVersionMeta();
    }

    private void UpdateVersionMeta()
    {
        var id = (VersionList.SelectedItem as VersionRow)?.Id;
        var isRunning = _gameProcess is { HasExited: false };
        LaunchTitleText.Text = isRunning ? Loc.T("停止游戏") : Loc.T("启动游戏");
        LaunchIcon.Glyph = isRunning ? "\uE71A" : "\uE768";
        LaunchSubText.Text = id ?? Loc.T("未选择版本");
        LaunchBtn.IsEnabled = isRunning || id is not null;
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
        if (sender is FrameworkElement { DataContext: VersionRow row })
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
    private void RefreshAccounts()
    {
        // 只列出当前模式的账户（microsoft / external）
        var list = _s.Accounts.Accounts
            .Where(a => a.Type == _s.LaunchAccount.Mode)
            .ToList();

        var def = _s.Accounts.Default;
        AccountCombo.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count > 0)
        {
            AccountCombo.ItemsSource = list
                .Select(a => new ComboItem(a.Uuid, a.IsDefault ? Loc.T("{0}（默认）", a.Username) : a.Username))
                .ToList();
            AccountCombo.SelectedValue = def is { } d && list.Any(a => a.Uuid == d.Uuid)
                ? d.Uuid : list[0].Uuid;
        }
        else
        {
            AccountNameText.Text = _s.LaunchAccount.Mode == "microsoft"
                ? Loc.T("尚无正版账户")
                : Loc.T("尚无第三方账户，请先到账户页登录");
            AvatarText.Text = "?";
        }
        RefreshNameFromSelection();
    }

    private void RefreshNameFromSelection()
    {
        var uuid = AccountCombo.SelectedValue as string;
        var acc = _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        var name = acc?.Username ?? "";
        AccountNameText.Text = string.IsNullOrWhiteSpace(name) ? Loc.T("未选择账户") : name;
        AvatarText.Text = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
        AvatarImage.Source = ResolveAvatar(acc, name);
    }

    /// <summary>
    /// 皮肤头像：覆盖首字母色块；未选账户 / 无网络 / 名字不存在时返回 null（露出色块）。
    /// </summary>
    private static ImageSource? ResolveAvatar(StoredAccount? acc, string name)
    {
        if (acc is null) return null;
        return AvatarHelper.Resolve(acc.Type, acc.Uuid, name, acc.ServerBase);
    }

    private void AccountCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (AccountCombo.SelectedValue is string uuid)
            _s.Accounts.SetDefault(uuid);
        RefreshNameFromSelection();
    }

    // ── 启动页状态机：ApplyStage 集中驱动（launch-overlay-design.md §3.2 表） ──

    /// <summary>统一驱动标题 / 步骤文字 / 进度条 / 按钮可见性 / 错误行，避免散落赋值。
    /// 同时把阶段同步到调度中心（悬浮任务面板可见启动进度）。</summary>
    private void ApplyStage(LaunchStage stage)
    {
        _stage = stage;
        _stageTimer.Stop();
        LaunchErrorText.Visibility = stage == LaunchStage.Failed ? Visibility.Visible : Visibility.Collapsed;

        // 调度中心同步：首次进入启动流程时登记任务，终态收尾
        var tc = Starcloud.Core.Services.TaskCenterService.Instance;
        if (stage != LaunchStage.Idle && _launchTask is null)
            _launchTask = tc.Begin(Loc.T("启动游戏 {0}", LaunchVersionText.Text));
        if (_launchTask is not null)
        {
            switch (stage)
            {
                case LaunchStage.Running:
                    tc.Complete(_launchTask, Loc.T("游戏运行中"));
                    _launchTask = null;
                    break;
                case LaunchStage.Exited:
                    tc.Complete(_launchTask, Loc.T("游戏已退出"));
                    _launchTask = null;
                    break;
                case LaunchStage.Failed:
                    tc.Fail(_launchTask, LaunchErrorText.Text is { Length: > 0 } m ? m : Loc.T("启动失败"));
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
                var sec = Math.Max(0, (Environment.TickCount64 - _runningStart) / 1000);
                LaunchStageStepText.Text = sec >= 60
                    ? Loc.T("本局时长 {0} 分 {1} 秒", sec / 60, sec % 60) : Loc.T("本局时长 {0} 秒", sec);
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
        LaunchCancelBtn.Content = Loc.T("取消");
        LaunchCancelBtn.IsEnabled = true;
        LaunchVersionText.Text = versionId;
        LaunchLoginModeText.Text = mode switch
        {
            "microsoft" => Loc.T("正版登录"),
            "external" => Loc.T("第三方登录"),
            _ => Loc.T("正版登录"),
        };
        LaunchTipText.Text = LaunchTips.Next();
        LaunchErrorText.Text = "";
        _retryCount = 0;
        _crashDialogOpen = false;
        ClearLogBuffer();
        LaunchLogToggle.IsChecked = _s.Settings.ShowGameLogOnLaunch; // 触发 LaunchLogToggle_Changed 同步可见性
        _logTimer.Start();
        LaunchOverlay.Opacity = 0;
        LaunchOverlay.Visibility = Visibility.Visible;

        // 150ms 淡入（克制，spec §3.6）
        var fadeIn = new Storyboard();
        var anim = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
        Storyboard.SetTarget(anim, LaunchOverlay);
        Storyboard.SetTargetProperty(anim, "Opacity");
        fadeIn.Children.Add(anim);
        fadeIn.Begin();

        ApplyStage(LaunchStage.VerifyingCredentials);

        // Nova 动态贴士（克制版，spec §3.5）：已登录 Starcloud ID 且 30% 概率；
        // 结果入队尾，下一次启动才显示；失败/超时静默，不碰 UI
        if (_s.StarcloudId.Current is not null && Random.Shared.Next(10) < 3)
            _ = FetchNovaTipAsync(versionId);
    }

    /// <summary>收起启动页回主页（spec §3.4）：状态机回 Idle，可选右栏状态文字。
    /// 不清 _cancelRequested：取消杀进程后 Exited 回调仍需感知（标记由 ShowOverlay 复位）。</summary>
    private void HideOverlay(string? statusText)
    {
        _logTimer.Stop();
        ApplyStage(LaunchStage.Idle);
        LaunchOverlay.Visibility = Visibility.Collapsed;
        if (statusText is not null)
        {
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
        if (_stage is LaunchStage.WaitingWindow or LaunchStage.Running)
        {
            // 已拉起：杀进程，Exited 回调感知取消标记 → 直接收层回主页
            try { _gameProcess?.Kill(); } catch { /* 已退出等情况忽略 */ }
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
        // 第二击「停止游戏」：杀当前进程，右栏状态由 Exited 回调收尾
        if (_gameProcess is { HasExited: false })
        {
            try { _gameProcess.Kill(); } catch { /* ignore */ }
            return;
        }

        var versionId = (VersionList.SelectedItem as VersionRow)?.Id;
        if (string.IsNullOrEmpty(versionId)) return;

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
            if (_cancelRequested) { HideOverlay("已取消启动"); return; }

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
                PluginHook: _s.PluginHost);
            _activeOptions = options;

            // 启动前环境体检（spec §6）：Block 弹窗由用户决定是否继续，Warn 只提示不阻断
            ApplyStage(LaunchStage.EnvChecking);
            var report = await _s.EnvCheck.CheckAsync(dotMc, versionId, options, _s.Settings);
            if (_cancelRequested) { HideOverlay("已取消启动"); return; }
            if (report.NeedsAttention)
            {
                LauncherLog.Write("launch", $"环境体检 {versionId}：" +
                    string.Join("、", report.Issues.Select(i => $"{i.Code}({i.Level})")));
                if (report.Worst == EnvLevel.Block)
                {
                    var go = await EnvCheckDialogs.ShowAsync(XamlRoot, report,
                        issue => FixEnvIssueAsync(issue, versionId, options));
                    if (!go) { HideOverlay("已取消启动"); return; }
                }
                else
                {
                    LaunchStageStepText.Text = Loc.T("环境提示：{0}", Loc.TCore(report.Issues[0].Title));
                }
            }
            if (_cancelRequested) { HideOverlay("已取消启动"); return; }

            // 闭包从 _activeOptions 取参数：体检一键修复后能立刻用上新内存档位
            _relaunch = () => AcquireAndLaunchAsync(dotMc, versionId, _activeOptions ?? options);
            await _relaunch();
        }
        catch (Exception ex)
        {
            ReleaseSession();
            if (_cancelRequested) { HideOverlay("已取消启动"); return; }
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

    /// <summary>占会话锁 → 预检补全 → 拉起进程（首次启动与自动重试共用）。</summary>
    private async Task AcquireAndLaunchAsync(string dotMc, string versionId, LaunchOptions options)
    {
        // 防双开（spec §8.3）：同一 .minecraft 下只允许一局游戏
        ReleaseSession();
        _sessionHandle = GameSessionGuard.TryAcquire(dotMc, versionId, out var blockedBy);
        if (_sessionHandle is null)
        {
            HideOverlay(null);
            LocalErrorBar.Message = Loc.T("版本 {0} 正在运行中，请先关闭游戏再启动", blockedBy);
            LocalErrorBar.IsOpen = true;
            LauncherLog.Write("launch", $"拒绝启动 {versionId}：{blockedBy} 会话仍在运行");
            return;
        }

        // 启动前预检：缺失/损坏文件全自动补全，进度显示在启动页，补完直接启动
        ApplyStage(LaunchStage.CheckingFiles);
        var preflight = await _s.Preflight.CheckAsync(dotMc, versionId);
        if (_cancelRequested) { HideOverlay("已取消启动"); return; }
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
            if (_cancelRequested) { HideOverlay("已取消启动"); return; }
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
            if (_cancelRequested) { HideOverlay("已取消启动"); return; }
        }

        var session = await _s.Launcher.LaunchVersionAsync(dotMc, versionId, options);
        _gameProcess = session;
        _sessionHandle?.Bind(session.Pid);       // 会话锁绑定真实 pid（防双开判活）
        session.OutputLine += OnGameOutputLine;  // 实时日志（spec §5）
        session.Exited += OnGameExited;          // 退出收尾 / 崩溃诊断 / 自动重试（spec §7、§8.1）
        if (_cancelRequested)
        {
            // 极短窗口内取消：杀掉刚拉起的进程回主页，Exited 回调续收尾
            try { session.Kill(); } catch { /* ignore */ }
            HideOverlay("已取消启动");
            return;
        }
        _launchStartedAt = Environment.TickCount64;
        _runningStart = _launchStartedAt;
        ApplyStage(LaunchStage.WaitingWindow); // 窗口出现由 _windowTimer 轮询（§3.3）

        // Nova 时刻：游戏启动即开观察会话（未授权捕捉则静默）；浮窗监测需要 pid
        _s.NovaRuntime.OnGameLaunched(versionId, session.Id);
        App.RunningVersionId = versionId;
        new InstanceSettingsStore(dotMc).TouchPlayed(versionId); // 记录版本档案「上次启动」
        _s.Settings.LastUsername = options.Credentials is { } c && c.UserType == "mojang"
            ? "ext:" + options.PlayerName
            : "ms:" + options.PlayerName;
        _s.Settings.LastVersion = versionId;
        _s.Settings.Save();
        LauncherLog.Write("launch", $"已拉起 {versionId}（pid {session.Pid}，退出码待定）");
    }

    /// <summary>释放防双开会话锁（退出 / 失败 / 取消 / 重试前都要调用）。</summary>
    private void ReleaseSession()
    {
        try { _sessionHandle?.Dispose(); } catch { /* 锁文件被占用时忽略 */ }
        _sessionHandle = null;
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

    /// <summary>游戏进程退出（后台线程）→ 切回 UI 线程收尾。</summary>
    private void OnGameExited(int exitCode) =>
        _dq.TryEnqueue(() => _ = HandleGameExitedAsync(exitCode));

    /// <summary>退出收尾（spec §7、§8.1）：自动重试 / 崩溃诊断 / 状态展示，最后统一跑收尾命令与 Nova 收尾。</summary>
    private async Task HandleGameExitedAsync(int exitCode)
    {
        var session = _gameProcess;
        var options = _activeOptions;
        var versionId = session?.VersionId ?? options?.VersionId ?? "";
        var uptimeMs = _launchStartedAt > 0
            ? Math.Max(0, Environment.TickCount64 - _launchStartedAt) : 0;
        var overlayVisible = LaunchOverlay.Visibility == Visibility.Visible && IsLoaded;

        App.RunningVersionId = null;
        ReleaseSession();

        // 自动重试（spec §8.1）：启动即崩且本局未重试过 → 静默重跑一次（不重跑体检）
        if (!_cancelRequested && exitCode != 0 && _retryCount == 0
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
            return;
        }

        if (_cancelRequested)
        {
            HideOverlay("已取消启动"); // 取消路径：直接收层回主页（spec §3.4）
        }
        else if (exitCode == 0)
        {
            LauncherLog.Write("launch", $"{versionId} 正常退出（运行 {uptimeMs / 1000} 秒）");
            if (overlayVisible)
                ApplyStage(LaunchStage.Exited); // 覆盖层显示：停在启动页展示本局时长
        }
        else
        {
            LauncherLog.Write("launch", $"{versionId} 异常退出（退出码 {exitCode}，运行 {uptimeMs / 1000} 秒）");
            if (overlayVisible)
            {
                LaunchStageTitle.Text = "游戏异常退出";
                LaunchStageTitle.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
                LaunchStageStepText.Text = "正在分析崩溃原因…";
                await ShowCrashDialogAsync(session, options, versionId, exitCode);
            }
        }

        await RunExitCleanupAsync(options);
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

    /// <summary>退出后统一收尾：刷新版本元数据 → 启动后命令 → Nova 战报（失败不阻断退出流程）。</summary>
    private async Task RunExitCleanupAsync(LaunchOptions? options)
    {
        UpdateVersionMeta();
        if (options is not null)
        {
            try { _s.Launcher.RunPostLaunch(options); }
            catch (Exception ex) { LauncherLog.Error("launch", "启动后命令执行失败", ex); }
        }
        try { await _s.NovaRuntime.OnGameExitedAsync(); }
        catch (Exception ex) { LauncherLog.Error("launch", "Nova 收尾失败", ex); }
    }

    // ── 入口 ──
    private void PickVersion_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");

    private void VersionSettings_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("settings");

    private void TileDownload_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");
    private void TileMods_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");
    private void TileMultiplayer_Click(object s, RoutedEventArgs e) =>
        LocalErrorBar.Message = "联机功能将在后续版本提供";

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
        icon.Glyph = ok ? "\uE73E" : "\uE7BA";
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
