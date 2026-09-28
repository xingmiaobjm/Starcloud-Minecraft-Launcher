// 游戏运行态唯一来源：1s 心跳采样 + 跨启动器重启识别 + 运行历史收口
// 设计：docs/designs/2026-09-26-game-running-status-panel-design.md §2 / §4
//
// 本类不知道 UI 的存在：只抛不可变快照与事件，由页面自己回 UI 线程。
// 进程存活判定可注入（isAliveProbe），测试因此不依赖机器上真的跑着 java。
//
// 多实例：同一 .minecraft 下允许同时跑多局（同版本也可多开），每局一个 InstanceState，
// 实例键就是 pid。对外展示 / 采样 / 结束 / 命令行 / 上下文一律以「选中实例」为准。

using System.Diagnostics;

namespace Starcloud.Core.Services;

/// <summary>某局游戏的即时快照（不可变；采样更新时整体替换）</summary>
public sealed record GameRunInfo(
    int Pid,
    string VersionId,
    DateTime StartedAt,
    TimeSpan Uptime,
    bool RecoveredFromLock,
    GameRunSample Latest);

/// <summary>
/// 运行态监控器（Core 单例，因为 Nova 启动路径在 Core 里够不到 AppServices）。
/// 冷启动时靠实例注册文件 + 判活恢复「上一轮启动器拉起的游戏」，进程退出时落一条运行历史。
/// </summary>
public sealed class GameRuntimeMonitor : IDisposable
{
    /// <summary>采样环形缓冲上限（≈10 分钟 @1s）</summary>
    public const int SampleCapacity = 600;

    /// <summary>连续采样失败多少次判「采样已暂停」</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>恢复路径读日志尾时最多回读的字节数</summary>
    private const int TailReadBytes = 64 * 1024;

    public static GameRuntimeMonitor Instance { get; } = new();

    /// <summary>单个游戏实例的全部跟踪状态</summary>
    private sealed class InstanceState
    {
        public int Pid;
        public string VersionId = "";
        public DateTime StartedAt;
        public GameSession? Session;
        public ProcessSampler? Sampler;
        public GameLaunchContext? Context;
        public string? CommandLine;
        public string? LogFile;
        public bool Recovered;
        public long PeakWorkingSet;
        public int ConsecutiveFailures;
        public bool SamplingPaused;
        public bool KilledByUser;
        public Action<int>? ExitedHandler;
        public readonly LinkedList<GameRunSample> Samples = new();
        public GameRunSample Latest;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, InstanceState> _states = new();
    private int _selectedPid;

    private Func<string>? _dotMcProvider;
    private GameRunHistoryStore _history = new(LauncherPaths.DataDir);
    private Func<int, bool> _isAliveProbe = DefaultAliveProbe;

    private System.Threading.Timer? _timer;
    private bool _started;
    private int _intervalMs = 1000;

    /// <summary>
    /// 注入依赖（AppServices 构造时调一次；测试传临时目录与假探针）。
    /// <paramref name="history"/> 为空时用 <see cref="LauncherPaths.DataDir"/> 下的默认历史文件。
    /// </summary>
    public void Configure(Func<string> dotMcProvider, GameRunHistoryStore? history = null,
        Func<int, bool>? isAliveProbe = null)
    {
        lock (_gate)
        {
            _dotMcProvider = dotMcProvider;
            if (history is not null) _history = history;
            if (isAliveProbe is not null) _isAliveProbe = isAliveProbe;
            _history.TryLoad();
        }
    }

    /// <summary>当前展示的这局快照（即选中实例；没有游戏在跑时为 null）。后台线程读，已加锁</summary>
    public GameRunInfo? Current { get { lock (_gate) return BuildInfo(ResolveLocked(0)); } }

    /// <summary>正在跑的全部实例（按启动时刻升序；下拉切换用）</summary>
    public IReadOnlyList<GameRunInfo> Instances
    {
        get
        {
            lock (_gate)
            {
                // 集合里都是非空实例，! 只为消掉 BuildInfo 的可空返回签名
                return [.. _states.Values.OrderBy(s => s.StartedAt).Select(s => BuildInfo(s)!)];
            }
        }
    }

    /// <summary>当前选中的实例 pid；0 表示没有可展示的实例</summary>
    public int SelectedPid { get { lock (_gate) return ResolveLocked(0)?.Pid ?? 0; } }

    /// <summary>选中实例是否由用户在界面里主动结束（用于区分「用户结束」与「游戏崩溃」）</summary>
    public bool KilledByUser { get { lock (_gate) return ResolveLocked(0)?.KilledByUser ?? false; } }

