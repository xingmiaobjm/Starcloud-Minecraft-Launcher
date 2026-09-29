// CurseForge 接入链路的离线测试：接口分层回退、密钥解析、CDN 密钥与镜像兜底、MCIM 简介翻译解析。
//
// 全部不打网络：只覆盖纯函数（分层顺序 / URL 改写 / JSON 解析 / 密钥优先级），
// 真实请求的行为由手工验证（设置页「CurseForge API Key」留空 → 走内置或镜像）。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class CurseForgeSupportTests
{
    // ── 接口分层：官方 → MCIM 镜像 → curse.tools 代理 ──────────────────────

    [Fact]
    public void 无密钥时_官方层不可用_从镜像开始()
    {
        var ladder = CurseForgeService.Ladder(hasKey: false, preferred: null);
        Assert.Equal(new[] { CurseForgeService.McimApiBase, CurseForgeService.ProxyApiBase }, ladder);
    }

    [Fact]
    public void 有密钥时_官方在最前_镜像与代理依次兜底()
    {
        var ladder = CurseForgeService.Ladder(hasKey: true, preferred: null);
        Assert.Equal(
            new[]
            {
                CurseForgeService.OfficialApiBase,
                CurseForgeService.McimApiBase,
                CurseForgeService.ProxyApiBase,
            },
            ladder);
    }

    [Fact]
    public void 上次成功的层被提到最前且不重复()
    {
        var ladder = CurseForgeService.Ladder(hasKey: true, preferred: CurseForgeService.McimApiBase);
        Assert.Equal(CurseForgeService.McimApiBase, ladder[0]);
        Assert.Equal(3, ladder.Count);
        Assert.Single(ladder, b => b == CurseForgeService.McimApiBase);
    }

    [Fact]
    public void 层名用于日志与设置页展示()
    {
        Assert.Equal("CurseForge 官方 API", CurseForgeService.EndpointName(CurseForgeService.OfficialApiBase));
        Assert.Equal("MCIM 镜像", CurseForgeService.EndpointName(CurseForgeService.McimApiBase));
        Assert.Equal("curse.tools 代理", CurseForgeService.EndpointName(CurseForgeService.ProxyApiBase));
    }

    // ── 密钥：自备优先，留空用内置 ────────────────────────────────────────

    [Fact]
    public void 自备密钥优先且去掉首尾空白()
    {
        var settings = new LauncherSettings { CurseForgeApiKey = "  own-key  " };
        Assert.Equal("own-key", CurseForgeKey.Effective(settings));
    }

    [Fact]
    public void 留空时回落到内置密钥_两者都没有则为null()
    {
        var settings = new LauncherSettings { CurseForgeApiKey = "   " };
        Assert.Equal(CurseForgeKey.HasBuiltIn ? CurseForgeKey.BuiltIn : null, CurseForgeKey.Effective(settings));
    }

    // ── CDN：密钥（handler）与镜像兜底 ────────────────────────────────────

    [Theory]
    [InlineData("https://edge.forgecdn.net/files/1234/56/ExampleMod.jar", true)]
    [InlineData("https://mediafilez.forgecdn.net/files/1234/56/ExampleMod.jar", true)]
    [InlineData("https://cdn.modrinth.com/data/a/versions/b/x.jar", false)]
    [InlineData("https://api.curseforge.com/v1/mods/238222", false)]
    public void 只认两台官方文件CDN(string url, bool expected) =>
        Assert.Equal(expected, CurseForgeCdn.IsCdnUrl(url));

    [Fact]
    public void 镜像兜底只换主机_路径不变()
    {
        var mirrored = CurseForgeCdn.Mirror("https://mediafilez.forgecdn.net/files/1234/56/ExampleMod.jar");
        Assert.Equal("https://mod.mcimirror.top/files/1234/56/ExampleMod.jar", mirrored);
    }

    [Fact]
    public void 非CDN直链不做镜像改写()
    {
        const string url = "https://cdn.modrinth.com/data/a/versions/b/x.jar";
        Assert.Equal(url, CurseForgeCdn.Mirror(url));
    }

    // ── MCIM 简介翻译 ────────────────────────────────────────────────────

    [Fact]
    public void 解析MCIM翻译响应()
    {
        const string json = """
        { "modId": 238222, "translated": "查看物品和配方", "original": "View Items and Recipes",
          "translated_at": "2025-02-02T10:01:52.805000" }
        """;
        Assert.Equal("查看物品和配方", ModSummaryTranslationService.ParseTranslated(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"translated\":\"   \"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void 空译文或非法响应返回null(string json) =>
        Assert.Null(ModSummaryTranslationService.ParseTranslated(json));
}
