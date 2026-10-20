// 运行状态页（一级）：把 GameRuntimeMonitor 的不可变快照渲染成「这一局在干什么」。
// 设计：docs/designs/2026-09-26-game-running-status-panel-design.md §5
// 移植自桌面 Starcloud.App.Views.RunningStatusPage，仅做安卓侧适配：
// - 导航入口：桌面 (App.MainWindow as MainWindow).NavigateTo → Uno 壳 ShellRootView.NavigateTo
// - 跨页启动请求：桌面 App.PendingLaunchVersionId → 安卓由发起页直写 HomePage.PendingLaunchVersionId
//   （HomePage.OnShown 消费，语义与桌面同约定）
// - 对话框 XamlRoot：Content.XamlRoot → Ui.Root（壳根视图注入）
//
// 刷新刻意分三层，不对齐频率：
//   采样回调（1s，后台线程）→ 只更新数字、曲线、日志尾（选中实例与实例集合都没变就不重建列表）
//   实例变化（起 / 止 / 下拉换选中）→ 整页重建（含下拉、启动配置与日志尾）
//   1s UI 定时器            →  只刷「已运行 00:42:17」这一行（采样暂停时 Changed 不再来）
// 页面 NavigationCacheMode=Disabled，每次进入都是新实例：Loaded 订阅 / Unloaded 退订，避免泄漏。

