using System.Text.Json;
using Starcloud.Core;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Multiplayer;

namespace Starcloud.Bridge;

/// <summary>运行态快照：进程采样、启动上下文降级重算（Win 端 RunningStatusPage 同源）。</summary>
public static partial class Program
{
    private static object RuntimeInstancePayload(GameRunInfo i) => new
    {
        pid = i.Pid,
        versionId = i.VersionId,
        startedAt = i.StartedAt,
        uptimeSec = i.Uptime.TotalSeconds,
        recovered = i.RecoveredFromLock,
        cpuPercent = i.Latest.CpuPercent,
        workingSetBytes = i.Latest.WorkingSetBytes,
        peakWorkingSetBytes = i.Latest.PeakWorkingSetBytes,
        threadCount = i.Latest.ThreadCount,
        gpuPercent = i.Latest.GpuPercent,
    };

    private static object RuntimePayload()
    {
        var monitor = GameRuntimeMonitor.Instance;
        var current = monitor.Current;
        var ctx = monitor.LaunchContext;
        var fallback = false;
        if (ctx is null && current is not null)
        {
            ctx = RuntimeFallbackContext(current.VersionId);
            fallback = ctx is not null;
        }
        return new
        {
            selectedPid = monitor.SelectedPid,
            samplingPaused = monitor.SamplingPaused,
            collectGpu = monitor.CollectGpu,
            intervalMs = monitor.IntervalMs,
            commandLine = monitor.CommandLine,
            contextFallback = fallback,
            context = ctx,
            instances = monitor.Instances.Select(RuntimeInstancePayload),
            samples = monitor.Samples(60).Select(s => new
            {
                at = s.At,
                cpuPercent = s.CpuPercent,
                workingSetBytes = s.WorkingSetBytes,
                gpuPercent = s.GpuPercent,
            }),
        };
    }

    /// <summary>本轮启动上下文缺失时（上一轮启动器拉起的会话）按当前配置降级重算，Win 同款。</summary>
    private static GameLaunchContext? RuntimeFallbackContext(string versionId)
    {
        if (string.IsNullOrEmpty(versionId) || versionId == "未知版本") return null;
        try
        {
            var dotMc = _settings.DotMinecraft;
            var profile = new InstanceSettingsStore(dotMc).Get(versionId);
            var isolated = InstanceLayout.IsIsolated(profile, _settings);
            var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
            var eff = InstanceLayout.Resolve(dotMc, versionId, profile, _settings,
                MemoryAdvisor.Probe(gameRoot));
            return GameLaunchContextStore.Capture(versionId, eff,
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "mods")),
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "shaderpacks")),
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "resourcepacks")));
        }
        catch { return null; }
    }
}
