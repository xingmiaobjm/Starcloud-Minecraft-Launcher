// Nova 启发式卡顿监测（设计：docs/designs/2026-09-08-nova-overlay-design.md §3.1）
// 纯软件层：只观测自己启动的 javaw 进程计数器，不读画面、不读剪贴板、不碰游戏进程。
// 判定「高且抖」：60s 滑窗内 CPU 持续 >85%（≥4 点）且波动剧烈（σ>15）→ SuspectedStutter。
// 诚实性：GPU 计数器拿不到就降级并如实标注（GpuAvailable=false），只报实测数值，不臆造帧数。

using System.Diagnostics;

namespace Starcloud.Core.Services;

/// <summary>疑似卡顿证据（全部为实测数值，UI 侧必须标注「启发式推测」）</summary>
public sealed record StutterEvidence(
    double CpuAvgPercent,
    double CpuStdDev,
    int WindowSeconds,
    bool GpuAvailable,
    double? GpuAvgPercent);

/// <summary>
/// 启发式帧数监测器：latest.log 里没有实时 FPS，用进程 CPU/GPU 占用突变近似「疑似卡顿」。
/// 生命周期 = 一局游戏：Start(pid) → 自动采样 → 游戏退出 Stop()。
/// 线程：事件在采样线程触发，由 NovaRuntime.RunOnUi 转发；本类不碰 UI。
/// </summary>
public sealed class NovaFpsWatcher : IDisposable
{
    // 采样参数（设计文档定值）
    private const int SampleIntervalMs = 3000;   // 3s 采样
    private const int WindowSamples = 20;        // 60s 滑窗
    private const double CpuHotPercent = 85.0;   // 单点「热」阈值
    private const int HotPointsNeeded = 4;       // ≥4 个热点才判「持续」
    private const double StdDevThreshold = 15.0; // 波动阈值：高且抖才算卡顿
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private const int MaxConsecutiveErrors = 3;  // 连续失败即自动停（静默禁用）

    private readonly object _lock = new();
    private readonly Queue<double> _cpuWindow = new();
    private readonly Queue<double> _gpuWindow = new();

    private System.Threading.Timer? _timer;
    private Process? _proc;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleAt;
    private int _errorStreak;
    private DateTime _lastRaisedAt = DateTime.MinValue;
    private int _gpuCollectFailStreak;
    private bool _gpuBroken; // GPU 链路判死（连续失败）后本局不再尝试

    /// <summary>疑似卡顿命中（采样线程触发）</summary>
    public event Action<StutterEvidence>? SuspectedStutter;

    public bool IsRunning { get; private set; }

