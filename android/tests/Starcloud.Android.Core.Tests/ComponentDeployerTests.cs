// 组件部署器测试：内置资产 zip/tar.xz 部署、哈希校验失败回收、缓存复用、已装跳过、全链部署计划

using System.IO.Compression;
using System.Security.Cryptography;
using Starcloud.Android.Core.Components;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class ComponentDeployerTests
{
    // 预生成 tar.xz fixture（bsdtar 制作）：dir/release=JAVA_VERSION=21、dir/jre/bin/java=fake-binary、dir/jre/lib.txt=hello-lib
    private const string TarXzFixtureBase64 =
        "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4BX/AMtdADIaSqciBJSzSrhRRSqbYa/cLpcZh7qTu6A5ua6Kwd+MJAw6bF1/vVlU06UHO8v3mVFTTPMiNRuKVivGcIB4l1Aj1Mdadcw08+Rgu0XaS6nTldbN9D0gi/XmQUjxvfWJEGlicnYIcYPKxvivlBnz/GtbEutUcvVkd2Aj2LoEsuveytGqc/ru6wHa+POJk31UdFpKm5pjW5kLKsUHduYVx0W6bNUPtT+JJ4daCG4UsJXaKfOoD54z1LH0dNsPfqkNecIbEkT6SB1q5YEAAADdz0fR50+C3AAB5wGALAAA6N96KbHEZ/sCAAAAAARZWg==";

    // 预生成 tar.xz fixture（bsdtar -C pkg . 制作，首条目为根目录 "."，复刻真机 JRE 包形态）：
    // ./root-a.txt=root-bytes、./sub/b.txt=sub-bytes
    private const string TarXzRootEntryFixtureBase64 =
        "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4A//AJldABcLvBx9AZWCHnrITHDLp0XKywXys1eKxHGuIGGFECi+H6rMDJFu1Wsf1HFBR35DFe2pPgvg7OSrX0tFrUYaz1HSPsfzbpMXcUC1Eh35vLC3rLNVWrn7wG8Bs+dxNR++CTTIkDmi999R1GBfp0YNrvJPFrj6Wt7UsqdUt6iHL8vsSmy7JpX4d1SN5ZgS1lji00EGmZ288a7BAAAAAAAG6Wi7fZ4HqQABtQGAIAAAcI8mYbHEZ/sCAAAAAARZWg==";

    // 合法格式但内容错误的 SHA256（64 位十六进制）
    private const string WrongSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly byte[] TarXzFixture = Convert.FromBase64String(TarXzFixtureBase64);
    private static readonly byte[] TarXzRootEntryFixture = Convert.FromBase64String(TarXzRootEntryFixtureBase64);

    private static string Sha256Of(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Fact]
    public Task 内置Zip资产部署成功并写入完成标记() => WithTempRootAsync(async root =>
    {
        var zip = BuildZip(("lib/libopenal.so", "native-bytes"), ("VERSION.txt", "1.23.1"));
        var source = new MemoryAssetSource(("asset://components/openal.zip", zip));
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"), source);
        var item = Item("openal", "1.23.1", "asset://components/openal.zip",
            Sha256Of(zip), ArchiveKind.Zip, root);

        await deployer.DeployAsync(item);

        Assert.Equal("native-bytes",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, "lib", "libopenal.so")));
        Assert.Equal("1.23.1",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, DeploymentPlanner.OkMarker)));
        Assert.True(File.Exists(item.CachePath)); // 包已落缓存，供下次免取
    });

    [Fact]
    public Task 内置TarXz资产部署成功并保留目录结构() => WithTempRootAsync(async root =>
    {
        var source = new MemoryAssetSource(("asset://components/jre21.tar.xz", TarXzFixture));
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"), source);
        var item = Item("jre21", "21.0.4", "asset://components/jre21.tar.xz",
            Sha256Of(TarXzFixture), ArchiveKind.TarXz, root);

        await deployer.DeployAsync(item);

        Assert.Equal("JAVA_VERSION=21",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, "dir", "release")));
        Assert.Equal("fake-binary",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, "dir", "jre", "bin", "java")));
        Assert.Equal("hello-lib",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, "dir", "jre", "lib.txt")));
        Assert.Equal("21.0.4",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, DeploymentPlanner.OkMarker)));
    });

    [Fact]
    public Task 带根目录条目的TarXz部署成功且跳过根段() => WithTempRootAsync(async root =>
    {
        var source = new MemoryAssetSource(("asset://components/jre-root.tar.xz", TarXzRootEntryFixture));
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"), source);
        var item = Item("jre-root", "21.0.4", "asset://components/jre-root.tar.xz",
            Sha256Of(TarXzRootEntryFixture), ArchiveKind.TarXz, root);

        await deployer.DeployAsync(item); // 修复前：根条目 "." 触发「组件包内出现空路径」

        Assert.Equal("root-bytes",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, "root-a.txt")));
        Assert.Equal("sub-bytes",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, "sub", "b.txt")));
        Assert.Equal("21.0.4",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, DeploymentPlanner.OkMarker)));
    });

    [Fact]
    public Task 哈希不符时抛异常且缓存删除且无部署目录() => WithTempRootAsync(async root =>
    {
        var zip = BuildZip(("a.txt", "content"));
        var source = new MemoryAssetSource(("asset://components/openal.zip", zip));
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"), source);
        var item = Item("openal", "1.23.1", "asset://components/openal.zip",
            WrongSha, ArchiveKind.Zip, root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => deployer.DeployAsync(item));

        Assert.Contains("哈希校验失败", ex.Message);
        Assert.False(File.Exists(item.CachePath));       // 坏包不留在缓存
        Assert.False(Directory.Exists(item.DeployDir));  // 未解压出半成品
    });

    [Fact]
    public Task 缓存命中且哈希合法时不访问资产源() => WithTempRootAsync(async root =>
    {
        var zip = BuildZip(("lib/lib.so", "bytes"));
        var source = new MemoryAssetSource(("asset://components/openal.zip", zip));
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"), source);
        var item = Item("openal", "1.23.1", "asset://components/openal.zip",
            Sha256Of(zip), ArchiveKind.Zip, root);

        Directory.CreateDirectory(Path.GetDirectoryName(item.CachePath)!);
        await File.WriteAllBytesAsync(item.CachePath, zip); // 预置合法缓存

        await deployer.DeployAsync(item);

        Assert.Equal(0, source.OpenCount);
        Assert.True(File.Exists(Path.Combine(item.DeployDir, "lib", "lib.so")));
    });

    [Fact]
    public Task 内置资产缺失时抛出异常() => WithTempRootAsync(async root =>
    {
        var source = new MemoryAssetSource(); // 空资产表
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"), source);
        var item = Item("gl4es", "114", "asset://components/gl4es.zip",
            WrongSha, ArchiveKind.Zip, root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => deployer.DeployAsync(item));

        Assert.Contains("内置组件包缺失", ex.Message);
    });

    [Fact]
    public Task 已部署组件直接跳过且缓存目录不创建() => WithTempRootAsync(async root =>
    {
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"));
        var item = Item("glfw-stub", "1.0", "asset://components/glfw-stub.zip",
            WrongSha, ArchiveKind.Zip, root);

        Directory.CreateDirectory(item.DeployDir);
        await File.WriteAllTextAsync(Path.Combine(item.DeployDir, DeploymentPlanner.OkMarker), "1.0");
        await File.WriteAllTextAsync(Path.Combine(item.DeployDir, "sentinel.txt"), "keep");

        await deployer.DeployAsync(item); // 无资产源也不应触发获取

        Assert.Equal("keep",
            await File.ReadAllTextAsync(Path.Combine(item.DeployDir, "sentinel.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    });

    [Fact]
    public Task 混合部署计划全链执行已装跳过待装解压() => WithTempRootAsync(async root =>
    {
        // 预装 gl4es：只放完成标记，计划应将其归入 AlreadyInstalled
        var installedDir = Path.Combine(root, "components", "gl4es", "114");
        Directory.CreateDirectory(installedDir);
        await File.WriteAllTextAsync(Path.Combine(installedDir, DeploymentPlanner.OkMarker), "114");
        await File.WriteAllTextAsync(Path.Combine(installedDir, "sentinel.txt"), "keep");

        var zip = BuildZip(("libpojavexec.so", "glfw-stub-bytes"));
        var manifest = ComponentManifest.Parse($$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "gl4es", "version": "114", "url": "asset://components/gl4es.zip",
                      "sha256": "{{WrongSha}}", "archive": "Zip", "license": "MIT" },
                    { "name": "glfw-stub", "version": "1.0", "url": "asset://components/glfw-stub.zip",
                      "sha256": "{{Sha256Of(zip)}}", "archive": "Zip", "license": "MIT" }
                ]
            }
            """);
        var source = new MemoryAssetSource(("asset://components/glfw-stub.zip", zip));
        var deployer = new ComponentDeployer(
            Path.Combine(root, "components"), Path.Combine(root, "cache"), source);

        var plan = DeploymentPlanner.Plan(manifest,
            Path.Combine(root, "components"), Path.Combine(root, "cache"), ["gl4es", "glfw-stub"]);
        Assert.Single(plan.Pending);
        Assert.Single(plan.AlreadyInstalled);

        await deployer.DeployAllAsync(plan);

        Assert.Equal(1, source.OpenCount); // 已装组件未触发取包
        Assert.Equal("glfw-stub-bytes",
            await File.ReadAllTextAsync(Path.Combine(root, "components", "glfw-stub", "1.0", "libpojavexec.so")));
        Assert.Equal("keep",
            await File.ReadAllTextAsync(Path.Combine(installedDir, "sentinel.txt")));
    });

    /// <summary>临时目录沙盒：用完即删</summary>
    private static async Task WithTempRootAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "starcloud-deployer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await test(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>按部署器目录约定折算组件条目（缓存扩展名仅用于可读性）</summary>
    private static ComponentDownloadItem Item(string name, string version, string url,
        string sha256, ArchiveKind archive, string root) =>
        new(name, version, url, sha256, archive,
            Path.Combine(root, "cache", "components", $"{name}-{version}"),
            Path.Combine(root, "components", name, version));

    /// <summary>内存构造 zip 包</summary>
    private static byte[] BuildZip(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>asset 来源内存实现：带访问计数供断言</summary>
    private sealed class MemoryAssetSource : IComponentArchiveSource
    {
        private readonly Dictionary<string, byte[]> _assets = new(StringComparer.OrdinalIgnoreCase);

        public int OpenCount;

        public MemoryAssetSource(params (string Url, byte[] Data)[] assets)
        {
            foreach (var (url, data) in assets)
                _assets[url] = data;
        }

        public Stream? OpenArchive(string url)
        {
            Interlocked.Increment(ref OpenCount);
            return _assets.TryGetValue(url, out var data) ? new MemoryStream(data) : null;
        }
    }
}
