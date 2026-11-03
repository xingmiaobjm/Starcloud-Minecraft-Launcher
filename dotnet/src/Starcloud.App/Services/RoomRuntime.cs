// 联机房间的运行时状态：当前所在房间、临时聊天、陶瓦联机码、房间事件。
//
// 分工：
//   · RoomService —— 只翻译 HTTP/SSE，无状态；
//   · RoomRuntime —— 记住「我现在在哪个房间」（跨页面存活），把 SSE 事件翻成
//     状态更新 + 通知，并在进房后维持一条断线重连的事件流。
//
// 事件在后台线程到达：订阅方（页面）自己 DispatcherQueue.TryEnqueue 回 UI 线程。

using System.Text.Json;
using Microsoft.UI.Dispatching;
using Starcloud.App.Localization;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Rooms;

namespace Starcloud.App.Services;

/// <summary>联机房间运行时（单例，跨页面存活）。</summary>
public sealed class RoomRuntime
{
    private readonly RoomService _rooms;
    private readonly MultiplayerService? _multiplayer;
    private readonly Func<string?> _myUserId;
    private readonly object _gate = new();
    private readonly List<RoomMessage> _messages = [];
    private CancellationTokenSource? _eventsCts;
    private int _entering;
    private volatile bool _leftByEvent;   // room_closed/kicked 已处理过退出
    private LanProbeService? _lanProbe;
    private DispatcherQueueTimer? _hostRetryTimer;
    private long _lastHostScanMs;
    private MultiplayerPhase _lastLoggedPhase = MultiplayerPhase.Idle;   // 相位日志去重

    /// <param name="myUserId">当前登录用户的 user_id（服务端踢人事件里靠它判断「是不是我」）</param>
    /// <param name="multiplayer">陶瓦联机会话；房主开房 / 玩家加入都走它</param>
    public RoomRuntime(RoomService rooms, Func<string?>? myUserId = null,
        MultiplayerService? multiplayer = null)
    {
        _rooms = rooms;
        _myUserId = myUserId ?? (() => null);
        _multiplayer = multiplayer;
        if (_multiplayer is not null) _multiplayer.Changed += OnMultiplayerChanged;
        // 游戏进程结束 → 房间跟着关（房主=解散（服务端房主不能 leave），成员=退出）。
        // 判定用「在房间里见过活游戏，之后游戏没了」而不是 RunFinished ——
        // 心跳会补收「恢复的已死会话」也发 RunFinished，会把刚建的房间秒关（实测踩过）。
        GameRuntimeMonitor.Instance.Changed += OnGameMonitorChanged;
    }

    /// <summary>进房后是否见过存活的游戏进程（见过才有资格判定「游戏退出了」）。</summary>
    private bool _sawGameWhileInRoom;

    private void OnGameMonitorChanged(GameRunInfo? _current)
    {
        if (Current is null)
        {
            _sawGameWhileInRoom = false;
            return;
        }

        var hasLiveGame = GameRuntimeMonitor.Instance.Instances.Count > 0;
        if (hasLiveGame)
        {
            _sawGameWhileInRoom = true;
            return;
        }
        // 没有活进程：只有「进房后确实见过游戏在跑」才算游戏退出
        if (!_sawGameWhileInRoom) return;
        _sawGameWhileInRoom = false;

        if (Current.IsOwner)
        {
            LauncherLog.Write("rooms", $"游戏进程结束，自动解散房间 {Current.Room.Code}");
            Notify?.Invoke(Loc.T("游戏已退出，房间已自动关闭"));
            _ = DissolveAsync();
        }
        else
        {
            LauncherLog.Write("rooms", $"游戏进程结束，自动退出房间 {Current.Room.Code}");
            Notify?.Invoke(Loc.T("游戏已退出，已退出房间"));
            _ = LeaveAsync();
        }
    }

    /// <summary>玩家侧：陶瓦连上房主后拿到的游戏内地址（--quickPlayMultiplayer 用）。</summary>
    public string? GuestAddress { get; private set; }

