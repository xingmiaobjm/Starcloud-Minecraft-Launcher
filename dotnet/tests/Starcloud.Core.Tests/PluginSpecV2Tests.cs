// 插件规范 2.0「模型与校验层」单测：
// 细粒度能力语法与风险分级、语义化版本区间、manifest 2.0 校验、整包摘要与签名三态、运行时版本闸门。

using System.Security.Cryptography;
using Starcloud.Core.Plugins;
using Xunit;

namespace Starcloud.Core.Tests;

// ── 2.0 细粒度能力 ─────────────────────────────────────────────

public class PluginCapabilityTests
{
    [Theory]
    [InlineData("instances:read", "instances", "read")]
    [InlineData("game:arguments", "game", "arguments")]
    [InlineData("network:any", "network", "any")]
    [InlineData("network:host=api.example.com", "network", "host")]
    [InlineData("network:host(host=*.example.com)", "network", "host")]
    [InlineData("storage:quota=2mb", "storage", "kv")]
    [InlineData("storage:kv(quota=256kb)", "storage", "kv")]
    [InlineData("instances:write(allow_delete)", "instances", "write")]
    public void TryParse_合法能力(string raw, string ns, string verb)
    {
        Assert.True(PluginPermissions.TryParse(raw, out var cap, out var error), error);
        Assert.Equal(ns, cap.Namespace);
        Assert.Equal(verb, cap.Verb);
        Assert.Equal(raw, cap.Raw);
    }

    [Fact]
    public void TryParse_allow_delete作为参数被记录()
    {
        Assert.True(PluginPermissions.TryParse("instances:write(allow_delete)", out var cap, out _));
        Assert.True(cap.HasParam("allow_delete"));
    }

