// 内核单测：注册表 / 事件总线 / 插件宿主（激活次序、懒激活、可逆停用、失败隔离）。
//
// 为什么这些必须测：插件化的价值全在"换得掉、关得掉"，
// 而"关得掉"最容易假实现——停用后契约还挂着、事件还连着、贡献还在导航里。

using Starcloud.Core.Kernel;
using Xunit;

namespace Starcloud.Core.Tests;

public class KernelTests
{
    private static KernelHost NewKernel(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "sc-kernel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new KernelHost(new KernelOptions { DataRoot = dir });
    }

    // ── 契约注册表 ──

    private interface IPing { string Say(); }
    private sealed class Ping(string tag) : IPing { public string Say() => tag; }

    [Fact]
    public async Task 注册表_同契约多实现按优先级取且同级后注册优先()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            var low = new Ping("低");
            var high = new Ping("高");
            // 三个都是引擎 → 按登记顺序激活，所以注册顺序就是 低(10) → 高(50) → 插队(50)
            kernel.Register(new PluginDescriptor { Id = "p.low", Name = "低", Kind = PluginKind.Engine, Provides = [typeof(IPing)] },
                ctx => new FixedPlugin(() => ctx.Services.Add<IPing>(low, priority: 10)));
            kernel.Register(new PluginDescriptor { Id = "p.high", Name = "高", Kind = PluginKind.Engine, Provides = [typeof(IPing)] },
                ctx => new FixedPlugin(() => ctx.Services.Add<IPing>(high, priority: 50)));
            kernel.Register(new PluginDescriptor { Id = "p.plug", Name = "插队", Kind = PluginKind.Engine, Provides = [typeof(IPing)] },
                ctx => new FixedPlugin(() => ctx.Services.Add<IPing>(new Ping("插队"), priority: 50)));
            await kernel.StartAsync();

            // 同为 50 时后注册的赢——这就是"外部插件能覆盖内置实现"的机制
            Assert.Equal("插队", kernel.Get<IPing>().Say());
            Assert.Equal(3, kernel.GetAll<IPing>().Count);          // 多实现全都在（资源源这类要用）
            Assert.Equal(3, kernel.Services.Snapshot.Count(s => s.Contract == typeof(IPing)));

