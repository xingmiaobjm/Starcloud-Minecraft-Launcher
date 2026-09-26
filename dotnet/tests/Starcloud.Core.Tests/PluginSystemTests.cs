// 插件系统 + 第三方登录（Yggdrasil）+ 账户存储 单元测试
// 覆盖：权限白名单/黑名单、身份参数拦截、manifest 校验、签名降级、
//       别名跳转与 3 跳防环、authenticate、皮肤纹理解码、离线账户身份与令牌不落盘。

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>测试全程把数据目录重定向到临时文件夹（LauncherPaths 支持环境变量），
/// 必须在任何 LauncherPaths 访问前执行 → ModuleInitializer。</summary>
internal static class TestDataDir
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "starcloud-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("STARCLOUD_DATA_DIR", dir);
    }
}

// ── 权限白名单 / 身份参数拦截 ──────────────────────────────────

public class PluginPermissionsTests
{
    [Theory]
    [InlineData("launcher:game_arguments", true)]
    [InlineData("launcher:ui_panel", true)]
    [InlineData("launcher:notification", true)]
    [InlineData("launcher:file_access", true)]
    [InlineData("launcher:account_verify", false)]
    [InlineData("launcher:auth_override", false)]
    [InlineData("launcher:uuid_override", false)]
    [InlineData("launcher:make_coffee", false)]
    public void IsAllowed_按白名单判定(string perm, bool allowed)
    {
        Assert.Equal(allowed, PluginPermissions.IsAllowed(perm));
        if (allowed) Assert.False(PluginPermissions.IsForbidden(perm));
    }

    [Fact]
    public void 身份校验类权限一律禁止()
    {
        foreach (var p in PluginPermissions.Forbidden)
            Assert.True(PluginPermissions.IsForbidden(p));
    }

    [Theory]
    [InlineData("--username Steve", "--username")]
    [InlineData("--accessToken=abc123", "--accessToken")]
    [InlineData("--uuid 069a79f4-44e9-4726-a5be-fca90e38aaf5", "--uuid")]
    [InlineData("--userType=legacy", "--userType")]
    [InlineData("--authlib-injector=https://example.org/yggdrasil", "--authlib-injector")]
    [InlineData("--AUTHSERVER https://x", "--authServer")] // 大小写不敏感
    [InlineData("-Xmx4G", "")]
    [InlineData("--workdir dir", "")]
    public void IsIdentityArg_识别身份参数(string arg, string matched)
    {
        var hit = PluginPermissions.IsIdentityArg(arg, out var key);
        Assert.Equal(matched.Length > 0, hit);
        Assert.Equal(matched, key);
    }
}

// ── 启动参数管理器（仅增量，无全量覆写） ──────────────────────

public class GameArgsManagerTests
{
    [Fact]
    public void Append_正常参数进入对应增量()
    {
        var m = new GameArgsManager();
        m.AppendJvm("-Xmx4G");
        m.AppendGame("--workdir data");
        Assert.Equal(new[] { "-Xmx4G" }, m.Jvm);
        Assert.Equal(new[] { "--workdir data" }, m.Game);
    }

    [Theory]
    [InlineData("--username Steve")]
    [InlineData("--accessToken=tok")]
    [InlineData("--authlib-injector https://evil.example")]
    public void Append_身份参数被拦截丢弃(string arg)
    {
        var m = new GameArgsManager();
        m.AppendGame(arg);
        m.AppendJvm(arg);
        Assert.Empty(m.Game);
        Assert.Empty(m.Jvm);
    }

    [Fact]
    public void RemoveKey_成对参数连同取值一并移除()
    {
        var m = new GameArgsManager();
        m.AppendGame("--workdir");
        m.AppendGame("some/dir");
        m.AppendGame("--keep");
        m.RemoveKey("--workdir");
        Assert.Equal(new[] { "--keep" }, m.Game);
    }

