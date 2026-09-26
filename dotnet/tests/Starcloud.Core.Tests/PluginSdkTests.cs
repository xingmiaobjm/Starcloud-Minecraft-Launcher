// 插件 SDK 扩展测试（plugin-spec.md §7）：权限门控、能力回调、存储沙箱、事件、
// 设置过滤与账户身份脱敏，以及 JS / Python 两条运行时的一致性。
//
// 口径：这里只测「能力层」，不测具体业务服务（那些在各自的测试文件里）。

using System.Text.Json;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>每个测试一套独立的临时目录与服务包（数据根已由 ModuleInitializer 重定向）</summary>
internal sealed class SdkFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(),
        "starcloud-sdk-" + Guid.NewGuid().ToString("N")[..8]);

    public LauncherSettings Settings { get; } = new();

    public AccountStore Accounts { get; } = new();

    public ManifestService Manifest { get; }
    public InstallerService Installer { get; }
    public DownloadService Download { get; }
    public NovaToolRegistry Tools { get; }
    public PluginHostServices Services { get; }
    public PluginHost Host { get; }

    public SdkFixture()
    {
        Directory.CreateDirectory(Dir);
        Settings.DotMinecraft = Path.Combine(Dir, "dotminecraft");
        Directory.CreateDirectory(Settings.DotMinecraft);

        Download = new DownloadService(() => Settings);
        Manifest = new ManifestService(Download);
        Installer = new InstallerService(Download);
        var mods = new ModrinthService(Download);
        var library = new ContentLibraryService(Download, mods, Manifest, () => Settings);
        Tools = new NovaToolRegistry(new NovaToolContext
        {
            Manifest = Manifest,
            Installer = Installer,
            Mods = mods,
            Library = library,
            Settings = () => Settings,
            SaveSettings = () => Settings.Save(),
        });
        Services = new PluginHostServices
        {
            Settings = () => Settings,
            SaveSettings = () => Settings.Save(),
            Manifest = Manifest,
            Installer = Installer,
            Download = Download,
            Accounts = Accounts,
            Tools = Tools,
        };
        Host = new PluginHost("9.9.9", Path.Combine(Dir, "logs"), "", Services);
    }

    /// <summary>写一个 JS 插件目录并返回路径</summary>
    public string WriteJs(string js, string id = "com.test.sdk")
    {
        var dir = Path.Combine(Dir, "plugins", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.js"), js);
        return dir;
    }

    public static PluginManifest ManifestWith(params string[] permissions) => new()
    {
        Id = "com.test.sdk",
        Name = "SDK 测试插件",
        Version = "1.0.0",
        ApiVersion = PluginEnvironment.ApiVersion,
        Author = "tests",
        Permissions = permissions,
    };

    /// <summary>用给定权限装配一个 ctx（Python 能力回调与单元级断言共用）</summary>
    public PluginContext Context(params string[] permissions)
    {
        var api = new PluginApiCore(Services, "com.test.sdk", Path.Combine(Dir, "data"),
            new PluginLogger("com.test.sdk", Path.Combine(Dir, "logs")), permissions, Host, null);
        return new PluginContext
        {
            Permissions = permissions,
            Launcher = new PluginLauncherInfo { Version = "9.9.9" },
            Files = permissions.Contains(PluginPermissions.FileAccess)
                ? new PluginFiles(Path.Combine(Dir, "data"),
                    new PluginLogger("com.test.sdk", Path.Combine(Dir, "logs")))
                : null,
            Storage = permissions.Contains(PluginPermissions.Storage)
                ? new PluginStorageApi(api) : null,
            Settings = permissions.Contains(PluginPermissions.SettingsRead)
                || permissions.Contains(PluginPermissions.SettingsWrite)
                ? new PluginSettingsApi(api) : null,
            Accounts = permissions.Contains(PluginPermissions.AccountsRead)
                ? new PluginAccountsApi(api) : null,
            Instances = permissions.Contains(PluginPermissions.InstancesRead)
                ? new PluginInstancesApi(api) : null,
            Versions = permissions.Contains(PluginPermissions.InstancesRead)
                ? new PluginVersionsApi(api) : null,
            Game = permissions.Contains(PluginPermissions.GameLaunch)
                ? new PluginGameApi(api) : null,
            Tasks = permissions.Contains(PluginPermissions.Tasks)
                ? new PluginTasksApi(api) : null,
            Events = permissions.Contains(PluginPermissions.Events)
                ? new PluginEventsApi(api) : null,
            Logger = new PluginLogger("com.test.sdk", Path.Combine(Dir, "logs")),
        };
    }

    public void Dispose()
    {
        try { Host.Dispose(); } catch { /* 清理失败忽略 */ }
        try { Directory.Delete(Dir, recursive: true); } catch { /* 文件被占用忽略 */ }
    }
}