    /// <summary>选中实例的完整启动命令行（含 --accessToken，仅内存持有；恢复路径拿不到 → null）</summary>
    public string? CommandLine { get { lock (_gate) return ResolveLocked(0)?.CommandLine; } }

    /// <summary>选中实例生效的启动上下文；缺失（跨启动器重启且无落盘）时为 null</summary>
    public GameLaunchContext? LaunchContext { get { lock (_gate) return ResolveLocked(0)?.Context; } }

    /// <summary>运行历史（新 → 旧）</summary>
    public IReadOnlyList<GameRunRecord> History
    {
        get { lock (_gate) return [.. _history.Records]; }
    }

    /// <summary>选中实例最近 <paramref name="last"/> 个采样点（sparkline 用）</summary>
    public IReadOnlyList<GameRunSample> Samples(int last)
    {
        lock (_gate)
        {
            var st = ResolveLocked(0);
            if (st is null || last <= 0) return [];
            var samples = st.Samples;
            return samples.Count <= last ? [.. samples] : samples.Skip(samples.Count - last).ToList();
        }
    }

    /// <summary>选中实例的连续采样失败暂停（页面显示「采样已暂停」并给「重试」）</summary>
    public bool SamplingPaused { get { lock (_gate) return ResolveLocked(0)?.SamplingPaused ?? false; } }

    /// <summary>采样间隔（毫秒）；变更后若在跑则重建定时器</summary>
    public int IntervalMs
    {
        get { lock (_gate) return _intervalMs; }
        set
        {
            var v = Math.Clamp(value, 250, 10_000);
            lock (_gate)
            {
                if (v == _intervalMs) return;
                _intervalMs = v;
                if (_started) RestartTimerLocked();
            }
        }
    }

    /// <summary>是否采集 GPU 占用（默认开；关掉可省 PDH 开销）</summary>
    public bool CollectGpu { get; set; } = true;

    /// <summary>当前展示的那局快照变化（后台线程触发；null = 已无游戏在跑）</summary>
    public event Action<GameRunInfo?>? Changed;

    /// <summary>一局结束并已落历史（后台线程触发）</summary>
    public event Action<GameRunRecord>? RunFinished;

