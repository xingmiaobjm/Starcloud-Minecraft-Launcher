// 联机页：内置陶瓦联机 + 第三方联机 SDK。
// 设计：docs/designs/2026-09-21-multiplayer-sdk-design.md
//
// 本页只做两件事：把 MultiplayerService 的状态画出来、把点击翻译成一次操作。
// 状态来源是服务的轮询（1.5 秒一次）+ 每次操作后的主动刷新，页面自己不解析任何协议。
//
// ── 界面分寸 ──────────────────────────────────────────────────────────────
// 常驻只有两张卡（状态 / 操作），日志、使用步骤、第三方 SDK、玩家名与节点地区一律折叠；
// 「加入房间」的邀请码输入收在弹窗里，点了才出现——首屏不摆任何当前用不到的控件。
//
// 安卓适配：DispatcherQueue.TryEnqueue → UiThread.Run；XamlRoot → Ui.Root；
// 「打开清单目录」无桌面资源管理器语义 → 弹窗展示路径；剪贴板保留并兜异常。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Behaviors;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Starcloud.Core.Services.Multiplayer;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class MultiplayerPage : Page, IShellPage
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly MultiplayerService _mp;

    /// <summary>有操作在跑（建房/进房/停止）：按钮统一禁用，避免连点出两次状态迁移</summary>
    private bool _busy;
    private bool _installing;
    private CancellationTokenSource? _installCts;
    /// <summary>程序化填充下拉框时抑制 SelectionChanged，免得被当成用户切换</summary>
    private bool _filling;
    /// <summary>首次 Loaded 已跑完：页面启用导航缓存，之后每次进入由 OnShown 负责恢复订阅与刷新。</summary>
    private bool _firstLoadDone;

    public MultiplayerPage()
    {
        InitializeComponent();
        _mp = _s.Multiplayer;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_firstLoadDone) return; // 缓存实例再次挂树：恢复与刷新由 OnShown 负责，避免重复订阅
        _firstLoadDone = true;
        await EnterPageAsync();
    }

    /// <summary>页面被导航显示（实例已缓存复用）：导航离开时 Unloaded 解绑了订阅并停了轮询，
    /// 首次进入之后的每次进入在这里重新挂上并拉一次最新状态。</summary>
    public void OnShown()
    {
        if (!_firstLoadDone) return; // 首次进入交给 Loaded（此刻页面尚未挂树）
        _ = EnterPageAsync();
    }

    /// <summary>挂订阅、重填下拉并拉一次状态（首次 Loaded 与缓存后每次进入共用；先摘再挂保证幂等）。</summary>
    private async Task EnterPageAsync()
    {
        _mp.Changed -= OnServiceChanged;
        _mp.Changed += OnServiceChanged;
        FillProviders();
        FillNodeRegions();
        FillSdkSection();
        NicknameBox.Text = _mp.Nickname;
        Apply(await _mp.RefreshAsync());
        _mp.StartPolling();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _mp.Changed -= OnServiceChanged;
        _mp.StopPolling();
        // 不取消正在跑的安装：离开页面只代表不再刷这一屏，进度仍在调度中心里走完
    }

    /// <summary>轮询线程来的状态：切回 UI 线程再画</summary>
    private void OnServiceChanged(MultiplayerSnapshot snapshot) =>
        UiThread.Run(() => Apply(snapshot));

    // ── 下拉：联机方案 / 节点地区 ─────────────────────────────────────────

    private void FillProviders()
    {
        _filling = true;
        ProviderCombo.Items.Clear();
        var activeId = _mp.Active.Id;
        foreach (var p in _mp.Providers)
        {
            ProviderCombo.Items.Add(new ComboBoxItem
            {
                // 第三方提供方在名字后面缀上署名，用户一眼能看出这条是谁家的
                Content = p.BuiltIn ? Loc.T(p.DisplayName) : $"{Loc.T(p.DisplayName)} · {p.Vendor}",
                Tag = p.Id,
                IsSelected = p.Id == activeId,
            });
        }
        if (ProviderCombo.SelectedIndex < 0 && ProviderCombo.Items.Count > 0)
            ProviderCombo.SelectedIndex = 0;
        _filling = false;
    }

    private async void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        if (ProviderCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        _mp.SelectProvider(id);
        NicknameBox.Text = _mp.Nickname;
        Apply(await _mp.RefreshAsync());
    }

    private void FillNodeRegions()
    {
        _filling = true;
        var current = _mp.NodeRegion;
        (string Tag, string Label)[] regions =
        [
            ("auto", Loc.T("自动")),
            ("cn", Loc.T("优先大陆节点")),
            ("global", Loc.T("仅国际节点")),
        ];
        NodeRegionCombo.Items.Clear();
        foreach (var (tag, label) in regions)
            NodeRegionCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = tag,
                IsSelected = tag == current,
            });
        if (NodeRegionCombo.SelectedIndex < 0) NodeRegionCombo.SelectedIndex = 0;
        _filling = false;
    }

    private void NodeRegion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        if (NodeRegionCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string region) return;
        _mp.NodeRegion = region;
    }

    private void FillSdkSection()
    {
        SdkDirText.Text = MultiplayerService.SdkManifestDir;
        var thirdParty = _mp.Providers.Where(p => !p.BuiltIn).Select(p => p.DisplayName).ToList();
        SdkListText.Text = thirdParty.Count == 0
            ? Loc.T("当前没有加载到第三方联机 SDK 清单。")
            : Loc.T("已加载第三方联机 SDK：{0}", string.Join("、", thirdParty));
    }

    // ── 画状态 ───────────────────────────────────────────────────────────

    private void Apply(MultiplayerSnapshot snap)
    {
        var core = snap.Core;
        var st = snap.State;

        CoreText.Text = core.Installed ? $"v{core.Version}" : Loc.T("未安装");
        SessionText.Text = core.SessionRunning ? Loc.T("运行中") : Loc.T("已停止");
        PhaseText.Text = PhaseLabel(st);
        switch (st.Phase)
        {
            case MultiplayerPhase.Failed:
                if (ThemeBrush("DangerBrush") is { } danger) PhaseText.Foreground = danger;
                break;
            case MultiplayerPhase.HostReady or MultiplayerPhase.GuestReady:
                if (ThemeBrush("SuccessBrush") is { } success) PhaseText.Foreground = success;
                break;
            default:
                // 其余阶段回到样式里的正常前景色（ClearValue 之后由 ScMetaValue 接管）
                PhaseText.ClearValue(TextBlock.ForegroundProperty);
                break;
        }

        // 长说明只在"需要用户做点什么"时才占一行，平时整行不存在
        var hint = StatusHintText(st);
        StatusHint.Text = hint;
        StatusHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 未装核心 → 只显示安装区；装了 → 显示建房/进房；有会话 → 显示停止区
        InstallActions.Visibility = core.Installed ? Visibility.Collapsed : Visibility.Visible;
        PlayActions.Visibility = core.Installed && !core.SessionRunning ? Visibility.Visible : Visibility.Collapsed;
        StopActions.Visibility = core.SessionRunning ? Visibility.Visible : Visibility.Collapsed;
        // 合规口径写进界面：核心是"装完启动器之后再下载"的，不随安装包分发
        InstallHint.Text = Loc.T(
            "首次使用需下载 {0} 核心（约 8 MB，国内镜像优先，可随时取消）；核心由启动器在首次使用时从官方 Releases 下载，不随安装包分发。",
            snap.DisplayName);

        // 联机详情：邀请码 / 加入侧提示 / 异常，只有真的有内容时才出现
        var roomVisible = st.Phase == MultiplayerPhase.HostReady;
        RoomBlock.Visibility = roomVisible ? Visibility.Visible : Visibility.Collapsed;
        if (roomVisible) RoomCodeText.Text = st.Room ?? "";

        var guestVisible = st.Phase is MultiplayerPhase.GuestConnecting or MultiplayerPhase.GuestEntering
            or MultiplayerPhase.GuestReady;
        GuestBlock.Visibility = guestVisible ? Visibility.Visible : Visibility.Collapsed;
        if (guestVisible)
        {
            GuestTitle.Text = PhaseLabel(st);
            GuestHint.Text = st.Phase switch
            {
                MultiplayerPhase.GuestConnecting => Loc.T("打洞进行中，稍等片刻，状态会自动更新。"),
                MultiplayerPhase.GuestEntering => Loc.T("P2P 难度：{0}。可先在游戏里打开「多人游戏」。",
                    DifficultyLabel(st.Difficulty)),
                _ => Loc.T("打开游戏里的「多人游戏」，在局域网世界列表中进入房主的世界。"),
            };
            var players = st.Players ?? [];
            GuestPlayers.Visibility = players.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (players.Count > 0) GuestPlayers.Text = Loc.T("房间成员：{0}", string.Join("、", players));
        }

        var failed = st.Phase == MultiplayerPhase.Failed;
        ErrorBlock.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        if (failed)
        {
            ErrorTitle.Text = Loc.T("联机异常（{0}）", st.ErrorKind ?? "unknown");
            ErrorHint.Text = st.Error is { Length: > 0 } msg ? Loc.T(msg) : Loc.T("联机发生未知错误");
        }

        DetailsPanel.Visibility = roomVisible || guestVisible || failed
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (snap.LastError is { Length: > 0 } err && NoticeBar.Severity != InfoBarSeverity.Error)
        {
            // 探活/查询失败：提示但不打断（核心没起来时也会走到这里）
            NoticeBar.Severity = InfoBarSeverity.Warning;
            NoticeBar.Message = Loc.T("联机状态查询失败：{0}", err);
            NoticeBar.IsOpen = true;
        }

        VendorLine.Text = Loc.T("联机由 {0} 提供技术支持", snap.Vendor);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        InstallBtn.IsEnabled = !_busy && !_installing;
        CancelInstallBtn.Visibility = _installing ? Visibility.Visible : Visibility.Collapsed;
        HostBtn.IsEnabled = !_busy;
        StopBtn.IsEnabled = !_busy;
        IdleBtn.IsEnabled = !_busy;
    }

    /// <summary>状态值：短到能放进一行</summary>
    private static string PhaseLabel(MultiplayerState st) => st.Phase switch
    {
        MultiplayerPhase.Idle => Loc.T("空闲"),
        MultiplayerPhase.HostScanning => Loc.T("创建房间中"),
        MultiplayerPhase.HostStarting => Loc.T("房间启动中"),
        MultiplayerPhase.HostReady => Loc.T("房间已创建"),
        MultiplayerPhase.GuestConnecting => Loc.T("连接房间中"),
        MultiplayerPhase.GuestEntering => Loc.T("已进入虚拟局域网"),
        MultiplayerPhase.GuestReady => Loc.T("联机成功"),
        _ => Loc.T("联机异常"),
    };

    /// <summary>状态下面那行"下一步要做什么"；没事可做时返回空串（整行隐藏）</summary>
    private static string StatusHintText(MultiplayerState st) => st.Phase switch
    {
        MultiplayerPhase.HostScanning => Loc.T("请先在游戏内开启「对局域网开放」，核心会自动接上那个世界。"),
        MultiplayerPhase.Failed => Loc.T("可以「回到空闲重试」，或展开下方日志看核心报了什么。"),
        _ => "",
    };

    private static string DifficultyLabel(string? difficulty) => difficulty switch
    {
        "easiest" => Loc.T("最简单"),
        "simple" => Loc.T("简单"),
        "medium" => Loc.T("中等"),
        "tough" => Loc.T("困难"),
        _ => Loc.T("未知"),
    };

    // ── 操作 ─────────────────────────────────────────────────────────────

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        _installCts = new CancellationTokenSource();
        InstallPanel.Visibility = Visibility.Visible;
        InstallBar.IsIndeterminate = true;
        InstallText.Text = Loc.T("正在准备安装…");
        UpdateButtons();

        var progress = new Progress<MultiplayerProgress>(p => UiThread.Run(() =>
        {
            InstallBar.IsIndeterminate = p.Total is null or 0;
            if (p.Total is > 0) InstallBar.Value = 100.0 * p.Bytes / p.Total.Value;
            InstallText.Text = p.SpeedBps > 0
                ? Loc.T("{0} · {1:F1} MB · {2:F0} KB/s", Loc.T(p.Stage), p.Bytes / 1048576.0, p.SpeedBps / 1024)
                : Loc.T(p.Stage);
        }));

        try
        {
            await _mp.InstallCoreAsync(progress, _installCts.Token);
            Notify(InfoBarSeverity.Success, Loc.T("联机核心安装完成。"));
        }
        catch (OperationCanceledException)
        {
            Notify(InfoBarSeverity.Informational, Loc.T("已取消安装。"));
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("安装失败：{0}", ex.Message));
        }
        finally
        {
            _installing = false;
            _installCts?.Dispose();
            _installCts = null;
            InstallPanel.Visibility = Visibility.Collapsed;
            Apply(await _mp.RefreshAsync());
        }
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        _installCts?.Cancel();
        Notify(InfoBarSeverity.Informational, Loc.T("正在取消安装…"));
    }

    private async void Host_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        await _mp.HostAsync(ct);
        Notify(InfoBarSeverity.Informational, Loc.T("已开始创建房间，等状态变成「房间已创建」后把邀请码发给好友。"));
    });

    /// <summary>「加入好友的房间」：弹出邀请码输入窗口，确认后再进房</summary>
    private async void JoinToggle_Click(object sender, RoutedEventArgs e)
    {
        var room = await MultiplayerDialogs.AskJoinCodeAsync(Ui.Root!);
        if (string.IsNullOrWhiteSpace(room)) return;
        await RunAsync(ct => _mp.JoinAsync(room, ct));
    }

    private async void Idle_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        await _mp.IdleAsync(ct);
        Notify(InfoBarSeverity.Informational, Loc.T("已回到空闲。"));
    });

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ThemedDialog
        {
            Title = Loc.T("停止联机"),
            Content = Loc.T("确定要停止联机吗？该房间的所有玩家都会断开连接。"),
            PrimaryButtonText = Loc.T("停止联机"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        await RunAsync(async ct =>
        {
            await _mp.StopAsync(ct);
            Notify(InfoBarSeverity.Informational, Loc.T("已停止联机。"));
        });
    }

    private void CopyRoom_Click(object sender, RoutedEventArgs e)
    {
        var code = RoomCodeText.Text;
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(code);
            Clipboard.SetContent(package);
            Notify(InfoBarSeverity.Success, Loc.T("邀请码已复制，发给好友即可加入。"));
        }
        catch (Exception ex)
        {
            // 剪贴板偶发被系统拒绝：把码亮在原地让用户手动复制
            Notify(InfoBarSeverity.Warning, Loc.T("复制失败：{0}（邀请码：{1}）", ex.Message, code));
        }
    }

    /// <summary>一次操作的标准骨架：禁用按钮 → 跑 → 刷新状态 → 出错就地提示</summary>
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await action(cts.Token);
            Apply(await _mp.RefreshAsync());
        }
        catch (OperationCanceledException)
        {
            Notify(InfoBarSeverity.Warning, Loc.T("操作超时：{0}", Loc.T("请检查游戏是否已开启「对局域网开放」，或稍后重试。")));
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("联机操作失败：{0}", ex.Message));
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    // ── 折叠区（抽屉）──────────────────────────────────────────────────────
    // 机制与下载页「大版本抽屉」一致：Button 做头 + 箭头旋转 + ExpandBehavior 高度过渡。
    // 框架自带 Expander 的展开/收起没有高度动画（单帧硬跳），收起时下方内容会整块跳一下。

    /// <summary>表头点击：按 Tag 找到对应抽屉，切换展开态</summary>
    private void SectionHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        var (pane, chevron) = key switch
        {
            "log" => ((FrameworkElement)LogPane, LogChevron),
            "steps" => (StepsPane, StepsChevron),
            "sdk" => (SdkPane, SdkChevron),
            _ => (AdvancedPane, AdvancedChevron),
        };
        SetDrawerExpanded(pane, chevron, ExpandBehavior.GetIsExpanded(pane) != true);
    }

    /// <summary>箭头首次进入可视树：收起态指向右侧，不做动画</summary>
    private void SectionChevron_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FontIcon chevron) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(chevron);
            visual.StopAnimation("RotationAngleInDegrees");
            visual.RotationAngleInDegrees = 0f;
        }
        catch
        {
            // 静默：动效失败不影响功能
        }
    }

    /// <summary>切换抽屉展开态并同步箭头角度（合成动画，绕自身中心）</summary>
    private static void SetDrawerExpanded(FrameworkElement pane, FontIcon chevron, bool expanded)
    {
        ExpandBehavior.SetIsExpanded(pane, expanded);
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(chevron);
            visual.CenterPoint = new Vector3((float)chevron.ActualWidth / 2f, (float)chevron.ActualHeight / 2f, 0f);
            if (!AppServices.Instance.Motion.Enabled)
            {
                visual.StopAnimation("RotationAngleInDegrees");
                visual.RotationAngleInDegrees = expanded ? 90f : 0f;
                return;
            }

            var compositor = visual.Compositor;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(MotionFx.ChevronMs);
            anim.InsertKeyFrame(1f, expanded ? 90f : 0f, MotionFx.ControlEase(compositor));
            visual.StartAnimation("RotationAngleInDegrees", anim);
        }
        catch
        {
            // 静默：箭头转不动不影响抽屉本身
        }
    }

    /// <summary>日志折叠区：点「查看日志」就地展开并拉一次</summary>
    private async void Logs_Click(object sender, RoutedEventArgs e)
    {
        SetDrawerExpanded(LogPane, LogChevron, true);
        var text = await _mp.FetchLogsAsync(CancellationToken.None);
        LogBox.Text = string.IsNullOrWhiteSpace(text) ? Loc.T("（暂无日志）") : text;
    }

    private void CloseLogs_Click(object sender, RoutedEventArgs e) =>
        SetDrawerExpanded(LogPane, LogChevron, false);

    private void Nickname_LostFocus(object sender, RoutedEventArgs e)
    {
        var name = NicknameBox.Text.Trim();
        if (name == _mp.Nickname) return;
        _mp.Nickname = name;
        NicknameBox.Text = _mp.Nickname;
    }

    private void OpenSdkDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(MultiplayerService.SdkManifestDir);
            _ = UiDialog.AlertAsync(Loc.T("联机 SDK 清单目录"), MultiplayerService.SdkManifestDir);
        }
        catch (Exception ex)
        {
            Notify(InfoBarSeverity.Error, Loc.T("打开目录失败：{0}", ex.Message));
        }
    }

    private void Notify(InfoBarSeverity severity, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    /// <summary>取应用级画刷；主题换代 / 资源不存在时返回 null，由调用方保留原样式色</summary>
    private static Microsoft.UI.Xaml.Media.Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v)
            ? v as Microsoft.UI.Xaml.Media.Brush
            : null;
}
