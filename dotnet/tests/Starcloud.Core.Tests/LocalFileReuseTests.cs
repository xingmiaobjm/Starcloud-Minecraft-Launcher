// 补全加速守卫：同版本文件优先本地复制，不再下一遍。
//
// 场景：装了多个版本，其中两个是同一个 MC 版本 → client jar 完全一样。
// 补全时从确认完好的那个实例复制（秒级），而不是重新下十几 MB。
// 安全线是 SHA1：内容对不上就老老实实下载。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>下载链路会往进程内单例 TaskCenterService 写聚合任务，
/// 与 TaskCenterServiceTests 抢同一个单例 —— 必须同集合串行。</summary>
[Collection("进程内单例")]
public class LocalFileReuseTests
{
    [Fact]
    public void 同版本实例之间可以复制client_jar()
    {
        using var dotMc = new TempDotMc();
        var jar = dotMc.VersionJar("Create Aero", Payload: "VANILLA-1211-JAR");
        var (sha1, size) = Hash(jar);
        var dest = dotMc.PathFor("versions/机械动力航空学整合包/机械动力航空学整合包.jar");

        var reuse = LocalFileReuse.Scan(dotMc.Path);
        var ok = reuse.TryProvide(dest, sha1, size, out var source);

        Assert.True(ok, "同内容的版本 jar 应该能复用");
        Assert.Equal(jar, source);
        Assert.True(File.Exists(dest));
        Assert.Equal("VANILLA-1211-JAR", File.ReadAllText(dest));
    }

    [Fact]
    public void 内容对不上就不复用()
    {
        using var dotMc = new TempDotMc();
        var jar = dotMc.VersionJar("坏实例", "CORRUPTED");
        var dest = dotMc.PathFor("versions/新实例/新实例.jar");

        // 期望的是另一份内容（SHA1 与磁盘上的不一致）
        var (otherSha1, otherSize) = HashOfText("REAL-CLIENT-JAR");
        var reuse = LocalFileReuse.Scan(dotMc.Path);
        var ok = reuse.TryProvide(dest, otherSha1, otherSize, out _);

        Assert.False(ok, "哈希对不上时不能拿别的实例的文件凑数");
        Assert.False(File.Exists(dest));
        Assert.Equal("CORRUPTED", File.ReadAllText(jar));
    }

    [Fact]
    public void 大小不同时直接跳过不用算哈希()
    {
        using var dotMc = new TempDotMc();
        dotMc.VersionJar("A", new string('x', 4096));
        var dest = dotMc.PathFor("versions/B/B.jar");

        var reuse = LocalFileReuse.Scan(dotMc.Path);
        var ok = reuse.TryProvide(dest, "00", size: 16, out _);

        Assert.False(ok);
    }

    [Fact]
    public void 同一个mod装在别的实例里也能复用()
    {
        using var dotMc = new TempDotMc();
        var mod = dotMc.File("versions/老实例/mods/sodium.jar", "MOD-BYTES");
        var (sha1, size) = Hash(mod);
        var dest = dotMc.PathFor("versions/新实例/mods/sodium.jar");

        var reuse = LocalFileReuse.Scan(dotMc.Path);

        Assert.True(reuse.TryProvide(dest, sha1, size, out var source));
        Assert.Equal(mod, source);
    }

    [Fact]
    public void 目标已是同一个文件时不会自己覆盖自己()
    {
        using var dotMc = new TempDotMc();
        var jar = dotMc.VersionJar("Only", "SAME");
        var (sha1, size) = Hash(jar);

        var reuse = LocalFileReuse.Scan(dotMc.Path);

        Assert.False(reuse.TryProvide(jar, sha1, size, out _));
    }

    [Fact]
    public void 资源库里的文件也能当来源()
    {
        using var dotMc = new TempDotMc();
        var library = Path.Combine(dotMc.Path, "library");
        Directory.CreateDirectory(Path.Combine(library, "shaders"));
        var cached = Path.Combine(library, "shaders", "complementary.zip");
        System.IO.File.WriteAllText(cached, "SHADER-BYTES");
        var (sha1, size) = Hash(cached);
        var dest = dotMc.PathFor("versions/某个实例/shaderpacks/complementary.zip");

        var reuse = LocalFileReuse.Scan(dotMc.Path);
        reuse.AddLibraryDirectory(library);

        Assert.True(reuse.TryProvide(dest, sha1, size, out var source));
        Assert.Equal(cached, source);
    }

    /// <summary>端到端：补全任务里全是「本地能提供」的文件时，一个网络请求都不该发</summary>
    [Fact]
    public async Task 批量补全优先本地复制不联网()
    {
        using var dotMc = new TempDotMc();
        var source = dotMc.VersionJar("已装好的", "CLIENT-JAR-BYTES");
        var (sha1, size) = Hash(source);
        var dest = dotMc.PathFor("versions/缺文件的/缺文件的.jar");

        var settings = new LauncherSettings { DotMinecraft = dotMc.Path };
        var download = new DownloadService(() => settings);

        // 故意给一个连不上的地址：只要走了网络这条测试就会失败
        await download.DownloadBatchAsync(
            [("http://127.0.0.1:1/never-reachable", dest, sha1, size)],
            "补全游戏文件", concurrency: 2, maxRetries: 1, progress: null);

        Assert.True(File.Exists(dest));
        Assert.Equal("CLIENT-JAR-BYTES", File.ReadAllText(dest));
    }

    // ── 测试基础设施 ──

    private static (string Sha1, long Size) Hash(string path)
    {
        using var fs = File.OpenRead(path);
        return (Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs)),
            new FileInfo(path).Length);
    }

    private static (string Sha1, long Size) HashOfText(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return (Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)), bytes.Length);
    }

    private sealed class TempDotMc : IDisposable
    {
        public TempDotMc()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "sc-reuse-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string PathFor(string relative) =>
            System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string File(string relative, string content)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        /// <summary>造一个版本目录 + 同名的版本 jar</summary>
        public string VersionJar(string id, string Payload)
        {
            File($"versions/{id}/{id}.json", $$"""{"id":"{{id}}"}""");
            return File($"versions/{id}/{id}.jar", Payload);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
