// Nova 全局运行时（NOVA_INTEGRATION.md：Nova 不是单独页面，而是内嵌在启动器里的 AI 助手）。
// 五个入口共享同一份状态机与消息流：
//   ① 顶栏 NovaOrb（状态着色 + 有提案角标）  ② 侧栏 Nova 页  ③ 实例右键「问 Nova」
//   ④ 底部可折叠对话条  ⑤ 状态栏「Nova: 待命/思考中/有提案/观察中/离线」
//
// 状态机（NOVA_INTEGRATION.md 9.1）：
//   Idle 待命 → Thinking 思考中 → HasPlan 有提案（含时刻候选）→ Observing 观察中（时刻捕捉开启）→ Offline 离线
//
// 时刻联动（NovaMomentsService）：游戏启动且「时刻捕捉」授权开启 → StartSession + 5 秒观察循环；
// 命中高光 → CandidateProposed → 全局消息流追加候选卡（克制：一句话 + 一个按钮），点头才转正。

using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

/// <summary>Nova 状态栏五态（NOVA_INTEGRATION.md 9.1）</summary>
public enum NovaState
{
    Idle,       // 待命（灰）
    Thinking,   // 思考中（紫）
    HasPlan,    // 有提案（黄，含未处理的时刻候选）
    Observing,  // 观察中（绿，时刻捕捉开启）
    Offline,    // 离线（暗灰，未登录 Starcloud ID）
}

/// <summary>浮窗 / 状态栏一句话提醒（Text 可直接展示；Evidence 仅疑似卡顿时携带）</summary>
public sealed record NovaOverlayToast(string Text, StutterEvidence? Evidence = null);

/// <summary>
/// Nova 全局运行时：一份消息流 / 一份对话历史 / 一个状态机，页面与浮层全部订阅它。
/// 由 AppServices 构造（UI 线程），之后页面 / 面板 / 状态栏只读它、调它。
/// </summary>
public sealed class NovaRuntime
{
    private readonly AppServices _s;
    private readonly DispatcherQueue? _dq; // AppServices 在 UI 线程构造，此处即可取到
    private readonly List<NovaChatTurn> _history = [];
    private readonly Dictionary<string, string> _candidatePlans = []; // planId → candidateId
    private readonly DispatcherQueueTimer? _observeTimer;

    private bool _busy;
    private bool _observeBusy;
    private bool _observing;

    public event Action<NovaState>? StateChanged;

    /// <summary>动手流水变化（确认执行 / 取消 / 授权开关）→ Nova 页右栏刷新</summary>
    public event Action? LogChanged;

    /// <summary>「问 Nova」上下文注入：带一句话进输入框（页面 / 面板 / 底部条各自接）</summary>
    public event Action<string>? InputRequested;

    /// <summary>当前实例变化 → 状态栏「实例: X」</summary>
    public event Action<string?>? InstanceChanged;

    /// <summary>浮窗 / 状态栏一句话提醒（详情在对话流证据卡，设计：nova-overlay-plan.md T4）</summary>
    public event Action<NovaOverlayToast>? OverlayToast;

    /// <summary>当前实例（最近一次启动 / 右键「问 Nova」选定的实例）</summary>
    public string? CurrentInstance { get; private set; }

    /// <summary>全局唯一消息流：Nova 页 / 顶栏面板 / 底部对话条共用</summary>
    public ObservableCollection<NovaChatMessage> Messages { get; } = [];

    public NovaRuntime(AppServices services)
    {
        _s = services;
        try { _dq = DispatcherQueue.GetForCurrentThread(); } catch { /* 非 UI 线程构造时退化为直呼 */ }

        // 时刻候选 → 全局消息流候选卡（状态切「有提案」，Orb 出角标）
        _s.NovaMoments.CandidateProposed += c => RunOnUi(() => ProposeCandidate(c));

        // 观察循环：5 秒一轮增量解析 latest.log（NOVA_INTEGRATION.md 7.1 观察中）
        var timer = _dq?.CreateTimer();
        if (timer is not null)
        {
            timer.Interval = TimeSpan.FromSeconds(5);
            timer.IsRepeating = true;
            timer.Tick += async (_, _) => await ObserveTickAsync();
        }
        _observeTimer = timer;

        if (_s.StarcloudId.IsLoggedIn) EnsureGreeting();
        RefreshState(); // 按已有会话初始化状态：否则冷启动时 Orb 面板会一直显示未登录引导
    }

