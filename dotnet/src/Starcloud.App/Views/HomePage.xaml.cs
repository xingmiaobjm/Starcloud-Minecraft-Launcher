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
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class HomePage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private List<VersionRow> _rows = [];
    private string _mode = "offline";
    private Process? _gameProcess;

    // ── 启动页状态机（设计：docs/superpowers/specs/2026-09-09-launch-overlay-design.md §3.2） ──
    private enum LaunchStage
    {
        Idle, VerifyingCredentials, CheckingFiles, RepairingFiles,
        StartingProcess, WaitingWindow, Running, Exited, Failed
    }

    private LaunchStage _stage;
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
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        SetMode(_s.Settings.LastUsername is { } u && u.StartsWith("ms:") ? "microsoft" : "offline");
        LoadVersions();
        RefreshAccounts();
        RefreshGuide();
    }

    private void SetMode(string mode)
    {
        _mode = mode;
        ModeMsBtn.IsChecked = mode == "microsoft";
        ModeOfflineBtn.IsChecked = mode == "offline";
        RefreshAccounts();
    }

    private void ModeMs_Click(object s, RoutedEventArgs e) => SetMode("microsoft");
    private void ModeOffline_Click(object s, RoutedEventArgs e) => SetMode("offline");

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
            LocalErrorBar.Message = $"读取已安装版本失败：{ex.Message}";
            LocalErrorBar.IsOpen = true;
        }
        VersionList.ItemsSource = _rows;
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

    private void VersionList_SelectionChanged(object s, SelectionChangedEventArgs e) => UpdateVersionMeta();

    private void UpdateVersionMeta()
    {
        var id = (VersionList.SelectedItem as VersionRow)?.Id;
        var isRunning = _gameProcess is { HasExited: false };
        LaunchTitleText.Text = isRunning ? "停止游戏" : "启动游戏";
        LaunchIcon.Glyph = isRunning ? "\uE71A" : "\uE768";
        LaunchSubText.Text = id ?? "未选择版本";
        LaunchBtn.IsEnabled = isRunning || id is not null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "未完成",
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

    /// <summary>入口③「问 Nova」：选定实例上下文 → 展开底部对话条承接，不跳页。</summary>
    private void AskNova_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        _s.NovaRuntime.SetCurrentInstance(id);
        _s.NovaRuntime.RequestInput($"帮我给 {id} 做一次游戏体检，有隐患直接说");
        (App.MainWindow as MainWindow)?.OpenNovaBar();
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        var dir = Path.Combine(_s.Settings.DotMinecraft, "versions", id);
        if (!Directory.Exists(dir))
        {
            LocalErrorBar.Message = "版本目录不存在";
            LocalErrorBar.IsOpen = true;
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        var dlg = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定要删除版本 {id} 吗？\n将删除其版本文件夹，此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
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
            LocalErrorBar.Message = $"删除失败：{ex.Message}";
            LocalErrorBar.IsOpen = true;
        }
    }

    // ── 账户 ──
    private void RefreshAccounts()
    {
        var msAccounts = _s.Accounts.Accounts.Where(a => a.Type == "microsoft").ToList();
        var offlineAccounts = _s.Accounts.Accounts.Where(a => a.Type == "offline").ToList();
        var list = _mode == "microsoft" ? msAccounts : offlineAccounts;

        var def = _s.Accounts.Default;
        var showInput = _mode == "offline" && list.Count == 0;
        PlayerNameBox.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;
        AccountCombo.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count > 0)
        {
            AccountCombo.ItemsSource = list
                .Select(a => new ComboItem(a.Uuid, a.IsDefault ? $"{a.Username}（默认）" : a.Username))
                .ToList();
            AccountCombo.SelectedValue = def is { } d && list.Any(a => a.Uuid == d.Uuid)
                ? d.Uuid : list[0].Uuid;
        }
        else
        {
            AccountNameText.Text = _mode == "microsoft" ? "尚无正版账户" : "输入玩家名";
            AvatarText.Text = "?";
        }
        RefreshNameFromSelection();
    }

    private void RefreshNameFromSelection()
    {
        var uuid = AccountCombo.SelectedValue as string;
        var acc = _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        var name = acc?.Username ?? PlayerNameBox.Text.Trim();
        AccountNameText.Text = string.IsNullOrWhiteSpace(name) ? "未选择账户" : name;
        AvatarText.Text = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
    }

    private void AccountCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (AccountCombo.SelectedValue is string uuid)
            _s.Accounts.SetDefault(uuid);
        RefreshNameFromSelection();
    }

    private void PlayerName_TextChanged(object s, TextChangedEventArgs e) => RefreshNameFromSelection();

    // ── 启动页状态机：ApplyStage 集中驱动（launch-overlay-design.md §3.2 表） ──

    /// <summary>统一驱动标题 / 步骤文字 / 进度条 / 按钮可见性 / 错误行，避免散落赋值。</summary>
    private void ApplyStage(LaunchStage stage)
    {
        _stage = stage;
        _stageTimer.Stop();
        LaunchErrorText.Visibility = stage == LaunchStage.Failed ? Visibility.Visible : Visibility.Collapsed;

        switch (stage)
        {
            case LaunchStage.VerifyingCredentials:
                SetStageUi("正在启动", "验证 Microsoft 凭据…", 0, 10);
                break;
            case LaunchStage.CheckingFiles:
                SetStageUi("正在启动", "检查游戏文件完整性…", 10, 25);
                break;
            case LaunchStage.RepairingFiles:
                // 真进度阶段：Progress 回调映射 25→75，这里不爬升
                SetStageUi("正在启动", "补全缺失文件…", 25, null);
                break;
            case LaunchStage.StartingProcess:
                SetStageUi("正在启动", "构建启动命令…", 75, 85);
                break;
            case LaunchStage.WaitingWindow:
                SetStageUi("已启动游戏", "等待游戏窗口出现…", 85, null);
                LaunchProgress.IsIndeterminate = true; // 不确定态闪烁
                _windowDeadline = Environment.TickCount64 + 120_000;
                _windowTimer.Start();
                break;
            case LaunchStage.Running:
                _windowTimer.Stop();
                LaunchStageTitle.Text = "已启动游戏";
                LaunchStageTitle.Foreground = null;
                LaunchStageStepText.Text = "游戏运行中";
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 100;
                break;
            case LaunchStage.Exited:
                _windowTimer.Stop();
                LaunchStageTitle.Text = "游戏已退出";
                LaunchStageTitle.Foreground = null;
                var sec = Math.Max(0, (Environment.TickCount64 - _runningStart) / 1000);
                LaunchStageStepText.Text = sec >= 60
                    ? $"本局时长 {sec / 60} 分 {sec % 60} 秒" : $"本局时长 {sec} 秒";
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 0;
                break;
            case LaunchStage.Failed:
                _windowTimer.Stop();
                LaunchStageTitle.Text = "启动失败";
                LaunchStageTitle.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 0;
                break;
            default: // Idle
                LaunchStageTitle.Text = "正在启动";
                LaunchStageTitle.Foreground = null;
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = 0;
                break;
        }

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
    private void ShowOverlay(string versionId, bool isMicrosoft)
    {
        _cancelRequested = false;
        LaunchCancelBtn.Content = "取消";
        LaunchCancelBtn.IsEnabled = true;
        LaunchVersionText.Text = versionId;
        LaunchLoginModeText.Text = isMicrosoft ? "正版登录" : "离线登录";
        LaunchTipText.Text = LaunchTips.Next();
        LaunchErrorText.Text = "";
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
        ApplyStage(LaunchStage.Idle);
        LaunchOverlay.Visibility = Visibility.Collapsed;
        if (statusText is not null)
        {
            StatusText.Text = statusText;
            StatusText.Visibility = Visibility.Visible;
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
        LaunchCancelBtn.Content = "正在取消…";
        LaunchCancelBtn.IsEnabled = false;
        if (_stage is LaunchStage.WaitingWindow or LaunchStage.Running)
        {
            // 已拉起：杀进程，Exited 回调感知取消标记 → 直接收层回主页
            try { _gameProcess?.Kill(true); } catch { /* 已退出等情况忽略 */ }
        }
        // 未拉起：等当前 await 完成后的阶段边界检查退出（LaunchBtn_Click）
    }

    private void LaunchBackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == LaunchStage.Running)
        {
            // 游戏仍在运行：只收层，主页复用「停止游戏」第二击路径（不杀进程）
            HideOverlay("游戏运行中");
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
    private async void LaunchBtn_Click(object s, RoutedEventArgs e)
    {
        // 第二击「停止游戏」：杀当前进程，右栏状态由 Exited 回调收尾
        if (_gameProcess is { HasExited: false })
        {
            try { _gameProcess.Kill(true); } catch { /* ignore */ }
            return;
        }

        var versionId = (VersionList.SelectedItem as VersionRow)?.Id;
        if (string.IsNullOrEmpty(versionId)) return;

        // 前置校验（spec §3.4）：玩家名不合法在进覆盖层前拦截
        string? offlineName = null;
        if (_mode != "microsoft")
        {
            offlineName = ResolvePlayerName();
            if (string.IsNullOrWhiteSpace(offlineName))
            {
                LocalErrorBar.Message = "请输入离线玩家名或选择账户";
                LocalErrorBar.IsOpen = true;
                return;
            }
        }

        LaunchBtn.IsEnabled = false;
        GlobalErrorBar.IsOpen = false;
        ShowOverlay(versionId, _mode == "microsoft");
        try
        {
            string playerName;
            LaunchCredentials? cred = null;
            if (_mode == "microsoft")
            {
                (playerName, cred) = await ResolveMicrosoftAccountAsync();
                if (_cancelRequested) { HideOverlay("已取消启动"); return; }
            }
            else
            {
                playerName = offlineName!;
            }

            var options = new LaunchOptions(
                versionId, playerName, _s.Settings.DotMinecraft,
                _s.Settings.DefaultMinMemoryMb, _s.Settings.DefaultMaxMemoryMb,
                _s.Settings.JavaExecutable, _s.Settings.ExtraJvmArgs,
                _s.Settings.UseBuiltinJvmArgs, cred);

            // 启动前预检：缺失/损坏文件全自动补全，进度显示在启动页，补完直接启动
            var dotMc = _s.Settings.DotMinecraft;
            ApplyStage(LaunchStage.CheckingFiles);
            var preflight = await _s.Preflight.CheckAsync(dotMc, versionId);
            if (_cancelRequested) { HideOverlay("已取消启动"); return; }
            if (preflight.NeedsRepair)
            {
                if (preflight.VersionJsonBroken && !preflight.CanAutoRestoreJson)
                {
                    HideOverlay(null);
                    LocalErrorBar.Message = "版本 JSON 缺失或损坏，无法自动恢复，请到下载页重新安装";
                    LocalErrorBar.IsOpen = true;
                    return;
                }
                ApplyStage(LaunchStage.RepairingFiles);
                // 唯一真进度：BatchProgress 线性映射到 25→75%（spec §3.2）
                var progress = new Progress<BatchProgress>(p =>
                {
                    LaunchProgress.Value = 25 + p.Percentage * 0.5;
                    LaunchStageStepText.Text = $"补全缺失文件 {p.DoneFiles}/{p.TotalFiles}（{p.Percentage}%）…";
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
                        listed += $"\n等 {ex.FailedFiles.Count} 个文件";
                    var dialog = new ContentDialog
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
            _gameProcess = await _s.Launcher.LaunchVersionAsync(
                _s.Settings.DotMinecraft, versionId, options);
            _gameProcess.Exited += (_, _) => _dq.TryEnqueue(async () =>
            {
                if (_cancelRequested)
                    HideOverlay("已取消启动");        // 取消路径：直接收层回主页（spec §3.4）
                else if (LaunchOverlay.Visibility == Visibility.Visible)
                    ApplyStage(LaunchStage.Exited);   // 覆盖层显示：停在启动页展示本局时长
                else
                {
                    StatusText.Text = "游戏已退出";   // 已收层：右栏状态（返回主页/停止游戏后退出）
                    StatusText.Visibility = Visibility.Visible;
                }
                UpdateVersionMeta();
                // Nova 收尾：补扫日志 → 生成战报卡（仅当本局开过观察会话）
                await _s.NovaRuntime.OnGameExitedAsync();
            });
            if (_cancelRequested)
            {
                // 极短窗口内取消：杀掉刚拉起的进程回主页，Exited 回调续收尾
                try { _gameProcess.Kill(true); } catch { /* ignore */ }
                HideOverlay("已取消启动");
                return;
            }
            _runningStart = Environment.TickCount64;
            ApplyStage(LaunchStage.WaitingWindow); // 窗口出现由 _windowTimer 轮询（§3.3）

            // Nova 时刻：游戏启动即开观察会话（未授权捕捉则静默）；浮窗监测需要 pid
            _s.NovaRuntime.OnGameLaunched(versionId, _gameProcess.Id);
            _s.Settings.LastUsername = _mode == "microsoft" ? "ms:" + playerName : playerName;
            _s.Settings.LastVersion = versionId;
            _s.Settings.Save();
        }
        catch (Exception ex)
        {
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

    private string ResolvePlayerName()
    {
        if (_mode == "microsoft")
        {
            var uuid = AccountCombo.SelectedValue as string;
            return _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)?.Username ?? "";
        }
        var fromCombo = _s.Accounts.Accounts.FirstOrDefault(
            a => a.Uuid == (AccountCombo.SelectedValue as string))?.Username;
        return fromCombo ?? PlayerNameBox.Text.Trim();
    }

    /// <summary>微软模式启动前解析凭据：优先 refresh_token 续期并回写存储；
    /// 续期失效或网络异常时降级用存量 accessToken（1 小时有效期内）；
    /// 无可用凭据时抛出（由启动页 Failed 态展示，spec §3.4）。</summary>
    private async Task<(string Name, LaunchCredentials Cred)> ResolveMicrosoftAccountAsync()
    {
        var uuid = AccountCombo.SelectedValue as string;
        var acc = _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid && a.Type == "microsoft");
        if (acc is null)
            throw new InvalidOperationException("尚无微软账户，请先到账户页登录");
        if (!string.IsNullOrEmpty(acc.RefreshToken))
        {
            try
            {
                var r = await _s.MicrosoftAuth.RefreshAsync(acc.RefreshToken);
                if (r is not null)
                {
                    _s.Accounts.AddOrUpdateMicrosoft(r.Uuid, r.Username, r.AccessToken, r.RefreshToken);
                    return (r.Username, new LaunchCredentials(r.Uuid, r.AccessToken, "msa", r.Xuid));
                }
            }
            catch { /* 网络异常：降级用存量 accessToken */ }
        }
        if (!string.IsNullOrEmpty(acc.AccessToken))
            return (acc.Username, new LaunchCredentials(acc.Uuid, acc.AccessToken, "msa"));
        throw new InvalidOperationException($"{acc.Username} 的 Microsoft 登录已过期，请到账户页重新登录");
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
        var hasAccount = _s.Accounts.Accounts.Count > 0 || PlayerNameBox.Text.Trim().Length > 0;
        SetCheckRow(CheckIconVersion, CheckTextVersion, CheckGoVersion,
            hasInstalled, "游戏版本", hasInstalled ? "就绪" : "未安装", "前往下载");
        SetCheckRow(CheckIconAccount, CheckTextAccount, CheckGoAccount,
            hasAccount, "账户", hasAccount ? "就绪" : "未配置", "前往账户");
        // Java 检测需要拉起 java -version，较慢，异步进行
        _javaOk = null;
        CheckTextJava.Text = "Java 运行时：检测中…";
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
            SetCheckRow(CheckIconJava, CheckTextJava, CheckGoJava, ok, "Java 运行时",
                ok ? (runtimes.Count > 0 ? $"就绪（检测到 {runtimes.Count} 个）" : "就绪（使用手动指定路径）") : "未检测到",
                "前往设置");
        }
        catch
        {
            _javaOk = false;
            SetCheckRow(CheckIconJava, CheckTextJava, CheckGoJava, false, "Java 运行时", "检测失败", "前往设置");
        }
        UpdateCheckCardVisibility();
    }

    private void UpdateCheckCardVisibility()
    {
        var hasInstalled = _rows.Count > 0;
        var hasAccount = _s.Accounts.Accounts.Count > 0 || PlayerNameBox.Text.Trim().Length > 0;
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
