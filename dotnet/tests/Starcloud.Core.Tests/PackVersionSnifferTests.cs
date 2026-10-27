// 裸内容整合包的游戏版本推断：pack.toml / mod jar 元数据 / 文件名。

using System.IO.Compression;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class PackVersionSnifferTests : IDisposable
{
    private readonly List<string> _files = [];

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-sniff-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        _files.Add(path);
        return path;
    }

    /// <summary>造一个只含 mods/x.jar 的包，jar 内放指定元数据文件</summary>
    private string MakePackWithJar(string metaEntry, string metaContent, string jarName = "mods/x.jar")
    {
        var jarPath = Path.Combine(Path.GetTempPath(), "sc-jar-" + Guid.NewGuid().ToString("N")[..8] + ".jar");
        using (var fs = File.Create(jarPath))
        using (var jar = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = jar.CreateEntry(metaEntry, CompressionLevel.Fastest);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(metaContent);
        }
        try
        {
            var pack = Path.Combine(Path.GetTempPath(), "sc-pk-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
            using (var fs = File.Create(pack))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(jarPath, jarName, CompressionLevel.Fastest);
            }
            _files.Add(pack);
            return pack;
        }
        finally
        {
            try { File.Delete(jarPath); } catch { }
        }
    }

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void packwiz的pack_toml直接给出游戏版本与加载器()
    {
        var pack = MakeZip(("pack.toml", """
            name = "Example Pack"
            [versions]
            minecraft = "1.20.1"
            fabric = "0.15.11"
            """));
        var info = PackVersionSniffer.Sniff(pack, "");
        Assert.Equal("1.20.1", info.Minecraft);
        Assert.Equal("fabric", info.Loader);
        Assert.Equal("0.15.11", info.LoaderVersion);
        Assert.Equal("pack.toml", info.Source);
    }

    [Fact]
    public void fabric_mod_json的depends给出Minecraft版本()
    {
        var pack = MakePackWithJar("fabric.mod.json", """
            {"id":"x","version":"1.0.0","depends":{"minecraft":">=1.20 <1.21","fabricloader":">=0.14"}}
            """);
        var info = PackVersionSniffer.Sniff(pack, "");
        Assert.Equal("1.20", info.Minecraft);
        Assert.Equal("fabric", info.Loader);
    }

    [Fact]
    public void quilt_mod_json的数组形态也能识别()
    {
        var pack = MakePackWithJar("quilt.mod.json", """
            {"quilt_loader":{"id":"x","depends":[{"id":"minecraft","versions":"1.19.2"}]}}
            """);
        var info = PackVersionSniffer.Sniff(pack, "");
        Assert.Equal("1.19.2", info.Minecraft);
        Assert.Equal("quilt", info.Loader);
    }

    [Fact]
    public void forge的mods_toml从versionRange取下界()
    {
        var pack = MakePackWithJar("META-INF/mods.toml", """
            modLoader="javafml"
            loaderVersion="[47,)"

            [[dependencies.example]]
                modId="minecraft"
                mandatory=true
                versionRange="[1.20.1,1.21)"
            """);
        var info = PackVersionSniffer.Sniff(pack, "");
        Assert.Equal("1.20.1", info.Minecraft);
        Assert.Equal("forge", info.Loader);
    }

    [Fact]
    public void 元数据缺失时退回文件名投票且需两个一致()
    {
        var pack = Path.Combine(Path.GetTempPath(), "sc-name-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        using (var fs = File.Create(pack))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "mods/create-1.20.1-0.5.jar", "mods/jei-1.20.1-15.2.jar", "mods/other-1.19.2-1.jar" })
            {
                var e = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write("not a real jar");
            }
        }
        _files.Add(pack);

        var info = PackVersionSniffer.Sniff(pack, "");
        Assert.Equal("1.20.1", info.Minecraft);
        Assert.Equal("mod 元数据", info.Source);
    }

    [Fact]
    public void 无任何线索时返回空()
    {
        var pack = MakeZip(("config/readme.txt", "hello"));
        var info = PackVersionSniffer.Sniff(pack, "");
        Assert.False(info.Found);
    }

    [Theory]
    [InlineData("1.20.1", "1.20.1")]
    [InlineData(">=1.20 <1.21", "1.20")]
    [InlineData("[1.20.1,1.21)", "1.20.1")]
    [InlineData("*", null)]
    [InlineData("", null)]
    [InlineData("1.20.x", "1.20")]
    public void 版本范围归一化(string raw, string? expected) =>
        Assert.Equal(expected, PackVersionSniffer.Normalize(raw));
}