    // ── 状态机 ──

    public NovaState State { get; private set; } = NovaState.Offline;

    private void RefreshState()
    {
        var next = ComputeState();
        if (next == State) return;
        State = next;
        StateChanged?.Invoke(next);
    }

    private NovaState ComputeState()
    {
        if (_s.StarcloudId.Current is null) return NovaState.Offline;
        if (_busy) return NovaState.Thinking;
        if (Messages.Any(m => m.Plan is not null && m.PlanState == "pending")) return NovaState.HasPlan;
        if (_observing) return NovaState.Observing;
        return NovaState.Idle;
    }

    public string StateText => State switch
    {
        NovaState.Idle => Loc.T("待命"),
        NovaState.Thinking => Loc.T("思考中…"),
        NovaState.HasPlan => Loc.T("有提案"),
        NovaState.Observing => Loc.T("观察中"),
        _ => Loc.T("离线"),
    };

    // ── 对话（入口 ①④② 共用：SendAsync → ChatStreamAsync → 消息流） ──

    /// <summary>
    /// 发送一句话。<paramref name="fullLog"/> = 用户在对话页打开了「发送完整日志」，
    /// 跳过崩溃日志裁剪（仅对崩溃日志正文生效；闲聊/提问一律原样）。
    /// </summary>
    public async Task SendAsync(string input, bool fullLog = false)
    {
        input = input.Trim();
        if (input.Length == 0 || _busy) return;
        if (_s.StarcloudId.Current is null)
        {
            AddAssistant(Loc.T("先在侧栏 Nova 页登录 Starcloud ID，我才能上线干活。"));
            return;
        }
        await RunTurnAsync(input, fullLog);
    }

    /// <summary>
    /// 一轮对话的核心：user 气泡（可选回显）→ 流式接收（文本增量 + 工具状态行 + 确认卡）→ 记历史。
    /// Agent 确认清单执行/取消后的总结轮也走这里（echoUser=false，指令进历史不进气泡）。
    /// </summary>
    private async Task RunTurnAsync(string input, bool fullLog = false, bool echoUser = true)
    {
        _busy = true;
        RefreshState();
        Messages.Add(new NovaChatMessage("user", input, Now()));
        _history.Add(new NovaChatTurn("user", input));
        if (!echoUser) Messages.RemoveAt(Messages.Count - 1); // 隐藏指令：进历史但不显示气泡

        var msg = new NovaChatMessage("assistant", "", Now());
        Messages.Add(msg);
        NovaPlan? plan = null; // 计划不挂在本条消息：计划文本留在气泡里，确认/取消走独立询问消息
        try
        {
            var stream = _s.Nova.ChatStreamAsync(
                input, _s.Settings, _history,
                fullLog: fullLog,
                onFullReply: reply =>
                {
                    if (reply.Plan is not null) plan = reply.Plan;
                },
                onToolStarted: (tool, args) =>
                {
                    // 工具状态行：只读工具立即执行的过程透明化，留在气泡里可回看
                    RunOnUi(() => msg.Content += $"\n→ {Loc.T("正在执行")} {tool} {args}\n");
                });
            await foreach (var delta in stream)
            {
                var d = delta;
                RunOnUi(() => msg.Content += d);
            }

            _history.Add(new NovaChatTurn("assistant", msg.Content));
            if (_history.Count > 20) _history.RemoveRange(0, _history.Count - 20);

            // 计划就绪 → 追加独立询问消息「确认执行吗？」：点头执行 / 取消（结果回喂见 ConfirmPlanAsync）
            if (plan is not null)
                Messages.Add(new NovaChatMessage("assistant", Loc.T("确认执行吗？"), Now(), plan: plan));
        }
        catch (Exception ex)
        {
            RunOnUi(() => msg.Content = Loc.T("出错了：{0}", ex.Message));
        }
        finally
        {
            _busy = false;
            RefreshState();
        }
    }