    [Fact]
    public void RemoveKey_等号形式一并移除且大小写不敏感()
    {
        var m = new GameArgsManager();
        m.AppendGame("--Custom=value");
        m.AppendGame("--keep");
        m.RemoveKey("--custom");
        Assert.Equal(new[] { "--keep" }, m.Game);
    }

    [Fact]
    public void RemoveKey_空键与未匹配键无副作用()
    {
        var m = new GameArgsManager();
        m.AppendGame("--keep");
        m.RemoveKey("");
        m.RemoveKey("  ");
        m.RemoveKey("--nope");
        Assert.Equal(new[] { "--keep" }, m.Game);
    }
}

// ── manifest 解析 / 校验 / 签名 ────────────────────────────────

public class PluginManifestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mclplug-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private string WriteManifest(string json)
    {
        var p = Path.Combine(_dir, "manifest.json");
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void ParseFile_缺失或坏JSON返回null()
    {
        Assert.Null(PluginManifest.ParseFile(Path.Combine(_dir, "nope.json")));
        Assert.Null(PluginManifest.ParseFile(WriteManifest("{ not json")));
    }

    [Fact]
    public void ParseFile_完整字段全部读出()
    {
        var path = WriteManifest("""
            {
              "id": "com.example.demo",
              "name": "Demo",
              "version": "1.2.3",
              "apiVersion": "1.0",
              "author": "Zhang",
              "description": "demo",
              "permissions": ["launcher:game_arguments"],
              "optionalPermissions": ["launcher:ui_panel"],
              "compatibility": { "minLauncherVersion": "0.5.0" },
              "requires_official_ai": true
            }
            """);
        var m = PluginManifest.ParseFile(path)!;
        Assert.Equal("com.example.demo", m.Id);
        Assert.Equal("1.2.3", m.Version);
        Assert.Equal("0.5.0", m.Compatibility.MinLauncherVersion);
        Assert.True(m.RequiresOfficialAi);
    }

    [Fact]
    public void Validate_合法清单通过()
    {
        var m = new PluginManifest
        {
            Id = "com.example.demo", Name = "d", Version = "1.0.0",
            ApiVersion = "1.0", Author = "a",
            Permissions = ["launcher:game_arguments", "launcher:ui_panel"],
        };
        Assert.Empty(m.Validate("0.5.0"));
    }

    [Fact]
    public void Validate_禁止权限与未知权限被拒()
    {
        var m = new PluginManifest
        {
            Id = "com.example.demo", Name = "d", Version = "1.0.0", ApiVersion = "1.0", Author = "a",
            Permissions = ["launcher:auth_override", "launcher:nuclear_launch"],
        };
        var errs = m.Validate("0.5.0");
        Assert.Contains(errs, e => e.Contains("launcher:auth_override"));
        Assert.Contains(errs, e => e.Contains("launcher:nuclear_launch"));
    }

    [Fact]
    public void Validate_id非法被拒()
    {
        var m = new PluginManifest { Id = "bad id", Name = "n", Version = "1", ApiVersion = "1.0", Author = "a" };
        Assert.Contains(m.Validate("0.5.0"), e => e.Contains("id 非法"));
    }

    [Fact]
    public void Validate_apiVersion不匹配被拒()
    {
        var m = new PluginManifest { Id = "com.x.y", Name = "n", Version = "1", ApiVersion = "9.9", Author = "a" };
        Assert.Contains(m.Validate("0.5.0"), e => e.Contains("apiVersion 不兼容"));
    }

    [Fact]
    public void Validate_requiresOfficialAi_非官方构建被拒()
    {
        // PluginEnvironment.OfficialChinaBuild 常量为 false（非官方国内构建）
        var m = new PluginManifest { Id = "com.x.ai", Name = "n", Version = "1", ApiVersion = "1.0", Author = "a", RequiresOfficialAi = true };
        if (!PluginEnvironment.OfficialChinaBuild)
            Assert.Contains(m.Validate("0.5.0"), e => e.Contains("仅官方国内发布版"));
    }

    [Fact]
    public void Validate_minLauncherVersion_不足被拒()
    {
        var m = new PluginManifest
        {
            Id = "com.x.y", Name = "n", Version = "1", ApiVersion = "1.0", Author = "a",
            Compatibility = new PluginCompatibility { MinLauncherVersion = "999.0.0" },
        };
        Assert.Contains(m.Validate("0.5.0"), e => e.Contains("需要启动器"));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("1.2", "1.2.1", false)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("0.5.0", "", true)]
    [InlineData("0.5.0", "0.0.0", true)]
    public void VersionAtLeast_数字段逐位比较(string actual, string required, bool expected) =>
        Assert.Equal(expected, PluginManifest.VersionAtLeast(actual, required));

    [Fact]
    public void VerifyOfficialSignature_无签名或未注入公钥返回false()
    {
        var path = WriteManifest("""{"id":"com.x.y"}""");
        Assert.False(new PluginManifest().VerifyOfficialSignature(path));
        // 官方公钥未注入（null）时一律按「第三方未验证」降级，不阻断加载
        Assert.False(new PluginManifest { Signature = "AAAA" }.VerifyOfficialSignature(path));
    }
}

