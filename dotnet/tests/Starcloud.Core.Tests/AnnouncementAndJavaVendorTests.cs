// 云端公告解析（只取文本字段）与 Java 发行版选包逻辑。

using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class AnnouncementAndJavaVendorTests
{
    [Fact]
    public void 公告兼容items与直接数组两种形态()
    {
        const string items = """
            {"items":[{"id":"a1","title":"维护通知","body_md":"# 今晚维护\n- 22:00 开始","published_at":"2026-10-24T10:00:00Z","pinned":true}]}
            """;
        var list = AnnouncementService.Parse(items);
        var only = Assert.Single(list);
        Assert.Equal("a1", only.Id);
        Assert.Equal("维护通知", only.Title);
        Assert.Contains("# 今晚维护", only.Body);
        Assert.True(only.Pinned);

        var array = AnnouncementService.Parse("""[{"id":"b","title":"T","body_md":"body"}]""");
        Assert.Single(array);
    }

    [Fact]
    public void 公告缺字段或坏JSON时忽略()
    {
        Assert.Empty(AnnouncementService.Parse("not json"));
        Assert.Empty(AnnouncementService.Parse("""{"items":[{"id":"x","title":"no body"}]}"""));
        Assert.Empty(AnnouncementService.Parse("""{"items":[]}"""));
    }

    [Fact]
    public void 未读挑选置顶优先且跳过已读()
    {
        var items = new List<Announcement>
        {
            new("a", "普通", "x"),
            new("b", "置顶", "y", Pinned: true),
        };
        Assert.Equal("b", AnnouncementService.PickUnread(items, [])!.Id);
        Assert.Equal("a", AnnouncementService.PickUnread(items, ["b"])!.Id);
        Assert.Null(AnnouncementService.PickUnread(items, ["a", "b"]));
    }

    [Fact]
    public void 龙井资产挑选按平台后缀与架构()
    {
        const string release = """
        {"tag_name":"dragonwell-21.0.6","assets":[
          {"name":"Alibaba_Dragonwell_Standard_21.0.6_x64_linux.tar.gz","browser_download_url":"https://example/linux.tgz"},
          {"name":"Alibaba_Dragonwell_Standard_21.0.6_x64_windows.zip","browser_download_url":"https://example/win.zip"},
          {"name":"source.zip","browser_download_url":"https://example/src.zip"}
        ]}
        """;
        using var doc = JsonDocument.Parse(release);
        var url = JavaDistributionsService.PickAsset(doc.RootElement);
        Assert.NotNull(url);
        Assert.Contains(PlatformInfo.IsWindows ? "win.zip" : "linux.tgz", url);
    }

    [Fact]
    public void 龙井无匹配资产时返回null()
    {
        using var doc = JsonDocument.Parse("""{"assets":[{"name":"readme.txt","browser_download_url":"https://example/readme"}]}""");
        Assert.Null(JavaDistributionsService.PickAsset(doc.RootElement));
    }

    [Fact]
    public void Temurin版本列表只有LTS才标LTS()
    {
        const string json = """
        {"available_releases":[8,11,17,21,25,26,27],"available_lts_releases":[8,11,17,21,25]}
        """;
        var list = JavaDistributionsService.ParseTemurinReleases(json);
        Assert.Equal(new[] { 27, 26, 25, 21, 17, 11, 8 }, list.Select(r => r.Major));
        Assert.Equal("Temurin 25（LTS）", list.First(r => r.Major == 25).Display);
        Assert.Equal("Temurin 27", list.First(r => r.Major == 27).Display);
    }

    [Fact]
    public void Temurin响应缺LTS字段时不崩且不误标()
    {
        var list = JavaDistributionsService.ParseTemurinReleases("""{"available_releases":[17,21]}""");
        Assert.Equal(2, list.Count);
        Assert.All(list, r => Assert.DoesNotContain("LTS", r.Display));
    }
}
