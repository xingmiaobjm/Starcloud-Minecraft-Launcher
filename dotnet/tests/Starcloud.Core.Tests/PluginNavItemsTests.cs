// 插件 2.0「一级导航项（navItems）」单测：
// 清单校验、注册与停用、ui:panel 闸门、图标白名单与回落、路由 tag 的拼/解、跨插件共存。

using Starcloud.Core.Plugins;
using Xunit;

namespace Starcloud.Core.Tests;

public class PluginNavItemsTests
{
    /// <summary>用一组 navItems 造一份 2.0 清单，只取校验错误</summary>
    private static List<string> ValidateNav(params PluginNavRef[] navs) => new PluginManifest
    {
        Id = "com.example.nav", Name = "n", Version = "2.0.0", ApiVersion = "2.0", Author = "a",
        Permissions = ["ui:panel"],
        Contributes = new PluginContributes { NavItems = navs },
    }.Validate("0.5.0");

    private static PluginNavRef Nav(string id, string title, string icon, string view) =>
        new() { Id = id, Title = title, Icon = icon, View = view };

    // ── 清单校验 ──────────────────────────────────────────────

    [Fact]
    public void 每插件最多一条导航项()
    {
        var errors = ValidateNav(
            Nav("a", "甲", "home", "views/a.json"),
            Nav("b", "乙", "log", "views/b.json"));
        Assert.Contains(errors, e => e.Contains("contributes.navItems 最多 1 条"));
    }

    [Fact]
    public void 导航项必填字段缺一不可()
    {
        Assert.Contains(ValidateNav(Nav("", "t", "home", "views/x.json")),
            e => e.Contains("contributes.navItems 缺少 id"));
        Assert.Contains(ValidateNav(Nav("a", "", "home", "views/x.json")),
            e => e.Contains("contributes.navItems 缺少 title"));
        Assert.Contains(ValidateNav(Nav("a", "t", "", "views/x.json")),
            e => e.Contains("contributes.navItems 缺少 icon"));
        Assert.Contains(ValidateNav(Nav("a", "t", "home", "../evil.json")),
            e => e.Contains("必须是包内相对路径"));
    }

    [Fact]
    public void 合法导航项通过()
    {
        Assert.Empty(ValidateNav(Nav("servers", "服务器浏览", "server", "views/servers.json")));
    }

    // ── 注册与停用 ────────────────────────────────────────────

    [Fact]
    public void 导航项随清单注册()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-nav-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "servers.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.nav", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes
                {
                    NavItems = [Nav("servers", "", "server", "views/servers.json")], // 没写 title
                },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));

            var reg = Assert.Single(host.GetNavItems());
            Assert.Equal("servers", reg.NavId);
            Assert.Equal("servers", reg.Title);                         // 缺 title 回落成 id
            Assert.Equal("\uEB91", reg.IconGlyph);                      // 命中 server 白名单
            Assert.False(reg.IconFallback);
            Assert.Equal(Path.Combine(dir, "views", "servers.json"), reg.EntryPath); // 拼在插件目录下
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 停用即摘除导航项()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-nav-stop-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "v.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.nav", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes { NavItems = [Nav("v", "视图", "home", "v.json")] },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));
            Assert.Single(host.GetNavItems());

            host.Stop("com.example.nav");
            Assert.Empty(host.GetNavItems());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 重复注册不重复条目()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-nav-dup-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "v.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.nav", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes { NavItems = [Nav("v", "视图", "home", "v.json")] },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));
            Assert.Null(host.Start(manifest, dir));   // 连续启动两次
            Assert.Single(host.GetNavItems());          // 不重复
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 没有ui面板能力则不注册导航项()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-nav-denied-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "v.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.nav", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["storage:kv"],
                Contributes = new PluginContributes { NavItems = [Nav("v", "视图", "home", "v.json")] },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));
            Assert.Empty(host.GetNavItems());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 跨插件导航项共存并按加载顺序排列()
    {
        var dirA = Directory.CreateTempSubdirectory("mclplug-nav-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("mclplug-nav-b-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dirA, "a.json"), """{ "type": "text", "value": "hi" }""");
            File.WriteAllText(Path.Combine(dirB, "b.json"), """{ "type": "text", "value": "hi" }""");
            var a = new PluginManifest
            {
                Id = "com.example.a", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes { NavItems = [Nav("a", "甲", "home", "a.json")] },
            };
            var b = new PluginManifest
            {
                Id = "com.example.b", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes { NavItems = [Nav("b", "乙", "log", "b.json")] },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dirA, "logs"), "");
            Assert.Null(host.Start(a, dirA));
            Assert.Null(host.Start(b, dirB));

            var items = host.GetNavItems();
            Assert.Equal(2, items.Count);          // 跨插件上限由宿主导航栏截断，注册表全量收下
            Assert.Equal("com.example.a", items[0].PluginId);
            Assert.Equal("com.example.b", items[1].PluginId);
        }
        finally
        {
            try { Directory.Delete(dirA, true); } catch { /* 清理失败忽略 */ }
            try { Directory.Delete(dirB, true); } catch { /* 清理失败忽略 */ }
        }
    }

    // ── 图标白名单与回落 ──────────────────────────────────────

    [Fact]
    public void 图标白名单命中与回落()
    {
        Assert.True(PluginExtensionPoints.TryResolveNavIcon("server", out var server));
        Assert.Equal("\uEB91", server);

        Assert.False(PluginExtensionPoints.TryResolveNavIcon("nope", out var fallback));
        Assert.Equal("\uEABC", fallback);                       // 未命中 → 默认 extension

        Assert.False(PluginExtensionPoints.TryResolveNavIcon(null, out var nul));
        Assert.Equal("\uEABC", nul);                            // 未声明 → 默认 extension
    }

    [Fact]
    public void 图标名未命中时注册项带回落标记()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-nav-icon-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "v.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.nav", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes { NavItems = [Nav("v", "视图", "nope", "v.json")] },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));

            var reg = Assert.Single(host.GetNavItems());
            Assert.True(reg.IconFallback);
            Assert.Equal("\uEABC", reg.IconGlyph);   // 回落默认图标
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    // ── 路由 tag 的拼与解 ─────────────────────────────────────

    [Fact]
    public void 导航Tag拼接格式()
    {
        Assert.Equal("plugin:com.a:servers", PluginExtensionPoints.PluginNavTag("com.a", "servers"));
    }

    [Theory]
    [InlineData("home")]                 // 内置 tag
    [InlineData("plugin:")]              // 空插件 id
    [InlineData("plugin:a")]             // 缺第二段
    [InlineData("plugin::b")]            // 插件 id 为空
    [InlineData("plugin:a:")]            // 导航 id 为空
    [InlineData("")]                     // 空串
    [InlineData(null)]                   // null
    public void 非法Tag解析失败(string? tag)
    {
        Assert.False(PluginExtensionPoints.TryParseNavTag(tag, out var pluginId, out var navId));
        Assert.Equal("", pluginId);
        Assert.Equal("", navId);
    }

    [Fact]
    public void 合法Tag解析出两段()
    {
        Assert.True(PluginExtensionPoints.TryParseNavTag("plugin:com.a:servers", out var pluginId, out var navId));
        Assert.Equal("com.a", pluginId);
        Assert.Equal("servers", navId);
    }
}