// ── Yggdrasil 第三方登录 ───────────────────────────────────────

public class YggdrasilServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, (int Status, string Body)> Routes = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            // Uri 会给无路径的根地址补尾斜杠（https://a → https://a/），规范化后再匹配
            var url = req.RequestUri!.ToString().TrimEnd('/');
            var (status, body) = Routes.TryGetValue(url, out var r) ? r : (404, "{}");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Base64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task ResolveServerAsync_别名跳转到真实base并取展示名()
    {
        var h = new FakeHandler();
        h.Routes["https://alias.example"] = (200, """{"serverLocation":"https://real.example/api/yggdrasil"}""");
        h.Routes["https://real.example/api/yggdrasil"] = (200, """{"meta":{"serverName":"真实服"}}""");
        var ygg = new YggdrasilService(h);

        var server = await ygg.ResolveServerAsync("alias.example");
        Assert.Equal("https://real.example/api/yggdrasil", server.Base);
        Assert.Equal("真实服", server.ServerName);
    }

    [Fact]
    public async Task ResolveServerAsync_无别名直接返回根()
    {
        var h = new FakeHandler();
        h.Routes["https://plain.example/api"] = (200, """{"meta":{"serverName":"Plain"}}""");
        var ygg = new YggdrasilService(h);

        var server = await ygg.ResolveServerAsync("https://plain.example/api");
        Assert.Equal("https://plain.example/api", server.Base);
        Assert.Equal("Plain", server.ServerName);
    }

    [Fact]
    public async Task ResolveServerAsync_环状跳转超过3层抛异常()
    {
        var h = new FakeHandler();
        h.Routes["https://a.example"] = (200, """{"serverLocation":"https://b.example"}""");
        h.Routes["https://b.example"] = (200, """{"serverLocation":"https://c.example"}""");
        h.Routes["https://c.example"] = (200, """{"serverLocation":"https://a.example"}""");
        var ygg = new YggdrasilService(h);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ygg.ResolveServerAsync("https://a.example"));
        Assert.Contains("3 层", ex.Message);
    }

    [Fact]
    public async Task ResolveServerAsync_空地址与非Yggdrasil地址报中文异常()
    {
        var empty = new YggdrasilService(new FakeHandler());
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.ResolveServerAsync("  "));

        var h = new FakeHandler();
        h.Routes["https://plain.example"] = (200, "<html>not yggdrasil</html>");
        var ygg = new YggdrasilService(h);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ygg.ResolveServerAsync("https://plain.example"));
        Assert.Contains("不是有效的 Yggdrasil", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_成功返回会话与角色()
    {
        var h = new FakeHandler();
        h.Routes["https://real.example/api/yggdrasil/authserver/authenticate"] = (200, """
            {
              "accessToken": "tok-1",
              "clientToken": "ct-1",
              "selectedProfile": { "id": "pid-1", "name": "Alice" }
            }
            """);
        var ygg = new YggdrasilService(h);

        var r = await ygg.AuthenticateAsync("https://real.example/api/yggdrasil", "alice", "pwd", "ct-1");
        Assert.Equal("tok-1", r.AccessToken);
        Assert.Equal("ct-1", r.ClientToken);
        Assert.Equal("pid-1", r.ProfileId);
        Assert.Equal("Alice", r.ProfileName);
    }

    [Fact]
    public async Task AuthenticateAsync_无selectedProfile时取availableProfiles首个()
    {
        var h = new FakeHandler();
        h.Routes["https://s.example/authserver/authenticate"] = (200, """
            {
              "accessToken": "tok-2",
              "clientToken": "ct-2",
              "availableProfiles": [
                { "id": "pid-a", "name": "Al" },
                { "id": "pid-b", "name": "Bob" }
              ]
            }
            """);
        var ygg = new YggdrasilService(h);

        var r = await ygg.AuthenticateAsync("https://s.example", "u", "p", "ct-2");
        Assert.Equal("pid-a", r.ProfileId);
        Assert.Equal("Al", r.ProfileName);
    }

    [Fact]
    public async Task AuthenticateAsync_服务端errorMessage透出()
    {
        var h = new FakeHandler();
        h.Routes["https://s.example/authserver/authenticate"] = (403, """{"errorMessage":"密码错误"}""");
        var ygg = new YggdrasilService(h);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ygg.AuthenticateAsync("https://s.example", "u", "p", "ct"));
        Assert.Contains("密码错误", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_没有任何角色时报错()
    {
        var h = new FakeHandler();
        h.Routes["https://s.example/authserver/authenticate"] = (200, """{"accessToken":"t","clientToken":"c"}""");
        var ygg = new YggdrasilService(h);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ygg.AuthenticateAsync("https://s.example", "u", "p", "ct"));
    }

    [Fact]
    public async Task GetSkinUrlAsync_解码textures取SKIN_url()
    {
        var h = new FakeHandler();
        var uuid = "069a79f444e94726a5befca90e38aaf5";
        h.Routes[$"https://s.example/sessionserver/session/minecraft/profile/{uuid}"] = (200,
            $$"""
            {
              "properties": [
                { "name": "textures", "value": "{{Base64("""{"textures":{"SKIN":{"url":"https://skin.example/alice.png"}}}""")}}" }
              ]
            }
            """);
        var ygg = new YggdrasilService(h);

        Assert.Equal("https://skin.example/alice.png", await ygg.GetSkinUrlAsync("https://s.example", uuid));
    }

    [Fact]
    public async Task GetSkinUrlAsync_无皮肤或异常返回null()
    {
        var h = new FakeHandler();
        h.Routes["https://s.example/sessionserver/session/minecraft/profile/u1"] =
            (200, """{"properties":[{"name":"cape","value":"e30="}]}""");
        var ygg = new YggdrasilService(h);

        Assert.Null(await ygg.GetSkinUrlAsync("https://s.example", "u1"));
        Assert.Null(await ygg.GetSkinUrlAsync("https://s.example", "missing")); // 404 → null
    }
}

