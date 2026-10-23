// 整合包兼容性增强：packwiz / ATLauncher 识别，以及 Windows 非法文件名段的清洗落盘。

using System.IO.Compression;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class PackArchiveCompatTests : IDisposable
{
    private readonly List<string> _files = [];

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-pack-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
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

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void 识别packwiz包()
    {
        var path = MakeZip(
            ("pack.toml", "name = \"My Pack\"\n[index]\nfile = \"index.toml\"\n"),
            ("index.toml", ""),
            ("mods/jei.pw.toml", "[[files]]\nurl = \"https://example.invalid/jei.jar\"\n"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Packwiz, probe!.Format);
    }

    [Fact]
    public void 识别atlauncher实例包()
    {
        var path = MakeZip(
            ("instance.json", "{\"launcher\":\"atlauncher\",\"name\":\"AT Pack\",\"id\":\"atpack\"}"),
            ("mods/a.jar", "x"),
            ("config/a.cfg", "y"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.AtLauncher, probe!.Format);
    }

    [Fact]
    public void 无清单但有mods仍识别为通用压缩包()
    {
        var path = MakeZip(("mods/a.jar", "x"), ("config/a.cfg", "y"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Raw, probe!.Format);
    }

    [Fact]
    public void 同名toml但内容不像packwiz不误判()
    {
        var path = MakeZip(("pack.toml", "just some text"), ("mods/a.jar", "x"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Raw, probe!.Format);
    }

    [Fact]
    public void 识别HMCL旧导出格式()
    {
        var path = MakeZip(
            ("modpack.json", "{\"modpackVersion\":\"1.0\",\"version\":\"1.12.2\",\"author\":\"x\"}"),
            ("overrides/mods/a.jar", "x"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Hmcl, probe!.Format);
    }

    [Fact]
    public void packwiz索引解析两种写法()
    {
        const string ranked = """
hash-format = "sha256"

[[files]]
file = "mods/jei.pw.toml"
hash = "aaa"

[[files]]
file = "mods/sodium.pw.toml"
hash = "bbb"
""";
        const string inline = """
hash-format = "sha256"
files = [
  { file = "mods/jei.pw.toml", hash = "aaa" },
  { file = "mods/sodium.pw.toml", hash = "bbb" },
]
""";
        Assert.Equal(["mods/jei.pw.toml", "mods/sodium.pw.toml"], PackwizToml.IndexFiles(ranked));
        Assert.Equal(["mods/jei.pw.toml", "mods/sodium.pw.toml"], PackwizToml.IndexFiles(inline));
    }

    [Fact]
    public void packwiz元数据解析url与文件名()
    {
        const string meta = """
name = "JEI"
filename = "jei-1.20.1-15.2.0.27.jar"
side = "client"

[download]
url = "https://cdn.modrinth.com/data/abc/versions/1/jei.jar"
hash-format = "sha512"
hash = "deadbeef"
""";
        var m = PackwizToml.ParseMetafile(meta);
        Assert.Equal("https://cdn.modrinth.com/data/abc/versions/1/jei.jar", m.Url);
        Assert.Equal("jei-1.20.1-15.2.0.27.jar", m.FileName);
        Assert.Equal("client", m.Side);
        Assert.False(m.Optional);
        Assert.Equal("mods/jei-1.20.1-15.2.0.27.jar",
            PackwizToml.TargetPath("mods/jei.pw.toml", m.FileName, m.Url));
    }

    [Fact]
    public void packwiz可选组默认不启用则跳过()
    {
        const string meta = """
[[option]]
name = "Shaders"
description = "optional"

[download]
url = "https://example.invalid/shaders.jar"
""";
        Assert.True(PackwizToml.ParseMetafile(meta).Optional);

        const string metaDefault = """
[[option]]
name = "Shaders"
default = true

[download]
url = "https://example.invalid/shaders.jar"
""";
        Assert.False(PackwizToml.ParseMetafile(metaDefault).Optional);
    }

    [Fact]
    public void 识别PCL导出的Minecraft大写目录()
    {
        var path = MakeZip(
            ("PCL/Setup.ini", "[Setup] VersionArgumentIndie=10"),
            ("Minecraft/mods/a.jar", "x"),
            ("Minecraft/config/a.cfg", "y"),
            ("Minecraft/minecraftversion.txt", "1.20.1"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Raw, probe!.Format);
        // 游戏根应当是 Minecraft 而不是包根（否则 PCL/ 配置目录会被铺进实例）
        Assert.Equal("Minecraft", probe.EntryRoot);
    }

    [Fact]
    public void 识别只导出单个版本文件夹()
    {
        var path = MakeZip(
            ("1.20.1/1.20.1.json", "{\"id\":\"1.20.1\"}"),
            ("1.20.1/1.20.1.jar", "x"),
            ("1.20.1/mods/a.jar", "y"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Raw, probe!.Format);
        Assert.Equal("1.20.1", probe.EntryRoot);
    }

    [Fact]
    public void 识别带libraries的完整游戏目录()
    {
        var path = MakeZip(
            ("libraries/a/b/1.0/b-1.0.jar", "x"),
            ("assets/indexes/17.json", "{}"),
            ("mods/a.jar", "y"));
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Raw, probe!.Format);
        Assert.Null(probe.EntryRoot);
    }

    [Fact]
    public void 非法字符段在清洗模式下保留文件()
    {
        // 严格模式：整条路径拒绝
        Assert.Null(PackPathGuard.Normalize("mods/a:b?.jar"));
        // 清洗模式：非法字符换下划线，仍然落盘
        Assert.Equal("mods/a_b_.jar", PackPathGuard.Normalize("mods/a:b?.jar", sanitize: true));
    }

    [Fact]
    public void 清洗模式仍然拒绝路径穿越()
    {
        Assert.Null(PackPathGuard.Normalize("../evil.txt", sanitize: true));
        Assert.Null(PackPathGuard.Normalize("/abs/path", sanitize: true));
        Assert.Null(PackPathGuard.Normalize("C:/windows/system32", sanitize: true));
    }
}