    /// <summary>Agent 确认清单执行/取消后：把结果回喂历史，再跑一轮出总结（不回显指令气泡）。</summary>
    private Task ResumeAfterPlanAsync(string instruction, bool fullLog = false) =>
        _busy ? Task.CompletedTask : RunTurnAsync(instruction, fullLog, echoUser: false);

    /// <summary>实例右键「问 Nova」：把带实例上下文的一句话送进输入框</summary>
    public void RequestInput(string text) => RunOnUi(() => InputRequested?.Invoke(text));

    // ── 确认清单：点头才动手，执行记流水（入口共用） ──

    public async Task ConfirmPlanAsync(NovaChatMessage msg)
    {
        if (msg.Plan is not { } plan || msg.PlanState != "pending") return;
        var (result, ok) = ("", false);
        try
        {
            result = await _s.NovaLog.ExecuteConfirmedAsync(plan);
            msg.PlanState = "done";
            msg.ResultText = result;
            ok = true;
        }
        catch (Exception ex)
        {
            msg.PlanState = "failed";
            msg.ResultText = ex.Message;
        }
        finally
        {
            LogChanged?.Invoke();
        }
        RefreshState();

        // Agent 清单执行完 → 结果回喂历史，让 Nova 出一句总结/下一步建议
        if (ok)
            await ResumeAfterPlanAsync(Loc.T(
                "[系统] 用户已确认执行「{0}」，执行结果：{1}。请用一两句话告诉用户结果和下一步（没有就直接说完成）。",
                plan.Title, result));
        else
            await ResumeAfterPlanAsync(Loc.T(
                "[系统] 用户确认的「{0}」执行失败：{1}。请简短说明原因和手动补救办法。",
                plan.Title, msg.ResultText));
    }

    public void CancelPlan(NovaChatMessage msg)
    {
        if (msg.Plan is not { } plan || msg.PlanState != "pending") return;
        _s.NovaLog.LogCancelled(plan);
        // 时刻候选卡被划掉 = 不要这个候选
        if (_candidatePlans.Remove(plan.Id, out var candidateId))
            _s.NovaMoments.Dismiss(candidateId);
        msg.PlanState = "cancelled";
        LogChanged?.Invoke();
        RefreshState();
        _ = ResumeAfterPlanAsync(Loc.T(
            "[系统] 用户取消了「{0}」，没有执行任何动作。简短回应即可，不要重复推销这个操作。", plan.Title));
    }

    // ── 时刻候选（入口 ①⑤：克制提案——一句话 + 一个按钮） ──

    private void ProposeCandidate(NovaMomentCandidate c)
    {
        var plan = new NovaPlan(
            Guid.NewGuid().ToString("N")[..12],
            Loc.T("做成 Nova 时刻"),
            [new NovaActionItem(
                Loc.T("收藏{0}片段", c.KindText), $"{c.TimeText} · {NovaMomentsService.ClipRange}")],
            _ => Task.FromResult(AcceptCandidateText(c.Id)));
        _candidatePlans[plan.Id] = c.Id;

        var msg = new NovaChatMessage("assistant", c.Caption, Now(), plan: plan);
        Messages.Add(msg);
        RefreshState();
    }

    /// <summary>直接点头收下候选（底部条 / 面板的快捷按钮走这条）</summary>
    public string AcceptCandidateText(string candidateId)
    {
        var moment = _s.NovaMoments.Accept(candidateId);
        return Loc.T("已收藏：{0} {1}（{2}）", moment.KindText, moment.TimeText, moment.ClipRangeText);
    }

