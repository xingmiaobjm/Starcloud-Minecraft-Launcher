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

    /// <summary>清除已结束的任务记录（完成 / 失败 / 取消），进行中的任务保留。
    /// 返回被清除的条数，便于 UI 判断是否需要刷新。</summary>
    public int ClearFinished()
    {
        int removed;
        lock (_gate) removed = _tasks.RemoveAll(t => t.State != AppTaskState.Running);
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    /// <summary>运行中任务数（悬浮按钮徽标）</summary>
    public int RunningCount
    {
        get { lock (_gate) return _tasks.Count(t => t.State == AppTaskState.Running); }
    }

    // ── 下载聚合（常驻「资源下载」任务：任意下载活动期间显示总速度） ──

    private AppTask? _downloadTask;
    private long _emaSpeed;                 // 指数滑动平均速度（字节/秒）
    private long _lastTick;
    private int _active;                    // 活动下载计数（>0 期间任务保持 Running）

    /// <summary>下载字节上报（DownloadService 各路径调用，n 为本次增量字节）。
    /// 首次调用自动登记常驻任务；活动结束约 3 秒后自动完成。</summary>
    public void ReportDownloadBytes(long n)
    {
        if (n <= 0) return;
        AppTask task;
        lock (_gate)
        {
            _downloadTask ??= BeginUnsafe("资源下载");
            task = _downloadTask;
            _active++;
            var now = Environment.TickCount64;
            if (_lastTick > 0)
            {
                var dt = now - _lastTick;
                if (dt > 0)
                {
                    var inst = n * 1000 / dt;
                    _emaSpeed = _emaSpeed == 0 ? inst : (_emaSpeed * 3 + inst) / 4;
                }
            }
            _lastTick = now;
            task.Detail = FormatSpeed(_emaSpeed);
        }
        Changed?.Invoke();
        _ = CloseWhenIdleAsync();
    }

    /// <summary>活动归零后延迟收尾；期间又有新字节则继续</summary>
    private async Task CloseWhenIdleAsync()
    {
        while (true)
        {
            await Task.Delay(3000);
            AppTask task;
            lock (_gate)
            {
                _active--;
                if (_active > 0) return;
                task = _downloadTask!;
                _downloadTask = null;
                _lastTick = 0;
                _emaSpeed = 0;
            }
            Finish(task, AppTaskState.Completed, "下载完成");
            return;
        }
    }

    private AppTask BeginUnsafe(string title)
    {
        var task = new AppTask { Title = title };
        _tasks.Insert(0, task);
        if (_tasks.Count > MaxKept) _tasks.RemoveAt(_tasks.Count - 1);
        return task;
    }

    // ── 多源竞速下载（单个大文件：进度 = 已下字节/总字节，Detail 列各源累计） ──

    private AppTask? _racingTask;
    private long _racingTotal;
    private long _racingChunkDone;                          // 已完成分片字节（分片级去重，进度用它）
    private readonly Dictionary<string, long> _racingBytes = new();

    /// <summary>竞速下载开始（文件总大小已知 → 显示确定进度）</summary>
    public void ReportRacingBegin(long totalSize)
    {
        AppTask task;
        lock (_gate)
        {
            _racingTask ??= BeginUnsafe("多源竞速下载");
            task = _racingTask;
            _racingTotal = totalSize;
            _racingChunkDone = 0;
            _racingBytes.Clear();
            task.Progress = 0;
            task.Mirrors = [];
            task.Detail = "分片竞速中…";
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
    public void ReportChunkDone(long chunkLength)
    {
        lock (_gate)
        {
            if (_racingTask is null) return;
            _racingChunkDone += chunkLength;
            _racingTask.Progress = _racingTotal > 0
                ? Math.Min(1.0, (double)_racingChunkDone / _racingTotal) : null;
            _racingTask.Detail = _racingTotal > 0
                ? $"已下载 {_racingChunkDone / 1048576.0:F1} MB / {_racingTotal / 1048576.0:F1} MB"
                : $"已下载 {_racingChunkDone / 1048576.0:F1} MB";
        }
        Changed?.Invoke();
    }

    /// <summary>竞速下载收尾（ok=false 显示失败，任务保留最近记录）</summary>
    public void ReportRacingEnd(bool ok)
    {
        AppTask? task;
        double total;
        lock (_gate)
        {
            task = _racingTask;
            total = _racingTotal;
            _racingTask = null;
            _racingTotal = 0;
        }
        if (task is null) return;
        // 完成态用实际收到的字节标注（探测的分母可能虚大，如「24/37 完成」= 分母错、文件实际 24MB）
        var done = task.Mirrors.Sum(m => m.Bytes);
        if (ok) Finish(task, AppTaskState.Completed,
            done > 0 ? $"下载完成（{done / 1048576.0:F1} MB）" : "下载完成");
        else Finish(task, AppTaskState.Failed, "竞速失败（已换源重试或整文件回退）");
    }

    internal static string FormatSpeed(long bytesPerSec) => bytesPerSec switch
    {
        >= 1L << 30 => $"{bytesPerSec / (double)(1L << 30):F1} GB/s",
        >= 1 << 20 => $"{bytesPerSec / 1048576.0:F1} MB/s",
        >= 1024 => $"{bytesPerSec / 1024.0:F0} KB/s",
        > 0 => $"{bytesPerSec} B/s",
        _ => "连接中…",
    };
}