/// <summary>
/// ctx.tasks 读的是进程内单例任务中心，与既有 Singleton 测试串行执行，
/// 否则并行跑会互相看到对方刚建的任务。
/// </summary>
[Collection("进程内单例")]
public class PluginSdkTests
{
    // ── 权限门控 ─────────────────────────────────────────────

    [Fact]
    public void 未声明权限的能力成员为null()
    {
        using var fx = new SdkFixture();
        var ctx = fx.Context(); // 零权限

        Assert.Null(ctx.Instances);
        Assert.Null(ctx.Game);
        Assert.Null(ctx.Content);
        Assert.Null(ctx.Settings);
        Assert.Null(ctx.Accounts);
        Assert.Null(ctx.Storage);
        Assert.Null(ctx.Events);
        Assert.Null(ctx.Http);
        Assert.Null(ctx.Tasks);
    }

    [Fact]
    public void 绕过装配直接调用仍被权限门挡住()
    {
        using var fx = new SdkFixture();
        var api = new PluginApiCore(fx.Services, "com.test.sdk", fx.Dir,
            new PluginLogger("com.test.sdk", fx.Dir), [], fx.Host, null);

        // 成员装配是第一道门，方法内的 Require 是第二道门
        var storage = new PluginStorageApi(api);
        var ex = Assert.Throws<UnauthorizedAccessException>(() => storage.Set("k", 1));
        Assert.Contains("launcher:storage", ex.Message);

        var accounts = new PluginAccountsApi(api);
        Assert.Throws<UnauthorizedAccessException>(() => accounts.List());
    }

    // ── 存储沙箱 ─────────────────────────────────────────────

