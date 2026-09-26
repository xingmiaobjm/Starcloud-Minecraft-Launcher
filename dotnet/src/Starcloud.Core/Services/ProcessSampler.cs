// 游戏进程采样器：CPU / 内存 / 线程 / GPU 单点读数
// 设计：docs/designs/2026-09-26-game-running-status-panel-design.md §3
//
// 只读自己启动的这一局进程：不枚举全机器进程，不做历史聚合。
// CPU% 口径与 NovaFpsWatcher 一致（TotalProcessorTime 差分 ÷ 间隔 ÷ 核数）；
// GPU% 复用同程序集的 internal PdhNative，任何失败返回 null（不写 0，避免与"真的 0%"混淆）。

using System.Diagnostics;

namespace Starcloud.Core.Services;

/// <summary>一次采样的读数；<see cref="GpuPercent"/> = null 表示计数器不可用</summary>
public readonly record struct GameRunSample(
    DateTime At,
    double CpuPercent,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    int ThreadCount,
    double? GpuPercent);

/// <summary>单个游戏进程的连续采样器（持有一次进程句柄，进程退出后 <see cref="TrySample"/> 恒 false）</summary>
public sealed class ProcessSampler : IDisposable
{
    private readonly Process? _process;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleAt;
    private int _gpuCollectFailStreak;
    private bool _gpuBroken; // GPU 链路判死（连续失败）后本局不再尝试
    private bool _disposed;

    public ProcessSampler(int pid)
    {
        try
        {
            _process = Process.GetProcessById(pid);
            _lastCpuTime = _process.TotalProcessorTime;
            _lastSampleAt = DateTime.UtcNow;
        }
        catch
        {
            _process = null; // pid 已失效：不采样，不抛
        }
    }

    /// <summary>本次采样是否成功（进程已退出 / 句柄失效 → false，不抛异常）</summary>
    public bool TrySample(bool collectGpu, out GameRunSample sample)
    {
        sample = default;
        var proc = _process;
        if (proc is null) return false;
        try
        {
            if (proc.HasExited) return false;
            proc.Refresh();
        }
        catch
        {
            return false;
        }

        // 各项独立取值，单项失败回退 0，不整条失败
        var workingSet = TryRead(() => proc.WorkingSet64);
        var peak = TryRead(() => proc.PeakWorkingSet64);
        var threads = (int)TryRead(() => proc.Threads.Count);

        // CPU%：与 NovaFpsWatcher 同口径（间隔过短时不给差分，避免除零与假尖峰）
        double cpu = 0;
        try
        {
            var nowCpu = proc.TotalProcessorTime;
            var nowAt = DateTime.UtcNow;
            var elapsed = (nowAt - _lastSampleAt).TotalSeconds;
            if (elapsed > 0.5)
            {
                cpu = (nowCpu - _lastCpuTime).TotalSeconds
                      / elapsed / Environment.ProcessorCount * 100.0;
                cpu = Math.Clamp(cpu, 0, 100);
                _lastCpuTime = nowCpu;
                _lastSampleAt = nowAt;
            }
        }
        catch { cpu = 0; }

        sample = new GameRunSample(
            DateTime.Now, cpu, workingSet, peak, threads,
            collectGpu ? SampleGpuPercent(proc.Id) : null);
        return true;
    }

    private static long TryRead(Func<long> read)
    {
        try { return read(); } catch { return 0; }
    }

    // ── PDH GPU Engine 采样（best-effort：任何失败返回 null，连续失败判死本局不再试） ──
    // 计数器路径与降级口径同 NovaFpsWatcher.SampleGpuPercent。

    private double? SampleGpuPercent(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (_gpuBroken) return null;
        var paths = ExpandGpuInstances(pid);
        if (paths.Count == 0)
        {
            MarkGpuFail();
            return null;
        }
        if (!PdhNative.TryCollectSum(paths, out var sum))
        {
            MarkGpuFail();
            return null;
        }
        _gpuCollectFailStreak = 0;
        return Math.Clamp(sum, 0, 100);
    }

    private void MarkGpuFail()
    {
        if (++_gpuCollectFailStreak >= 2) _gpuBroken = true; // 两次失败即判死，省开销
    }

    private static List<string> ExpandGpuInstances(int pid)
    {
        var pattern = $@"\GPU Engine(pid_{pid}_*)\Utilization Percentage";
        return PdhNative.ExpandWildcard(pattern);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process?.Dispose(); } catch { /* 已退出忽略 */ }
    }
}
