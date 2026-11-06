// 整合包导入性能回归：Modrinth 包不应再被「整包解压两次」。
//
// 背景：原来预览与安装各把整包解压到临时目录，overrides 还要再整目录拷一遍 ——
// 同一份数据写两遍磁盘，几百 MB 的包要几分钟。
// 现在改成：读 zip 内的索引 + 只解压 overrides 前缀到目标目录。

using System.IO.Compression;
using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class MrPackImportSpeedTests
{
    /// <summary>造一个 .mrpack：索引 + 若干 overrides 文件。</summary>
    private static string MakeMrPack(string dir, int overrideFiles, int overrideBytesEach)
    {
        var path = Path.Combine(dir, "pack.mrpack");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var index = new
        {
            formatVersion = 1,
            gameVersion = "1.21.1",
            name = "SpeedTest",
            versionId = "1.0.0",
            dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1" },
            files = Array.Empty<object>(),
        };
        var indexEntry = zip.CreateEntry("modrinth.index.json");
        using (var s = indexEntry.Open())
        using (var w = new StreamWriter(s))
            w.Write(JsonSerializer.Serialize(index));

        for (var i = 0; i < overrideFiles; i++)
        {
            var entry = zip.CreateEntry($"overrides/config/test{i}.cfg");
            using var s = entry.Open();
            s.Write(new byte[overrideBytesEach]);
        }
        // 一个 server-overrides 条目：客户端不该导入它
        var server = zip.CreateEntry("server-overrides/secret.txt");
        using (var s = server.Open())
            s.Write(new byte[] { 1, 2, 3 });

        return path;
    }

    [Fact]
    public void 预览不落地任何文件()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-mrpack-speed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var pack = MakeMrPack(dir, overrideFiles: 20, overrideBytesEach: 1024);
            var installer = new MrPackInstallerService(new DownloadService(() => new LauncherSettings()));

            var info = installer.ReadPackageInfo(pack);

            Assert.Equal("SpeedTest", info.Name);
            Assert.Equal("1.21.1", info.GameVersion);
            // overrides 条目数要统计对，但一个字节都不该落地
            Assert.Equal(20, info.OverrideFileCount);
            Assert.Equal(0, Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Count(p => !p.EndsWith("pack.mrpack", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 安装只把overrides落到目标目录且不带server部分()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-mrpack-speed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var pack = MakeMrPack(dir, overrideFiles: 20, overrideBytesEach: 1024);
            var target = Path.Combine(dir, "instance");
            Directory.CreateDirectory(target);

            var installer = new MrPackInstallerService(new DownloadService(() => new LauncherSettings()));
            var settings = new LauncherSettings { DotMinecraft = target };

            var result = await installer.InstallAsync(pack, settings, target);

            Assert.Equal("1.21.1", result.GameVersion);
            // overrides 全部落到位（config/test*.cfg）
            Assert.Equal(20, Directory.GetFiles(Path.Combine(target, "config"), "*.cfg").Length);
            // server-overrides 不该出现在客户端实例里
            Assert.False(Directory.Exists(Path.Combine(target, "server-overrides")));
            // 不该留下临时解压目录
            var tempRoot = Path.Combine(Path.GetTempPath(), "starcloud-mrpack");
            if (Directory.Exists(tempRoot))
            {
                var leftovers = Directory.GetDirectories(tempRoot).Length;
                Assert.True(leftovers <= 1, $"临时解压目录残留 {leftovers} 个");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void sha512校验通过与失败都能识别()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-mrpack-sha512-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "a.bin");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });

            var correct = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(file)));
            Assert.True(DownloadService.VerifySha512(file, correct));
            Assert.True(DownloadService.VerifySha512(file, correct.ToLowerInvariant()));
            Assert.False(DownloadService.VerifySha512(file, new string('0', 128)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
