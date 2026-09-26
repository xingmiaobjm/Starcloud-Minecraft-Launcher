// 版本号过滤守卫（用户报的「一让推荐东西就出错」的根因）：
//   实例名（Create Aero：New Horizons）以前被当成 Minecraft 版本号塞进 Modrinth 的 facets，
//   服务端直接 400 → 本地推荐整条挂掉，界面只显示「出错了：...400 Bad Request」。
//   现在发请求前统一过滤；实例 → 原版版本号也走 inheritsFrom 解析。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class GameVersionFilterTests
{
    [Theory]
    [InlineData("1.21.1")]
    [InlineData("1.21")]
    [InlineData("1.7.10")]
    [InlineData("1.21.1-pre2")]
    [InlineData("1.20.5-rc1")]
    [InlineData("24w14a")]
    [InlineData(" 1.21.1 ")]
    public void 官方版本号认得出(string value) => Assert.True(GameVersionFilter.IsValid(value));

    [Theory]
    [InlineData("Create Aero：New Horizons")]
    [InlineData("1.20.1-优化")]
    [InlineData("我的整合包")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 实例名与空值不会被当版本号(string? value) => Assert.False(GameVersionFilter.IsValid(value));

    [Fact]
    public void 不合法时返回null让调用方省略筛选()
    {
        Assert.Null(GameVersionFilter.OrNull("Create Aero：New Horizons"));
        Assert.Equal("1.21.1", GameVersionFilter.OrNull(" 1.21.1 "));
    }

    // ── 实例 → 原版版本号 ──

    [Fact]
    public void 加载器实例沿inheritsFrom取到原版号()
    {
        using var dotMc = new TempDotMc();
        dotMc.Version("fabric-loader-0.15.0-1.21.1", """{"id":"fabric-loader-0.15.0-1.21.1","inheritsFrom":"1.21.1"}""");
        dotMc.Version("1.21.1", """{"id":"1.21.1"}""");

        Assert.Equal("1.21.1", InstanceVersion.BaseGameVersion(dotMc.Path, "fabric-loader-0.15.0-1.21.1"));
    }

    [Fact]
    public void 被改名的原版实例用版本JSON里的id()
    {
        using var dotMc = new TempDotMc();
        dotMc.Version("Create Aero：New Horizons", """{"id":"1.21.1"}""");

        Assert.Equal("1.21.1", InstanceVersion.BaseGameVersion(dotMc.Path, "Create Aero：New Horizons"));
    }

    [Fact]
    public void 认不出原版号时返回null而不是实例名()
    {
        using var dotMc = new TempDotMc();
        dotMc.Version("我的整合包", """{"id":"我的整合包"}""");

        Assert.Null(InstanceVersion.BaseGameVersion(dotMc.Path, "我的整合包"));
    }

    [Fact]
    public void 改名时留下的原版号能读回来()
    {
        // 整合包实例没有 inheritsFrom，改名又覆盖了 id：
        // 只有改名时顺手记下的 starcloudBaseVersion 能救回来。
        using var dotMc = new TempDotMc();
        dotMc.Version("Create Aero：New Horizons",
            """{"id":"Create Aero：New Horizons","starcloudBaseVersion":"1.21.1"}""");

        Assert.Equal("1.21.1", InstanceVersion.BaseGameVersion(dotMc.Path, "Create Aero：New Horizons"));
    }

    [Fact]
    public void 目录名本身就是版本号时直接可用()
    {
        using var dotMc = new TempDotMc();

        Assert.Equal("1.20.1", InstanceVersion.BaseGameVersion(dotMc.Path, "1.20.1"));
    }

    private sealed class TempDotMc : IDisposable
    {
        public TempDotMc()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "sc-dotmc-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Version(string id, string json)
        {
            var dir = System.IO.Path.Combine(Path, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, id + ".json"), json);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
