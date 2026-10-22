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