using System.Globalization;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Windows.Foundation;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class RunningStatusPage : Page
{
    /// <summary>曲线取最近多少个采样点（1s 间隔 ≈ 1 分钟）</summary>
    private const int SparkPoints = 60;

    /// <summary>日志尾展示行数</summary>
    private const int LogLines = 200;

    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();

    private DispatcherQueueTimer? _uiTimer;
    private int _lastPid;
    private int _lastCount;
    private bool _syncingCombo;
    private bool _hooked;
    private bool _rendering;
    private bool _autoScroll = true;

    public RunningStatusPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── 生命周期 ──────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_hooked)
            {
                _hooked = true;
                _s.Runtime.Changed += OnRuntimeChanged;
            }
            _lastPid = _s.Runtime.Current?.Pid ?? 0;
            _lastCount = _s.Runtime.Instances.Count;
            try { SyncSampleMenu(); }
            catch (Exception ex) { LauncherLog.Error("running", "SyncSampleMenu 抛出", ex); }
            try { Render(); }
            catch (Exception ex) { LauncherLog.Error("running", "Render 抛出", ex); }
            try
            {
                _uiTimer ??= CreateUiTimer();
                _uiTimer.Start();
            }
            catch (Exception ex) { LauncherLog.Error("running", "启动 UI 计时器抛出", ex); }
        }
        catch (Exception ex)
        {
            LauncherLog.Error("running", "OnLoaded 整体抛出", ex);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hooked)
        {
            _hooked = false;
            _s.Runtime.Changed -= OnRuntimeChanged;
        }
        _uiTimer?.Stop();
    }

    private DispatcherQueueTimer CreateUiTimer()
    {
        var timer = _dq.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => UpdateUptime();
        return timer;
    }

    /// <summary>秒表兜底：采样暂停（或间隔被调大）时 Changed 不按秒来，这里保证秒数仍在走。</summary>
    private void UpdateUptime()
    {
        if (_rendering) return;
        if (_s.Runtime.Current is not { } info) return;
        _rendering = true;
        try { RenderUptime(info); }
        finally { _rendering = false; }
    }

    // ── Monitor 事件（后台线程）────────────────────────────────

    private void OnRuntimeChanged(GameRunInfo? info)
    {
        var pid = info?.Pid ?? 0;
        var count = _s.Runtime.Instances.Count;
        _dq.TryEnqueue(() =>
        {
            // 选中实例换了（含下拉切换）或实例集合变了（多开起止）都要整页重建
            var changed = pid != _lastPid || count != _lastCount;
            _lastPid = pid;
            _lastCount = count;
            if (changed) Render();
            else RenderLive();
        });
    }

    // ── 渲染分层 ──────────────────────────────────────────────

    /// <summary>整页重建：只在「这一局的归属变了」或用户主动刷新时调用。</summary>
    private void Render()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            var info = _s.Runtime.Current;
            ApplyVisibility(info is not null);

            if (info is not null)
            {
                RebuildInstanceCombo();
                var ctx = ResolveContext(info.VersionId, out var fallback);
                RenderHero(info, ctx);
                RenderPerf(info);
                RenderConfig(ctx, fallback);
                RenderLog();
            }
        }
        finally { _rendering = false; }
    }

    /// <summary>轻量刷新：数字 / 曲线 / 日志尾，每秒一次，不碰列表与配置。</summary>
    private void RenderLive()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            var info = _s.Runtime.Current;
            ApplyVisibility(info is not null);
            if (info is null) return;

            RenderUptime(info);
            RenderPerf(info);
            RenderLog();
        }
        finally { _rendering = false; }
    }

    private void ApplyVisibility(bool running)
    {
        var live = running ? Visibility.Visible : Visibility.Collapsed;
        HeroCard.Visibility = live;
        PerfGrid.Visibility = live;
        DetailGrid.Visibility = live;
        EmptyCard.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        // 「重试」只在采样卡住时露出来，平时用页头那个刷新按钮
        RetryBtn.Visibility = running && _s.Runtime.SamplingPaused
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Hero ─────────────────────────────────────────────────

    /// <summary>
    /// 用正在跑的全部实例重建 Hero 第一行的下拉框（选中项 = 当前展示的实例）。
    /// 同一个版本多开时靠 PID 区分，所以每一项都是「版本 · PID」。
    /// </summary>
    private void RebuildInstanceCombo()
    {
        var instances = _s.Runtime.Instances;
        var selected = _s.Runtime.SelectedPid;

        _syncingCombo = true;   // 填选项会触发 SelectionChanged，别让它再回头 Select 一次
        try
        {
            HeroInstanceCombo.Items.Clear();
            foreach (var info in instances)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(new TextBlock
                {
                    Text = info.VersionId,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock
                {
                    Text = $"PID {info.Pid}",
                    FontSize = 12,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                    FontFamily = new FontFamily("Cascadia Code"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                HeroInstanceCombo.Items.Add(new ComboBoxItem { Content = row, Tag = info.Pid });
            }

            var index = 0;
            for (var i = 0; i < instances.Count; i++)
                if (instances[i].Pid == selected) { index = i; break; }
            HeroInstanceCombo.SelectedIndex = instances.Count == 0 ? -1 : index;

            // 只有一个实例时不必提示「共 N 个」，多开才把数量说出来
            HeroInstanceCount.Text = Loc.T("共 {0} 个实例", instances.Count);
            HeroInstanceCount.Visibility = instances.Count > 1
                ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncingCombo = false; }
    }

    private void HeroInstanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombo) return;
        if (HeroInstanceCombo.SelectedItem is not ComboBoxItem { Tag: int pid }) return;
        if (!_s.Runtime.Select(pid)) return;
        _lastPid = pid;                 // Select 会抛 Changed，这里先对齐避免多一次整页重建
        _lastCount = _s.Runtime.Instances.Count;
        Render();
    }

    private void RenderHero(GameRunInfo info, GameLaunchContext? ctx)
    {
        HeroIsolation.Text = DescribeIsolation(ctx);
        HeroMemory.Text = ctx is null ? "—" : Loc.T("{0} MB", ctx.MaxMemoryMb);
        HeroStarted.Text = info.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        // 命令行含 --accessToken，只在本进程内存里；恢复路径必然拿不到
        var cmd = _s.Runtime.CommandLine;
        var hasCmd = !string.IsNullOrEmpty(cmd);
        CopyCmdBtn.IsEnabled = hasCmd;
        ToolTipService.SetToolTip(CopyCmdBtn,
            hasCmd ? cmd : Loc.T("这是上一轮启动器启动的会话，拿不到命令行"));

        RenderUptime(info);
    }

    private void RenderUptime(GameRunInfo info)
    {
        var t = info.Uptime;
        var text = $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        HeroUptime.Text = info.RecoveredFromLock ? Loc.T("已运行约 {0}", text) : Loc.T("已运行 {0}", text);
        RecoveredChip.Visibility = info.RecoveredFromLock ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 性能三卡 ──────────────────────────────────────────────

    private void RenderPerf(GameRunInfo info)
    {
        var samples = _s.Runtime.Samples(SparkPoints);
        var latest = info.Latest;

        CpuValue.Text = $"{latest.CpuPercent:0.0}%";
        CpuSub.Text = Loc.T("{0} 线程", latest.ThreadCount);

        MemValue.Text = FormatBytes(latest.WorkingSetBytes);
        long peak = 0;
        foreach (var s in samples)
            if (s.WorkingSetBytes > peak) peak = s.WorkingSetBytes;
        MemSub.Text = Loc.T("峰值 {0} · {1} 线程", FormatBytes(peak), latest.ThreadCount);

        if (latest.GpuPercent is double gpu)
        {
            GpuValue.Text = $"{gpu:0.0}%";
            GpuSub.Text = Loc.T("GPU 引擎占用");
        }
        else
        {
            // GpuPercent 为 null 有两种可能：用户关了采集，或这台机器上计数器不可用
            GpuValue.Text = "--";
            GpuSub.Text = _s.Runtime.CollectGpu ? Loc.T("计数器不可用") : Loc.T("未采集 GPU");
        }

        RedrawSparks(samples);

        var paused = _s.Runtime.SamplingPaused ? Visibility.Visible : Visibility.Collapsed;
        CpuPaused.Visibility = paused;
        MemPaused.Visibility = paused;
        GpuPaused.Visibility = paused;
    }

    /// <summary>画三条曲线；尺寸还没定下来（首次布局前 ActualWidth=0）时由 SizeChanged 补画。</summary>
    private void RedrawSparks(IReadOnlyList<GameRunSample> samples)
    {
        if (samples.Count == 0) return;

        Draw(CpuSpark, CpuSparkArea, samples.Select(s => s.CpuPercent).ToList(), 0, 100);
        Draw(MemSpark, MemSparkArea, samples.Select(s => (double)s.WorkingSetBytes).ToList(), null, null);

        var gpus = samples.Where(s => s.GpuPercent.HasValue)
            .Select(s => s.GpuPercent!.Value).ToList();
        if (gpus.Count > 0) Draw(GpuSpark, GpuSparkArea, gpus, 0, 100);
        else GpuSpark.Points = new PointCollection();
    }

    private void SparkArea_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RedrawSparks(_s.Runtime.Samples(SparkPoints));

    /// <summary>
    /// 把一串样本点归一化到画布上。
    /// <paramref name="fixedMin"/> / <paramref name="fixedMax"/> 给定（CPU / GPU 的 0–100）时就按固定量程，
    /// 否则按窗口自身的 min/max 自适应——内存曲线绝对值大、波动小，固定 0 起点会压成一条直线。
    /// </summary>
    private static void Draw(Microsoft.UI.Xaml.Shapes.Polyline line, Canvas area, IReadOnlyList<double> values,
        double? fixedMin, double? fixedMax)
    {
        var w = area.ActualWidth;
        var h = area.ActualHeight;
        var n = values.Count;
        if (n < 2 || w < 2 || h < 2)
        {
            line.Points = new PointCollection();
            return;
        }

        double min, max;
        if (fixedMin is double fmin && fixedMax is double fmax)
        {
            min = fmin;
            max = fmax;
        }
        else
        {
            min = max = values[0];
            for (var i = 1; i < n; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }
            var span = max - min;
            var pad = span > 0 ? span * 0.08 : Math.Max(1, max * 0.02);
            min = Math.Max(0, min - pad);
            max += pad;
        }
        if (max <= min) max = min + 1;

        var pts = new PointCollection();
        for (var i = 0; i < n; i++)
        {
            var norm = Math.Clamp((values[i] - min) / (max - min), 0, 1);
            pts.Add(new Point(i * w / (n - 1), h - norm * h));
        }
        line.Points = pts;
    }

    // ── 启动配置与 JVM ────────────────────────────────────────

    /// <summary>优先用本局落盘的上下文；缺失（上一轮启动器启动、或落盘失败）时按当前配置降级重算。</summary>
    private GameLaunchContext? ResolveContext(string versionId, out bool fallback)
    {
        if (_s.Runtime.LaunchContext is { } live)
        {
            fallback = false;
            return live;
        }
        fallback = true;
        return ResolveFallbackContext(versionId);
    }

    private GameLaunchContext? ResolveFallbackContext(string versionId)
    {
        if (string.IsNullOrEmpty(versionId) || versionId == "未知版本") return null;
        try
        {
            var dotMc = _s.Settings.DotMinecraft;
            var profile = new InstanceSettingsStore(dotMc).Get(versionId);
            var isolated = InstanceLayout.IsIsolated(profile, _s.Settings);
            var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
            var eff = InstanceLayout.Resolve(dotMc, versionId, profile, _s.Settings,
                MemoryAdvisor.Probe(gameRoot));
            return GameLaunchContextStore.Capture(versionId, eff,
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "mods")),
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "shaderpacks")),
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "resourcepacks")));
        }
        catch { return null; }
    }

    private void RenderConfig(GameLaunchContext? ctx, bool fallback)
    {
        ConfigFallbackChip.Visibility = fallback && ctx is not null
            ? Visibility.Visible : Visibility.Collapsed;

        ConfigRows.Children.Clear();
        ChipMods.Text = Loc.T("Mod {0}", ctx?.ModCount ?? 0);
        ChipShaders.Text = Loc.T("着色器 {0}", ctx?.ShaderPackCount ?? 0);
        ChipPacks.Text = Loc.T("资源包 {0}", ctx?.ResourcePackCount ?? 0);

        if (ctx is null)
        {
            ConfigRows.Children.Add(new TextBlock
            {
                Text = Loc.T("读取不到这一局的启动参数"),
                Style = ResStyle("ScMetaValue"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var java = string.IsNullOrWhiteSpace(ctx.JavaPath) ? Loc.T("自动选择") : ctx.JavaPath;
        AddConfigRow(Loc.T("Java"), java, mono: true, tip: java);
        AddConfigRow(Loc.T("内存"),
            Loc.T("-Xmx {0} MB · -Xms {1} MB", ctx.MaxMemoryMb, ctx.MinMemoryMb));
        AddConfigRow(Loc.T("窗口"), DescribeWindow(ctx));
        AddConfigRow(Loc.T("实例模式"), DescribeIsolation(ctx));

        var args = ctx.ExtraJvmArgs;
        AddConfigRow(Loc.T("额外 JVM 参数"),
            args.Length > 0 ? string.Join("\n", args) : Loc.T("无"),
            mono: args.Length > 0);
    }

    private void AddConfigRow(string label, string value, bool mono = false, string? tip = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        var lab = new TextBlock { Text = label, Style = ResStyle("ScMetaLabel") };
        var val = new TextBlock
        {
            Text = value,
            Style = ResStyle("ScMetaValue"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        if (mono) val.FontFamily = new FontFamily("Cascadia Code");
        if (tip is not null) ToolTipService.SetToolTip(val, tip);

        Grid.SetColumn(lab, 0);
        Grid.SetColumn(val, 1);
        grid.Children.Add(lab);
        grid.Children.Add(val);
        ConfigRows.Children.Add(grid);
    }

    // ── 实时日志尾 ────────────────────────────────────────────

    private void RenderLog()
    {
        var text = string.Join("\n", _s.Runtime.TailLog(LogLines));
        if (LogText.Text == text) return;   // 文本没变就别重设，否则用户选中的文字会被清掉

        var stick = _autoScroll || IsAtBottom();
        LogText.Text = text;
        if (!stick)
        {
            ToBottomBtn.Visibility = Visibility.Visible;
            return;
        }

        // 必须等布局跑完才能拿到新的 ScrollableHeight，否则吸到底部的还是旧高度
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        _autoScroll = true;
        ToBottomBtn.Visibility = Visibility.Collapsed;
    }

    private bool IsAtBottom() =>
        LogScroll.ScrollableHeight <= 0 ||
        LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 8;

    private void LogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        var atBottom = IsAtBottom();
        _autoScroll = atBottom;
        ToBottomBtn.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToBottom_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll = true;
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    // ── 动作 ─────────────────────────────────────────────────

    private void GoHome_Click(object sender, RoutedEventArgs e) =>
        (App.MainWindow?.Content as ShellRootView)?.NavigateTo("home");

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        App.PendingLogSource = "game";
        (App.MainWindow?.Content as ShellRootView)?.NavigateTo("logs");
    }

    /// <summary>页头刷新与「重试」共用：清掉暂停态并重置心跳（Monitor.Start 幂等）。</summary>
    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _s.Runtime.Start();
        Render();
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        DiagnoseBtn.IsEnabled = false;
        DiagBar.Severity = InfoBarSeverity.Informational;
        DiagBar.Title = Loc.T("诊断");
        DiagBar.Message = Loc.T("正在扫描版本目录…");
        DiagBar.IsOpen = true;
        try
        {
            var report = await _s.Diagnostics.DiagnoseAsync(_s.Settings.DotMinecraft);
            if (report.Issues.Count == 0)
            {
                DiagBar.Severity = InfoBarSeverity.Success;
                DiagBar.Title = Loc.T("未发现问题");
                DiagBar.Message = Loc.T("扫描 {0} 个版本，未发现问题", report.ScannedCount);
            }
            else
            {
                DiagBar.Severity = InfoBarSeverity.Warning;
                DiagBar.Title = Loc.T("发现 {0} 个问题", report.Issues.Count);
                DiagBar.Message = string.Join("\n", report.Issues.Take(20)
                    .Select(i => $"{i.VersionId} · {i.Kind} · {i.Detail}"));
            }
        }
        catch (Exception ex)
        {
            DiagBar.Severity = InfoBarSeverity.Error;
            DiagBar.Title = Loc.T("诊断失败");
            DiagBar.Message = ex.Message;
        }
        finally
        {
            DiagnoseBtn.IsEnabled = true;
        }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        var cmd = _s.Runtime.CommandLine;
        if (string.IsNullOrEmpty(cmd)) return;

        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        pkg.SetText(cmd);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

        var original = CopyCmdBtn.Content;
        CopyCmdBtn.Content = Loc.T("已复制");
        CopyCmdBtn.IsEnabled = false;
        var timer = _dq.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            CopyCmdBtn.Content = original;
            CopyCmdBtn.IsEnabled = true;
            timer.Stop();
        };
        timer.Start();
    }

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Runtime.Current is not { } info) return;

        var dlg = new ThemedDialog
        {
            Title = Loc.T("结束游戏进程"),
            Content = Loc.T("将强制结束 {0}（PID {1}），未保存的进度会丢失。", info.VersionId, info.Pid),
            PrimaryButtonText = Loc.T("结束进程"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _s.Runtime.Kill();
        }
        catch (Exception ex)
        {
            DiagBar.Severity = InfoBarSeverity.Error;
            DiagBar.Title = Loc.T("结束进程失败");
            DiagBar.Message = ex.Message;
            DiagBar.IsOpen = true;
        }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Runtime.Current is not { } info) return;

        var dlg = new ThemedDialog
        {
            Title = Loc.T("重启游戏"),
            Content = Loc.T("将结束当前进程，并回到首页用 {0} 重新启动。", info.VersionId),
            PrimaryButtonText = Loc.T("重启"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _s.Runtime.Kill();
        // 桌面记在 App.PendingLaunchVersionId；安卓壳由发起页直写 HomePage 静态（HomePage.OnShown 消费）
        HomePage.PendingLaunchVersionId = info.VersionId;
        (App.MainWindow?.Content as ShellRootView)?.NavigateTo("home");
    }

    // ── 采样设置 ──────────────────────────────────────────────

    private void SyncSampleMenu()
    {
        var ms = _s.Runtime.IntervalMs;
        Interval1s.IsChecked = ms <= 1000;
        Interval2s.IsChecked = ms > 1000 && ms <= 2000;
        Interval5s.IsChecked = ms > 2000;
        GpuToggle.IsChecked = _s.Runtime.CollectGpu;
    }

    private void Interval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var ms)) return;
        _s.Runtime.IntervalMs = ms;
        SyncSampleMenu();
    }

    private void GpuToggle_Click(object sender, RoutedEventArgs e)
    {
        _s.Runtime.CollectGpu = GpuToggle.IsChecked;
        RenderLive();
    }

    // ── 小工具 ───────────────────────────────────────────────

    private static string DescribeIsolation(GameLaunchContext? ctx) =>
        ctx is null ? "—"
        : ctx.Isolation == "isolated" ? Loc.T("隔离实例")
        : Loc.T("共享 .minecraft");

    private static string DescribeWindow(GameLaunchContext ctx)
    {
        if (ctx.Fullscreen) return Loc.T("全屏");
        return ctx.WindowWidth > 0 && ctx.WindowHeight > 0
            ? Loc.T("{0} × {1}", ctx.WindowWidth, ctx.WindowHeight)
            : Loc.T("默认窗口");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        const double Mb = 1024 * 1024;
        return bytes >= 1024 * Mb
            ? (bytes / (1024 * Mb)).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
            : (bytes / Mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>取主题资源里的样式（ScMetaLabel / ScMetaValue 只在 App.xaml 顶层字典，直接取即可）。</summary>
    private static Style? ResStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Style : null;

    /// <summary>取主题画刷；<c>DangerBrush</c> / <c>SuccessBrush</c> 是顶层常量，可直接取。</summary>
    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
}
