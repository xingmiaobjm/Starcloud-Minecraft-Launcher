// 版本行元数据读取测试：类型 / 加载器判定、图标文件名、上次启动与大小格式化
// （逻辑移植自桌面端 VersionRowHelpers，用例与其行为逐条对齐）

using Starcloud.Android.Core.Metadata;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class VersionMetaReaderTests : IDisposable
{
    private readonly string _root;

    public VersionMetaReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sc-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 清理失败不影响结果 */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>写入一个版本 JSON（versions/&lt;id&gt;/&lt;id&gt;.json）</summary>
    private void WriteVersion(string id, string json)
    {
        var dir = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), json);
    }

    [Fact]
    public void 缺失版本文件时按正式版兜底()
    {
        Assert.Equal("正式版", VersionMetaReader.DetectKind(_root, "1.21.4"));
        Assert.Equal("natural_grass_block.png", VersionMetaReader.IconFileOf(_root, "1.21.4"));
    }

    [Fact]
    public void 损坏版本JSON按正式版兜底()
    {
        WriteVersion("broken", "{ not json !!!");
        Assert.Equal("正式版", VersionMetaReader.DetectKind(_root, "broken"));
        Assert.Equal("natural_grass_block.png", VersionMetaReader.IconFileOf(_root, "broken"));
    }

    [Theory]
    [InlineData("release", "正式版", "natural_grass_block.png")]
    [InlineData("snapshot", "快照版", "block_of_amethyst.png")]
    [InlineData("old_beta", "Beta 版", "masonry_cobblestone.png")]
    [InlineData("old_alpha", "Alpha 版", "natural_dirt.png")]
    public void 按版本类型给出标签与图标(string type, string expectedKind, string expectedIcon)
    {
        WriteVersion("v1", $$"""{"type":"{{type}}"}""");
        Assert.Equal(expectedKind, VersionMetaReader.DetectKind(_root, "v1"));
        Assert.Equal(expectedIcon, VersionMetaReader.IconFileOf(_root, "v1"));
    }

    [Theory]
    [InlineData("neoforge", "NeoForge", "neoforge.png")]
    [InlineData("forge", "Forge", "forge.png")]
    [InlineData("fabric", "Fabric", "fabric.png")]
    [InlineData("quilt", "Quilt", "quilt.png")]
    public void 按版本JSON加载器痕迹给出标签与图标(string loader, string expectedKind, string expectedIcon)
    {
        // JSON 痕迹优先于 id：id 与加载器无关的自建实例也能识别
        WriteVersion("我的整合包", $$"""{"loader":"{{loader}}"}""");
        Assert.Equal(expectedKind, VersionMetaReader.DetectKind(_root, "我的整合包"));
        Assert.Equal(expectedIcon, VersionMetaReader.IconFileOf(_root, "我的整合包"));
    }

    [Fact]
    public void 版本ID含加载器关键字时兜底识别()
    {
        // 无 JSON 文件，仅靠 id 关键字
        Assert.Equal("Fabric", VersionMetaReader.DetectKind(_root, "1.20.1-fabric"));
    }

    [Fact]
    public void 加载器判定顺序敏感_neoforge优先于forge()
    {
        // id 同时含两者子串（neoforge 包含 forge），必须判为 NeoForge
        WriteVersion("1.21.1-neoforge", "{}");
        Assert.Equal("NeoForge", VersionMetaReader.DetectKind(_root, "1.21.1-neoforge"));
        Assert.Equal("neoforge.png", VersionMetaReader.IconFileOf(_root, "1.21.1-neoforge"));
    }

    [Fact]
    public void 从未启动的文案()
    {
        Assert.Equal("从未启动", VersionMetaReader.FormatLastPlayed(null));
    }

    [Fact]
    public void 今天启动的文案带时分()
    {
        var text = VersionMetaReader.FormatLastPlayed(DateTime.Now);
        Assert.StartsWith("今天 ", text);
    }

    [Fact]
    public void 昨天启动的文案带时分()
    {
        var text = VersionMetaReader.FormatLastPlayed(DateTime.Now.AddDays(-1));
        Assert.StartsWith("昨天 ", text);
    }

    [Fact]
    public void 一月前的文案按三十天折算()
    {
        Assert.Equal("1 个月前", VersionMetaReader.FormatLastPlayed(DateTime.Now.AddDays(-45)));
        Assert.Equal("3 个月前", VersionMetaReader.FormatLastPlayed(DateTime.Now.AddDays(-90)));
    }

    [Fact]
    public void 超过一年的文案落到日期()
    {
        var t = DateTime.Now.AddDays(-400);
        Assert.Equal($"{t:yyyy-MM-dd}", VersionMetaReader.FormatLastPlayed(t));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void 字节数格式化(long bytes, string expected)
    {
        Assert.Equal(expected, VersionMetaReader.FormatSize(bytes));
    }
}