    public void DismissCandidate(string candidateId) => _s.NovaMoments.Dismiss(candidateId);

    // ── 游戏生命周期联动（HomePage 启动 / 退出时调用） ──

    private string? _currentInstance;

    public void SetCurrentInstance(string? versionId)
    {
        if (_currentInstance == versionId) return;
        _currentInstance = versionId;
        _s.Nova.SetCurrentInstance(versionId);
        InstanceChanged?.Invoke(versionId); // 状态栏「实例: X」联动
    }

    public void OnGameLaunched(string versionId, int gamePid = 0)
    {
        SetCurrentInstance(versionId);
        _s.NovaMoments.StartSession(versionId);

        // 游戏内浮窗（设计：nova-overlay-design.md §4；开关默认关 = 开启即显式授权）
        if (_s.Settings.NovaOverlayEnabled)
        {
            ShowOverlay();
            if (gamePid > 0)
            {
                EnsureWatcher();
                _watcher!.Start(gamePid);
            }
        }

        if (!_s.NovaPermissions.IsOn(NovaPermissionStore.Capture))
        {
            RefreshState();
            return; // 未授权捕捉：不观察不说话（克制）
        }

        StartObserving();
        AddAssistant(Loc.T("跟你说一声：时刻捕捉开着，我会留意这一局的高光，有料会喊你。"));
        RefreshState();
    }

    public async Task OnGameExitedAsync()
    {
        _watcher?.Stop(); // 一局结束，监测即停
        StopObserving();
        if (_s.NovaMoments.IsSessionActive)
        {
            try
            {
                // 收尾补扫一轮：把退出前最后几行日志的事件收进战报
                await _s.NovaMoments.ObserveOnceAsync(_s.Settings.DotMinecraft);
            }
            catch { /* 补扫失败不影响战报 */ }
            var report = _s.NovaMoments.EndSession();
            AddAssistant(_s.NovaMoments.BuildReportText(report));
            OverlayToast?.Invoke(new NovaOverlayToast(Loc.T("Nova：本局战报已生成，点浮窗查看")));
        }
        HideOverlaySoon(); // 战报留 5s 阅读窗，随后收浮窗
        RefreshState();
    }

    private void StartObserving()
    {
        _observing = true;
        _observeTimer?.Start();
    }

    private void StopObserving()
    {
        _observing = false;
        _observeTimer?.Stop();
    }

    // ── 游戏内浮窗联动（设计：nova-overlay-plan.md T4；浮窗单例由 Runtime 懒创建持有） ──

    private Views.NovaOverlayWindow? _overlay;
    private NovaFpsWatcher? _watcher;

    private void ShowOverlay()
    {
        try
        {
            _overlay ??= new Views.NovaOverlayWindow();
            _overlay.ShowOverlay();
        }
        catch { /* 浮窗创建失败不影响主流程（克制降级） */ }
    }

