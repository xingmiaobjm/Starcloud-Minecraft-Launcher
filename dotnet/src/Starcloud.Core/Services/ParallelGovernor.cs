// 主机级并行度调节器：不同 CDN 对并发连接数的容忍度差一个数量级。
//
// 实测（2026-09-21，BMCLAPI 22 MB 客户端 jar）：
//   · 8 条并发分片 → 前 9 个请求正常，随后整批 429（Too Many Requests），连整文件兜底也被牵连；
//   · 1 条连接 → 稳定但只有 1.2 MB/s。
// 所以并行度不能写死成 8：得按主机试探、按主机记忆——被限流过的主机，下一个文件就不该再被 8 条并发打一遍。
//
// 规则：起始 4 条 → 连续 8 次成功后 +1（镜像上限 8、官方上限 16）→ 一旦 429/503 立刻减半并冷却
// （30s/60s/120s…封顶 5 分钟），冷却期内同主机的请求先等冷却结束。

using System.Collections.Concurrent;

namespace Starcloud.Core.Services;

internal static class ParallelGovernor
{
    private const int MinLimit = 1;
    private const int StartLimit = 4;
    private const int SuccessesPerStep = 8;
    private const int MaxCooldownSec = 300;

    private sealed class State
    {
        public int Limit;
        public int Successes;
        public int Throttles;
        public long CooldownUntilMs;
    }

    private static readonly ConcurrentDictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);

    private static State Get(string? host) =>
        States.GetOrAdd(host ?? "", _ => new State { Limit = StartFor(host) });

    /// <summary>
    /// 起始并发：镜像 4 条（试探，怕 429），官方 8 条。
    /// <para>官方 CDN 按连接限速，4 条要「连续成功 8 次 +1」爬到 16 要等好几轮，
    /// 一个大文件下完都爬不到 —— 实测那样每个文件只用了 4 条连接，白丢一半带宽。</para>
    /// </summary>
    private static int StartFor(string? host) =>
        host is not null && DownloadSourceRegistry.IsMojangHost(host)
            ? Math.Min(8, ChunkedFileDownloader.MaxWorkersPerMojangFile)
            : StartLimit;

    /// <summary>
    /// 该主机允许放开到多少条：Mojang 官方 CDN 16 条（实测连接数就是它的主要提速手段，
    /// 32 条也不限流），镜像仍是 8 条（网关按 IP 限流，8 条以上直接 429）。
    /// </summary>
    private static int MaxFor(string? host) =>
        host is not null && DownloadSourceRegistry.IsMojangHost(host)
            ? ChunkedFileDownloader.MaxWorkersPerMojangFile
            : ChunkedFileDownloader.MaxWorkersPerFile;

    /// <summary>当前推荐的并发连接数（1..该主机上限）</summary>
    public static int LimitFor(string? host) =>
        Math.Min(MaxFor(host), Volatile.Read(ref Get(host).Limit));

    /// <summary>一次成功：连续成功够多就逐步放开，直到该主机上限</summary>
    public static void ReportSuccess(string? host)
    {
        var s = Get(host);
        var max = MaxFor(host);
        if (Volatile.Read(ref s.Limit) >= max) return;
        if (Interlocked.Increment(ref s.Successes) < SuccessesPerStep) return;
        lock (s)
        {
            s.Successes = 0;
            if (s.Limit < max) s.Limit++;
        }
    }

    /// <summary>被限流（429/503）：并发减半 + 指数冷却（30s 起，封顶 5 分钟）</summary>
    public static void ReportThrottled(string? host)
    {
        var s = Get(host);
        lock (s)
        {
            s.Limit = Math.Max(MinLimit, s.Limit / 2);
            s.Successes = 0;
            s.Throttles = Math.Min(s.Throttles + 1, 4);
            var sec = Math.Min(30 * (1 << (s.Throttles - 1)), MaxCooldownSec);
            s.CooldownUntilMs = Environment.TickCount64 + sec * 1000L;
        }
    }

    /// <summary>还要等多久才适合再打这个主机（Zero = 现在就可以）</summary>
    public static TimeSpan CooldownFor(string? host)
    {
        var left = Volatile.Read(ref Get(host).CooldownUntilMs) - Environment.TickCount64;
        return left > 0 ? TimeSpan.FromMilliseconds(left) : TimeSpan.Zero;
    }

    /// <summary>
    /// 服务端给了 Retry-After：按它说的等，比我们自己猜的指数冷却准
    /// （参照 PrismLauncher 的 Request::downloadError）。
    /// </summary>
    public static void ReportRetryAfter(string? host, TimeSpan wait)
    {
        var s = Get(host);
        lock (s)
        {
            // 服务端说的时长优先，但不短于我们自己那套冷却
            var own = TimeSpan.FromSeconds(Math.Min(30 * (1 << Math.Max(0, s.Throttles - 1)), MaxCooldownSec));
            var effective = wait > own ? wait : own;
            s.CooldownUntilMs = Environment.TickCount64 + (long)effective.TotalMilliseconds;
        }
    }

    /// <summary>测试用：清空全部记忆</summary>
    internal static void ResetForTests() => States.Clear();
}
