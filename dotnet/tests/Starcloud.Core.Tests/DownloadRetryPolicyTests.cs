// 下载重试策略回归：Retry-After 感知 + 404/410 不重试。
//
// 参照 PrismLauncher 的 Request::downloadError：429 时优先用服务端给的 Retry-After，
// 而不是自己猜指数冷却；404 明确「没有重试的意义」。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class DownloadRetryPolicyTests
{
    [Fact]
    public void RetryAfter秒数被解析()
    {
        var wait = RetryAfterParser.Parse("120", DateTimeOffset.UtcNow);

        Assert.NotNull(wait);
        Assert.Equal(120, wait!.Value.TotalSeconds);
    }

    [Fact]
    public void RetryAfter日期被解析()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var header = now.AddSeconds(90).ToString("R");

        var wait = RetryAfterParser.Parse(header, now);

        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalSeconds, 85, 95);
    }

    [Fact]
    public void RetryAfter缺失或格式不认识时返回空()
    {
        Assert.Null(RetryAfterParser.Parse(null, DateTimeOffset.UtcNow));
        Assert.Null(RetryAfterParser.Parse("   ", DateTimeOffset.UtcNow));
        Assert.Null(RetryAfterParser.Parse("soon", DateTimeOffset.UtcNow));
        Assert.Null(RetryAfterParser.Parse("0", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RetryAfter日期已过期时返回空()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var header = now.AddSeconds(-30).ToString("R");

        Assert.Null(RetryAfterParser.Parse(header, now));
    }

    [Fact]
    public void 服务端要求的等待比自己的冷却长时以服务端为准()
    {
        ParallelGovernor.ResetForTests();
        ParallelGovernor.ReportThrottled("example.com");   // 先产生一次自己的冷却

        ParallelGovernor.ReportRetryAfter("example.com", TimeSpan.FromSeconds(180));

        var cooldown = ParallelGovernor.CooldownFor("example.com");
        Assert.True(cooldown >= TimeSpan.FromSeconds(170),
            $"冷却应按服务端要求的 180 秒，实际 {cooldown.TotalSeconds:F0} 秒");
        ParallelGovernor.ResetForTests();
    }

    [Fact]
    public void 永久性错误是IO异常的子类便于统一捕获()
    {
        // 上层按 IOException 捕获时不能漏掉它
        Assert.IsAssignableFrom<IOException>(new PermanentHttpException("404"));
    }
}