// ── JS 运行时无头冒烟（Jint 沙箱 + 上下文装配） ───────────────

public class JsPluginRuntimeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mclplug-js-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private string WriteEntry(string js)
    {
        var p = Path.Combine(_dir, "main.js");
        File.WriteAllText(p, js);
        return p;
    }

    private PluginContext Ctx(GameArgsManager? args = null) => new()
    {
        Permissions = ["launcher:game_arguments"],
        Launcher = new PluginLauncherInfo { Version = "0.5.0", ApiVersion = "1.0" },
        Args = args,
        Logger = new PluginLogger("js-smoke", LauncherPaths.PluginLogsDir),
    };

    [Fact]
    public void Load_onLoad钩子执行_返回null()
    {
        var rt = new JsPluginRuntime();
        Assert.Null(rt.Load(WriteEntry("function onLoad(ctx) { ctx.logger.info('loaded'); }"), Ctx()));
        rt.Dispose();
    }

    [Fact]
    public void InvokeHook_append追加_身份参数被拦截()
    {
        var args = new GameArgsManager();
        var rt = new JsPluginRuntime();
        Assert.Null(rt.Load(WriteEntry("""
            function onLoad(ctx) {}
            function beforeGameLaunch(ctx) {
                ctx.args.append('-Xmx4G');
                ctx.args.append('--username Hacker'); // 身份参数：必须被本体拦截
            }
            """), Ctx(args)));

        rt.InvokeHook("beforeGameLaunch", Ctx(args));
        Assert.Equal(new[] { "-Xmx4G" }, args.Game); // append 即 game 增量
        Assert.Empty(args.Jvm);
        rt.Dispose();
    }

    [Fact]
    public void Load_语法错误返回错误信息()
    {
        var rt = new JsPluginRuntime();
        var err = rt.Load(WriteEntry("function onLoad( { broken"), Ctx());
        Assert.NotNull(err);
        Assert.Contains("JS 加载失败", err);
        rt.Dispose();
    }

    [Fact]
    public void InvokeHook_未定义钩子为空操作()
    {
        var rt = new JsPluginRuntime();
        Assert.Null(rt.Load(WriteEntry("function onLoad(ctx) {}"), Ctx()));
        rt.InvokeHook("beforeGameLaunch", Ctx(new GameArgsManager())); // 无该钩子 → 不抛
        rt.Dispose();
    }
}

