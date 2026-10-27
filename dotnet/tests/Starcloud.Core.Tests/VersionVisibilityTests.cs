// 版本列表可见性：整合包联动安装的原版 / 加载器不应单列为版本条目。

using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class VersionVisibilityTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private string MakeMinecraft(params (string Id, string? InheritsFrom)[] versions)
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-vis-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _dirs.Add(root);
        foreach (var (id, parent) in versions)
        {
            var dir = Path.Combine(root, "versions", id);
            Directory.CreateDirectory(dir);
            var json = parent is null
                ? $$"""{"id":"{{id}}","mainClass":"net.minecraft.client.Main"}"""
                : $$"""{"id":"{{id}}","inheritsFrom":"{{parent}}"}""";
            File.WriteAllText(Path.Combine(dir, id + ".json"), json);
        }
        return root;
    }

    public void Dispose()
    {
        foreach (var d in _dirs) try { Directory.Delete(d, recursive: true); } catch { }
    }

    [Fact]
    public void 被继承的原版与加载器被判定为依赖()
    {
        var mc = MakeMinecraft(
            ("1.20.1", null),
            ("fabric-loader-0.15.11-1.20.1", "1.20.1"),
            ("我的整合包", "fabric-loader-0.15.11-1.20.1"));

        var hidden = VersionVisibility.ResolveHidden(mc);

        Assert.Contains("1.20.1", hidden);
        Assert.Contains("fabric-loader-0.15.11-1.20.1", hidden);
        Assert.DoesNotContain("我的整合包", hidden);
    }

    [Fact]
    public void 列表只保留整合包条目()
    {
        var mc = MakeMinecraft(
            ("1.20.1", null),
            ("fabric-loader-0.15.11-1.20.1", "1.20.1"),
            ("我的整合包", "fabric-loader-0.15.11-1.20.1"));
        var installed = new List<InstalledVersionInfo>
        {
            new("1.20.1", 0), new("fabric-loader-0.15.11-1.20.1", 0), new("我的整合包", 5_000_000),
        };

        var visible = VersionVisibility.Filter(mc, installed);

        var only = Assert.Single(visible);
        Assert.Equal("我的整合包", only.Id);
    }

    [Fact]
    public void 独立安装的原版不被隐藏()
    {
        var mc = MakeMinecraft(("1.20.1", null), ("1.21", null));
        var installed = new List<InstalledVersionInfo> { new("1.20.1", 0), new("1.21", 0) };

        Assert.Equal(2, VersionVisibility.Filter(mc, installed).Count);
        Assert.Empty(VersionVisibility.ResolveHidden(mc));
    }

    [Fact]
    public void 纯加载器且无自有内容时隐藏()
    {
        var mc = MakeMinecraft(
            ("1.20.1", null),
            ("neoforge-26.2.0.88", "1.20.1"),
            ("独立原版", null));
        var hidden = VersionVisibility.ResolveHidden(mc);
        Assert.Contains("neoforge-26.2.0.88", hidden);
        Assert.DoesNotContain("独立原版", hidden);
    }

    [Fact]
    public void 加载器里装了mod就不再隐藏()
    {
        var mc = MakeMinecraft(
            ("1.20.1", null),
            ("fabric-loader-0.15.11-1.20.1", null));
        Directory.CreateDirectory(Path.Combine(mc, "versions", "fabric-loader-0.15.11-1.20.1", "mods"));
        File.WriteAllText(Path.Combine(mc, "versions", "fabric-loader-0.15.11-1.20.1", "mods", "sodium.jar"), "x");

        Assert.DoesNotContain("fabric-loader-0.15.11-1.20.1", VersionVisibility.ResolveHidden(mc));
    }

    [Fact]
    public void 只有config或options的加载器仍按依赖隐藏()
    {
        var mc = MakeMinecraft(("1.20.1", null), ("neoforge-26.2.0.88", null));
        var dir = Path.Combine(mc, "versions", "neoforge-26.2.0.88");
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "a.toml"), "x");
        File.WriteAllText(Path.Combine(dir, "options.txt"), "fov:0.5");

        Assert.Contains("neoforge-26.2.0.88", VersionVisibility.ResolveHidden(mc));
    }

    [Fact]
    public void 显式依赖标记的版本被隐藏()
    {
        var mc = MakeMinecraft(("1.20.1", null), ("26.2", null));
        VersionVisibility.MarkDependency(mc, "26.2");
        Assert.Contains("26.2", VersionVisibility.ResolveHidden(mc));
    }

    [Fact]
    public void 过滤后为空时兜底显示全部()
    {
        // 只剩被继承的版本时不能把列表清空
        var mc = MakeMinecraft(
            ("1.20.1", null),
            ("fabric-loader-0.15.11-1.20.1", "1.20.1"));
        var installed = new List<InstalledVersionInfo> { new("1.20.1", 0) };

        var visible = VersionVisibility.Filter(mc, installed);

        Assert.Single(visible);
        Assert.Equal("1.20.1", visible[0].Id);
    }
}
