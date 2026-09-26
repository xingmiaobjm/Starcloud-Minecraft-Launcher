// 整合包归档测试：三格式识别、嵌套根目录、路径守卫、MultiMC 读写、三格式导出布局与排除规则。
//
// 导出侧用「随机字节」当 mod 内容，保证任何来源库都反查不到 → 必然落到 overrides，
// 于是断言与网络状态无关；同时把 MetadataTimeout 压到 300ms，让反查快速失败而不是干等 12 秒。

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class ModPackTests
{
    // ── 格式识别 ─────────────────────────────────────────────────────────

    [Fact]
    public void 识别_根级三种格式()
    {
        var root = NewTempDir();
        try
        {
            var mrpack = Path.Combine(root, "a.mrpack");
            var cf = Path.Combine(root, "b.zip");
            var mmc = Path.Combine(root, "c.zip");

            WriteZip(mrpack, new()
            {
                ["modrinth.index.json"] = """{ "formatVersion": 1, "files": [] }""",
                ["overrides/config/x.toml"] = "k=1",
            });
            WriteZip(cf, new()
            {
                ["manifest.json"] = """{ "minecraft": { "version": "1.20.1" }, "name": "P" }""",
                ["overrides/config/x.toml"] = "k=1",
            });
            WriteZip(mmc, new()
            {
                ["instance.cfg"] = "name=MyPack",
                ["mmc-pack.json"] = """{ "components": [] }""",
                [".minecraft/mods/a.jar"] = "x",
            });

            Assert.Equal(ModPackFormat.Modrinth, ModPackDetector.Detect(mrpack)!.Format);
            Assert.Equal(ModPackFormat.CurseForge, ModPackDetector.Detect(cf)!.Format);
            Assert.Equal(ModPackFormat.MultiMc, ModPackDetector.Detect(mmc)!.Format);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 识别_同名manifest但缺minecraft字段不算CurseForge()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "x.zip");
            WriteZip(path, new() { ["manifest.json"] = """{ "name": "只是个同名文件" }""" });
            Assert.Null(ModPackDetector.Detect(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 识别_外层多套一层目录时取该层为根()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "nested.mrpack");
            WriteZip(path, new()
            {
                ["MyPack/modrinth.index.json"] = """{ "formatVersion": 1, "files": [] }""",
                ["MyPack/overrides/x.txt"] = "y",
            });

            var probe = ModPackDetector.Detect(path)!;
            Assert.Equal(ModPackFormat.Modrinth, probe.Format);
            Assert.Equal("MyPack", probe.EntryRoot);
            Assert.Equal("MyPack/overrides/x.txt", probe.Entry("overrides/x.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 预处理_嵌套包重打成根级包()
    {
        var root = NewTempDir();
        var flattened = (string?)null;
        try
        {
            var path = Path.Combine(root, "nested.zip");
            WriteZip(path, new()
            {
                ["Outer/Inner/manifest.json"] = """{ "minecraft": { "version": "1.20.1" } }""",
                ["Outer/Inner/overrides/options.txt"] = "fov:0.5",
                ["Unrelated/junk.txt"] = "nope",
            });

            var probe = ModPackDetector.Detect(path)!;
            Assert.Equal("Outer/Inner", probe.EntryRoot);

            flattened = ModPackArchive.FlattenIfNested(path, probe);
            Assert.NotEqual(path, flattened);

            using var zip = ZipFile.OpenRead(flattened);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("manifest.json", names);
            Assert.Contains("overrides/options.txt", names);
            Assert.DoesNotContain(names, n => n.StartsWith("Unrelated/", StringComparison.Ordinal));
            Assert.Equal(ModPackFormat.CurseForge, ModPackDetector.Detect(flattened)!.Format);
        }
        finally
        {
            if (flattened is not null && File.Exists(flattened)) File.Delete(flattened);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 预处理_根级包原样返回()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "flat.mrpack");
            WriteZip(path, new() { ["modrinth.index.json"] = "{}" });
            var probe = ModPackDetector.Detect(path)!;
            Assert.Same(path, ModPackArchive.FlattenIfNested(path, probe));
        }
        finally { Directory.Delete(root, true); }
    }

    // ── 路径守卫 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("mods/a.jar", "mods/a.jar")]
    [InlineData("overrides\\config\\x.toml", "overrides/config/x.toml")]
    [InlineData("./mods//a.jar", "mods/a.jar")]
    [InlineData("../evil.txt", null)]
    [InlineData("mods/../../evil.txt", null)]
    [InlineData("/etc/passwd", null)]
    [InlineData("C:/windows/x.dll", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void 路径守卫_规范化(string input, string? expected) =>
        Assert.Equal(expected, PackPathGuard.Normalize(input));

    [Fact]
    public void 路径守卫_解析结果不越出根目录()
    {
        var root = NewTempDir();
        try
        {
            Assert.NotNull(PackPathGuard.Resolve(root, "mods/a.jar"));
            Assert.Null(PackPathGuard.Resolve(root, "../outside.txt"));
            Assert.Null(PackPathGuard.Resolve(root, "/abs.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 路径守卫_文件名压成单段()
    {
        Assert.Equal("a.jar", PackPathGuard.SafeFileName("mods/a.jar"));
        Assert.Equal("b.jar", PackPathGuard.SafeFileName("..\\..\\b.jar"));
        Assert.Equal("fallback", PackPathGuard.SafeFileName("   ", "fallback"));
    }

    // ── MultiMC / Prism 读写 ─────────────────────────────────────────────

    [Fact]
    public void MultiMC_读取嵌套包的元信息()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "pack.zip");
            WriteZip(path, new()
            {
                ["MyPack/instance.cfg"] = "[General]\nname=整合测试包\nInstanceType=OneSix\n",
                ["MyPack/mmc-pack.json"] = MmcPackJson,
                ["MyPack/.minecraft/mods/a.jar"] = "aaa",
                ["MyPack/.minecraft/config/x.toml"] = "k=1",
            });

            var info = new MultiMcPackService().ReadPackageInfo(path);
            Assert.Equal("整合测试包", info.Name);
            Assert.Equal("1.20.1", info.GameVersion);
            Assert.Equal("0.15.11", info.Dependencies["fabric-loader"]);
            Assert.Equal("1.20.1", info.Dependencies["minecraft"]);
            Assert.Equal(2, info.FileCount);
            Assert.Equal(3 + 3, info.TotalBytes);   // "aaa" + "k=1"（实例元文件不算内容）
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MultiMC_安装把内容铺进实例目录且不带元文件()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "pack.zip");
            WriteZip(path, new()
            {
                ["instance.cfg"] = "name=MyPack\n",
                ["mmc-pack.json"] = MmcPackJson,
                [".minecraft/mods/a.jar"] = "aaa",
                [".minecraft/options.txt"] = "fov:0.5",
            });

            var target = Path.Combine(root, "inst");
            var result = await new MultiMcPackService().InstallAsync(path, LauncherSettings.Load(), target);

            Assert.Equal("1.20.1", result.GameVersion);
            Assert.Equal("0.15.11", result.Dependencies["fabric-loader"]);
            Assert.True(File.Exists(Path.Combine(target, "mods", "a.jar")));
            Assert.True(File.Exists(Path.Combine(target, "options.txt")));
            // 实例元文件不能铺进游戏根（它们描述的是 MultiMC 实例，不是游戏内容）
            Assert.False(File.Exists(Path.Combine(target, "instance.cfg")));
            Assert.False(File.Exists(Path.Combine(target, "mmc-pack.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MultiMC_拒收其它格式的包()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "not-mmc.zip");
            WriteZip(path, new() { ["modrinth.index.json"] = "{}" });
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new MultiMcPackService().InstallAsync(path, LauncherSettings.Load(), Path.Combine(root, "x")));
        }
        finally { Directory.Delete(root, true); }
    }

    // ── 导出：MultiMC（自包含，全程离线） ────────────────────────────────

    [Fact]
    public async Task 导出_MultiMC布局与排除规则()
    {
        var root = NewTempDir();
        try
        {
            var gameRoot = Path.Combine(root, "game");
            WriteFile(gameRoot, "mods/a.jar", "aaa");
            WriteFile(gameRoot, "mods/secret.jar", "bbb");
            WriteFile(gameRoot, "config/x.toml", "k=1");
            WriteFile(gameRoot, "options.txt", "fov:0.5");
            WriteFile(gameRoot, "logs/latest.log", "noise");

            var target = Path.Combine(root, "out.zip");
            var request = new ExportRequest(gameRoot, target, "测试包", "1.20.1",
                new Dictionary<string, string> { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.15.11" },
                ["mods", "config"])
            {
                Format = ModPackExportFormat.MultiMc,
                VersionId = "2.3.0",
                Author = "星云",
                Description = "说明",
                ExcludeRules = ["secret.jar"],
                IncludeGameSettings = true,
            };

            var result = await ExportService().ExportAsync(request);

            using var zip = ZipFile.OpenRead(target);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("instance.cfg", names);
            Assert.Contains("mmc-pack.json", names);
            Assert.Contains("pack.json", names);
            Assert.Contains(".minecraft/mods/a.jar", names);
            Assert.Contains(".minecraft/config/x.toml", names);
            Assert.Contains(".minecraft/options.txt", names);
            Assert.DoesNotContain(".minecraft/mods/secret.jar", names);

            Assert.Equal(3, result.OverriddenFiles);   // a.jar + x.toml + options.txt
            Assert.Equal(1, result.SkippedFiles);      // secret.jar
            Assert.Equal(0, result.DownloadableFiles);

            Assert.Contains("name=测试包", ReadEntry(zip, "instance.cfg"));
            var mmc = JsonDocument.Parse(ReadEntry(zip, "mmc-pack.json")).RootElement;
            var uids = mmc.GetProperty("components").EnumerateArray()
                .Select(c => c.GetProperty("uid").GetString()).ToList();
            Assert.Contains("net.minecraft", uids);
            Assert.Contains("net.fabricmc.fabric-loader", uids);

            // 导出的包要能被自家导入器原样读回（往返一致）
            var info = new MultiMcPackService().ReadPackageInfo(target);
            Assert.Equal("1.20.1", info.GameVersion);
            Assert.Equal("0.15.11", info.Dependencies["fabric-loader"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 导出_不勾选实例设置则不打包options()
    {
        var root = NewTempDir();
        try
        {
            var gameRoot = Path.Combine(root, "game");
            WriteFile(gameRoot, "mods/a.jar", "aaa");
            WriteFile(gameRoot, "options.txt", "fov:0.5");

            var target = Path.Combine(root, "out.zip");
            var request = new ExportRequest(gameRoot, target, "P", "1.20.1",
                new Dictionary<string, string> { ["minecraft"] = "1.20.1" }, ["mods"])
            {
                Format = ModPackExportFormat.MultiMc,
                IncludeGameSettings = false,
            };
            await ExportService().ExportAsync(request);

            using var zip = ZipFile.OpenRead(target);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.DoesNotContain(".minecraft/options.txt", names);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 导出_加载器缺失时给出提示()
    {
        var root = NewTempDir();
        try
        {
            var gameRoot = Path.Combine(root, "game");
            WriteFile(gameRoot, "mods/a.jar", "aaa");
            var request = new ExportRequest(gameRoot, Path.Combine(root, "out.zip"), "P", "1.20.1",
                new Dictionary<string, string> { ["minecraft"] = "1.20.1" }, ["mods"])
            {
                Format = ModPackExportFormat.MultiMc,
            };
            var result = await ExportService().ExportAsync(request);
            Assert.NotNull(result.LoaderWarning);
        }
        finally { Directory.Delete(root, true); }
    }

    // ── 导出：Modrinth / CurseForge（反查离线失败 → 全部落 overrides） ────

    [Fact]
    public async Task 导出_Modrinth布局与索引元数据()
    {
        var root = NewTempDir();
        var saved = DownloadService.MetadataTimeout;
        DownloadService.MetadataTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var gameRoot = Path.Combine(root, "game");
            WriteFile(gameRoot, "mods/unknown.jar", RandomContent());

            var target = Path.Combine(root, "out.mrpack");
            var request = new ExportRequest(gameRoot, target, "M包", "1.20.1",
                new Dictionary<string, string> { ["minecraft"] = "1.20.1", ["forge"] = "47.2.0" }, ["mods"])
            {
                Format = ModPackExportFormat.Modrinth,
                VersionId = "1.4.2",
            };

            var result = await ExportService().ExportAsync(request);

            using var zip = ZipFile.OpenRead(target);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("modrinth.index.json", names);
            Assert.Contains("overrides/mods/unknown.jar", names);
            Assert.Equal(1, result.OverriddenFiles);
            Assert.Equal(0, result.DownloadableFiles);

            var index = JsonDocument.Parse(ReadEntry(zip, "modrinth.index.json")).RootElement;
            Assert.Equal(1, index.GetProperty("formatVersion").GetInt32());
            Assert.Equal("minecraft", index.GetProperty("game").GetString());
            Assert.Equal("1.20.1", index.GetProperty("gameVersion").GetString());
            Assert.Equal("M包", index.GetProperty("name").GetString());
            Assert.Equal("1.4.2", index.GetProperty("versionId").GetString());
            Assert.Equal("47.2.0", index.GetProperty("dependencies").GetProperty("forge").GetString());
        }
        finally
        {
            DownloadService.MetadataTimeout = saved;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 导出_CurseForge布局与清单元数据()
    {
        var root = NewTempDir();
        var saved = DownloadService.MetadataTimeout;
        DownloadService.MetadataTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var gameRoot = Path.Combine(root, "game");
            WriteFile(gameRoot, "mods/unknown.jar", RandomContent());

            var target = Path.Combine(root, "out.zip");
            var request = new ExportRequest(gameRoot, target, "C包", "1.20.1",
                new Dictionary<string, string> { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.15.11" }, ["mods"])
            {
                Format = ModPackExportFormat.CurseForge,
                VersionId = "3.0.1",
                Author = "作者",
                Description = "简介",
            };

            var result = await ExportService().ExportAsync(request);

            using var zip = ZipFile.OpenRead(target);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("manifest.json", names);
            Assert.Contains("overrides/mods/unknown.jar", names);
            Assert.Equal(1, result.OverriddenFiles);

            var manifest = JsonDocument.Parse(ReadEntry(zip, "manifest.json")).RootElement;
            Assert.Equal("minecraftModpack", manifest.GetProperty("manifestType").GetString());
            Assert.Equal("1.20.1", manifest.GetProperty("minecraft").GetProperty("version").GetString());
            Assert.Equal("fabric-0.15.11",
                manifest.GetProperty("minecraft").GetProperty("modLoaders")[0].GetProperty("id").GetString());
            Assert.Equal("C包", manifest.GetProperty("name").GetString());
            Assert.Equal("3.0.1", manifest.GetProperty("version").GetString());
            Assert.Equal("作者", manifest.GetProperty("author").GetString());
            Assert.Equal("overrides", manifest.GetProperty("overrides").GetString());
        }
        finally
        {
            DownloadService.MetadataTimeout = saved;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 导出_候选目录排除运行期目录()
    {
        var root = NewTempDir();
        try
        {
            var gameRoot = Path.Combine(root, "game");
            WriteFile(gameRoot, "mods/a.jar", "aaa");
            WriteFile(gameRoot, "schematics/house.litematic", "x");
            Directory.CreateDirectory(Path.Combine(gameRoot, "libraries"));
            Directory.CreateDirectory(Path.Combine(gameRoot, "logs"));

            var dirs = ExportService().DiscoverDirs(gameRoot);
            Assert.Contains(dirs, d => d.Name == "mods" && d.IsStandard && d.FileCount == 1);
            Assert.Contains(dirs, d => d.Name == "schematics" && d.Hint is not null);
            Assert.DoesNotContain(dirs, d => d.Name == "libraries");
            Assert.DoesNotContain(dirs, d => d.Name == "logs");
        }
        finally { Directory.Delete(root, true); }
    }

    // ── CurseForge 指纹 ─────────────────────────────────────────────────

    [Fact]
    public void 指纹_Murmur2剔除空白字节且落为正31位()
    {
        // 同一内容里插入空格 / 换行 / Tab 后指纹必须不变（CurseForge 官方规则）
        var a = FingerprintOf("hello world");
        var b = FingerprintOf("hello\tworld");
        var c = FingerprintOf("hello\nworld");
        var d = FingerprintOf(" hell o\r\nworld ");
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a, d);

        Assert.True(a > 0);                        // 抹掉最高位后必然是正整数
        Assert.NotEqual(a, FingerprintOf("hello worlD"));

        // 已知值锁定：murmur2("helloworld", seed 1) & 0x7FFFFFFF
        // （标准 murmur2 初值为 seed ^ length，reference 实现独立核算过）
        Assert.Equal(677166573, a);
    }

    // ── 测试脚手架 ───────────────────────────────────────────────────────

    private const string MmcPackJson = """
    {
      "formatVersion": 1,
      "components": [
        { "uid": "net.minecraft", "version": "1.20.1", "important": true },
        { "uid": "net.fabricmc.fabric-loader", "version": "0.15.11" }
      ]
    }
    """;

    private static ModPackExportService ExportService()
    {
        var settings = LauncherSettings.Load();
        var download = new DownloadService(() => settings);
        return new ModPackExportService(
            new ModrinthService(download), new CurseForgeService(download, () => settings));
    }

    private static int FingerprintOf(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return CurseForgeService.Fingerprint(stream);
    }

    private static string RandomContent() => "random-" + Guid.NewGuid().ToString("N");

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "starcloud-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
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

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"归档里没有 {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