// ── PluginHost 生命周期（onLoad 面板注册时序回归） ─────────────

public class PluginHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mclplug-host-").FullName;
    private readonly PluginHost _host;

    public PluginHostTests() =>
        _host = new PluginHost("0.2.4",
            Path.Combine(_dir, "logs"), Path.Combine(_dir, "starcloud_host.py"));

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private PluginManifest Manifest(params string[] perms) => new()
    {
        Id = "com.test.host", Name = "T", Version = "1.0.0", ApiVersion = "1.0",
        Author = "a", Permissions = perms,
    };

    private string WritePlugin(string js)
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        File.WriteAllText(Path.Combine(_dir, "assets", "panel.html"), "<h1>t</h1>");
        File.WriteAllText(Path.Combine(_dir, "main.js"), js);
        return _dir;
    }

    [Fact]
    public void Start_onLoad内注册面板成功()
    {
        var dir = WritePlugin(
            "function onLoad(ctx) { ctx.ui.registerPanel('p1', '我的面板', 'assets/panel.html'); }");
        var err = _host.Start(Manifest("launcher:ui_panel"), dir);
        Assert.Null(err);
        var panel = Assert.Single(_host.Panels);
        Assert.Equal("com.test.host", panel.PluginId);
        Assert.Equal("我的面板", panel.Title);
        Assert.True(File.Exists(panel.EntryPath)); // entry 已解析为绝对路径
    }

    [Fact]
    public void Start_onLoad内通知触发事件()
    {
        var dir = WritePlugin("function onLoad(ctx) { ctx.ui.notify('标题', '内容'); }");
        string? notified = null;
        _host.NotifyRequested += (_, title, _) => notified = title;
        var err = _host.Start(Manifest("launcher:notification"), dir);
        Assert.Null(err);
        Assert.Equal("标题", notified);
    }

    [Fact]
    public void Start_未声明ui权限时ctxUi为null_注册面板报错被捕获()
    {
        var dir = WritePlugin("function onLoad(ctx) { ctx.ui.registerPanel('p', 't', 'a.html'); }");
        var err = _host.Start(Manifest(), dir); // 无 ui_panel 权限 → ctx.ui = null
        Assert.NotNull(err);
        Assert.Contains("加载失败", err.Message); // JS TypeError: registerPanel of null
        Assert.Empty(_host.Panels); // 回滚：无残留面板登记
        Assert.False(_host.IsLoaded("com.test.host")); // 回滚：登记已清除
    }

    [Fact]
    public void Stop_回调onUnload并清空面板登记()
    {
        var dir = WritePlugin("""
            function onLoad(ctx) { ctx.ui.registerPanel('p1', 't', 'a.html'); }
            function onUnload(ctx) { ctx.logger.info('bye'); }
            """);
        _host.Start(Manifest("launcher:ui_panel"), dir);
        Assert.Single(_host.Panels);
        _host.Stop("com.test.host");
        Assert.Empty(_host.Panels);
        Assert.False(_host.IsLoaded("com.test.host"));
    }
}