    [Theory]
    [InlineData("", "空权限串")]
    [InlineData("instances", "格式非法")]
    [InlineData("launcher:network", "1.x 写法")]
    [InlineData("unknownns:read", "未知权限命名空间")]
    [InlineData("instances:destroy", "未知权限")]
    [InlineData("instances:read(allow_delete)", "不接受参数")]
    [InlineData("network:host=", "缺少参数")]
    [InlineData("network:host=http://evil.example", "主机名非法")]
    [InlineData("network:host=bad host", "主机名非法")]
    [InlineData("storage:kv(quota=100gb)", "超上限")]
    [InlineData("storage:kv(quota=abc)", "配额非法")]
    [InlineData("instances:write(allow_delete", "括号未闭合")]
    public void TryParse_非法能力被拒(string raw, string fragment)
    {
        Assert.False(PluginPermissions.TryParse(raw, out _, out var error));
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void TryParseAll_逐条累积错误()
    {
        var ok = PluginPermissions.TryParseAll(
            ["instances:read", "network:any", "bogus:read"], out var caps, out var errors);
        Assert.False(ok);
        Assert.Equal(2, caps.Count);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("instances:read", PluginRiskLevel.Low)]
    [InlineData("instances:write", PluginRiskLevel.Medium)]
    [InlineData("instances:write(allow_delete)", PluginRiskLevel.High)]
    [InlineData("saves:write(allow_delete)", PluginRiskLevel.High)]
    [InlineData("network:host=example.com", PluginRiskLevel.Low)]
    [InlineData("network:any", PluginRiskLevel.High)]
    [InlineData("settings:write", PluginRiskLevel.High)]
    [InlineData("ui:html", PluginRiskLevel.High)]
    [InlineData("tasks:spawn", PluginRiskLevel.Medium)]
    public void RiskOf_按目录与参数分级(string raw, PluginRiskLevel expected)
    {
        Assert.True(PluginPermissions.TryParse(raw, out var cap, out var error), error);
        Assert.Equal(expected, PluginPermissions.RiskOf(cap));
        Assert.Equal(expected == PluginRiskLevel.High, PluginPermissions.RequiresConfirmation(cap));
    }

    [Theory]
    [InlineData("256kb", 256)]
    [InlineData("2mb", 2048)]
    [InlineData("32mb", 32 * 1024)]
    [InlineData("512", 512)]
    public void TryParseQuota_单位换算(string text, int expectedKb)
    {
        Assert.True(PluginPermissions.TryParseQuota(text, out var kb, out var error), error);
        Assert.Equal(expectedKb, kb);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1mb")]
    [InlineData("33mb")]
    [InlineData("1gb")]
    [InlineData("")]
    public void TryParseQuota_越界或非法被拒(string text) =>
        Assert.False(PluginPermissions.TryParseQuota(text, out _, out _));

    [Fact]
    public void 粗粒度权限常量保持可用()
    {
        Assert.True(PluginPermissions.IsAllowed("launcher:network"));
        Assert.True(PluginPermissions.IsForbidden("launcher:uuid_override"));
    }
}

// ── 语义化版本区间 ─────────────────────────────────────────────

public class PluginVersionRangeTests
{
    [Theory]
    [InlineData("^1.2.3", "1.2.3", true)]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData(">=1.0.0 <2.0.0", "1.5.0", true)]
    [InlineData(">=1.0.0 <2.0.0", "2.0.0", false)]
    [InlineData(">=1.0.0, <2.0.0", "1.0.0", true)]
    [InlineData("*", "9.9.9", true)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    public void Matches_按区间判定(string range, string version, bool expected)
    {
        Assert.True(PluginVersionRange.TryParse(range, out var parsed, out var error), error);
        Assert.Equal(expected, parsed!.Matches(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("~")]
    [InlineData(">=")]
    [InlineData("abc")]
    public void TryParse_非法区间被拒(string range) =>
        Assert.False(PluginVersionRange.TryParse(range, out _, out _));
}

// ── manifest 2.0 ──────────────────────────────────────────────

public class PluginManifestV2Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mclplug-v2-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private static PluginManifest V2(
        string version = "2.1.0",
        string? engines = ">=0.3.0 <1.0.0",
        string[]? permissions = null,
        string? runtime = null,
        Dictionary<string, string>? dependencies = null,
        PluginContributes? contributes = null) => new()
    {
        Id = "com.example.demo", Name = "Demo", Version = version, ApiVersion = "2.0", Author = "a",
        Engines = engines is null ? null : new PluginEngines { Starcloud = engines },
        Runtime = runtime,
        Permissions = permissions ?? ["instances:read", "game:arguments", "network:host=api.example.com"],
        Dependencies = dependencies ?? [],
        Contributes = contributes,
    };

    [Fact]
    public void 合法2_0清单通过() => Assert.Empty(V2().Validate("0.5.0"));

    [Fact]
    public void version必须是严格语义化()
    {
        var errors = V2(version: "2.1").Validate("0.5.0");
        Assert.Contains(errors, e => e.Contains("严格语义化版本"));
    }

    [Fact]
    public void engines区间不满足被拒()
    {
        var errors = V2(engines: ">=1.0.0").Validate("0.5.0");
        Assert.Contains(errors, e => e.Contains("需要启动器版本"));
    }

    [Fact]
    public void engines缺失时回落1_x的minLauncherVersion()
    {
        var m = new PluginManifest
        {
            Id = "com.x.y", Name = "n", Version = "2.0.0", ApiVersion = "2.0", Author = "a",
            Compatibility = new PluginCompatibility { MinLauncherVersion = "0.9.0" },
        };
        Assert.Contains(m.Validate("0.5.0"), e => e.Contains("需要启动器 0.9.0"));
        Assert.Empty(m.Validate("0.9.1"));
    }

    [Fact]
    public void 旧写法权限被拒()
    {
        var errors = V2(permissions: ["launcher:network"]).Validate("0.5.0");
        Assert.Contains(errors, e => e.Contains("1.x 写法"));
    }

    [Fact]
    public void 未知runtime被拒()
    {
        var errors = V2(runtime: "lua").Validate("0.5.0");
        Assert.Contains(errors, e => e.Contains("未知 runtime"));
    }

    [Fact]
    public void 依赖id或区间非法被拒()
    {
        var errors = V2(dependencies: new Dictionary<string, string>
        {
            ["bad id"] = "^1.0.0",
            ["com.example.lib"] = "not-a-range",
        }).Validate("0.5.0");
        Assert.Contains(errors, e => e.Contains("插件 id 非法"));
        Assert.Contains(errors, e => e.Contains("版本区间非法"));
    }

    [Fact]
    public void 扩展点路径穿越被拒()
    {
        var contributes = new PluginContributes
        {
            Panels = [new PluginViewRef { Id = "main", Title = "t", View = "../evil.json" }],
        };
        var errors = V2(contributes: contributes).Validate("0.5.0");
        Assert.Contains(errors, e => e.Contains("必须是包内相对路径"));
    }

    [Fact]
    public void 未知挂载点被拒()
    {
        var contributes = new PluginContributes
        {
            Slots = [new PluginSlotRef { Point = "nowhere.top", View = "views/x.json" }],
            ContextMenu = [new PluginMenuRef { Point = "unknown.row", Action = "run" }],
        };
        var errors = V2(contributes: contributes).Validate("0.5.0");
        Assert.Contains(errors, e => e.Contains("未知挂载点"));
        Assert.Contains(errors, e => e.Contains("未知菜单挂载点"));
    }

    [Fact]
    public void 合法扩展点通过()
    {
        var contributes = new PluginContributes
        {
            Panels = [new PluginViewRef { Id = "main", Title = "t", View = "panels/main.json" }],
            Slots = [new PluginSlotRef { Point = "download.top", View = "views/banner.json", Height = 120 }],
            HomeCards = [new PluginViewRef { Id = "hello", Title = "h", View = "views/hello.json" }],
            Settings = new PluginSettingsRef { Schema = "settings.schema.json" },
        };
        Assert.Empty(V2(contributes: contributes).Validate("0.5.0"));
    }

    [Fact]
    public void ParseFile_2_0完整字段读出()
    {
        var path = Path.Combine(_dir, "manifest.json");
        File.WriteAllText(path, """
            {
              "id": "com.example.myplugin",
              "name": "My Plugin",
              "version": "2.1.0",
              "apiVersion": "2.0",
              "author": "Example",
              "license": "MIT",
              "engines": { "starcloud": ">=0.4.0 <1.0.0" },
              "runtime": "js",
              "i18n": { "zh-CN": { "name": "我的插件", "description": "简短描述" } },
              "keywords": ["ui", "optimization"],
              "permissions": ["instances:read", "storage:quota=2mb"],
              "dependencies": { "com.example.lib": "^1.2.0" },
              "conflicts": { "com.example.rival": "*" },
              "contributes": { "panels": [{ "id": "main", "title": "我的面板", "view": "panels/main.json" }] },
              "updates": { "manifestUrl": "https://example.com/update.json" }
            }
            """);
        var m = PluginManifest.ParseFile(path)!;
        Assert.True(m.IsApi2);
        Assert.Equal("MIT", m.License);
        Assert.Equal("js", m.Runtime);
        Assert.Equal(">=0.4.0 <1.0.0", m.Engines!.Starcloud);
        Assert.Equal("我的插件", m.I18n!["zh-CN"].Name);
        Assert.Equal("main", m.Contributes!.Panels[0].Id);
        Assert.Equal("^1.2.0", m.Dependencies["com.example.lib"]);
        Assert.Equal("https://example.com/update.json", m.Updates!.ManifestUrl);
        Assert.Empty(m.Validate("0.5.0"));
    }

    [Fact]
    public void 旧版清单仍按老规则校验()
    {
        var m = new PluginManifest
        {
            Id = "com.example.old", Name = "Old", Version = "1.0", ApiVersion = "1.0", Author = "a",
            Permissions = ["launcher:game_arguments"],
        };
        Assert.False(m.IsApi2);
        Assert.Empty(m.Validate("0.5.0"));
    }
}

// ── 整包摘要与签名 ─────────────────────────────────────────────

public class PluginIntegrityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mclplug-int-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Compute_跳过integrity自身与data目录()
    {
        Write("manifest.json", """{"id":"com.x.y"}""");
        Write("main.js", "export function onLoad(){}");
        Write("data/cache.txt", "runtime-only");
        File.WriteAllText(Path.Combine(_dir, PluginIntegrity.FileName), "{}");

        var integrity = PluginIntegrity.Compute(_dir, "com.x.y", "1.0.0");
        Assert.Contains("manifest.json", integrity.Files.Keys);
        Assert.Contains("main.js", integrity.Files.Keys);
        Assert.DoesNotContain("data/cache.txt", integrity.Files.Keys);
        Assert.DoesNotContain(PluginIntegrity.FileName, integrity.Files.Keys);
        Assert.Equal(integrity.Package, PluginIntegrity.ComputePackage(integrity.Files));
        Assert.Empty(integrity.VerifyFiles(_dir));
    }

    [Fact]
    public void ComputePackage_与路径遍历顺序无关()
    {
        var a = new Dictionary<string, string> { ["b.txt"] = "1", ["a.txt"] = "2" };
        var b = new Dictionary<string, string> { ["a.txt"] = "2", ["b.txt"] = "1" };
        Assert.Equal(PluginIntegrity.ComputePackage(a), PluginIntegrity.ComputePackage(b));
    }

    [Fact]
    public void VerifyFiles_改一个字节即失效()
    {
        Write("manifest.json", "{ }");
        Write("main.js", "console.log(1)");
        var integrity = PluginIntegrity.Compute(_dir, "com.x.y", "1.0.0");
        Assert.Empty(integrity.VerifyFiles(_dir));

        Write("main.js", "console.log(2)");
        Assert.Contains(integrity.VerifyFiles(_dir), e => e.Contains("main.js"));
    }

    [Fact]
    public void VerifyFiles_多出未登记文件被发现()
    {
        Write("a.txt", "1");
        var integrity = PluginIntegrity.Compute(_dir, "com.x.y", "1.0.0");
        Write("b.txt", "2");
        Assert.Contains(integrity.VerifyFiles(_dir), e => e.Contains("多出未登记文件"));
    }

    [Fact]
    public void VerifyFiles_缺失文件被发现()
    {
        Write("a.txt", "1");
        Write("b.txt", "2");
        var integrity = PluginIntegrity.Compute(_dir, "com.x.y", "1.0.0");
        File.Delete(Path.Combine(_dir, "b.txt"));
        Assert.Contains(integrity.VerifyFiles(_dir), e => e.Contains("缺少文件"));
    }

    [Fact]
    public void BuildSignaturePayload_键升序且无空白() =>
        Assert.Equal("""{"id":"com.x.y","package":"ab","version":"1.2.3"}""",
            PluginIntegrity.BuildSignaturePayload("com.x.y", "1.2.3", "ab"));

    [Fact]
    public void 签名三态_未签名_已验证_无效()
    {
        Write("main.js", "console.log(1)");
        var integrity = PluginIntegrity.Compute(_dir, "com.x.y", "1.0.0");

        // 未签名
        Assert.Equal(PluginTrustState.Unsigned, integrity.Verify(out _));

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var value = Convert.ToBase64String(
            ecdsa.SignData(integrity.SignaturePayloadBytes(), HashAlgorithmName.SHA256));
        var keys = new Dictionary<string, string> { ["starcloud-test"] = publicKey };

        // 已验证
        var signed = new PluginIntegrity
        {
            Id = integrity.Id, Version = integrity.Version, Package = integrity.Package,
            Files = integrity.Files,
            Signature = new PluginSignature { KeyId = "starcloud-test", Value = value },
        };
        Assert.Equal(PluginTrustState.Verified, signed.Verify(out var detail, keys));
        Assert.Contains("starcloud-test", detail);

        // 内容被替换 → package 变 → 签名无效（拒绝加载）
        var tampered = new PluginIntegrity
        {
            Id = integrity.Id, Version = integrity.Version, Package = new string('0', 64),
            Files = integrity.Files,
            Signature = new PluginSignature { KeyId = "starcloud-test", Value = value },
        };
        Assert.Equal(PluginTrustState.Invalid, tampered.Verify(out _, keys));

        // 未知 keyId → 未签名（提示升级，不误判为无效）
        var unknown = new PluginIntegrity
        {
            Id = integrity.Id, Version = integrity.Version, Package = integrity.Package,
            Files = integrity.Files,
            Signature = new PluginSignature { KeyId = "rotated-key", Value = value },
        };
        Assert.Equal(PluginTrustState.Unsigned, unknown.Verify(out _, keys));
    }

    [Fact]
    public void ParseFile_缺失或坏JSON返回null()
    {
        Assert.Null(PluginIntegrity.ParseFile(Path.Combine(_dir, "nope.json")));
        File.WriteAllText(Path.Combine(_dir, PluginIntegrity.FileName), "{ not json");
        Assert.Null(PluginIntegrity.ParseFile(Path.Combine(_dir, PluginIntegrity.FileName)));
    }
}

// ── 依赖解析与冲突 ─────────────────────────────────────────────

public class PluginDependencyResolverTests
{
    private static PluginManifest P(string id, string version = "1.0.0",
        Dictionary<string, string>? dependencies = null,
        Dictionary<string, string>? conflicts = null) => new()
    {
        Id = id, Name = id, Version = version, ApiVersion = "1.0", Author = "a",
        Dependencies = dependencies ?? [],
        Conflicts = conflicts ?? [],
    };

    [Fact]
    public void 依赖先于被依赖者()
    {
        var resolution = PluginDependencyResolver.Resolve(
        [
            P("com.x.a", dependencies: new Dictionary<string, string> { ["com.x.b"] = "^1.0.0" }),
            P("com.x.b"),
        ]);
        Assert.Empty(resolution.Blocked);
        Assert.Equal(["com.x.b", "com.x.a"], resolution.LoadOrder);
    }

    [Fact]
    public void 多级依赖拓扑排序()
    {
        var resolution = PluginDependencyResolver.Resolve(
        [
            P("com.x.c", dependencies: new Dictionary<string, string> { ["com.x.b"] = "*" }),
            P("com.x.b", dependencies: new Dictionary<string, string> { ["com.x.a"] = "*" }),
            P("com.x.a"),
        ]);
        Assert.Equal(["com.x.a", "com.x.b", "com.x.c"], resolution.LoadOrder);
    }

    [Fact]
    public void 未安装依赖被阻断()
    {
        var resolution = PluginDependencyResolver.Resolve(
            [P("com.x.a", dependencies: new Dictionary<string, string> { ["com.x.missing"] = "^1.0.0" })]);
        Assert.Contains("未安装依赖 com.x.missing", resolution.Blocked["com.x.a"]);
        Assert.Empty(resolution.LoadOrder);
    }

    [Fact]
    public void 依赖未启用被阻断()
    {
        var resolution = PluginDependencyResolver.Resolve(
            [
                P("com.x.a", dependencies: new Dictionary<string, string> { ["com.x.b"] = "*" }),
                P("com.x.b"),
            ],
            id => id == "com.x.a");
        Assert.Contains("依赖 com.x.b 未启用", resolution.Blocked["com.x.a"]);
    }

    [Fact]
    public void 依赖版本不满足被阻断()
    {
        var resolution = PluginDependencyResolver.Resolve(
        [
            P("com.x.a", dependencies: new Dictionary<string, string> { ["com.x.b"] = "^2.0.0" }),
            P("com.x.b", version: "1.4.0"),
        ]);
        Assert.Contains("版本不满足", resolution.Blocked["com.x.a"]);
    }

    [Fact]
    public void 循环依赖全部被阻断()
    {
        var resolution = PluginDependencyResolver.Resolve(
        [
            P("com.x.a", dependencies: new Dictionary<string, string> { ["com.x.b"] = "*" }),
            P("com.x.b", dependencies: new Dictionary<string, string> { ["com.x.a"] = "*" }),
        ]);
        Assert.Contains("循环依赖", resolution.Blocked["com.x.a"]);
        Assert.Contains("循环依赖", resolution.Blocked["com.x.b"]);
        Assert.Empty(resolution.LoadOrder);
    }

    [Fact]
    public void 声明冲突的一方被阻断()
    {
        var resolution = PluginDependencyResolver.Resolve(
        [
            P("com.x.a", conflicts: new Dictionary<string, string> { ["com.x.b"] = "*" }),
            P("com.x.b"),
        ]);
        Assert.Contains("冲突", resolution.Blocked["com.x.a"]);
        Assert.False(resolution.Blocked.ContainsKey("com.x.b"));
        Assert.Equal(["com.x.b"], resolution.LoadOrder);
    }

    [Fact]
    public void 依赖不可用时传递阻断()
    {
        var resolution = PluginDependencyResolver.Resolve(
        [
            P("com.x.a", dependencies: new Dictionary<string, string> { ["com.x.b"] = "*" }),
            P("com.x.b", dependencies: new Dictionary<string, string> { ["com.x.missing"] = "*" }),
        ]);
        Assert.Contains("未安装依赖 com.x.missing", resolution.Blocked["com.x.b"]);
        Assert.Contains("依赖 com.x.b 当前不可用", resolution.Blocked["com.x.a"]);
    }

    [Fact]
    public void 无依赖时按id升序且不阻断()
    {
        var resolution = PluginDependencyResolver.Resolve([P("com.x.b"), P("com.x.a")]);
        Assert.Empty(resolution.Blocked);
        Assert.Equal(["com.x.a", "com.x.b"], resolution.LoadOrder);
    }
}


// ── 运行时 API 版本闸门 ───────────────────────────────────────

public class PluginHostApiGateTests
{
    [Fact]
    public void 未来版本拒绝启用且不加载()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-gate-").FullName;
        try
        {
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            var manifest = new PluginManifest
            {
                Id = "com.example.demo", Name = "n", Version = "1.0.0",
                ApiVersion = "3.0", Author = "a",
            };
            var error = host.Start(manifest, dir);
            Assert.NotNull(error);
            Assert.Contains("API 3.0", error!.Message);
            Assert.False(host.IsLoaded("com.example.demo"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 二点零已可加载且声明式面板随之注册()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-v2-").FullName;
        try
        {
            // 纯声明式插件：只有 contributes，没有入口脚本也应当能加载
            File.WriteAllText(Path.Combine(dir, "view.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.v2", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a",
                Permissions = ["ui:panel"],
                Contributes = new PluginContributes
                {
                    Panels = [new PluginViewRef { Id = "main", Title = "面板", View = "view.json" }],
                },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            var error = host.Start(manifest, dir);
            Assert.Null(error);
            Assert.True(host.IsLoaded("com.example.v2"));
            var panel = Assert.Single(host.Panels);
            Assert.True(panel.Declarative);
            Assert.Equal("面板", panel.Title);
            Assert.Equal(Path.Combine(dir, "view.json"), panel.EntryPath);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 二点零声明式挂载区随清单注册()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-slot-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "banner.json"), """{ "type": "text", "value": "hi" }""");
            File.WriteAllText(Path.Combine(dir, "sec.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.slot", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a",
                Permissions = ["ui:panel"],
                Contributes = new PluginContributes
                {
                    Slots =
                    [
                        new PluginSlotRef { Point = "versions.top", View = "banner.json", Height = 96 },
                        new PluginSlotRef { Point = "who.knows", View = "banner.json" },
                    ],
                    VersionSections = [new PluginViewRef { Id = "s", Title = "分区", View = "sec.json" }],
                },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));

            var slot = Assert.Single(host.GetSlots("versions.top"));
            Assert.True(slot.Declarative);
            Assert.Equal(96, slot.Height);
            Assert.Empty(host.GetSlots("who.knows")); // 宿主不支持的挂载点被跳过

            var section = Assert.Single(host.GetSlots(PluginHost.VersionSectionsPoint));
            Assert.Equal("分区", section.Title);
            Assert.Equal(0, section.Height);

            host.Stop("com.example.slot");
            Assert.Empty(host.GetSlots("versions.top")); // 停用即摘掉
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 没有ui面板能力则不注册挂载区()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-slot-denied-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "banner.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.noslot", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a",
                Permissions = ["storage:kv"],
                Contributes = new PluginContributes
                {
                    Slots = [new PluginSlotRef { Point = "download.top", View = "banner.json" }],
                },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));
            Assert.Empty(host.GetSlots("download.top"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 二点零首页卡片随清单注册()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-cards-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), """{ "type": "text", "value": "hi" }""");
            File.WriteAllText(Path.Combine(dir, "b.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.cards", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a",
                Permissions = ["ui:panel"],
                Contributes = new PluginContributes
                {
                    HomeCards =
                    [
                        new PluginViewRef { Id = "one", Title = "今日推荐", View = "a.json" },
                        new PluginViewRef { Id = "two", View = "b.json" },                 // 没写 title
                        new PluginViewRef { Id = "one", Title = "覆盖", View = "b.json" },  // 同 id 覆盖
                        new PluginViewRef { Id = "", Title = "无 id", View = "b.json" },    // 缺 id 跳过
                    ],
                },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));

            var cards = host.GetHomeCards();
            Assert.Equal(2, cards.Count); // 同一插件可声明多张；缺 id 的条目被跳过、同 id 只留一条

            var one = Assert.Single(cards, c => c.CardId == "one");
            Assert.Equal("覆盖", one.Title);
            Assert.Equal(Path.Combine(dir, "b.json"), one.EntryPath);

            var two = Assert.Single(cards, c => c.CardId == "two");
            Assert.Equal("two", two.Title); // 缺 title 回落成 id

            host.Stop("com.example.cards");
            Assert.Empty(host.GetHomeCards()); // 停用即摘掉
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 多插件首页卡片共存并按加载顺序排列()
    {
        var dirA = Directory.CreateTempSubdirectory("mclplug-cards-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("mclplug-cards-b-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dirA, "a.json"), """{ "type": "text", "value": "hi" }""");
            File.WriteAllText(Path.Combine(dirB, "b.json"), """{ "type": "text", "value": "hi" }""");
            var a = new PluginManifest
            {
                Id = "com.example.a", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes
                {
                    HomeCards = [new PluginViewRef { Id = "ca", Title = "A 卡", View = "a.json" }],
                },
            };
            var b = new PluginManifest
            {
                Id = "com.example.b", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a", Permissions = ["ui:panel"],
                Contributes = new PluginContributes
                {
                    HomeCards = [new PluginViewRef { Id = "cb", Title = "B 卡", View = "b.json" }],
                },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dirA, "logs"), "");
            Assert.Null(host.Start(a, dirA));
            Assert.Null(host.Start(b, dirB));

            var cards = host.GetHomeCards();
            Assert.Equal(2, cards.Count); // 不同插件的卡片都收下（张数上限由宿主卡片带决定）
            Assert.Equal("A 卡", cards[0].Title);
            Assert.Equal("B 卡", cards[1].Title);

            host.Stop("com.example.a");
            var left = Assert.Single(host.GetHomeCards());
            Assert.Equal("B 卡", left.Title); // 只摘掉自己那几张
        }
        finally
        {
            try { Directory.Delete(dirA, true); } catch { /* 清理失败忽略 */ }
            try { Directory.Delete(dirB, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void 没有ui面板能力则不注册首页卡片()
    {
        var dir = Directory.CreateTempSubdirectory("mclplug-cards-denied-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "card.json"), """{ "type": "text", "value": "hi" }""");
            var manifest = new PluginManifest
            {
                Id = "com.example.nocards", Name = "n", Version = "2.0.0",
                ApiVersion = "2.0", Author = "a",
                Permissions = ["storage:kv"],
                Contributes = new PluginContributes
                {
                    HomeCards = [new PluginViewRef { Id = "c", Title = "卡", View = "card.json" }],
                },
            };
            using var host = new PluginHost("0.5.0", Path.Combine(dir, "logs"), "");
            Assert.Null(host.Start(manifest, dir));
            Assert.Empty(host.GetHomeCards());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }
}
