// DownloadSourceRegistry 单元测试：域名改写 / 源 ID 解析 / 健康度与竞速池排序

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class DownloadSourceRegistryTests
{
    /// <summary>降级阈值：跟随实现，避免测试里散落魔法数字</summary>
    private const int DegradeAfterFailures = DownloadSourceRegistry.DegradeAfterFailures;

    /// <summary>「无视 Range」判定阈值：同样跟随实现</summary>
    private const int RangeIgnoredThreshold = DownloadSourceRegistry.RangeIgnoredThreshold;

    private static DownloadSourceRegistry Registry(string source = "auto", string? customMirror = "") =>
        new(() => new LauncherSettings { DownloadSource = source, DownloadCustomMirrorUrl = customMirror });

    // ── 1. 官方恒等 ────────────────────────────────────────────

    [Theory]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://piston-data.mojang.com/v1/objects/ab/cd/client.jar")]
    [InlineData("https://launchermeta.mojang.com/mc/game/version_manifest.json")]
    [InlineData("https://resources.download.minecraft.net/ab/abcdef")]
    [InlineData("https://libraries.minecraft.net/com/example/lib/1.0/lib-1.0.jar")]
    [InlineData("https://api.modrinth.com/v2/project/sodium")]
    public void Official_原样返回(string url) =>
        Assert.Equal(url, DownloadSourceRegistry.Rewrite(url, SourceKind.Official, null));

    // ── 2. BMCLAPI / RainYun：单一镜像根 ───────────────────────

    [Theory]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
                "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://piston-data.mojang.com/v1/objects/ab/cd/client.jar",
                "https://bmclapi2.bangbang93.com/v1/objects/ab/cd/client.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/abcdef",
                "https://bmclapi2.bangbang93.com/assets/ab/abcdef")]
    [InlineData("https://libraries.minecraft.net/com/example/lib/1.0/lib-1.0.jar",
                "https://bmclapi2.bangbang93.com/maven/com/example/lib/1.0/lib-1.0.jar")]
    public void Bmclapi_按单一镜像根改写(string url, string expected) =>
        Assert.Equal(expected, DownloadSourceRegistry.Rewrite(url, SourceKind.Bmclapi, null));

    [Theory]
    [InlineData("https://resources.download.minecraft.net/ab/abcdef",
                "https://mirrors.rainyun.com/assets/ab/abcdef")]
    [InlineData("https://libraries.minecraft.net/com/example/lib/1.0/lib-1.0.jar",
                "https://mirrors.rainyun.com/maven/com/example/lib/1.0/lib-1.0.jar")]
    public void Rainyun_按单一镜像根改写(string url, string expected) =>
        Assert.Equal(expected, DownloadSourceRegistry.Rewrite(url, SourceKind.Rainyun, null));

    // ── 3. FastMinecraftMirror：按服务拆子域 ───────────────────

    [Theory]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
                "https://launchermeta.fastmcmirror.org/mc/game/version_manifest_v2.json")]
    [InlineData("https://launchermeta.mojang.com/mc/game/version_manifest.json",
                "https://launchermeta.fastmcmirror.org/mc/game/version_manifest.json")]
    [InlineData("https://piston-data.mojang.com/v1/objects/ab/cd/client.jar",
                "https://launcher.fastmcmirror.org/v1/objects/ab/cd/client.jar")]
    [InlineData("https://launcher.mojang.com/v1/objects/ab/cd/client.jar",
                "https://launcher.fastmcmirror.org/v1/objects/ab/cd/client.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/abcdef",
                "https://resources.fastmcmirror.org/ab/abcdef")]
    [InlineData("https://libraries.minecraft.net/com/example/lib/1.0/lib-1.0.jar",
                "https://libraries.fastmcmirror.org/com/example/lib/1.0/lib-1.0.jar")]
    [InlineData("https://libraries.mojang.com/com/example/lib/1.0/lib-1.0.jar",
                "https://libraries.fastmcmirror.org/com/example/lib/1.0/lib-1.0.jar")]
    public void Fastmirror_按子域改写(string url, string expected) =>
        Assert.Equal(expected, DownloadSourceRegistry.Rewrite(url, SourceKind.Fastmirror, null));

    // ── 4. 非 Mojang 域名：所有源都原样放行 ─────────────────────

    [Theory]
    [InlineData(SourceKind.Official)]
    [InlineData(SourceKind.Bmclapi)]
    [InlineData(SourceKind.Fastmirror)]
    [InlineData(SourceKind.Rainyun)]
    [InlineData(SourceKind.Custom)]
    public void 非Mojang域名_所有源放行(SourceKind kind)
    {
        const string url = "https://api.modrinth.com/v2/project/sodium/version";
        Assert.Equal(url, DownloadSourceRegistry.Rewrite(url, kind, "https://mirror.example.com"));
    }

    // ── 5. 自定义镜像：有地址改写 / 无地址放行 ─────────────────

    [Theory]
    [InlineData("mirror.example.com", "https://mirror.example.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://mirror.example.com", "https://mirror.example.com/mc/game/version_manifest_v2.json")]
    [InlineData("http://mirror.example.com/", "https://mirror.example.com/mc/game/version_manifest_v2.json")]
    [InlineData("  mirror.example.com//  ", "https://mirror.example.com/mc/game/version_manifest_v2.json")]
    public void Custom_有地址时改写(string mirror, string expected)
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        Assert.Equal(expected, DownloadSourceRegistry.Rewrite(url, SourceKind.Custom, mirror));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    public void Custom_无地址时放行(string? mirror)
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        Assert.Equal(url, DownloadSourceRegistry.Rewrite(url, SourceKind.Custom, mirror));
    }

    // ── 6. 源 ID 解析 ──────────────────────────────────────────

    [Theory]
    [InlineData(SourceKind.Official)]
    [InlineData(SourceKind.Bmclapi)]
    [InlineData(SourceKind.Fastmirror)]
    [InlineData(SourceKind.Rainyun)]
    [InlineData(SourceKind.Custom)]
    public void TryFromId_往返一致(SourceKind kind)
    {
        Assert.True(DownloadSourceRegistry.TryFromId(DownloadSourceRegistry.Id(kind), out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData("  BMCLAPI ")]
    [InlineData("FastMirror")]
    [InlineData("RainYun")]
    public void TryFromId_大小写与空白无关(string id)
    {
        Assert.True(DownloadSourceRegistry.TryFromId(id, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("unknown-mirror")]
    public void TryFromId_未知ID返回false(string? id) =>
        Assert.False(DownloadSourceRegistry.TryFromId(id, out _));

    // ── 7. 健康度与竞速池排序 ──────────────────────────────────

    [Fact]
    public void RacingPool_默认顺序为镜像在前官方最后()
    {
        var pool = Registry().RacingPool();

        Assert.Equal(
            [SourceKind.Bmclapi, SourceKind.Fastmirror, SourceKind.Official],
            pool);
    }

    [Fact]
    public void RacingPool_配置自定义镜像后纳入池中且仍在官方之前()
    {
        var pool = Registry(customMirror: "https://mirror.example.com").RacingPool();

        Assert.Equal(
            [SourceKind.Bmclapi, SourceKind.Fastmirror, SourceKind.Custom,
             SourceKind.Official],
            pool);
    }

    [Fact]
    public void RacingPool_降级源排在健康源之后且官方仍最后()
    {
        var reg = Registry();
        Assert.Equal(SourceKind.Bmclapi, reg.Preferred());

        for (var i = 0; i < DegradeAfterFailures; i++) reg.RecordFailure(SourceKind.Bmclapi);

        Assert.Equal(
            [SourceKind.Fastmirror, SourceKind.Bmclapi, SourceKind.Official],
            reg.RacingPool());
    }

    [Fact]
    public void RecordFailure_未达阈值不降级()
    {
        var reg = Registry();
        for (var i = 0; i < DegradeAfterFailures - 1; i++) reg.RecordFailure(SourceKind.Bmclapi);

        Assert.Equal(SourceKind.Bmclapi, reg.RacingPool()[0]);
    }

    [Fact]
    public void RecordSuccess_重置失败计数并恢复降级()
    {
        var reg = Registry();
        for (var i = 0; i < DegradeAfterFailures; i++) reg.RecordFailure(SourceKind.Bmclapi);
        Assert.NotEqual(SourceKind.Bmclapi, reg.RacingPool()[0]);

        reg.RecordSuccess(SourceKind.Bmclapi);

        Assert.Equal(SourceKind.Bmclapi, reg.RacingPool()[0]);
    }

    [Fact]
    public void RecordSuccess_重置计数后需重新累计失败才降级()
    {
        var reg = Registry();
        for (var i = 0; i < DegradeAfterFailures - 1; i++) reg.RecordFailure(SourceKind.Bmclapi);
        reg.RecordSuccess(SourceKind.Bmclapi);
        for (var i = 0; i < DegradeAfterFailures - 1; i++) reg.RecordFailure(SourceKind.Bmclapi);

        Assert.Equal(SourceKind.Bmclapi, reg.RacingPool()[0]);
    }

    [Fact]
    public void 某通道连续无视Range后该源只在该通道不能分片()
    {
        var reg = Registry();
        var maven = DownloadSourceRegistry.ChannelOf(
            "https://libraries.minecraft.net/com/example/lib/1.0/lib-1.0.jar");
        var client = DownloadSourceRegistry.ChannelOf(
            "https://piston-data.mojang.com/v1/objects/aa/client.jar");

        // 一次不算数（镜像 302 到随机节点，个别节点不认 Range 很正常）
        reg.RecordRangeIgnored(SourceKind.Bmclapi, maven);
        Assert.False(reg.NoRangeSupport(SourceKind.Bmclapi, maven));

        for (var i = 0; i < RangeIgnoredThreshold - 1; i++)
            reg.RecordRangeIgnored(SourceKind.Bmclapi, maven);
        Assert.True(reg.NoRangeSupport(SourceKind.Bmclapi, maven));

        // 关键：同一进程里 BMCLAPI 给客户端 jar 分片的本事不受影响
        Assert.False(reg.NoRangeSupport(SourceKind.Bmclapi, client));

        // 命中 206 即清零（节点抖动不该永久罚一个源）
        reg.RecordRangeHonored(SourceKind.Bmclapi, maven);
        Assert.False(reg.NoRangeSupport(SourceKind.Bmclapi, maven));
    }

    [Fact]
    public void ResetHealth_清空降级状态()
    {
        var reg = Registry();
        for (var i = 0; i < DegradeAfterFailures; i++) reg.RecordFailure(SourceKind.Fastmirror);
        Assert.NotEqual(SourceKind.Fastmirror, reg.RacingPool()[0]);

        reg.ResetHealth();

        Assert.Equal(SourceKind.Bmclapi, reg.RacingPool()[0]);
    }

    // ── 8. 指定单一源 ──────────────────────────────────────────

    [Theory]
    [InlineData("official", SourceKind.Official)]
    [InlineData("bmclapi", SourceKind.Bmclapi)]
    [InlineData("fastmirror", SourceKind.Fastmirror)]
    [InlineData("rainyun", SourceKind.Rainyun)]
    [InlineData("custom", SourceKind.Custom)]
    public void 指定单一源时池内只有它(string id, SourceKind expected)
    {
        Assert.Equal([expected], Registry(id).RacingPool());
    }

    [Fact]
    public void 指定单一源时不受降级影响()
    {
        var reg = Registry("bmclapi");
        for (var i = 0; i < 10; i++) reg.RecordFailure(SourceKind.Bmclapi);

        Assert.Equal([SourceKind.Bmclapi], reg.RacingPool());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("unknown-mirror")]
    public void 自动或未知ID时回落多源竞速(string? id) =>
        Assert.Equal(SourceKind.Bmclapi, Registry(id!).RacingPool()[0]);

    // ── 9. 出口改写（RewritePreferred） ────────────────────────

    [Fact]
    public void RewritePreferred_自动模式走首个镜像()
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            Registry().RewritePreferred(url));
    }

    [Fact]
    public void RewritePreferred_仅官方时不改写()
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        Assert.Equal(url, Registry("official").RewritePreferred(url));
    }

    // ── 10. Mojang 域名判定（分片启用门槛） ────────────────────

    [Theory]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", true)]
    [InlineData("https://piston-data.mojang.com/v1/objects/ab/cd/client.jar", true)]
    [InlineData("https://resources.download.minecraft.net/ab/abcdef", true)]
    [InlineData("https://libraries.minecraft.net/com/example/lib/1.0/lib-1.0.jar", true)]
    [InlineData("https://libraries.mojang.com/com/example/lib/1.0/lib-1.0.jar", true)]
    [InlineData("https://api.modrinth.com/v2/project/sodium", false)]
    [InlineData("https://meta.fabricmc.net/v2/versions/loader", false)]
    [InlineData("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", false)]
    [InlineData("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", false)]
    [InlineData("", false)]
    public void IsMojangUrl_只认官方域名(string url, bool expected) =>
        Assert.Equal(expected, DownloadSourceRegistry.IsMojangUrl(url));

    // ── 11. 源显示名 ──────────────────────────────────────────

    [Fact]
    public void Name_各源均有显示名()
    {
        foreach (var kind in Enum.GetValues<SourceKind>())
            Assert.False(string.IsNullOrWhiteSpace(DownloadSourceRegistry.Name(kind)));
    }

    // ── 12. 按 URL 取竞速池（非 Mojang 域名不伪造同址"源"） ─────

    [Fact]
    public void RacingPoolFor_Mojang域名给出完整镜像池()
    {
        const string url = "https://piston-data.mojang.com/v1/objects/ab/cd/client.jar";

        Assert.Equal(Registry().RacingPool(), Registry().RacingPoolFor(url));
    }

    [Fact]
    public void RacingPoolFor_非Mojang域名只留一项()
    {
        // Modrinth / CurseForge 这类域名所有源都改写为同一 URL：池里塞 5 个只会重复打同一台 CDN，
        // 还会把无关镜像记成不健康。并行提速由分片 worker 提供，不靠重复造源。
        const string url = "https://cdn.modrinth.com/data/abc/versions/1/sodium.jar";

        Assert.Equal([SourceKind.Official], Registry().RacingPoolFor(url));
        // 用户显式指定了单一源：池内就是那一个（改写对它同样是空操作，URL 不变）
        Assert.Equal([SourceKind.Bmclapi], Registry("bmclapi").RacingPoolFor(url));
    }

    // ── 13. 主机级 Range 能力记忆 ──────────────────────────────

    [Fact]
    public void RangeSupport_默认未知_记录后按主机读回()
    {
        var reg = Registry();

        Assert.Null(reg.RangeSupport("cdn.modrinth.com"));

        reg.MarkRangeSupport("cdn.modrinth.com", true);
        Assert.True(reg.RangeSupport("cdn.modrinth.com"));

        reg.MarkRangeSupport("cdn.modrinth.com", false); // 后来发现不支持：以最新为准
        Assert.False(reg.RangeSupport("cdn.modrinth.com"));
        Assert.Null(reg.RangeSupport("edge.forgecdn.net")); // 别的域名不受影响
    }

    [Fact]
    public void IsMojangHost_与URL判定同口径()
    {
        Assert.True(DownloadSourceRegistry.IsMojangHost("piston-data.mojang.com"));
        Assert.True(DownloadSourceRegistry.IsMojangHost("PISTON-DATA.MOJANG.COM"));
        Assert.False(DownloadSourceRegistry.IsMojangHost("cdn.modrinth.com"));
        Assert.False(DownloadSourceRegistry.IsMojangHost(""));
    }

    // ── 14. 主机级并行度调节器（实测 BMCLAPI 8 条并发会被 429） ──

    [Fact]
    public void ParallelGovernor_默认保守_连续成功后才放开()
    {
        ParallelGovernor.ResetForTests();
        const string host = "cdn.example.com";

        var start = ParallelGovernor.LimitFor(host);
        Assert.InRange(start, 1, ChunkedFileDownloader.MaxWorkersPerFile);

        for (var i = 0; i < 64; i++) ParallelGovernor.ReportSuccess(host);

        Assert.Equal(ChunkedFileDownloader.MaxWorkersPerFile, ParallelGovernor.LimitFor(host));
    }

    [Fact]
    public void ParallelGovernor_被限流立刻减半并进入冷却()
    {
        ParallelGovernor.ResetForTests();
        const string host = "bmclapi2.bangbang93.com";
        for (var i = 0; i < 64; i++) ParallelGovernor.ReportSuccess(host);
        var peak = ParallelGovernor.LimitFor(host);

        ParallelGovernor.ReportThrottled(host);

        Assert.Equal(Math.Max(1, peak / 2), ParallelGovernor.LimitFor(host));
        Assert.True(ParallelGovernor.CooldownFor(host) > TimeSpan.Zero, "限流后必须有冷却时间");
    }

    [Fact]
    public void ParallelGovernor_冷却与并发度按主机隔离()
    {
        ParallelGovernor.ResetForTests();
        const string throttled = "slow.example.com";
        const string healthy = "fast.example.com";

        for (var i = 0; i < 64; i++) ParallelGovernor.ReportSuccess(healthy);
        ParallelGovernor.ReportThrottled(throttled);

        Assert.True(ParallelGovernor.CooldownFor(throttled) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, ParallelGovernor.CooldownFor(healthy));
        Assert.True(ParallelGovernor.LimitFor(healthy) > ParallelGovernor.LimitFor(throttled));
    }

    [Fact]
    public void ParallelGovernor_并发度不会掉到0()
    {
        ParallelGovernor.ResetForTests();
        const string host = "flaky.example.com";

        for (var i = 0; i < 8; i++) ParallelGovernor.ReportThrottled(host);

        Assert.Equal(1, ParallelGovernor.LimitFor(host));
    }
}
