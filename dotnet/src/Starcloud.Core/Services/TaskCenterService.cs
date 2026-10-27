// 调度中心：全局任务总线。下载（聚合速度）/ 游戏启动 / Java 搜索等长任务在此登记，
// UI（主窗口悬浮面板）订阅 Changed 定时快照刷新。线程安全；任务列表保留最近 60 条。

using System.Collections.ObjectModel;

namespace Starcloud.Core.Services;

/// <summary>调度中心任务状态</summary>
public enum AppTaskState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>竞速下载的单源贡献（TotalBytes 预留：竞速分片无每源预期总量，显示为贡献占比）</summary>
public sealed record AppTaskMirror(string Name, long Bytes, long? TotalBytes);

/// <summary>调度中心的一条任务（进度 null = 不确定进度；Detail 为速度或阶段文本）</summary>
public sealed class AppTask
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; internal set; } = "";
    public AppTaskState State { get; internal set; } = AppTaskState.Running;
    public double? Progress { get; internal set; }          // 0 ~ 1
    public string Detail { get; internal set; } = "";       // 速度 / 阶段 / 结果
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>竞速下载各源实时贡献（非竞速任务为空）</summary>
    public List<AppTaskMirror> Mirrors { get; internal set; } = [];
}

/// <summary>调度中心（单例）：Begin 登记 → Update 报告进度 → Complete / Fail 收尾。</summary>
public sealed class TaskCenterService
{
    public static TaskCenterService Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<AppTask> _tasks = [];
    private const int MaxKept = 60;

    /// <summary>任意变化（新增 / 进度 / 收尾）后触发；UI 侧应节流刷新（定时快照）</summary>
    public event Action? Changed;

    /// <summary>登记新任务；已存在同标题的运行中任务时直接复用（避免 Java 搜索等高频重复登记）</summary>
    public AppTask Begin(string title)
    {
        lock (_gate)
        {
            var existing = _tasks.FirstOrDefault(t =>
                t.State == AppTaskState.Running && t.Title == title);
            if (existing is not null) return existing;
            var task = new AppTask { Title = title };
            _tasks.Insert(0, task);
            if (_tasks.Count > MaxKept) _tasks.RemoveAt(_tasks.Count - 1);
            Changed?.Invoke();
            return task;
        }
    }

    /// <summary>更新进度与详情（均可选，null = 保持不变）</summary>
    public void Update(AppTask task, double? progress = null, string? detail = null)
    {
        lock (_gate)
        {
            task.Progress = progress ?? task.Progress;
            if (detail is not null) task.Detail = detail;
            Changed?.Invoke();
        }
    }

    /// <summary>收尾：完成（带结果文本）</summary>
    public void Complete(AppTask task, string? detail = null)
    {
        Finish(task, AppTaskState.Completed, detail);
    }

    /// <summary>收尾：失败（原因进 Detail）</summary>
    public void Fail(AppTask task, string message)
    {
        Finish(task, AppTaskState.Failed, message);
    }

    private void Finish(AppTask task, AppTaskState state, string? detail)
    {
        lock (_gate)
        {
            if (task.State != AppTaskState.Running) return;
            task.State = state;
            if (detail is not null) task.Detail = detail;
            else if (state == AppTaskState.Completed) task.Detail = "完成";
            else if (state == AppTaskState.Cancelled) task.Detail = "已取消";
            try { task.Cts.Cancel(); } catch { /* 已释放 */ }
            Changed?.Invoke();
        }
        // 历史通知：任务结束不再留在队列里，而是落成一条带时间的记录（锁外调用，避免与通知中心交叉持锁）
        NotifyFinished(task, state);
    }

    /// <summary>任务收尾 → 通知文案（Core 产出简体中文，UI 经 Loc.TCore 本地化）</summary>
    private static void NotifyFinished(AppTask task, AppTaskState state)
    {
        var notices = NotificationCenter.Instance;
        switch (state)
        {
            case AppTaskState.Completed:
                notices.Info(string.IsNullOrEmpty(task.Detail) || task.Detail == "完成"
                    ? $"{task.Title} 完成"
                    : $"{task.Title} 完成（{task.Detail}）");
                break;
            case AppTaskState.Failed:
                notices.Error($"{task.Title} 失败（{task.Detail}）");
                break;
            case AppTaskState.Cancelled:
                notices.Warn($"{task.Title} 已取消");
                break;
        }
    }

    /// <summary>请求取消任务（下载流 / 阶段循环会在下一个检查点退出）</summary>
    public void Cancel(AppTask task)
    {
        if (task.State != AppTaskState.Running) return;
        Finish(task, AppTaskState.Cancelled, "已取消");
    }

    /// <summary>当前任务快照（最新在前）</summary>
    public ReadOnlyCollection<AppTask> Snapshot()
    {
        lock (_gate) return _tasks.AsReadOnly();
    }