    private void HideOverlaySoon()
    {
        if (_overlay is null) return;
        var t = _dq?.CreateTimer();
        if (t is null) { _overlay.HideOverlay(); return; }
        t.Interval = TimeSpan.FromSeconds(5);
        t.Tick += (_, _) => { t.Stop(); _overlay?.HideOverlay(); };
        t.Start();
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null) return;
        var w = new NovaFpsWatcher();
        // 采样线程触发 → 统一转 UI 线程（与其他事件同一转发纪律）
        w.SuspectedStutter += e => RunOnUi(() => OnStutter(e));
        _watcher = w;
    }

    /// <summary>疑似卡顿证据卡：一句话 + [游戏体检] 确认清单（点头才动手），同步弹浮窗 Toast。</summary>
    private void OnStutter(StutterEvidence e)
    {
        // 上一次疑似卡顿还没处理完就不再叠卡（克制；Watcher 侧另有 10 分钟冷却）
        // 标题由 Loc.T 产出（en-US 下会翻译），此处必须用同一取词，否则去重守卫在英文界面失配
        if (Messages.Any(m => m.Plan is not null && m.Plan.Title == Loc.T("游戏体检") && m.PlanState == "pending"))
            return;

        var gpu = e.GpuAvailable && e.GpuAvgPercent is { } g ? Loc.T("，GPU 平均 {0:F0}%", g) : "";
        var text = Loc.T("刚才 {0} 秒里 CPU 平均 {1:F0}%、抖动明显{2}，", e.WindowSeconds, e.CpuAvgPercent, gpu) +
                   Loc.T("疑似卡顿（启发式推测，不是实测帧数）。要我跑一次游戏体检找原因吗？");
        var plan = new NovaPlan(
            Guid.NewGuid().ToString("N")[..12],
            Loc.T("游戏体检"),
            [new NovaActionItem(Loc.T("跑一次游戏体检"), Loc.T("CPU 均值 {0:F0}% · {1} 秒窗口", e.CpuAvgPercent, e.WindowSeconds))],
            ct => RunCheckupAsync(ct));
        Messages.Add(new NovaChatMessage("assistant", text, Now(), plan: plan));

        OverlayToast?.Invoke(new NovaOverlayToast(
            Loc.T("Nova：疑似卡顿（CPU {0:F0}%），点浮窗看详情", e.CpuAvgPercent), e));
        RefreshState();
    }

    /// <summary>证据卡「跑一次游戏体检」→ 复用既有体检意图路由（ChatAsync），不另起逻辑。</summary>
    private async Task<string> RunCheckupAsync(CancellationToken ct)
    {
        if (_busy) return Loc.T("Nova 正在处理别的请求，稍等再点一次。");
        // 该文本同时是 Core 侧 NovaService 的中文关键词路由依据（lower.Contains("体检")），故不本地化
        await SendAsync("游戏体检");
        return Loc.T("体检方案已生成，见下方确认清单。");
    }

    // ── 协议激活（starcloud://join?t=…）：好友邀请直达确认清单，不过 LLM ──

    public async Task HandleJoinUrlAsync(string url)
    {
        if (!NovaInviteService.TryParseJoinUrl(url, out var token) || token is null)
        {
            AddAssistant(Loc.T("这个邀请链接无效或已过期，让好友重新生成一份吧。"));
            RefreshState();
            return;
        }
        if (_s.StarcloudId.Current is null)
        {
            AddAssistant(Loc.T("先在侧栏 Nova 页登录 Starcloud ID，我才能带你进好友的世界。"));
            RefreshState();
            return;
        }
        try
        {
            var plan = await _s.NovaInvites.BuildJoinPlanAsync(token, _s.Settings);
            Messages.Add(new NovaChatMessage("assistant", Loc.T("收到好友邀请，确认执行吗？"), Now(), plan: plan));
        }
        catch (Exception ex)
        {
            AddAssistant(Loc.T("邀请处理失败：{0}", ex.Message));
        }
        RefreshState();
    }

    private async Task ObserveTickAsync()
    {
        if (_observeBusy || !_s.NovaMoments.IsSessionActive) return;
        _observeBusy = true;
        try
        {
            await _s.NovaMoments.ObserveOnceAsync(_s.Settings.DotMinecraft);
        }
        catch { /* 单轮观察失败不阻断循环 */ }
        finally
        {
            _observeBusy = false;
        }
    }

    // ── 登录态 / 会话（Nova 页登录、退出时调用） ──

    public void NotifyLoginChanged()
    {
        Messages.Clear();
        _history.Clear();
        _candidatePlans.Clear();
        if (_s.StarcloudId.Current is not null) EnsureGreeting();
        RefreshState();
    }

    private void EnsureGreeting()
    {
        if (Messages.Count > 0 || _s.StarcloudId.Current is not { } s) return;
        Messages.Add(new NovaChatMessage("assistant",
            Loc.T("你好，{0}！我是 Nova——你说一句人话，我打理启动器里的脏活累活；所有落盘动作先给确认清单，点头才执行。\n", s.DisplayName) +
            Loc.T("试试：崩了帮我看看 / 游戏体检 / 检查模组冲突 / 开箱即玩 / 找个整合包"),
            Now()));
    }

    // ── 消息辅助 ──

    private void AddAssistant(string text) =>
        Messages.Add(new NovaChatMessage("assistant", text, Now()));

    private void RunOnUi(Action action)
    {
        var dq = _dq;
        if (dq is null) { action(); return; }
        if (dq.HasThreadAccess) action();
        else dq.TryEnqueue(() => action());
    }

    private static string Now() => DateTime.Now.ToString("HH:mm");
}

