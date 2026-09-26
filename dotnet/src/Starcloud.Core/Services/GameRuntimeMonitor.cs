// 游戏运行态唯一来源：1s 心跳采样 + 跨启动器重启识别 + 运行历史收口
// 设计：docs/designs/2026-09-26-game-running-status-panel-design.md §2 / §4
//
// 本类不知道 UI 的存在：只抛不可变快照与事件，由页面自己回 UI 线程。
// 进程存活判定可注入（isAliveProbe），测试因此不依赖机器上真的跑着 java。

using System.Diagnostics;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>当前这一局的即时快照（不可变；采样更新时整体替换）</summary>
public sealed record GameRunInfo(
    int Pid,
    string VersionId,
    DateTime StartedAt,
    TimeSpan Uptime,
    bool RecoveredFromLock,
    GameRunSample Latest);

/// <summary>
/// 运行态监控器（Core 单例，因为 Nova 启动路径在 Core 里够不到 AppServices）。
/// 冷启动时靠锁文件 + 判活恢复「上一轮启动器拉起的游戏」，进程退出时落一条运行历史。
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

    private readonly object _gate = new();
    private readonly LinkedList<GameRunSample> _samples = new();

    private Func<string>? _dotMcProvider;
    private GameRunHistoryStore _history = new(LauncherPaths.DataDir);
    private Func<int, bool> _isAliveProbe = DefaultAliveProbe;

    private System.Threading.Timer? _timer;
    private bool _started;
    private int _intervalMs = 1000;

    private GameSession? _session;
    private ProcessSampler? _sampler;
    private GameLaunchContext? _context;
    private string? _commandLine;
    private GameRunInfo? _info;
    private int _pid;
    private string _versionId = "";
    private DateTime _startedAt;
    private bool _recovered;
    private long _peakWorkingSet;
    private int _consecutiveFailures;
    private bool _samplingPaused;
    private bool _killedByUser;

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

    /// <summary>当前运行快照；没有游戏在跑时为 null（后台线程读，已加锁）</summary>
    public GameRunInfo? Current { get { lock (_gate) return _info; } }

    /// <summary>本局完整启动命令行（含 --accessToken，仅内存持有；恢复路径拿不到 → null）</summary>
    public string? CommandLine { get { lock (_gate) return _commandLine; } }

    /// <summary>本局生效的启动上下文；缺失（跨启动器重启且无落盘）时为 null</summary>
    public GameLaunchContext? LaunchContext { get { lock (_gate) return _context; } }

    /// <summary>运行历史（新 → 旧）</summary>
    public IReadOnlyList<GameRunRecord> History
    {
        get { lock (_gate) return [.. _history.Records]; }
    }

    /// <summary>最近 <paramref name="last"/> 个采样点（sparkline 用）</summary>
    public IReadOnlyList<GameRunSample> Samples(int last)
    {
        lock (_gate)
        {
            if (last <= 0) return [];
            return _samples.Count <= last ? [.. _samples] : _samples.Skip(_samples.Count - last).ToList();
        }
    }

    /// <summary>连续采样失败导致的暂停（页面显示「采样已暂停」并给「重试」）</summary>
    public bool SamplingPaused { get { lock (_gate) return _samplingPaused; } }

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

    /// <summary>运行快照变化（后台线程触发；null = 已无游戏在跑）</summary>
    public event Action<GameRunInfo?>? Changed;

    /// <summary>一局结束并已落历史（后台线程触发）</summary>
    public event Action<GameRunRecord>? RunFinished;

    /// <summary>开始心跳（AppServices 构造时调一次；幂等）</summary>
    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            _samplingPaused = false;
            _consecutiveFailures = 0;
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

    /// <summary>接管本进程启动的这一局（启动成功后由启动路径调用）</summary>
    public void Attach(GameSession session, GameLaunchContext? context, string? commandLine) =>
        AttachCore(session.Pid, session.VersionId, session.StartedAt, session, context, commandLine);

    /// <summary>结束当前游戏进程（连子进程）；被系统拒绝时由调用方提示</summary>
    public void Kill()
    {
        lock (_gate)
        {
            _killedByUser = true;
            if (_session is not null)
            {
                _session.Kill();
                return;
            }
            var pid = _pid;
            if (_info is null || pid <= 0) return;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { /* 已退出 / 权限不足：交给下一次心跳收口 */ }
        }
    }

    /// <summary>放弃跟踪（重启时调用；不删除历史）</summary>
    public void Detach()
    {
        lock (_gate) CleanupLocked(deleteContext: true);
        Changed?.Invoke(null);
    }

    /// <summary>日志尾若干行（接管路径读内存环形缓冲；恢复路径读最新一局日志文件尾部）</summary>
    public IReadOnlyList<string> TailLog(int count)
    {
        if (count <= 0) return [];
        GameSession? session;
        bool recovered;
        lock (_gate)
        {
            if (_info is null) return [];
            session = _session;
            recovered = _recovered;
        }
        if (session is not null) return session.Tail(count);
        if (!recovered) return [];
        return ReadLogTail(count);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _sampler?.Dispose();
            _sampler = null;
        }
    }

    // ── 测试入口（InternalsVisibleTo: Starcloud.Core.Tests） ──

    /// <summary>测试用：不经 GameSession 直接建立接管态</summary>
    internal void AttachForTest(int pid, string versionId, DateTime startedAt,
        GameSession? session = null, GameLaunchContext? context = null, string? commandLine = null) =>
        AttachCore(pid, versionId, startedAt, session, context, commandLine);

    /// <summary>测试用：模拟会话退出收口</summary>
    internal void RaiseSessionExitedForTest(int exitCode) => OnSessionExited(exitCode);

    /// <summary>测试用：直接推入一个采样点</summary>
    internal void PushSampleForTest(in GameRunSample sample)
    {
        lock (_gate)
        {
            PushSampleLocked(sample);
            if (_info is not null) _info = BuildInfoLocked(sample);
        }
    }

    // ── 内部实现 ──

    private void AttachCore(int pid, string versionId, DateTime startedAt, GameSession? session,
        GameLaunchContext? context, string? commandLine)
    {
        lock (_gate)
        {
            _session = session;
            _sampler?.Dispose();
            _sampler = new ProcessSampler(pid);
            _pid = pid;
            _versionId = versionId;
            _startedAt = startedAt;
            _context = context;
            _commandLine = commandLine;
            _recovered = false;
            _killedByUser = false;
            _consecutiveFailures = 0;
            _samplingPaused = false;
            _peakWorkingSet = 0;
            _samples.Clear();

            if (session is not null) session.Exited += OnSessionExited;

            var dotMc = _dotMcProvider?.Invoke();
            if (context is not null && !string.IsNullOrEmpty(dotMc))
                GameLaunchContextStore.Write(dotMc, context);

            _info = BuildInfoLocked(default);
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
        GameRunInfo? changed = null;
        bool changedRaised = false;
        GameRunRecord? finished = null;

        lock (_gate)
        {
            if (!_started) return;
            if (_samplingPaused) return;

            if (_info is null)
            {
                changedRaised = TryRecoverLocked(out changed);
            }
            else if (_sampler is null || !_sampler.TrySample(CollectGpu, out var sample))
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxConsecutiveFailures && !_samplingPaused)
                {
                    _samplingPaused = true;
                    changedRaised = true;
                    changed = _info;
                }
                // 恢复路径没有 GameSession.Exited 事件，进程死掉只能靠心跳收口。
                // 收口发生在第 1 次失败（早于 3 次失败阈值），所以这里必须自己补抛 null，
                // 否则清空后的 _info 没有对应事件，页面会永远停在「运行中 + 采样已暂停」。
                if (_recovered && (finished = FinishLocked(null)) is not null)
                {
                    changedRaised = true;
                    changed = null;
                }
            }
            else
            {
                PushSampleLocked(sample);
                _consecutiveFailures = 0;
                _info = BuildInfoLocked(sample);
                changedRaised = true;
                changed = _info;
            }
        }

        if (finished is not null) RunFinished?.Invoke(finished);
        if (changedRaised) Changed?.Invoke(changed);
    }

    /// <summary>冷启动恢复：锁文件 + 判活探针；返回是否产生了新的运行快照</summary>
    private bool TryRecoverLocked(out GameRunInfo? info)
    {
        info = null;
        var dotMc = _dotMcProvider?.Invoke();
        if (string.IsNullOrEmpty(dotMc)) return false;

        var lockInfo = ReadLockFile(dotMc);
        if (lockInfo is null) return false;
        if (!_isAliveProbe(lockInfo.Pid)) return false;

        _pid = lockInfo.Pid;
        _versionId = string.IsNullOrEmpty(lockInfo.VersionId) ? "未知版本" : lockInfo.VersionId;
        _startedAt = lockInfo.StartedAt;
        _recovered = true;
        _consecutiveFailures = 0;
        _peakWorkingSet = 0;
        _samples.Clear();
        _commandLine = null;
        _sampler?.Dispose();
        _sampler = new ProcessSampler(lockInfo.Pid);

        var ctx = GameLaunchContextStore.Read(dotMc, _versionId);
        if (ctx is null) GameLaunchContextStore.Delete(dotMc); // 上一局残留 / 版本不符 → 丢弃
        _context = ctx;

        _info = BuildInfoLocked(default);
        info = _info;
        return true;
    }

    /// <summary>收口并落一条运行历史；返回落盘记录（无事可收时返回 null）</summary>
    private GameRunRecord? FinishLocked(int? exitCode)
    {
        if (_info is null) return null;
        var endedAt = DateTime.Now;
        var duration = _session is not null ? _session.Uptime : endedAt - _startedAt;
        var rec = new GameRunRecord(
            _versionId,
            _startedAt,
            endedAt,
            Math.Max(0, duration.TotalSeconds),
            exitCode,
            _peakWorkingSet,
            Crashed: !_killedByUser && exitCode is not (null or 0));

        _history.Append(rec);
        CleanupLocked(deleteContext: true);
        return rec;
    }

    private void OnSessionExited(int exitCode)
    {
        GameRunRecord? rec;
        lock (_gate)
        {
            var session = _session;
            if (session is not null) session.Exited -= OnSessionExited;
            rec = FinishLocked(exitCode);
        }
        if (rec is not null) RunFinished?.Invoke(rec);
        Changed?.Invoke(null);
    }

    /// <summary>清空跟踪状态（不触发事件；调用方负责通知）</summary>
    private void CleanupLocked(bool deleteContext)
    {
        var dotMc = _dotMcProvider?.Invoke();
        if (deleteContext && !string.IsNullOrEmpty(dotMc)) GameLaunchContextStore.Delete(dotMc);

        if (_session is not null) _session.Exited -= OnSessionExited;
        _sampler?.Dispose();
        _sampler = null;
        _session = null;
        _context = null;
        _commandLine = null;
        _info = null;
        _pid = 0;
        _versionId = "";
        _recovered = false;
        _killedByUser = false;
        _samplingPaused = false;
        _consecutiveFailures = 0;
        _peakWorkingSet = 0;
        _samples.Clear();
    }

    private void PushSampleLocked(in GameRunSample sample)
    {
        _samples.AddLast(sample);
        while (_samples.Count > SampleCapacity) _samples.RemoveFirst();
        if (sample.PeakWorkingSetBytes > _peakWorkingSet) _peakWorkingSet = sample.PeakWorkingSetBytes;
        if (sample.WorkingSetBytes > _peakWorkingSet) _peakWorkingSet = sample.WorkingSetBytes;
    }

    private GameRunInfo BuildInfoLocked(in GameRunSample latest) =>
        new(_pid, _versionId, _startedAt, UptimeLocked(), _recovered, latest);

    private TimeSpan UptimeLocked() =>
        _session is not null ? _session.Uptime : DateTime.Now - _startedAt;

    /// <summary>读取跨进程会话锁文件（只读；失效/损坏返回 null）</summary>
    private static GameSessionLock? ReadLockFile(string dotMc)
    {
        try
        {
            var file = LauncherPaths.GameSessionFile(dotMc);
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<GameSessionLock>(File.ReadAllText(file));
        }
        catch { return null; }
    }

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

    /// <summary>读最新一局游戏日志的尾部若干行（只回读文件末尾 64 KB，不整文件加载）</summary>
    private static IReadOnlyList<string> ReadLogTail(int count)
    {
        try
        {
            var dir = LauncherPaths.GameLogsDir;
            if (!Directory.Exists(dir)) return [];
            var latest = Directory.EnumerateFiles(dir, "game-*.log")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latest is null) return [];

            using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
