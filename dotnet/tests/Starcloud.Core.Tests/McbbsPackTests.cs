// MCBBS 整合包（国内主流格式）：mcbbs.packmeta / 带 addons 的 manifest.json。
// 与 CurseForge 同为 manifest.json，靠 "有没有 addons 数组" 区分——这个区分错了，
// 包会被当成 CurseForge 去读 minecraft 字段，然后在安装阶段炸掉。

using System.IO.Compression;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class McbbsPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sc-mcbbs-" + Guid.NewGuid().ToString("N"));

    public McbbsPackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string WriteZip(string name, Dictionary<string, string> entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var e = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    [Fact]
    public void 识别_mcbbs_packmeta()
    {
        var path = WriteZip("a.zip", new()
        {
            ["mcbbs.packmeta"] = """{"name":"我的整合包","addons":[{"id":"game","version":"1.20.1"},{"id":"forge","version":"47.2.0"}]}""",
            ["overrides/mods/x.jar"] = "x",
        });
        var probe = ModPackDetector.Detect(path);
        Assert.NotNull(probe);
        Assert.Equal(ModPackFormat.Mcbbs, probe!.Format);
    }

    [Fact]
    public void 识别_addons版manifest与CurseForge区分开()
    {
        var mcbbs = WriteZip("mcbbs.zip", new()
        {
            ["manifest.json"] = """{"name":"P","addons":[{"id":"game","version":"1.19.2"}]}""",
            ["overrides/config/a.cfg"] = "k=1",
        });
        var curseForge = WriteZip("cf.zip", new()
        {
            ["manifest.json"] = """{"minecraft":{"version":"1.20.1","modLoaders":[]},"name":"P","files":[]}""",
            ["overrides/config/a.cfg"] = "k=1",
        });

        Assert.Equal(ModPackFormat.Mcbbs, ModPackDetector.Detect(mcbbs)!.Format);
        Assert.Equal(ModPackFormat.CurseForge, ModPackDetector.Detect(curseForge)!.Format);
    }

    [Fact]
    public void 读清单_游戏版本与加载器归一()
    {
        var path = WriteZip("p.zip", new()
        {
            ["mcbbs.packmeta"] = """
                {"name":"GTNH 风格","addons":[
                  {"id":"game","version":"1.12.2"},
                  {"id":"forge","version":"14.23.5.2860"},
                  {"id":"fabric","version":"0.15.11"}]}
                """,
            ["overrides/mods/a.jar"] = "x",
        });

        var meta = ModPackDetector.ReadMcbbsMeta(path);
        Assert.NotNull(meta);
        Assert.Equal("GTNH 风格", meta!.Value.Name);
        Assert.Equal("1.12.2", meta.Value.GameVersion);
        Assert.Equal("14.23.5.2860", meta.Value.Dependencies["forge"]);
        // addons 里 fabric 归一成依赖表用的 fabric-loader 键
        Assert.Equal("0.15.11", meta.Value.Dependencies["fabric-loader"]);
    }

    [Fact]
    public void 缺addons_不算MCBBS()
    {
        var path = WriteZip("no-addons.zip", new()
        {
            ["manifest.json"] = """{"name":"just a file"}""",
            ["mods/a.jar"] = "x",
        });
        Assert.NotEqual(ModPackFormat.Mcbbs, ModPackDetector.Detect(path)?.Format ?? ModPackFormat.Raw);
        Assert.Null(ModPackDetector.ReadMcbbsMeta(path));
    }
}