    public void Start(int processId)
    {
        lock (_lock)
        {
            StopCore();
            try
            {
                _proc = Process.GetProcessById(processId);
            }
            catch
            {
                _proc = null; // pid 已失效（游戏秒退）：不监测，不报错
            }
            if (_proc is null) return;

            _cpuWindow.Clear();
            _gpuWindow.Clear();
            _errorStreak = 0;
            _gpuCollectFailStreak = 0;
            _gpuBroken = false;
            _lastCpuTime = _proc.TotalProcessorTime;
            _lastSampleAt = DateTime.UtcNow;
            _lastRaisedAt = DateTime.MinValue;
            IsRunning = true;
            _timer = new System.Threading.Timer(_ => SampleTick(), null,
                SampleIntervalMs, SampleIntervalMs);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        _timer?.Dispose();
        _timer = null;
        IsRunning = false;
        try { _proc?.Dispose(); } catch { /* 已退出 */ }
        _proc = null;
    }

    private void SampleTick()
    {
        StutterEvidence? evidence = null;
        lock (_lock)
        {
            if (!IsRunning) return;
            var proc = _proc;
            if (proc is null) { StopCore(); return; }

            try
            {
                if (proc.HasExited) { StopCore(); return; }
                proc.Refresh();

                // CPU%：TotalProcessorTime 差分 ÷ 间隔 ÷ 核数（自算，不用 PerformanceCounter）
                var nowCpu = proc.TotalProcessorTime;
                var nowAt = DateTime.UtcNow;
                var elapsed = (nowAt - _lastSampleAt).TotalSeconds;
                double cpu = 0;
                if (elapsed > 0.5)
                {
                    cpu = (nowCpu - _lastCpuTime).TotalSeconds
                          / elapsed / Environment.ProcessorCount * 100.0;
                    cpu = Math.Clamp(cpu, 0, 100);
                    _lastCpuTime = nowCpu;
                    _lastSampleAt = nowAt;
                }
                Push(_cpuWindow, cpu, WindowSamples);

                // GPU%：PDH best-effort，任何失败降级（诚实标注 GpuAvailable=false）
                var gpu = SampleGpuPercent(proc.Id);
                if (gpu is { } g) Push(_gpuWindow, g, WindowSamples);

                _errorStreak = 0;
                evidence = Evaluate();
            }
            catch
            {
                // 进程句柄失效 / 权限变化：连续失败即静默停（禁用帧数提醒，不影响其他功能）
                if (++_errorStreak >= MaxConsecutiveErrors) StopCore();
            }
        }
        if (evidence is not null) SuspectedStutter?.Invoke(evidence);
    }

    private void Push(Queue<double> q, double v, int cap)
    {
        q.Enqueue(v);
        while (q.Count > cap) q.Dequeue();
    }

    private StutterEvidence? Evaluate()
    {
        if (_cpuWindow.Count < WindowSamples) return null; // 窗口未满不判定
        var arr = _cpuWindow.ToArray();
        var hot = arr.Count(v => v > CpuHotPercent);
        if (hot < HotPointsNeeded) return null;

        var avg = arr.Average();
        var stdDev = Math.Sqrt(arr.Average(v => (v - avg) * (v - avg)));
        if (stdDev < StdDevThreshold) return null; // 高但平稳（如着色器编译）不算卡顿

        if (DateTime.UtcNow - _lastRaisedAt < Cooldown) return null; // 冷却防骚扰
        _lastRaisedAt = DateTime.UtcNow;

        var gpuArr = _gpuWindow.ToArray();
        return new StutterEvidence(
            Math.Round(avg, 1),
            Math.Round(stdDev, 1),
            _cpuWindow.Count * SampleIntervalMs / 1000,
            GpuAvailable: gpuArr.Length > 0,
            GpuAvgPercent: gpuArr.Length > 0 ? Math.Round(gpuArr.Average(), 1) : null);
    }

    // ── PDH GPU Engine 采样（best-effort：任何失败返回 null，连续失败判死本局不再试） ──
    //
    // 计数器路径：\GPU Engine(pid_<pid>_*)\Utilization Percentage
    // 中英系统差异：对象/计数器名在中文 Windows 上是本地化的，PdhExpandWildCardPathW 对英文
    // 路径可能失败——失败即走降级路径，不影响 CPU 判定（设计文档 §5 静默降级）。

    private double? SampleGpuPercent(int pid)
    {
        // PDH 仅 Windows：unix 无对应计数器，GPU 数据如实缺席（GpuAvailable=false），CPU 判定不受影响
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

    public void Dispose() => Stop();
}

/// <summary>PDH P/Invoke（同程序集 internal，供 NovaFpsWatcher 与 ProcessSampler 共用）。所有失败静默返回 false/null，由上层降级。</summary>
internal static class PdhNative
{
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    public static List<string> ExpandWildcard(string englishPath)
    {
        var result = new List<string>();
        try
        {
            uint size = 0;
            var hr = PdhExpandWildCardPathW(null, englishPath, null, ref size, 0);
            if (hr != PDH_MORE_DATA || size == 0) return result;

            var buf = new char[size];
            hr = PdhExpandWildCardPathW(null, englishPath, buf, ref size, 0);
            if (hr != 0) return result;

            // 双 \0 分隔的多字符串
            var start = 0;
            for (var i = 0; i < size && buf[i] != '\0' || start < i; i++)
            {
                if (buf[i] == '\0')
                {
                    if (i > start) result.Add(new string(buf, start, i - start));
                    start = i + 1;
                    if (i + 1 < size && buf[i + 1] == '\0') break; // 列表结束
                }
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    /// <summary>一次性收集：为每个实例建 counter，collect 一轮后求和。3s 一次，开销可忽略。</summary>
    public static bool TryCollectSum(List<string> paths, out double sum)
    {
        sum = 0;
        if (paths.Count == 0 || paths.Count > 128) return false; // 异常膨胀直接放弃
        IntPtr query = IntPtr.Zero;
        var counters = new List<IntPtr>(paths.Count);
        try
        {
            if (PdhOpenQueryW(null, 0, out query) != 0) return false;
            foreach (var p in paths)
            {
                if (PdhAddEnglishCounterW(query, p, IntPtr.Zero, out var h) != 0) return false;
                counters.Add(h);
            }
            if (PdhCollectQueryData(query) != 0) return false;

            // GPU Utilization 是 double 采样值（非差值型），单轮即可读
            foreach (var h in counters)
            {
                if (PdhGetFormattedCounterValueD(h, out var v) == 0) sum += v;
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var h in counters) PdhRemoveCounter(h);
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int PdhExpandWildCardPathW(string? szDataSource, string szPath,
        char[]? mszPathList, ref uint pcchPathList, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath,
        IntPtr dwUserData, out IntPtr phCounter);

    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat,
        IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    private static extern int PdhRemoveCounter(IntPtr hCounter);

    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr hQuery);

    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    private static int PdhGetFormattedCounterValueD(IntPtr hCounter, out double value)
    {
        var hr = PdhGetFormattedCounterValue(hCounter, PDH_FMT_DOUBLE, IntPtr.Zero, out var v);
        value = v.doubleValue;
        return hr;
    }
}