            // 停掉插队的，高优先级的"高"就露出来了（切换实现 = 停一个 + 起一个）
            await kernel.DeactivateAsync("p.plug");
            Assert.Equal("高", kernel.Get<IPing>().Say());
            Assert.Equal(2, kernel.GetAll<IPing>().Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 注册表_契约必须是接口()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            KernelException? caught = null;
            kernel.Register(new PluginDescriptor { Id = "p.bad", Name = "坏" },
                ctx => new FixedPlugin(() =>
                {
                    try { ctx.Services.Add(new Ping("具体类")); }
                    catch (KernelException ex) { caught = ex; }
                }));
            await kernel.StartAsync();
            Assert.NotNull(caught);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 事件总线 ──

    private sealed record Ding(int Value);

    [Fact]
    public async Task 事件总线_退订后不再收到且单个订阅者异常不影响其他人()
    {
        var bus = new EventBus();
        var hits = new List<int>();
        var boom = bus.Subscribe<Ding>(_ => throw new InvalidOperationException("订阅者崩了"));
        using var ok = bus.Subscribe<Ding>(d => hits.Add(d.Value));
        Assert.Equal(2, bus.SubscriberCount<Ding>());

        bus.Publish(new Ding(1));
        Assert.Equal([1], hits);                 // 有人抛异常，别人照收

        boom.Dispose();
        Assert.Equal(1, bus.SubscriberCount<Ding>());
        bus.Publish(new Ding(2));
        Assert.Equal([1, 2], hits);
    }

    // ── 插件宿主 ──

    [Fact]
    public async Task 插件宿主_按种类激活且依赖先于被依赖者()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            var order = new List<string>();
            kernel.Register(Plugin("u.ui", PluginKind.Ui, order), ctx => new FixedPlugin(() => order.Add("u.ui")));
            kernel.Register(Plugin("f.feature", PluginKind.Feature, order), ctx => new FixedPlugin(() => order.Add("f.feature")));
            kernel.Register(Plugin("e.engine", PluginKind.Engine, order), ctx => new FixedPlugin(() => order.Add("e.engine")));
            await kernel.StartAsync();

            Assert.Equal(["e.engine", "f.feature", "u.ui"], order);   // 引擎 → 功能 → UI
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 插件宿主_依赖缺失标失败且不抛异常()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            kernel.Register(new PluginDescriptor
            {
                Id = "a.needs",
                Name = "要依赖的",
                Kind = PluginKind.Feature,
                Depends = new Dictionary<string, string> { ["a.missing"] = "^1.0.0" },
            }, ctx => new FixedPlugin(() => { }));

            await kernel.StartAsync();   // 不抛
            var runtime = kernel.Plugins.Single();
            Assert.Equal(PluginState.Failed, runtime.State);
            Assert.Contains("缺少依赖", runtime.Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 插件宿主_懒激活_第一次取契约才激活提供者()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            var activated = false;
            kernel.Register(new PluginDescriptor
            {
                Id = "l.lazy",
                Name = "懒插件",
                Kind = PluginKind.Engine,
                Activation = PluginActivation.Lazy,
                Provides = [typeof(IPing)],
            }, ctx => new FixedPlugin(() =>
            {
                activated = true;
                ctx.Services.Add<IPing>(new Ping("懒"));
            }));

            await kernel.StartAsync();
            Assert.False(activated);                                 // 启动时不激活
            Assert.Equal(PluginState.Registered, kernel.Plugins.Single().State);

            Assert.Equal("懒", kernel.Get<IPing>().Say());            // 用到才激活
            Assert.True(activated);
            Assert.Equal(PluginState.Active, kernel.Plugins.Single().State);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 插件宿主_停用可逆_契约贡献与订阅都回到基线()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            var contributions = (ContributionRegistry)kernel.Contributions;
            var hits = 0;
            kernel.Register(new PluginDescriptor
            {
                Id = "x.plugin",
                Name = "示例",
                Kind = PluginKind.Feature,
                Provides = [typeof(IPing)],
            }, ctx => new FixedPlugin(() =>
            {
                ctx.Services.Add<IPing>(new Ping("示例"));
                ctx.Contributions.Add(new Contribution(ContributionKind.NavItem, ctx.Descriptor.Id, "x.nav", "示例"));
                ctx.Contributions.Add(new Contribution(ContributionKind.Page, ctx.Descriptor.Id, "x.page", "示例页"));
                ctx.Subscribe<Ding>(_ => hits++);
            }));

            await kernel.StartAsync();
            Assert.True(kernel.TryGet<IPing>(out _));
            Assert.Equal(2, contributions.Count);
            Assert.Equal(1, kernel.Events.SubscriberCount<Ding>());
            kernel.Events.Publish(new Ding(1));
            Assert.Equal(1, hits);

            await kernel.DeactivateAsync("x.plugin");
            Assert.False(kernel.TryGet<IPing>(out _));                 // 契约注销
            Assert.Equal(0, contributions.Count);                      // 贡献撤下
            Assert.Equal(0, kernel.Events.SubscriberCount<Ding>());    // 订阅断开（上下文代管）
            kernel.Events.Publish(new Ding(2));
            Assert.Equal(1, hits);                                     // 停用后不再收到

            // 再激活一次必须能回到基线（不能重复注册/重复挂贡献）
            Assert.True(await kernel.ActivateAsync("x.plugin"));
            Assert.True(kernel.TryGet<IPing>(out _));
            Assert.Equal(2, contributions.Count);
            Assert.Equal(1, kernel.Events.SubscriberCount<Ding>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 插件宿主_循环依赖不会死等()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            kernel.Register(new PluginDescriptor
            {
                Id = "c.one",
                Name = "环1",
                Kind = PluginKind.Feature,
                Depends = new Dictionary<string, string> { ["c.two"] = "^1.0.0" },
            }, ctx => new FixedPlugin(() => { }));
            kernel.Register(new PluginDescriptor
            {
                Id = "c.two",
                Name = "环2",
                Kind = PluginKind.Feature,
                Depends = new Dictionary<string, string> { ["c.one"] = "^1.0.0" },
            }, ctx => new FixedPlugin(() => { }));

            await kernel.StartAsync();
            Assert.All(kernel.Plugins, p => Assert.NotEqual(PluginState.Activating, p.State));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 插件宿主_配置分区按插件隔离且能读回()
    {
        var kernel = NewKernel(out var dir);
        try
        {
            ISettingsScope? scope = null;
            kernel.Register(new PluginDescriptor { Id = "s.plugin", Name = "配置" },
                ctx => new FixedPlugin(() =>
                {
                    scope = ctx.Settings;
                    ctx.Settings.Set("enabled", true);
                    ctx.Settings.Set("retries", 3);
                }));
            await kernel.StartAsync();

            Assert.NotNull(scope);
            Assert.True(scope!.GetBool("enabled"));
            Assert.Equal(3, scope.GetInt("retries"));
            Assert.True(File.Exists(Path.Combine(dir, "plugin-config", "s.plugin.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 测试用插件 ──

    private static PluginDescriptor Plugin(string id, PluginKind kind, List<string> order) => new()
    {
        Id = id,
        Name = id,
        Kind = kind,
        Provides = [],
    };

    /// <summary>用委托做初始化/停用的插件，省得每个用例写一个类</summary>
    private sealed class FixedPlugin(Action onInit, Action? onShutdown = null) : IPlugin
    {
        public Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
        {
            onInit();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken ct = default)
        {
            onShutdown?.Invoke();
            return Task.CompletedTask;
        }
    }
}
