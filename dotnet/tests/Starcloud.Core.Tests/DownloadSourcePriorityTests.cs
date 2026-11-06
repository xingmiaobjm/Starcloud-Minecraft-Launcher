using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>下载源重构：BMCLAPI 降到末位、SJTU / Starcloud S3 新源、改写正确性。</summary>
public class DownloadSourcePriorityTests
{
    private static DownloadSourceRegistry Registry(string source = "auto", string? custom = null) =>
        new(() => new LauncherSettings
        {
            DownloadSource = source,
            DownloadCustomMirrorUrl = custom,
        });

    [Fact]
    public void 自动模式BMCLAPI排在镜像池末位()
    {
        var pool = Registry().RacingPool();
        Assert.NotEqual(SourceKind.Bmclapi, pool[0]);   // 不再打头阵
        // 官方恒在池尾；BMCLAPI 是官方之前的最后一个（= 镜像里最低优先级）
        Assert.Equal(SourceKind.Official, pool[^1]);
        Assert.Equal(SourceKind.Bmclapi, pool[^2]);
        Assert.Contains(SourceKind.Sjtu, pool);
    }

    /// <summary>S3 只做极端情况兜底（用户定调 2026-11-02）：常规竞速里不该出现它。</summary>
    [Fact]
    public void 兜底源默认不参与竞速()
    {
        Assert.True(DownloadSourceRegistry.IsAuxiliary(SourceKind.Starcloud));
        Assert.DoesNotContain(SourceKind.Starcloud, Registry().RacingPool());

        // 用户显式锁定它时照常生效（那是明确选择，不是自动竞速）
        Assert.Equal([SourceKind.Starcloud], Registry("starcloud").RacingPool());
    }

    [Fact]
    public void 主源全部失败后兜底源自动上线()
    {
        var registry = Registry();
        Assert.DoesNotContain(SourceKind.Starcloud, registry.RacingPool());

        registry.UnlockAuxiliary();   // 整文件路径在「主源全试完仍失败」时调它

        Assert.Contains(SourceKind.Starcloud, registry.RacingPool());
        // 只是临时放行：换批次要重新判定
        registry.ResetAuxiliary();
        Assert.DoesNotContain(SourceKind.Starcloud, registry.RacingPool());
    }

    [Fact]
    public void 自动模式首选源不再是BMCLAPI()
    {
        Assert.NotEqual(SourceKind.Bmclapi, Registry().Preferred());
    }

    [Fact]
    public void 用户锁定单一源时不受降级影响()
    {
        var pool = Registry("bmclapi").RacingPool();
        Assert.Single(pool, SourceKind.Bmclapi);
    }

    [Fact]
    public void SJTU改写_meta与data走原样路径()
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        Assert.Equal("https://mirror.sjtu.edu.cn/minecraft/mc/game/version_manifest_v2.json",
            Registry().Rewrite(url, SourceKind.Sjtu));

        const string data = "https://piston-data.mojang.com/v1/objects/abc/client.jar";
        Assert.Equal("https://mirror.sjtu.edu.cn/minecraft/v1/objects/abc/client.jar",
            Registry().Rewrite(data, SourceKind.Sjtu));
    }

    [Fact]
    public void SJTU改写_assets与libraries()
    {
        const string asset = "https://resources.download.minecraft.net/aa/bb";
        Assert.Equal("https://mirror.sjtu.edu.cn/minecraft/assets/aa/bb",
            Registry().Rewrite(asset, SourceKind.Sjtu));

        const string lib = "https://libraries.minecraft.net/net/fabricmc/x.jar";
        Assert.Equal("https://mirror.sjtu.edu.cn/minecraft/libraries/net/fabricmc/x.jar",
            Registry().Rewrite(lib, SourceKind.Sjtu));
    }

    [Fact]
    public void StarcloudS3改写_BMCLAPI风格布局()
    {
        const string meta = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        Assert.Equal("https://starcloud.cn-nb1.rains3.com/mc/mc/game/version_manifest_v2.json",
            Registry().Rewrite(meta, SourceKind.Starcloud));

        const string asset = "https://resources.download.minecraft.net/aa/bb";
        Assert.Equal("https://starcloud.cn-nb1.rains3.com/mc/assets/aa/bb",
            Registry().Rewrite(asset, SourceKind.Starcloud));

        const string lib = "https://libraries.minecraft.net/net/fabricmc/x.jar";
        Assert.Equal("https://starcloud.cn-nb1.rains3.com/mc/maven/net/fabricmc/x.jar",
            Registry().Rewrite(lib, SourceKind.Starcloud));
    }

    [Fact]
    public void 非Mojang域名不改写()
    {
        const string url = "https://modrinth-api.example.com/x";
        Assert.Equal(url, Registry().Rewrite(url, SourceKind.Sjtu));
        Assert.Equal(url, Registry().Rewrite(url, SourceKind.Starcloud));
    }

    [Fact]
    public void S3改写_libraries_mojang_com同样走maven()
    {
        const string lib = "https://libraries.mojang.com/net/minecraft/x.jar";
        Assert.Equal("https://starcloud.cn-nb1.rains3.com/mc/maven/net/minecraft/x.jar",
            Registry().Rewrite(lib, SourceKind.Starcloud));
        Assert.Equal("https://bmclapi2.bangbang93.com/maven/net/minecraft/x.jar",
            Registry().Rewrite(lib, SourceKind.Bmclapi));
    }

    [Fact]
    public void 连发404触发短时避让但不降级()
    {
        var registry = Registry();
        // 窗口内 19 次：还没到阈值，不避让
        for (var i = 0; i < 19; i++) registry.RecordNotFound(SourceKind.Starcloud);
        Assert.False(registry.InNotFoundCooldown(SourceKind.Starcloud));
        Assert.False(registry.IsDegraded(SourceKind.Starcloud));   // 404 永远不进降级
        // 第 20 次：进入避让（排序靠后），但源仍在池里、也没被记为失败
        registry.RecordNotFound(SourceKind.Starcloud);
        Assert.True(registry.InNotFoundCooldown(SourceKind.Starcloud));
        Assert.False(registry.IsDegraded(SourceKind.Starcloud));
        // 兜底源本批次已放行时才会出现在池里
        registry.UnlockAuxiliary();
        Assert.Contains(SourceKind.Starcloud, registry.RacingPool());
    }

    [Fact]
    public void 避让中的S3排到其它镜像之后仍在官方之前()
    {
        var registry = Registry();
        for (var i = 0; i < 25; i++) registry.RecordNotFound(SourceKind.Starcloud);
        registry.UnlockAuxiliary();   // 兜底源已上线，才谈得上排序
        var pool = registry.RacingPool();
        Assert.Equal(SourceKind.Official, pool[^1]);              // 官方恒兜底
        Assert.Equal(SourceKind.Starcloud, pool[^2]);             // 避让源贴官方、其它镜像在前
        Assert.Equal(SourceKind.Fastmirror, pool[0]);             // 声明顺序不受影响
    }
}