    [Fact]
    public void 插件存储可读写且锁在data目录内()
    {
        using var fx = new SdkFixture();
        var storage = fx.Context(PluginPermissions.Storage).Storage!;

        Assert.Null(storage.Get("missing"));
        storage.Set("count", 3);
        storage.Set("nested", new Dictionary<string, object?> { ["a"] = "b" });
        Assert.Equal(3, storage.Get("count"));
        Assert.Equal(["count", "nested"], storage.Keys().OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(storage.Delete("count"));
        Assert.False(storage.Delete("count"));

        // 键是数据不是路径：带 ../ 的键名只当普通键，不会写出去
        storage.Set("../escape", "still-inside");
        Assert.Equal("still-inside", storage.Get("../escape"));
        Assert.False(File.Exists(Path.Combine(fx.Dir, "escape.json")));

        // 真正会拼路径的是 ctx.files / ctx.download，越界一律拒绝
        var files = new PluginFiles(Path.Combine(fx.Dir, "data"),
            new PluginLogger("com.test.sdk", fx.Dir));
        Assert.Throws<UnauthorizedAccessException>(() => files.Read("../escape.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => files.Write("a/../../escape.txt", "x"));

        // 落盘位置就在插件 data 目录里
        Assert.True(File.Exists(Path.Combine(fx.Dir, "data", "storage.json")));
    }

    // ── 设置 ─────────────────────────────────────────────────

    [Fact]
    public void 设置读取过滤主机私有键()
    {
        using var fx = new SdkFixture();
        fx.Settings.NovaLlmApiKey = "SECRET-KEY";
        fx.Settings.CurseForgeApiKey = "CF-KEY";
        fx.Settings.Theme = "dark";

        var settings = fx.Context(PluginPermissions.SettingsRead).Settings!;
        var snapshot = settings.Get();

        Assert.Equal("dark", snapshot["theme"]);
        Assert.False(snapshot.ContainsKey("nova_llm_api_key"));
        Assert.False(snapshot.ContainsKey("curseforge_api_key"));
        Assert.Contains("nova_llm_api_key", (string[])snapshot["filtered"]!);
        // 双保险：整个返回值里不能出现密钥字面量
        Assert.DoesNotContain("SECRET-KEY", JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain("CF-KEY", JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void 设置写入拒绝主机私有键与未知键()
    {
        using var fx = new SdkFixture();
        var settings = fx.Context(PluginPermissions.SettingsWrite).Settings!;

        Assert.Throws<UnauthorizedAccessException>(() =>
            settings.Set("""{"nova_llm_api_key":"stolen"}"""));
        Assert.Throws<UnauthorizedAccessException>(() =>
            settings.Set("""{"update_manifest_url":"http://evil"}"""));
        Assert.Throws<UnauthorizedAccessException>(() => settings.Set("""{"不存在的键":1}"""));
        Assert.Throws<ArgumentException>(() => settings.Set("""["不是对象"]"""));
    }

    [Fact]
    public void 设置写入走与Nova同一套执行层()
    {
        using var fx = new SdkFixture();
        var settings = fx.Context(PluginPermissions.SettingsWrite).Settings!;

        var result = settings.Set("""{"theme":"dark"}""");

        Assert.True((bool)result["ok"]!);
        Assert.Equal("dark", fx.Settings.Theme);
    }

    // ── 账户身份脱敏 ─────────────────────────────────────────

    [Fact]
    public void 账户列表只有身份没有任何令牌()
    {
        using var fx = new SdkFixture();
        var ms = fx.Accounts.AddOrUpdateMicrosoft("uuid-ms", "Alex");
        fx.Accounts.AddOrUpdateOffline("Steve");
        fx.Accounts.SetSession(ms.Uuid, new AccountSession("SECRET-ACCESS-TOKEN", "SECRET-REFRESH", "9"));

        var list = fx.Context(PluginPermissions.AccountsRead).Accounts!.List();
        var json = JsonSerializer.Serialize(list);

        Assert.Equal(2, list.Length);
        Assert.Contains("Alex", json);
        Assert.Contains("Steve", json);
        Assert.DoesNotContain("SECRET", json);
        Assert.Equal(AccountStore.OfflineType,
            ((Dictionary<string, object?>)list[1]!)["type"]);
    }

    // ── Python 能力回调分发（capability 名 → ctx 成员方法） ──

    [Fact]
    public void 能力回调_按名字调用并做参数绑定()
    {
        using var fx = new SdkFixture();
        var ctx = fx.Context(PluginPermissions.Storage, PluginPermissions.AccountsRead);

        // storage.set("k", {"n":1}) —— 参数按位置绑定
        PluginHost.InvokeCapability(ctx, "storage.set", """["k",{"n":1}]""");
        var value = (Dictionary<string, object?>)PluginHost.InvokeCapability(ctx, "storage.get", """["k"]""")!;
        Assert.Equal(1, value["n"]);

        // accounts.list() —— 无参
        var accounts = (object?[])PluginHost.InvokeCapability(ctx, "accounts.list", "[]")!;
        Assert.NotNull(accounts);

        // 未声明的能力（成员为 null）如实报错
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            PluginHost.InvokeCapability(ctx, "game.launch", """["1.20"]"""));
        Assert.Contains("game", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 写错名字也要说清楚
        Assert.Throws<ArgumentException>(() =>
            PluginHost.InvokeCapability(ctx, "storage.setAll", "[]"));
    }

    [Fact]
    public void 能力回调_无上下文时给出可读错误()
    {
        using var fx = new SdkFixture();
        var reply = fx.Host.HostCall("com.test.sdk", "storage.get", """["k"]""");
        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("钩子", doc.RootElement.GetProperty("error").GetString()!);
    }

    // ── 事件 ─────────────────────────────────────────────────

    [Fact]
    public void JS插件_订阅事件后收到onLauncherEvent()
    {
        using var fx = new SdkFixture();
        var dir = fx.WriteJs("""
            function onLoad(ctx) { ctx.events.on('game.launched'); }
            function onLauncherEvent(ctx) {
                ctx.storage.set('event', ctx.event.name);
                ctx.storage.set('payload', ctx.event.data);
            }
            """);

        var err = fx.Host.Start(SdkFixture.ManifestWith(
            PluginPermissions.Events, PluginPermissions.Storage), dir);
        Assert.Null(err);
        Assert.Equal(["game.launched"], fx.Host.Subscriptions("com.test.sdk"));

        fx.Host.RaiseEvent("game.launched", new { versionId = "1.20.1", pid = 42 });

        var json = File.ReadAllText(Path.Combine(dir, "data", "storage.json"));
        Assert.Contains("game.launched", json);
        Assert.Contains("1.20.1", json);
        Assert.Contains("42", json);
    }

    [Fact]
    public void 未订阅的事件不投递()
    {
        using var fx = new SdkFixture();
        var dir = fx.WriteJs("""
            function onLoad(ctx) { ctx.events.off('game.exited'); }
            function onLauncherEvent(ctx) { ctx.storage.set('hit', 1); }
            """);
        fx.Host.Start(SdkFixture.ManifestWith(
            PluginPermissions.Events, PluginPermissions.Storage), dir);

        fx.Host.RaiseEvent("game.exited", new { versionId = "1.20.1" });

        Assert.False(File.Exists(Path.Combine(dir, "data", "storage.json")));
        Assert.Empty(fx.Host.Subscriptions("com.test.sdk"));
    }

    [Fact]
    public void 未知事件名被拒绝()
    {
        using var fx = new SdkFixture();
        var ctx = fx.Context(PluginPermissions.Events);
        Assert.Throws<ArgumentException>(() => ctx.Events!.On("game.爆装备"));
    }

    [Fact]
    public void 未声明事件权限时ctxEvents为null()
    {
        using var fx = new SdkFixture();
        var dir = fx.WriteJs("function onLoad(ctx) { ctx.events.on('game.launched'); }");
        var err = fx.Host.Start(SdkFixture.ManifestWith(PluginPermissions.Storage), dir);
        Assert.NotNull(err); // ctx.events 是 undefined → TypeError
        Assert.False(fx.Host.IsLoaded("com.test.sdk"));
    }

    // ── JS 端到端：实例读取 ───────────────────────────────────

    [Fact]
    public void JS插件_可读取实例列表与游戏状态()
    {
        using var fx = new SdkFixture();
        var dir = fx.WriteJs("""
            function onLoad(ctx) {
                const list = ctx.instances.list();       // 未装任何版本 → 空数组
                ctx.storage.set('count', list.length);
                const status = ctx.game.status();        // 没在跑 → running=false
                ctx.storage.set('running', status.running);
                ctx.storage.set('tasks', ctx.tasks.list().length);
            }
            """);

        var err = fx.Host.Start(SdkFixture.ManifestWith(
            PluginPermissions.InstancesRead, PluginPermissions.GameLaunch,
            PluginPermissions.Tasks, PluginPermissions.Storage), dir);
        Assert.Null(err);

        var json = File.ReadAllText(Path.Combine(dir, "data", "storage.json"));
        Assert.Contains("\"count\": 0", json);
        Assert.Contains("\"running\": false", json);
    }

    [Fact]
    public void JS插件_游戏状态字段完整()
    {
        using var fx = new SdkFixture();
        var dir = fx.WriteJs("""
            function onLoad(ctx) {
                const s = ctx.game.status();
                ctx.storage.set('keys', Object.keys(s).sort().join(','));
            }
            """);
        fx.Host.Start(SdkFixture.ManifestWith(
            PluginPermissions.GameLaunch, PluginPermissions.Storage), dir);

        var json = File.ReadAllText(Path.Combine(dir, "data", "storage.json"));
        foreach (var key in new[] { "pid", "running", "startedAt", "uptimeSeconds", "versionId" })
            Assert.Contains(key, json);
    }

    // ── Python 运行时端到端（需要本机 Python，缺失则跳过） ──

    [Fact]
    public void Python插件_可加载并通过host_call调用宿主能力()
    {
        if (PyPluginRuntime.FindPython() is null) return; // 本机没装 Python：跳过
        using var fx = new SdkFixture();
        var dir = Path.Combine(fx.Dir, "plugins", "com.test.sdk");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.py"), """
            def onLoad(ctx):
                ctx.logger.info("PY instances=%d" % len(ctx.instances.list()))
                ctx.storage.set("k", "v")
                ctx.logger.info("PY storage=%s" % ctx.storage.get("k"))
                ctx.events.on("game.exited")

            def onLauncherEvent(ctx):
                ctx.logger.info("PY event=%s" % ctx.event["name"])
            """);
        // 宿主 SDK 路径：直接用仓库里的文件（App 打包时会随输出目录发布）
        var host = new PluginHost("9.9.9", Path.Combine(fx.Dir, "logs"),
            FindHostSdk(), fx.Services);
        try
        {
            var err = host.Start(SdkFixture.ManifestWith(
                PluginPermissions.InstancesRead, PluginPermissions.Storage,
                PluginPermissions.Events), dir);
            Assert.Null(err);

            host.RaiseEvent("game.exited", new { versionId = "1.21.1" });

            var log = File.ReadAllText(Path.Combine(fx.Dir, "logs", "com.test.sdk.log"));
            Assert.Contains("PY instances=0", log);
            Assert.Contains("PY storage=v", log);
            Assert.Contains("PY event=game.exited", log);
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>仓库里的 Python 宿主 SDK 路径（找不到时用一个不存在的路径，插件会加载失败）</summary>
    private static string FindHostSdk()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Starcloud.App", "Assets", "pluginsdk", "starcloud_host.py");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "starcloud_host.py");
    }
}