    /// <summary>清空全部任务历史（启动器启动时调用：面板只保留本次会话的任务）</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _tasks.Clear();
            _downloadTask = null;
            _racingTask = null;
            _racingBytes.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>运行中任务数（悬浮按钮徽标）</summary>
    public int RunningCount
    {
        get { lock (_gate) return _tasks.Count(t => t.State == AppTaskState.Running); }
    }

    // ── 下载聚合（常驻「资源下载」任务：任意下载活动期间显示总速度） ──

    private AppTask? _downloadTask;
    private double _emaSpeed;                // 指数滑动平均速度（字节/秒）
    private long _windowBytes;               // 当前采样窗口内累计的字节
    private long _windowStart;               // 当前采样窗口的起点（TickCount64）
    private int _active;                    // 活动下载计数（>0 期间任务保持 Running）
    private int _closeLoop;                 // 收尾循环单飞标志（1 = 已在跑）

    /// <summary>下载字节上报（DownloadService 各路径调用，n 为本次增量字节）。
    /// 首次调用自动登记常驻任务；活动结束约 3 秒后自动完成。
    /// <para>调用方已按 10 Hz 聚合上报，这里不再做逐次去抖；收尾循环是单飞的
    /// （原来每次上报都 `_ = CloseWhenIdleAsync()`，满速下载时每秒几百个睡眠任务）。</para>
    /// <para>速度按「固定采样窗口」结算，而不是按「距上次任意上报的间隔」：
    /// 多个下载线程的窗口边界天然对齐，第 1 个上报时 dt≈100 ms（算得对），
    /// 紧接着其余几个 dt≈0 ms 而字节数仍是完整窗口的量 → 瞬时速度被放大十倍上百倍，
    /// 面板上就表现为速度疯狂跳动。改成固定窗口后，读数才是真实吞吐。</para></summary>
    public void ReportDownloadBytes(long n)
    {
        if (n <= 0) return;
        AppTask task;
        lock (_gate)
        {
            _downloadTask ??= BeginUnsafe("资源下载");
            task = _downloadTask;
            Volatile.Write(ref _active, Volatile.Read(ref _active) + 1);

            var now = Environment.TickCount64;
            if (_windowStart == 0)
            {
                _windowStart = now;
                _windowBytes = 0;
            }
            _windowBytes += n;

            // 采样窗口到 200 ms 结算一次：用「窗口字节 ÷ 窗口时长」得到这段时间的真实吞吐
            var windowMs = now - _windowStart;
            if (windowMs >= 200)
            {
                var inst = _windowBytes * 1000.0 / windowMs;
                _emaSpeed = _emaSpeed <= 0 ? inst : (_emaSpeed * 3 + inst) / 4;
                _windowStart = now;
                _windowBytes = 0;
                task.Detail = FormatSpeed(_emaSpeed);
            }
            else if (_emaSpeed > 0)
            {
                // 窗口内沿用上一轮的值，只更新显示
                task.Detail = FormatSpeed(_emaSpeed);
            }
        }
        Changed?.Invoke();
        EnsureCloseLoop();
    }

    /// <summary>启动单飞的收尾循环（已在跑就直接返回）</summary>
    private void EnsureCloseLoop()
    {
        if (Interlocked.CompareExchange(ref _closeLoop, 1, 0) != 0) return;
        _ = CloseWhenIdleAsync();
    }