// ── 账户存储：离线身份 + 令牌不落盘 ───────────────────────────

/// <summary>这些用例读写进程级 accounts.json，与其它写同一文件的用例串行执行</summary>
[Collection("进程内单例")]
public class AccountStoreTests
{
    [Fact]
    public void Load_保留离线账户并补算缺失的派生UUID()
    {
        File.WriteAllText(LauncherPaths.AccountsFile,
            """[{"uuid":"","username":"Notch","type":"offline"},{"uuid":"m1","username":"msa","type":"microsoft"}]""");

        var store = AccountStore.Load();

        Assert.Equal(2, store.Accounts.Count);
        var offline = store.Accounts.First(a => a.Type == AccountStore.OfflineType);
        Assert.Equal("Notch", offline.Username);
        // 官方 nameUUIDFromBytes 算法：与 Java 端 UUID.toString() 完全一致
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", offline.Uuid);
        // 迁移结果已回写（下次启动不必再算）
        Assert.Contains("b50ad385-829d-3141-a216-7e7d7539ba7f",
            File.ReadAllText(LauncherPaths.AccountsFile));
    }

    [Fact]
    public void 离线UUID算法与Java官方实现一致()
    {
        // 已知向量（Java: UUID.nameUUIDFromBytes("OfflinePlayer:Notch".getBytes(UTF_8)))
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", AccountStore.OfflineUuid("Notch"));
        // 大小写敏感（Java 端同样如此）：不同名字必须是不同身份
        Assert.NotEqual(AccountStore.OfflineUuid("Notch"), AccountStore.OfflineUuid("notch"));
        // 标准带连字符写法，且版本位 / 变体位符合 v3 UUID 规范
        var uuid = AccountStore.OfflineUuid("AB");
        Assert.Equal(36, uuid.Length);
        Assert.Equal('3', uuid[14]);
        Assert.Contains(uuid[19], "89ab");
    }

    [Fact]
    public void 离线用户名校验_3到16位字母数字下划线()
    {
        Assert.True(AccountStore.IsValidOfflineName("Player_1"));
        Assert.True(AccountStore.IsValidOfflineName("abc"));
        Assert.False(AccountStore.IsValidOfflineName("ab"));
        Assert.False(AccountStore.IsValidOfflineName("abcdefghijklmnopq"));
        Assert.False(AccountStore.IsValidOfflineName("玩家"));
        Assert.False(AccountStore.IsValidOfflineName("bad name"));
        Assert.False(AccountStore.IsValidOfflineName(""));
    }

    [Fact]
    public void 添加离线账户_按名字派生UUID并可设为默认()
    {
        var store = new AccountStore();
        var acc = store.AddOrUpdateOffline("Steve");

        Assert.Equal(AccountStore.OfflineType, acc.Type);
        Assert.Equal(AccountStore.OfflineUuid("Steve"), acc.Uuid);
        Assert.True(acc.IsDefault); // 第一个账户自动成为默认

        // 同名重复添加：不产生第二条，只更新
        store.AddOrUpdateOffline("Steve");
        Assert.Single(store.Accounts);

        Assert.Throws<ArgumentException>(() => store.AddOrUpdateOffline("x"));
    }

