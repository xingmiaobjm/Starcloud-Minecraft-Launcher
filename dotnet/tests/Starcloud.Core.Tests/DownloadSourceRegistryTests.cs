// DownloadSourceRegistry 单元测试：域名改写 / 源 ID 解析 / 健康度与竞速池排序

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class DownloadSourceRegistryTests
{
    /// <summary>降级阈值：跟随实现，避免测试里散落魔法数字</summary>
    private const int DegradeAfterFailures = DownloadSourceRegistry.DegradeAfterFailures;

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
            [SourceKind.Bmclapi, SourceKind.Fastmirror, SourceKind.Rainyun, SourceKind.Official],
            pool);
    }

    [Fact]
    public void RacingPool_配置自定义镜像后纳入池中且仍在官方之前()
    {
        var pool = Registry(customMirror: "https://mirror.example.com").RacingPool();

        Assert.Equal(
            [SourceKind.Bmclapi, SourceKind.Fastmirror, SourceKind.Rainyun, SourceKind.Custom,
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
            [SourceKind.Fastmirror, SourceKind.Rainyun, SourceKind.Bmclapi, SourceKind.Official],
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
    public void MarkNoRangeSupport_该源退出分片池()
    {
        var reg = Registry();
        reg.MarkNoRangeSupport(SourceKind.Rainyun);

        Assert.Equal(
            [SourceKind.Bmclapi, SourceKind.Fastmirror, SourceKind.Official],
            reg.RacingPool());
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
}
