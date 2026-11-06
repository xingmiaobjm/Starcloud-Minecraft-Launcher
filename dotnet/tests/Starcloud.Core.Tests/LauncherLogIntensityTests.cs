using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>日志强度：普通 / 详细 / 调试 的门控与下限锁定（非正式版锁调试）。</summary>
public class LauncherLogIntensityTests : IDisposable
{
    public void Dispose() =>
        LauncherLog.SetFloor(LauncherLog.LogIntensity.Normal);   // 恢复下限，别污染其它测试

    [Fact]
    public void 普通强度下调试日志被丢弃()
    {
        LauncherLog.ApplyIntensity(LauncherLog.LogIntensity.Normal);
        var marker = $"INTENSITY-MARK-{Guid.NewGuid():N}";
        LauncherLog.Debug("intensity-test", marker);
        Assert.DoesNotContain(LauncherLog.Tail(100), l => l.Contains(marker));
    }

    [Fact]
    public void 详细强度下调试日志保留()
    {
        LauncherLog.ApplyIntensity(LauncherLog.LogIntensity.Verbose);
        var marker = $"INTENSITY-MARK-{Guid.NewGuid():N}";
        LauncherLog.Debug("intensity-test", marker);
        Assert.Contains(LauncherLog.Tail(100), l => l.Contains(marker));
    }

    [Fact]
    public void 调试强度下跟踪日志保留()
    {
        LauncherLog.ApplyIntensity(LauncherLog.LogIntensity.Debug);
        var marker = $"INTENSITY-MARK-{Guid.NewGuid():N}";
        LauncherLog.Trace("intensity-test", marker);
        Assert.Contains(LauncherLog.Tail(100), l => l.Contains(marker));
    }

    [Fact]
    public void 普通日志任何强度都保留()
    {
        LauncherLog.ApplyIntensity(LauncherLog.LogIntensity.Normal);
        var marker = $"INTENSITY-MARK-{Guid.NewGuid():N}";
        LauncherLog.Write("intensity-test", marker);
        Assert.Contains(LauncherLog.Tail(100), l => l.Contains(marker));
    }

    [Fact]
    public void 下限锁定后设置改不下去()
    {
        LauncherLog.SetFloor(LauncherLog.LogIntensity.Debug);
        LauncherLog.ApplyIntensity(LauncherLog.LogIntensity.Normal);
        Assert.Equal(LauncherLog.LogIntensity.Debug, LauncherLog.Intensity);
    }
}
