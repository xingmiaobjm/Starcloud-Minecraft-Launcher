// 整合包容器的兼容性回归：嵌套归档（下载站双层包）、7z / tar.gz / rar 入口形态。
//
// 这些是「识别成功但导入失败」与「根本识别不了」的两类真实故障：
// · 双层包（外层 zip 里只有一个 .mrpack）：探测能认出格式，但安装器读外层找不到清单；
// · 7z / tar.gz：入口只认 .zip / .mrpack 时用户根本选不了文件。

using System.IO.Compression;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class PackCompatTests
{
    private const string MrIndex = """
        {"formatVersion":1,"game":"minecraft","versionId":"1.0.0","name":"MrDemo",
         "files":[],"dependencies":{"minecraft":"1.20.1"}}
        """;

    private const string CfManifest = """
        {"minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge","version":"47.2.0"}]},
         "manifestType":"minecraftModpack","manifestVersion":1,"name":"Demo","version":"1.0",
         "files":[],"overrides":"overrides"}
        """;

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-packcompat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteZip(string path, Dictionary<string, string> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    [Theory]
    [InlineData("pack.zip", true)]
    [InlineData("pack.mrpack", true)]
    [InlineData("pack.7z", true)]
    [InlineData("pack.rar", true)]
    [InlineData("pack.tgz", true)]
    [InlineData("pack.tar", true)]
    [InlineData("pack.gz", true)]
    [InlineData("pack.zipx", false)]
    [InlineData("pack.txt", false)]
    public void 归档入口接受全部受支持的容器扩展名(string name, bool expected) =>
        Assert.Equal(expected, ModPackDetector.IsArchive(name));

    [Fact]
    public void 外层zip里只有一个mrpack时探针带上内层归档名()
    {
        var dir = NewDir();
        try
        {
            var inner = Path.Combine(dir, "inner.mrpack");
            WriteZip(inner, new() { ["modrinth.index.json"] = MrIndex });

            var outer = Path.Combine(dir, "outer.zip");
            using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(inner, "real.mrpack");

            var probe = ModPackDetector.Detect(outer);
            Assert.NotNull(probe);
            Assert.Equal(ModPackFormat.Modrinth, probe!.Format);
            // 关键：没有这个标记，安装器会拿着外层 zip 找 modrinth.index.json，必然失败
            Assert.Equal("real.mrpack", probe.NestedEntry);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 嵌套mrpack拍平后能直接读出索引()
    {
        var dir = NewDir();
        string? flattened = null;
        try
        {
            var inner = Path.Combine(dir, "inner.mrpack");
            WriteZip(inner, new() { ["modrinth.index.json"] = MrIndex });
            var outer = Path.Combine(dir, "outer.zip");
            using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(inner, "real.mrpack");

            var probe = ModPackDetector.Detect(outer)!;
            flattened = ModPackArchive.FlattenIfNested(outer, probe);
            Assert.NotEqual(outer, flattened);

            // 拍平后的包直接就是有效的 mrpack：索引能读出来
            var info = new MrPackInstallerService(null!).ReadPackageInfo(flattened);
            Assert.Equal("MrDemo", info.Name);
            Assert.Equal("1.20.1", info.GameVersion);
        }
        finally
        {
            if (flattened is not null && File.Exists(flattened)) File.Delete(flattened);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 嵌套zip套目录且内层还有清单时两层都能拍平()
    {
        var dir = NewDir();
        string? flattened = null;
        try
        {
            // 外层 zip → 内层 zip（内含 Outer/manifest.json，还套了一层目录）
            var innerZip = Path.Combine(dir, "inner.zip");
            WriteZip(innerZip, new()
            {
                ["Outer/manifest.json"] = CfManifest,
                ["Outer/overrides/options.txt"] = "fov:0.5",
            });
            var outer = Path.Combine(dir, "outer.zip");
            using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(innerZip, "download.zip");

            var probe = ModPackDetector.Detect(outer)!;
            Assert.Equal(ModPackFormat.CurseForge, probe.Format);
            Assert.Equal("Outer", probe.EntryRoot);
            Assert.Equal("download.zip", probe.NestedEntry);

            flattened = ModPackArchive.FlattenIfNested(outer, probe);
            using var flat = ZipFile.OpenRead(flattened);
            var names = flat.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("manifest.json", names);
            Assert.Contains("overrides/options.txt", names);
        }
        finally
        {
            if (flattened is not null && File.Exists(flattened)) File.Delete(flattened);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task tgz包归一化成zip后能按内容识别()
    {
        var dir = NewDir();
        try
        {
            // 造一个 tar.gz：内容 = .minecraft/mods + options.txt（裸内容包形态）
            var stage = Path.Combine(dir, "stage");
            Directory.CreateDirectory(Path.Combine(stage, ".minecraft", "mods"));
            await File.WriteAllTextAsync(Path.Combine(stage, ".minecraft", "mods", "a.jar"), "x");
            await File.WriteAllTextAsync(Path.Combine(stage, ".minecraft", "options.txt"), "lang:zh_cn");

            var tgz = Path.Combine(dir, "pack.tgz");
            using (var fs = File.Create(tgz))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var tar = new SharpCompress.Writers.Tar.TarWriter(gz,
                new SharpCompress.Writers.Tar.TarWriterOptions(SharpCompress.Common.CompressionType.None, true)))
            {
                foreach (var f in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                {
                    using var content = File.OpenRead(f);
                    tar.Write(Path.GetRelativePath(stage, f), content, null);
                }
            }

            var (zipPath, work) = await ModPackArchive.NormalizeAsync(tgz);
            try
            {
                Assert.NotEqual(tgz, zipPath);
                Assert.True(File.Exists(zipPath));

                var probe = ModPackDetector.Detect(zipPath);
                Assert.NotNull(probe);
                Assert.Equal(ModPackFormat.Raw, probe!.Format);
                Assert.Equal(".minecraft", probe.EntryRoot);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task zip和mrpack归一化时原样返回()
    {
        var dir = NewDir();
        try
        {
            var zip = Path.Combine(dir, "plain.zip");
            WriteZip(zip, new() { ["manifest.json"] = CfManifest });

            var (path, work) = await ModPackArchive.NormalizeAsync(zip);
            Assert.Same(zip, path);
            Assert.Equal("", work);
        }
        finally { Directory.Delete(dir, true); }
    }
}