/// <summary>
/// 对话消息模型（Bubble/TextBrush/Align 按 Role 一次性求值）；
/// Plan 非空 = 确认清单卡，PlanState 跟踪 pending/done/cancelled/failed。
/// 定义在 Runtime 层：Nova 页 / 顶栏面板 / 底部对话条共用同一份模型与模板。
/// </summary>
public sealed class NovaChatMessage : INotifyPropertyChanged
{
    private string _content;
    private NovaPlan? _plan;
    private string _planState;
    private string _resultText;

    public NovaChatMessage(
        string role, string content, string timeText,
        NovaPlan? plan = null, string planState = "pending", string resultText = "")
    {
        Role = role;
        _content = content;
        TimeText = timeText;
        _plan = plan;
        _planState = planState;
        _resultText = resultText;
    }

    public string Role { get; }
    public string TimeText { get; }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(TextVisible)); // 气泡可见性跟内容走（流式空串 → 首个增量显形）
        }
    }

    public NovaPlan? Plan
    {
        get => _plan;
        set
        {
            if (_plan == value) return;
            _plan = value;
            OnPropertyChanged(nameof(Plan));
            OnPropertyChanged(nameof(TextVisible));
            OnPropertyChanged(nameof(HasPlanVisible));
            OnPropertyChanged(nameof(PlanButtonsVisible));
            OnPropertyChanged(nameof(PlanStateVisible));
        }
    }

    public string PlanState
    {
        get => _planState;
        set
        {
            if (_planState == value) return;
            _planState = value;
            OnPropertyChanged(nameof(PlanState));
            OnPropertyChanged(nameof(PlanButtonsVisible));
            OnPropertyChanged(nameof(PlanStateVisible));
            OnPropertyChanged(nameof(PlanStateText));
        }
    }

    public string ResultText
    {
        get => _resultText;
        set
        {
            if (_resultText == value) return;
            _resultText = value;
            OnPropertyChanged(nameof(ResultText));
            OnPropertyChanged(nameof(PlanStateText));
        }
    }

    public HorizontalAlignment Align => Role == "user"
        ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    // 用户气泡右下角收口、AI 气泡左下角收口（Fluent 会话样式）
    public CornerRadius Corners => Role == "user"
        ? new CornerRadius(12, 12, 4, 12) : new CornerRadius(12, 12, 12, 4);

    public Brush Bubble => Role == "user"
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Brush)Application.Current.Resources["GlassCardBrush"];

    public Brush TextBrush => Role == "user"
        ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    public Visibility TextVisible => string.IsNullOrEmpty(Content)
        ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HasPlanVisible => Plan is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PlanButtonsVisible =>
        Plan is not null && PlanState == "pending" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlanStateVisible =>
        Plan is not null && PlanState != "pending" ? Visibility.Visible : Visibility.Collapsed;

    public string PlanStateText => PlanState switch
    {
        "done" => Loc.T("✔ 已确认执行（已记入动手流水）") +
                  (string.IsNullOrEmpty(ResultText) ? "" : $"\n{ResultText}"),
        "cancelled" => Loc.T("已取消，未做任何改动"),
        "failed" => Loc.T("执行失败（已记入动手流水）：{0}", ResultText),
        _ => "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