    [Fact]
    public void 磁盘文件只存身份信息_绝不含令牌()
    {
        var store = new AccountStore();
        store.AddOrUpdateExternal("uuid-1", "Alice", "https://s.example/api", "https://s.example", "服");
        store.AddOrUpdateMicrosoft("uuid-2", "Bob");
        store.SetSession("uuid-1", new AccountSession("SECRET_ACCESS_TOKEN"));
        store.Save();

        var text = File.ReadAllText(LauncherPaths.AccountsFile);
        Assert.DoesNotContain("SECRET_ACCESS_TOKEN", text);
        Assert.DoesNotContain("accessToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client_token", text); // 设备标识允许落盘（非鉴权 token）
    }

    [Fact]
    public void SetDefault_可切换默认账户()
    {
        var store = new AccountStore();
        var a = store.AddOrUpdateMicrosoft("u1", "A");
        store.AddOrUpdateMicrosoft("u2", "B");
        Assert.True(a.IsDefault); // 首个账户自动成为默认
        store.SetDefault("u2");
        Assert.False(store.Accounts.First(x => x.Uuid == "u1").IsDefault);
        Assert.True(store.Accounts.First(x => x.Uuid == "u2").IsDefault);
    }

    // ── 离线启动身份解析 ──────────────────────────────────────────

    [Fact]
    public async Task 离线模式解析身份不要求任何会话凭证()
    {
        var store = new AccountStore();
        store.AddOrUpdateOffline("Steve"); // 只写身份，绝不写会话（离线本来就没有凭证）
        var svc = new LaunchAccountService(store, () => new LauncherSettings { LastUsername = null });
        svc.Mode = "offline";

        var player = await svc.ResolveAsync();

        Assert.Equal("Steve", player.Name);
        Assert.NotNull(player.Credentials);
        Assert.Equal(store.Accounts[0].Uuid, player.Credentials!.Uuid);
        Assert.Equal("legacy", player.Credentials.UserType);
        Assert.False(string.IsNullOrEmpty(player.Credentials.AccessToken)); // 占位令牌必须非空
        Assert.Null(player.ExternalBase); // 离线身份不注入 authlib-injector
    }

    [Fact]
    public async Task 离线模式无离线账户时给出可读错误()
    {
        var store = new AccountStore();
        store.AddOrUpdateMicrosoft("u1", "A");
        var svc = new LaunchAccountService(store, () => new LauncherSettings());
        svc.Mode = "offline";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ResolveAsync());
        Assert.Contains("离线账户", ex.Message);
    }

    [Fact]
    public async Task 在线模式缺凭证仍然要求就地登录()
    {
        var store = new AccountStore();
        store.AddOrUpdateOffline("Steve");
        var svc = new LaunchAccountService(store, () => new LauncherSettings());
        svc.Mode = "microsoft";

        // 默认账户是离线身份，与正版模式不匹配 → 提示先登录，而不是静默降级成离线
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ResolveAsync());
    }

    [Theory]
    [InlineData("ms:Alex", "microsoft")]
    [InlineData("ext:Alex", "external")]
    [InlineData("off:Alex", "offline")]
    [InlineData(null, "microsoft")]
    [InlineData("Alex", "microsoft")]
    public void 上次玩家名前缀决定默认模式(string? last, string expected) =>
        Assert.Equal(expected, LaunchAccountService.ModeOfLastUsername(last));

    [Fact]
    public void LastUsername前缀与模式一一对应() =>
        Assert.Equal("off:Steve", LaunchAccountService.PrefixFor("offline") + "Steve");
}