    /// <summary>房主侧：本机联机房间是否已开好（陶瓦核心发现局域网世界）。</summary>
    public bool HostReady { get; private set; }

    public RoomService Service => _rooms;

    /// <summary>当前所在房间（未进房为 null）。</summary>
    public RoomDetail? Current { get; private set; }

    /// <summary>验密通过后下发的陶瓦联机码（房主还没开房时为 null）。</summary>
    public string? TerracottaCode { get; private set; }

    /// <summary>房间临时聊天（最近 200 条）。</summary>
    public IReadOnlyList<RoomMessage> Messages
    {
        get { lock (_gate) return _messages.ToList(); }
    }

    /// <summary>房间状态变化（后台线程）。</summary>
    public event Action? Changed;

    /// <summary>需要弹提示的事件（被踢、房间关闭、有人进出）；后台线程。</summary>
    public event Action<string>? Notify;

    /// <summary>陶瓦码已就绪（房主上报后 / 进房拿到时），供「自动连接」使用。</summary>
    public event Action<string>? TerracottaReady;

    public bool InRoom => Current is not null;

    /// <summary>进房：验密 → 拉历史 → 订阅事件。失败抛 <see cref="RoomApiException"/>。</summary>
    public async Task<RoomJoinResult> EnterAsync(string code, string? password,
        CancellationToken ct = default)
    {
        // 重复点击保护：进房要建 SSE 连接，连点会堆出一串事件流
        if (Interlocked.Exchange(ref _entering, 1) == 1)
            throw new RoomApiException("正在进入房间，请稍候");

        try
        {
            await LeaveAsync(silent: true);   // 换房：先把上一个退掉
            var result = await _rooms.JoinAsync(code, password, ct);

            var history = await _rooms.GetMessagesAsync(code, null, 100, ct);
            lock (_gate)
            {
                _messages.Clear();
                _messages.AddRange(history);
            }
            Current = result.Room;
            TerracottaCode = result.TerracottaCode;
            LauncherLog.Debug("rooms",
                $"进房完成 {code}：{result.Room.Members.Count} 名成员"
                + $"，陶瓦码 {(result.TerracottaCode is null ? "未下发" : "已下发")}");
            RaiseChanged();
            if (result.TerracottaCode is { Length: > 0 } tc) TerracottaReady?.Invoke(tc);
            _leftByEvent = false;
            StartEvents(code);
            StartHeartbeat(code);
            // 陶瓦核心状态轮询必须跟着房间走：旧联机页（唯一的 StartPolling 调用方）
            // 已从导航下线，不在这里开轮询的话 HostReady / GuestReady 永远不会被感知
            // —— 表现就是房主开了 LAN、核心也要了管理员权限，房间却一直「房主尚未开房」。
            _multiplayer?.StartPolling();
            // 玩家侧：房主已经开好房就立刻连过去（拿到游戏内地址后「开始游戏」才可用）
            if (!result.Room.IsOwner && result.TerracottaCode is { Length: > 0 })
                _ = JoinGuestAsync();
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _entering, 0);
        }
    }

    public async Task LeaveAsync(bool silent = false, CancellationToken ct = default)
    {
        var code = Current?.Room.Code;
        StopEvents();
        StopHeartbeat();
        // 房主侧探测与重试只在房间里有效：离开/解散就停掉，别在后台一直发扫描请求
        _lanProbe?.Dispose();
        _lanProbe = null;
        _hostRetryTimer?.Stop();
        _hostRetryTimer = null;
        _multiplayer?.StopPolling();   // 与 EnterAsync 的 StartPolling 成对：不在房间就不轮询核心
        if (code is not null)
        {
            try { await _rooms.LeaveAsync(code, ct); }
            catch { /* 退房失败（已解散/断网）不影响本地状态清理 */ }
        }
        Current = null;
        TerracottaCode = null;
        GuestAddress = null;
        HostReady = false;
        // 房间相关的一次性状态必须清零，否则会跨房间串味：
        // ① 旧房间"见过游戏在跑"的标记会让**新房间**在游戏结束时被误解散；
        // ② 残留的 Failed 相位会让新房间首次扫描前多打一次 Idle（可能把正在进行的扫描复位）；
        // ③ 残留的错误文本会让新房间的同类错误被去重吞掉（用户看不到提示）。
        _sawGameWhileInRoom = false;
        _lastLoggedPhase = MultiplayerPhase.Idle;
        _lastHostScanError = null;
        lock (_gate) _messages.Clear();
        if (!silent) RaiseChanged();
    }

    public async Task SendAsync(string content, CancellationToken ct = default)
    {
        if (Current is not { } room) return;
        var message = await _rooms.SendMessageAsync(room.Room.Code, content, ct);
        // 自己的消息不经 SSE 回显（服务端可能也推一份，按 Id 去重）
        lock (_gate)
        {
            if (_messages.All(m => m.Id != message.Id)) _messages.Add(message);
        }
        RaiseChanged();
    }

    public async Task KickAsync(string userId, CancellationToken ct = default)
    {
        if (Current is not { } room) return;
        await _rooms.KickAsync(room.Room.Code, userId, ct);
        await RefreshAsync(ct);
    }

    public async Task DissolveAsync(CancellationToken ct = default)
    {
        if (Current is not { } room) return;
        await _rooms.DissolveAsync(room.Room.Code, ct);
        await LeaveAsync(silent: true, ct);
        RaiseChanged();
    }

    /// <summary>重新拉一次房间详情（成员变化、陶瓦码上报后）。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Current is not { } room) return;
        try
        {
            var detail = await _rooms.GetAsync(room.Room.Code, ct);
            Current = detail;
            RaiseChanged();
        }
        catch (RoomApiException ex) when (ex.IsGone)
        {
            // 房间在别处被解散了
            await LeaveAsync(silent: true, ct);
            Notify?.Invoke(Loc.T("房间已被解散"));
            RaiseChanged();
        }
    }

    /// <summary>房主自动开房成功后上报陶瓦码（大厅据此判断「可进」）。</summary>
    public async Task ReportTerracottaAsync(string terracottaCode, CancellationToken ct = default)
    {
        if (Current is not { } room) return;
        await _rooms.ReportTerracottaAsync(room.Room.Code, terracottaCode, ct);
        TerracottaCode = terracottaCode;
        await RefreshAsync(ct);
    }

    // ── 陶瓦联机会话（房主开房 / 玩家加入 / 拿到自动连接地址） ──

    /// <summary>
    /// 房主侧：开始盯着本机局域网世界。**不能在游戏开局域网之前就开始扫描** ——
    /// 陶瓦核心只扫一次，扫空就停（实测表现：开了 LAN 之后等很久也没反应）。
    /// 所以这里只挂一个 UDP 4445 探测：收到 MC 的 LAN 广播（= 你点了「对局域网开放」）
    /// 才让核心去扫描；另加一个低频重试兜底（某些网络环境收不到广播）。
    /// </summary>
    public void StartHostWatch()
    {
        if (_multiplayer is null) return;
        if (_lanProbe is null)
        {
            _lanProbe = new LanProbeService();
            _lanProbe.WorldFound += (motd, port) =>
            {
                LauncherLog.Write("rooms", $"发现局域网世界「{motd}」端口 {port}，通知陶瓦扫描");
                _ = TryHostScanAsync();
            };
            _lanProbe.Start();
        }
        // 兜底：每 15 秒试一次（节流在 TryHostScanAsync 里），直到开好或离开房间
        _hostRetryTimer ??= StartHostRetryTimer();
    }

    private DispatcherQueueTimer? StartHostRetryTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (timer is null) return null;
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (Current is not { IsOwner: true } || HostReady) return;
            _ = TryHostScanAsync();
        };
        timer.Start();
        return timer;
    }

    private async Task TryHostScanAsync()
    {
        if (_multiplayer is null || Current is not { IsOwner: true } || HostReady) return;
        // 节流：核心扫描需要几秒，连续广播（每秒一次）不能打成风暴
        var now = Environment.TickCount64;
        if (now - _lastHostScanMs < 8000) return;
        _lastHostScanMs = now;
        try
        {
            // 核心卡在异常态时直接发 host 命令不再推进：先回空闲复位再重扫
            if (_lastLoggedPhase == MultiplayerPhase.Failed)
            {
                try { await _multiplayer.IdleAsync(CancellationToken.None); }
                catch { /* 复位失败不影响重扫尝试 */ }
            }
            await _multiplayer.HostAsync(CancellationToken.None);
            _lastHostScanError = null;
        }
        catch (Exception ex)
        {
            // 重试定时器 + LAN 广播都会反复调到这里：同样的失败只在第一次弹提示，
            // 之后只进日志 —— 否则提示条每 8~15 秒重弹一次，看起来在不停闪（实测踩过）
            var message = Loc.T("联机核心开房失败：{0}", ex.Message);
            if (message != _lastHostScanError)
            {
                _lastHostScanError = message;
                Notify?.Invoke(message);
            }
            else
            {
                LauncherLog.Write("rooms", $"开房重试失败（重复，仅记录）：{ex.Message}");
            }
        }
    }

    /// <summary>上一次开房失败的提示文本（去重：同样的失败不重复弹）。</summary>
    private string? _lastHostScanError;

    /// <summary>房主侧：让陶瓦核心开始扫描本机局域网世界（探测到 LAN 时自动调用）。</summary>
    public async Task HostLanAsync(CancellationToken ct = default)
    {
        if (_multiplayer is null) throw new InvalidOperationException("联机核心未就绪");
        StartHostWatch();
        await TryHostScanAsync();
        Notify?.Invoke(Loc.T("已就绪：在游戏里点「对局域网开放」，房间会自动开好"));
    }

    /// <summary>
    /// 玩家侧：用房间里的陶瓦邀请码加入房主的虚拟局域网。
    /// 房间还没有邀请码（房主没开房）时不做事，等 terracotta_ready 事件再调。
    /// </summary>
    public async Task JoinGuestAsync(CancellationToken ct = default)
    {
        if (_multiplayer is null) return;
        if (TerracottaCode is not { Length: > 0 } code) return;
        try
        {
            await _multiplayer.JoinAsync(code, ct);
            Notify?.Invoke(Loc.T("正在连接房主的世界…"));
        }
        catch (Exception ex)
        {
            Notify?.Invoke(Loc.T("连接联机房间失败：{0}", ex.Message));
        }
    }

    /// <summary>陶瓦状态变化：房主开好房 → 上报邀请码；玩家连上 → 记录自动连接地址。</summary>
    private void OnMultiplayerChanged(MultiplayerSnapshot snapshot)
    {
        if (Current is null) return;
        var state = snapshot.State;
        // 相位变化留痕：开房链路（探测 → 扫描 → HostReady → 上报）哪里断了，双机测试日志一眼可见
        if (state.Phase != _lastLoggedPhase)
        {
            _lastLoggedPhase = state.Phase;
            LauncherLog.Write("rooms",
                $"陶瓦状态：{state.Phase}"
                + (state.Error is { Length: > 0 } err ? $"（错误：{err}）" : ""));
        }
        switch (state.Phase)
        {
            case MultiplayerPhase.HostReady when state.Room is { Length: > 0 } roomCode:
                if (!HostReady)
                {
                    HostReady = true;
                    _ = ReportTerracottaSafeAsync(roomCode);
                }
                break;
            case MultiplayerPhase.Failed:
                // 核心异常（如「与服务器的连接被重置」）：弹给用户看（去重），重试定时器会带动恢复
                var failedMessage = Loc.T("联机核心出错：{0}",
                    string.IsNullOrEmpty(state.Error) ? Loc.T("未知错误") : state.Error);
                if (failedMessage != _lastHostScanError)
                {
                    _lastHostScanError = failedMessage;
                    Notify?.Invoke(failedMessage);
                }
                break;
            case MultiplayerPhase.GuestReady when state.Address is { Length: > 0 } address:
                if (!string.Equals(GuestAddress, address, StringComparison.Ordinal))
                {
                    GuestAddress = address;
                    Notify?.Invoke(Loc.T("已连上房主的世界，可以开始游戏了"));
                    RaiseChanged();
                }
                break;
        }
    }

    private async Task ReportTerracottaSafeAsync(string terracottaCode)
    {
        try
        {
            await ReportTerracottaAsync(terracottaCode);
            Notify?.Invoke(Loc.T("联机房间已开好，玩家可以进来了"));
        }
        catch (Exception ex)
        {
            Notify?.Invoke(Loc.T("上报联机码失败：{0}", ex.Message));
        }
        finally
        {
            RaiseChanged();
        }
    }

    // ── 事件流 ──

    private void StartEvents(string code)
    {
        StopEvents();
        var cts = new CancellationTokenSource();
        _eventsCts = cts;
        _ = Task.Run(() => EventLoopAsync(code, cts.Token));
    }

    private void StopEvents()
    {
        _eventsCts?.Cancel();
        _eventsCts?.Dispose();
        _eventsCts = null;
    }

    // ── HTTP 心跳兜底：SSE 半开连接（静默断掉）时客户端毫无感知，
    // 服务端 60 秒看不到房主在线连接就自动关房 —— 定期打 heartbeat 刷新活跃 ──

    private DispatcherQueueTimer? _heartbeatTimer;
    private string? _heartbeatCode;

    private void StartHeartbeat(string code)
    {
        StopHeartbeat();
        _heartbeatCode = code;
        // 只创建在 UI 线程：GetForCurrentThread 在后台线程返回 null 会让心跳静默失效，
        // 房间就会在 60 秒后被服务端关掉且毫无线索 —— 记日志，别让它无声消失
        _heartbeatTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_heartbeatTimer is null)
        {
            LauncherLog.Write("rooms", "心跳定时器创建失败（不在 UI 线程），房间可能被服务端超时关闭");
            return;
        }
        _heartbeatTimer.Interval = TimeSpan.FromSeconds(30);
        _heartbeatTimer.IsRepeating = true;
        _heartbeatTimer.Tick += (_, _) => _ = HeartbeatSafeAsync();
        _heartbeatTimer.Start();
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Stop();
        _heartbeatTimer = null;
        _heartbeatCode = null;
    }

    private async Task HeartbeatSafeAsync()
    {
        var code = _heartbeatCode;
        if (code is null || Current is null) { StopHeartbeat(); return; }
        try
        {
            await _rooms.HeartbeatAsync(code, CancellationToken.None);
        }
        catch (RoomApiException ex) when (ex.IsGone)
        {
            // 房间确实没了（服务端已关）：清场并告知
            StopHeartbeat();
            await LeaveAsync(silent: true);
            Notify?.Invoke(Loc.T("房间已被解散"));
            RaiseChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 单次心跳失败不折腾（SSE 正常时本来就不需要它）
            LauncherLog.Debug("rooms", $"心跳失败（忽略）：{ex.Message}");
        }
    }

    private async Task EventLoopAsync(string code, CancellationToken ct)
    {
        // 退避序列按服务端文档（§7.3）：2s / 5s / 10s，之后封顶 30s
        int[] delays = [2, 5, 10, 30];
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _rooms.StreamEventsAsync(code, HandleEventAsync, ct);
                // 服务端正常关闭事件流。room_closed 处理时已经退出并通知过 ——
                // 那种情况下服务端紧接着断开 SSE 是预期行为，不再补「连接已断开」
                if (!ct.IsCancellationRequested && !_leftByEvent)
                {
                    // 服务端主动断流 ≠ 房间没了：先探活，活着就重连（SSE 半死不重连的话，
                    // 房主 60 秒无在线连接会被服务端自动关房 —— 实测踩过）
                    if (await ResyncAsync(code, ct))
                    {
                        LauncherLog.Write("rooms", "SSE 连接断开，房间仍在，重连事件流");
                        continue;
                    }
                    return;
                }
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (RoomApiException ex) when (ex.IsGone)
            {
                await LeaveAsync(silent: true, ct);
                Notify?.Invoke(Loc.T("房间已被解散"));
                RaiseChanged();
                return;
            }
            catch
            {
                // 断线：退避等待，然后先补齐状态与消息再重连
                // （SSE 不补发断线期间的事件，光重连会丢成员变化和聊天）
                var wait = TimeSpan.FromSeconds(delays[Math.Min(attempt, delays.Length - 1)]);
                attempt++;
                LauncherLog.Debug("rooms", $"SSE 断线，{wait.TotalSeconds:F0}s 后第 {attempt} 次重连");
                try { await Task.Delay(wait, ct); } catch { return; }
                if (!await ResyncAsync(code, ct)) return;   // 房间没了就别再连
            }
        }
    }

    /// <summary>重连前补齐：拉一次详情（members/state）+ 一次消息。房间已关闭返回 false。</summary>
    private async Task<bool> ResyncAsync(string code, CancellationToken ct)
    {
        try
        {
            var detail = await _rooms.GetAsync(code, ct);
            var history = await _rooms.GetMessagesAsync(code, null, 100, ct);
            Current = detail;
            lock (_gate)
            {
                _messages.Clear();
                _messages.AddRange(history);
            }
            RaiseChanged();
            return true;
        }
        catch (RoomApiException ex) when (ex.IsGone)
        {
            await LeaveAsync(silent: true, ct);
            Notify?.Invoke(Loc.T("房间已被解散"));
            RaiseChanged();
            return false;
        }
        catch
        {
            // 补数据失败（还在断网）：不打断重连，下一次循环继续
            return true;
        }
    }

    private async Task HandleEventAsync(RoomEvent ev)
    {
        switch (ev.Type)
        {
            case RoomEvent.Message:
                if (ev.Parse<RoomMessage>() is { } message)
                {
                    lock (_gate)
                    {
                        if (_messages.All(m => m.Id != message.Id)) _messages.Add(message);
                        if (_messages.Count > 200) _messages.RemoveRange(0, _messages.Count - 200);
                    }
                    RaiseChanged();
                }
                break;

            case RoomEvent.MemberJoined:
                Notify?.Invoke(Loc.T(
                    "{0} 加入了房间", DisplayNameOf(ev)));
                await RefreshAsync();
                break;

            case RoomEvent.MemberLeft:
                await RefreshAsync();
                break;

            case RoomEvent.Kicked:
                if (IsMe(ev))
                {
                    _leftByEvent = true;
                    await LeaveAsync(silent: true);
                    Notify?.Invoke(Loc.T("你已被房主移出房间"));
                    RaiseChanged();
                }
                else
                {
                    await RefreshAsync();
                }
                break;

            case RoomEvent.RoomClosed:
                _leftByEvent = true;
                await LeaveAsync(silent: true);
                Notify?.Invoke(Loc.T("房主已解散房间"));
                RaiseChanged();
                break;

            case RoomEvent.TerracottaReady:
                if (ev.Data.TryGetProperty("terracotta_code", out var tc) &&
                    tc.GetString() is { Length: > 0 } code)
                {
                    TerracottaCode = code;
                    TerracottaReady?.Invoke(code);
                    RaiseChanged();
                    // 玩家侧：房主刚开好房，自动连过去
                    if (Current is { IsOwner: false }) _ = JoinGuestAsync();
                }
                break;
        }
    }

    private static string DisplayNameOf(RoomEvent ev)
    {
        try
        {
            return ev.Data.TryGetProperty("display_name", out var n) ? n.GetString() ?? "玩家" : "玩家";
        }
        catch (JsonException) { return "玩家"; }
    }

    private bool IsMe(RoomEvent ev)
    {
        try
        {
            if (!ev.Data.TryGetProperty("user_id", out var id)) return false;
            var uid = id.GetString();
            if (string.IsNullOrEmpty(uid)) return false;
            if (uid == "me") return true;   // 演示后端的固定 id
            return string.Equals(uid, _myUserId(), StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private void RaiseChanged()
    {
        // 订阅方异常不能打断事件流（否则一次绘制失败就断线重连）
        try { Changed?.Invoke(); } catch { }
    }
}