    /// <summary>开始心跳（AppServices 构造时调一次；幂等）</summary>
    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            foreach (var st in _states.Values)
            {
                st.SamplingPaused = false;
                st.ConsecutiveFailures = 0;
            }
            RestartTimerLocked();
        }
    }

    /// <summary>停止心跳（退出应用时调用）</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>接管本进程启动的这一局（启动成功后由启动路径调用）；新的一局自然成为展示对象</summary>
    public void Attach(GameSession session, GameLaunchContext? context, string? commandLine) =>
        AttachCore(session.Pid, session.VersionId, session.StartedAt, session, context, commandLine);

    /// <summary>切换要展示 / 操作的实例（下拉框用）；pid 不存在返回 false</summary>
    public bool Select(int pid)
    {
        lock (_gate)
        {
            if (!_states.ContainsKey(pid)) return false;
            if (_selectedPid == pid) return true;
            _selectedPid = pid;
        }
        Changed?.Invoke(Current);
        return true;
    }

    /// <summary>结束指定实例的游戏进程（pid ≤ 0 时结束选中实例，连子进程一起）</summary>
    public void Kill(int pid = 0)
    {
        lock (_gate)
        {
            var st = ResolveLocked(pid);
            if (st is null) return;
            st.KilledByUser = true;
            if (st.Session is not null)
            {
                st.Session.Kill();
                return;
            }
            var target = st.Pid;
            try
            {
                using var p = Process.GetProcessById(target);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { /* 已退出 / 权限不足：交给下一次心跳收口 */ }
        }
    }

    /// <summary>选中实例的日志尾若干行（接管路径读内存环形缓冲；恢复路径读本局日志文件尾部）</summary>
    public IReadOnlyList<string> TailLog(int count)
    {
        if (count <= 0) return [];
        InstanceState? st;
        lock (_gate) st = ResolveLocked(0);
        if (st is null) return [];
        if (st.Session is not null) return st.Session.Tail(count);
        return st.LogFile is null ? [] : ReadLogTail(st.LogFile, count);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            foreach (var st in _states.Values)
            {
                st.Sampler?.Dispose();
                st.Sampler = null;
            }
            _states.Clear();
            _selectedPid = 0;
        }
    }

    // ── 测试入口（InternalsVisibleTo: Starcloud.Core.Tests） ──

    /// <summary>测试用：不经 GameSession 直接建立接管态</summary>
    internal void AttachForTest(int pid, string versionId, DateTime startedAt,
        GameSession? session = null, GameLaunchContext? context = null, string? commandLine = null) =>
        AttachCore(pid, versionId, startedAt, session, context, commandLine);

    /// <summary>测试用：模拟会话退出收口（pid ≤ 0 用当前选中实例）</summary>
    internal void RaiseSessionExitedForTest(int exitCode, int pid = 0) => OnSessionExited(pid, exitCode);

    /// <summary>测试用：直接给选中实例推入一个采样点</summary>
    internal void PushSampleForTest(in GameRunSample sample)
    {
        lock (_gate)
        {
            var st = ResolveLocked(0);
            if (st is null) return;
            PushSampleLocked(st, sample);
            st.Latest = sample;
        }
    }

    // ── 内部实现 ──

    private void AttachCore(int pid, string versionId, DateTime startedAt, GameSession? session,
        GameLaunchContext? context, string? commandLine)
    {
        if (pid <= 0) return;   // pid 即实例键，无 pid 无法跟踪
        lock (_gate)
        {
            if (_states.TryGetValue(pid, out var old))
            {
                if (old.Session is not null && old.ExitedHandler is not null) old.Session.Exited -= old.ExitedHandler;
                old.Sampler?.Dispose();
                _states.Remove(pid);
            }

            var st = new InstanceState
            {
                Pid = pid,
                VersionId = versionId,
                StartedAt = startedAt,
                Session = session,
                Sampler = new ProcessSampler(pid),
                Context = context,
                CommandLine = commandLine,
                LogFile = session?.LogFile,
            };
            if (session is not null)
            {
                // 事件不带会话标识：必须用按 pid 绑定的闭包匹配，不能靠 sender
                Action<int> handler = code => OnSessionExited(pid, code);
                st.ExitedHandler = handler;
                session.Exited += handler;
            }
            _states[pid] = st;
            _selectedPid = pid;

            // 注册文件由本监控器独占维护（跨启动器重启能看见本机在跑的游戏）
            var dotMc = _dotMcProvider?.Invoke();
            if (!string.IsNullOrEmpty(dotMc))
            {
                GameSessionGuard.Publish(dotMc, pid, versionId, startedAt, st.LogFile);
                if (context is not null) GameLaunchContextStore.Write(dotMc, pid, context);
            }

            if (_started) RestartTimerLocked();
        }
        Changed?.Invoke(Current);
    }

    private void RestartTimerLocked()
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Tick(), null, _intervalMs, _intervalMs);
    }

    private void Tick()
    {
        var finished = new List<GameRunRecord>();
        var changed = false;

        lock (_gate)
        {
            if (!_started) return;

            DiscoverLocked();

            foreach (var st in _states.Values.ToList())
            {
                if (st.SamplingPaused) continue;

                if (st.Sampler is null || !st.Sampler.TrySample(CollectGpu, out var sample))
                {
                    st.ConsecutiveFailures++;
                    if (st.ConsecutiveFailures >= MaxConsecutiveFailures && !st.SamplingPaused)
                    {
                        st.SamplingPaused = true;
                        changed = true;
                    }
                    // 恢复路径没有 GameSession.Exited 事件，进程死掉只能靠心跳收口。
                    // 收口发生在第 1 次失败（早于 3 次失败阈值），所以必须在这里就落历史并移除状态，
                    // 否则页面会永远停在「运行中 + 采样已暂停」。
                    if (st.Recovered && FinishLocked(st, null) is { } rec)
                    {
                        finished.Add(rec);
                        changed = true;
                    }
                }
                else
                {
                    PushSampleLocked(st, sample);
                    st.Latest = sample;
                    st.ConsecutiveFailures = 0;
                    changed = true;
                }
            }
        }

        foreach (var rec in finished) RunFinished?.Invoke(rec);
        if (changed) Changed?.Invoke(Current);
    }

    /// <summary>扫描实例注册文件，纳入本进程尚未跟踪的存活实例（其它启动器窗口拉起的也算）</summary>
    private void DiscoverLocked()
    {
        var dotMc = _dotMcProvider?.Invoke();
        if (string.IsNullOrEmpty(dotMc)) return;

        var alive = GameSessionGuard.All(dotMc, _isAliveProbe);
        GameLaunchContextStore.PruneDead(dotMc, [.. alive.Select(l => l.Pid)]);

        foreach (var lk in alive)
        {
            if (_states.ContainsKey(lk.Pid)) continue;
            var st = new InstanceState
            {
                Pid = lk.Pid,
                VersionId = string.IsNullOrEmpty(lk.VersionId) ? "未知版本" : lk.VersionId,
                StartedAt = lk.StartedAt,
                Recovered = true,
                LogFile = lk.LogFile,
                Sampler = new ProcessSampler(lk.Pid),
            };
            var ctx = GameLaunchContextStore.Read(dotMc, lk.Pid, st.VersionId);
            if (ctx is null) GameLaunchContextStore.Delete(dotMc, lk.Pid);   // 上一局残留 / 版本不符 → 丢弃
            st.Context = ctx;
            _states[lk.Pid] = st;
        }
    }

    /// <summary>解析目标实例：pid &gt; 0 取该实例；否则取选中实例（失效时退到最近启动的一个）</summary>
    private InstanceState? ResolveLocked(int pid)
    {
        if (pid > 0) return _states.GetValueOrDefault(pid);
        if (_states.TryGetValue(_selectedPid, out var selected)) return selected;
        return _states.Count == 0 ? null : _states.Values.MaxBy(s => s.StartedAt);
    }

    /// <summary>收口并落一条运行历史，随后移除该实例状态；返回落盘记录</summary>
    private GameRunRecord? FinishLocked(InstanceState st, int? exitCode)
    {
        var endedAt = DateTime.Now;
        var duration = st.Session is not null ? st.Session.Uptime : endedAt - st.StartedAt;
        var killedByUser = st.KilledByUser || st.Session?.KilledByUser == true;
        var rec = new GameRunRecord(
            st.VersionId,
            st.StartedAt,
            endedAt,
            Math.Max(0, duration.TotalSeconds),
            exitCode,
            st.PeakWorkingSet,
            Crashed: !killedByUser && exitCode is not (null or 0));

        _history.Append(rec);
        RemoveLocked(st);
        return rec;
    }

    private void OnSessionExited(int pid, int exitCode)
    {
        GameRunRecord? rec;
        lock (_gate)
        {
            var st = ResolveLocked(pid);
            if (st is null) return;
            if (st.Session is not null && st.ExitedHandler is not null) st.Session.Exited -= st.ExitedHandler;
            rec = FinishLocked(st, exitCode);
        }
        if (rec is not null) RunFinished?.Invoke(rec);
        Changed?.Invoke(Current);
    }

    /// <summary>移除实例状态（注销注册文件 + 删该实例的上下文、退订事件、释放采样器；不触发事件，调用方负责通知）</summary>
    private void RemoveLocked(InstanceState st)
    {
        var dotMc = _dotMcProvider?.Invoke();
        if (!string.IsNullOrEmpty(dotMc))
        {
            GameSessionGuard.Unpublish(dotMc, st.Pid);
            GameLaunchContextStore.Delete(dotMc, st.Pid);
        }

        if (st.Session is not null && st.ExitedHandler is not null) st.Session.Exited -= st.ExitedHandler;
        st.Sampler?.Dispose();
        st.Sampler = null;
        st.Session = null;
        _states.Remove(st.Pid);
        if (_selectedPid == st.Pid) _selectedPid = 0;
    }

    private static void PushSampleLocked(InstanceState st, in GameRunSample sample)
    {
        st.Samples.AddLast(sample);
        while (st.Samples.Count > SampleCapacity) st.Samples.RemoveFirst();
        if (sample.PeakWorkingSetBytes > st.PeakWorkingSet) st.PeakWorkingSet = sample.PeakWorkingSetBytes;
        if (sample.WorkingSetBytes > st.PeakWorkingSet) st.PeakWorkingSet = sample.WorkingSetBytes;
    }

    private static GameRunInfo? BuildInfo(InstanceState? st) =>
        st is null ? null : new GameRunInfo(
            st.Pid, st.VersionId, st.StartedAt,
            st.Session is not null ? st.Session.Uptime : DateTime.Now - st.StartedAt,
            st.Recovered, st.Latest);

    /// <summary>默认判活：pid 存活且进程名形如 java*（与 GameSessionGuard 同口径，防止 pid 复用误判）</summary>
    private static bool DefaultAliveProbe(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>读取某局日志文件的尾部若干行（只回读文件末尾 64 KB，不整文件加载）</summary>
    private static IReadOnlyList<string> ReadLogTail(string file, int count)
    {
        try
        {
            if (!File.Exists(file)) return [];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(TailReadBytes, fs.Length);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var lines = text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();
            // 回读起点可能落在半行上，首行丢弃（除非整文件已读完）
            if (take < fs.Length && lines.Count > 0) lines.RemoveAt(0);
            return lines.Count <= count ? lines : lines.Skip(lines.Count - count).ToList();
        }
        catch { return []; }
    }
}