    /// <summary>
    /// 活动归零后延迟收尾：每 3 秒结算一次计数，归零即完成常驻任务。
    /// 只要期间还有上报（_active > 0）就继续等，所以实现是「一个循环 + 计数器」，而不是「每次上报一个任务」。
    /// </summary>
    private async Task CloseWhenIdleAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(3000);
                AppTask task;
                lock (_gate)
                {
                    var pending = Volatile.Read(ref _active);
                    if (pending > 0)
                    {
                        Volatile.Write(ref _active, 0); // 本轮活动已结算，继续等下一轮
                        continue;
                    }
                    if (_downloadTask is null) return;  // 已被清空：收工
                    task = _downloadTask;
                    _downloadTask = null;
                    _windowStart = 0;
                    _windowBytes = 0;
                    _emaSpeed = 0;
                }
                Finish(task, AppTaskState.Completed, "下载完成");
                return;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _closeLoop, 0);
        }
    }

    private AppTask BeginUnsafe(string title)
    {
        var task = new AppTask { Title = title };
        _tasks.Insert(0, task);
        if (_tasks.Count > MaxKept) _tasks.RemoveAt(_tasks.Count - 1);
        return task;
    }

    // ── 多源竞速下载（分片大文件：进度 = 各文件已完成分片 / 各文件总大小，Detail 列各源累计） ──
    //
    // 支持**多文件并发**（整合包安装时同时会有好几个大文件在分片）：每个在跑的文件带一个 token，
    // 只按 token 记账，最后一个文件收尾时才结束这条任务。早期版本是单文件全局字段，
    // 并发时后开始的文件会把前一个的进度清零（表现为进度条来回跳）。

    private sealed class RacingFile
    {
        public string Name = "";
        public long Size;
        public long Done;
    }

    private AppTask? _racingTask;
    private readonly Dictionary<object, RacingFile> _racingFiles = new();
    private readonly Dictionary<string, long> _racingBytes = new();
    private bool _racingFailed;

    /// <summary>竞速下载开始（文件总大小已知 → 显示确定进度）</summary>
    public void ReportRacingBegin(object token, string fileName, long totalSize)
    {
        AppTask task;
        lock (_gate)
        {
            _racingTask ??= BeginUnsafe("多源竞速下载");
            task = _racingTask;
            if (_racingFiles.Count == 0)
            {
                _racingFailed = false;
                _racingBytes.Clear();
                task.Mirrors = [];
            }
            _racingFiles[token] = new RacingFile { Name = fileName, Size = Math.Max(0, totalSize) };
            UpdateRacingProgress();
        }
        Changed?.Invoke();
    }

    /// <summary>按源上报字节增量（FetchRangeAsync 每块调用）；只驱动每源贡献条，进度由分片完成量统计</summary>
    public void ReportSourceBytes(string sourceName, long n)
    {
        lock (_gate)
        {
            if (_racingTask is null) return;
            _racingBytes[sourceName] = _racingBytes.GetValueOrDefault(sourceName) + n;
            _racingTask.Mirrors = _racingBytes.OrderByDescending(kv => kv.Value)
                .Select(kv => new AppTaskMirror(kv.Key, kv.Value, null)).ToList();
        }
        Changed?.Invoke();
    }

    /// <summary>单个分片成功完成（FetchRangeAsync return true 前）：进度按分片去重累计，单调准确</summary>
    public void ReportChunkDone(object token, long chunkLength)
    {
        lock (_gate)
        {
            if (_racingTask is null) return;
            if (_racingFiles.TryGetValue(token, out var file)) file.Done += chunkLength;
            UpdateRacingProgress();
        }
        Changed?.Invoke();
    }

    /// <summary>竞速下载收尾（ok=false 显示失败，任务保留最近记录）；并发时等最后一个文件结束</summary>
    public void ReportRacingEnd(object token, bool ok, long actualBytes)
    {
        AppTask? task = null;
        List<AppTaskMirror>? mirrors = null;
        var failed = false;
        lock (_gate)
        {
            _racingFiles.Remove(token);
            if (!ok) _racingFailed = true;
            if (_racingFiles.Count > 0)
            {
                UpdateRacingProgress();
            }
            else
            {
                task = _racingTask;
                mirrors = task?.Mirrors.Count > 0 ? task.Mirrors : null;
                failed = _racingFailed;
                _racingTask = null;
                _racingBytes.Clear();
            }
        }
        if (task is null)
        {
            Changed?.Invoke();
            return;
        }

        // 完成态用实际收到的字节标注（探测的分母可能虚大，如「24/37 完成」= 分母错、文件实际 24MB）
        var done = mirrors?.Sum(m => m.Bytes) ?? actualBytes;
        if (!failed) Finish(task, AppTaskState.Completed,
            done > 0 ? $"下载完成（{done / 1048576.0:F1} MB）" : "下载完成");
        else Finish(task, AppTaskState.Failed, "竞速失败（已换源重试或整文件回退）");
    }

    /// <summary>按各在跑文件的分片完成量刷新进度与 Detail（调用方持锁）</summary>
    private void UpdateRacingProgress()
    {
        var task = _racingTask;
        if (task is null) return;
        var total = _racingFiles.Values.Sum(f => f.Size);
        var done = _racingFiles.Values.Sum(f => f.Done);
        task.Progress = total > 0 ? Math.Min(1.0, (double)done / total) : null;
        var name = _racingFiles.Count == 1 ? _racingFiles.Values.First().Name : "";
        task.Detail = total > 0
            ? $"已下载 {done / 1048576.0:F1} MB / {total / 1048576.0:F1} MB"
              + (name.Length > 0 ? $"（{name}）" : $"（{_racingFiles.Count} 个文件）")
            : $"已下载 {done / 1048576.0:F1} MB";
    }

    internal static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1L << 30 => $"{bytesPerSec / (double)(1L << 30):F1} GB/s",
        >= 1 << 20 => $"{bytesPerSec / 1048576.0:F1} MB/s",
        >= 1024 => $"{bytesPerSec / 1024.0:F0} KB/s",
        > 0 => $"{bytesPerSec:F0} B/s",
        _ => "连接中…",
    };
}
