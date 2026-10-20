// 部署计划测试：缓存路径折算 / 就位判定 / 待下载与已装分组

using Starcloud.Android.Core.Components;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class DeploymentPlanTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root;
    private readonly string _componentsRoot;
    private readonly string _cacheRoot;

    public DeploymentPlanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "starcloud-tests", Guid.NewGuid().ToString("N"));
        _componentsRoot = Path.Combine(_root, "components");
        _cacheRoot = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_componentsRoot);
        Directory.CreateDirectory(_cacheRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败可忽略 */ }
    }

    private static ComponentEntry Entry(string name, string version, ArchiveKind archive) => new()
    {
        Name = name,
        Version = version,
        Url = $"https://example.com/{name}.pkg",
        Sha256 = Sha,
        Archive = archive,
        License = "MIT",
    };

    private static ComponentManifest ManifestWith(params ComponentEntry[] entries)
    {
        var manifest = new ComponentManifest { ManifestVersion = 1 };
        manifest.Components.AddRange(entries);
        return manifest;
    }

    [Theory]
    [InlineData(ArchiveKind.TarXz, ".tar.xz")]
    [InlineData(ArchiveKind.Zip, ".zip")]
    [InlineData(ArchiveKind.None, ".bin")]
    public void 缓存路径按压缩格式折算扩展名(ArchiveKind archive, string ext)
    {
        var path = DeploymentPlanner.CachePath("/cache", Entry("lwjgl3", "3.3.3", archive));

        Assert.Equal(Path.Combine("/cache", "components", $"lwjgl3-3.3.3{ext}"), path);
    }

    [Fact]
    public void 组件目录为名称加版本两级()
    {
        Assert.Equal(
            Path.Combine("/data", "jre21", "21.0.4"),
            DeploymentPlanner.ComponentDir("/data", "jre21", "21.0.4"));
    }

    [Fact]
    public void 目录与完成标记齐备才算已部署()
    {
        var entry = Entry("jre8", "8u402", ArchiveKind.TarXz);
        var dir = DeploymentPlanner.ComponentDir(_componentsRoot, entry.Name, entry.Version);

        Directory.CreateDirectory(dir);
        Assert.False(DeploymentPlanner.IsDeployed(_componentsRoot, entry)); // 缺标记

        File.WriteAllText(Path.Combine(dir, DeploymentPlanner.OkMarker), "ok");
        Assert.True(DeploymentPlanner.IsDeployed(_componentsRoot, entry));
    }

    [Fact]
    public void 未部署组件全部进入待下载清单()
    {
        var manifest = ManifestWith(Entry("jre21", "21.0.4", ArchiveKind.TarXz),
            Entry("lwjgl3", "3.3.3", ArchiveKind.Zip));

        var plan = DeploymentPlanner.Plan(manifest, _componentsRoot, _cacheRoot, ["jre21", "lwjgl3"]);

        Assert.False(plan.IsComplete);
        Assert.Empty(plan.AlreadyInstalled);
        Assert.Equal(2, plan.Pending.Count);
        var item = plan.Pending[0];
        Assert.Equal("jre21", item.Name);
        Assert.Equal("21.0.4", item.Version);
        Assert.Equal(DeploymentPlanner.CachePath(_cacheRoot, manifest.Components[0]), item.CachePath);
        Assert.Equal(
            DeploymentPlanner.ComponentDir(_componentsRoot, "jre21", "21.0.4"), item.DeployDir);
    }

    [Fact]
    public void 已部署组件进入已装清单且计划视为完成()
    {
        var entries = new[] { Entry("openal", "1.23", ArchiveKind.Zip) };
        var manifest = ManifestWith(entries);
        var dir = DeploymentPlanner.ComponentDir(_componentsRoot, "openal", "1.23");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DeploymentPlanner.OkMarker), "ok");

        var plan = DeploymentPlanner.Plan(manifest, _componentsRoot, _cacheRoot, ["openal"]);

        Assert.True(plan.IsComplete);
        Assert.Empty(plan.Pending);
        Assert.Single(plan.AlreadyInstalled);
    }

    [Fact]
    public void 混合状态按就位与否分组()
    {
        var manifest = ManifestWith(
            Entry("jre17", "17.0.10", ArchiveKind.TarXz),
            Entry("glfw-stub", "1.0", ArchiveKind.Zip));
        var dir = DeploymentPlanner.ComponentDir(_componentsRoot, "jre17", "17.0.10");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DeploymentPlanner.OkMarker), "ok");

        var plan = DeploymentPlanner.Plan(manifest, _componentsRoot, _cacheRoot,
            ["jre17", "glfw-stub"]);

        var pending = Assert.Single(plan.Pending);
        Assert.Equal("glfw-stub", pending.Name);
        var installed = Assert.Single(plan.AlreadyInstalled);
        Assert.Equal("jre17", installed.Name);
        Assert.False(plan.IsComplete);
    }

    [Fact]
    public void 清单缺必需组件时计划直接抛出异常()
    {
        var manifest = ManifestWith(Entry("jre21", "21.0.4", ArchiveKind.TarXz));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeploymentPlanner.Plan(manifest, _componentsRoot, _cacheRoot, ["mesa-zink"]));
        Assert.Contains("组件清单缺少 mesa-zink", ex.Message);
    }
}
