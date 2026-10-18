// 内核 · 插件宿主：登记 → 依赖解析 → 按种类激活 → 可逆停用。
//
// 三条纪律（代码评审按这个卡）：
//   ① 内核里不许出现具体功能的名字（下载/Nova/版本…）——出现就是设计错误；
//   ② 激活顺序：引擎 → 功能 → 助手 → UI，依赖先于被依赖者，懒插件等第一次用到；
//   ③ 停用必须可逆：契约注销 + 贡献撤下 + 订阅断开，重启用不能重复注册（单测覆盖）。

using System.Diagnostics;
using System.Text.Json;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel;

public sealed class KernelOptions
{
    /// <summary>启动器数据根目录（%APPDATA%/starcloud-launcher）</summary>
    public required string DataRoot { get; init; }
}

public interface IKernel
{
    IEventBus Events { get; }
    IServiceRegistry Services { get; }
    IContributionRegistry Contributions { get; }
    IReadOnlyList<PluginRuntime> Plugins { get; }

    /// <summary>登记一个内置插件（外部插件由沙箱宿主另行登记）</summary>
    void Register(PluginDescriptor descriptor, Func<IPluginContext, IPlugin> factory);

    /// <summary>按契约取实现；提供者还没激活就先激活它（懒加载的关键）</summary>
    T Get<T>() where T : class;
    bool TryGet<T>(out T? service) where T : class;
    IReadOnlyList<T> GetAll<T>() where T : class;

    /// <summary>按契约类型找"谁的 Provides 里有它"（诊断/插件页用）</summary>
    PluginRuntime? ProviderOf(Type contract);

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<bool> ActivateAsync(string pluginId, CancellationToken ct = default);
    Task DeactivateAsync(string pluginId, CancellationToken ct = default);

    /// <summary>插件状态变化（插件页/诊断订阅它刷新）</summary>
    event Action<PluginRuntime>? PluginChanged;
}

public sealed class KernelHost : IKernel
{
    private readonly object _gate = new();
    private readonly List<PluginRuntime> _plugins = [];
    private readonly Dictionary<string, Func<IPluginContext, IPlugin>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlugin> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginContext> _contexts = new(StringComparer.Ordinal);
    private readonly ServiceRegistry _services;
    private readonly ContributionRegistry _contributions = new();
    private readonly KernelOptions _options;
    private string _activating = "";

    public KernelHost(KernelOptions options)
    {
        _options = options;
        _services = new ServiceRegistry(() => _activating);
        Events = new EventBus(message => LauncherLog.Write("kernel", message));
    }

    public IEventBus Events { get; }
    public IServiceRegistry Services => _services;
    public IContributionRegistry Contributions => _contributions;

    public IReadOnlyList<PluginRuntime> Plugins
    {
        get
        {
            lock (_gate) return [.. _plugins];
        }
    }

    public event Action<PluginRuntime>? PluginChanged;

    public void Register(PluginDescriptor descriptor, Func<IPluginContext, IPlugin> factory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_plugins.Any(p => p.Descriptor.Id == descriptor.Id))
                throw new KernelException($"插件 id 重复：{descriptor.Id}");
            _plugins.Add(new PluginRuntime(descriptor));
            _factories[descriptor.Id] = factory;
        }
    }

    public void Disable(string pluginId)
    {
        if (Find(pluginId) is not { } runtime) return;
        runtime.State = PluginState.Disabled;
        PluginChanged?.Invoke(runtime);
    }

    // ── 激活 / 停用 ──

    public async Task StartAsync(CancellationToken ct = default)
    {
        // 引擎 → 功能 → 助手 → UI；同种按登记顺序。依赖由 ActivateAsync 递归先激活。
        foreach (var kind in new[] { PluginKind.Engine, PluginKind.Feature, PluginKind.Assistant, PluginKind.Ui })
        {
            foreach (var runtime in Snapshot(kind, PluginActivation.Eager))
            {
                ct.ThrowIfCancellationRequested();
                await ActivateAsync(runtime.Descriptor.Id, ct);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var runtime in Plugins.Reverse().Where(p => p.State == PluginState.Active))
            await DeactivateAsync(runtime.Descriptor.Id, ct);
    }

    public async Task<bool> ActivateAsync(string pluginId, CancellationToken ct = default)
    {
        var runtime = Find(pluginId);
        if (runtime is null) throw new KernelException($"没有登记插件 {pluginId}");
        if (runtime.State is PluginState.Active) return true;
        if (runtime.State is PluginState.Disabled or PluginState.Failed) return false;
        // 循环依赖保护：状态必须在**递归激活依赖之前**置为 Activating，
        // 否则 A→B→A 的依赖会让 ActivateAsync 无限递归到栈溢出（单测抓到过）
        if (runtime.State is PluginState.Activating) return false;

        runtime.State = PluginState.Activating;
        PluginChanged?.Invoke(runtime);

        // 依赖先激活（拓扑递归）
        foreach (var dependency in runtime.Descriptor.Depends.Keys)
        {
            if (Find(dependency) is not { } dep)
            {
                Fail(runtime, $"缺少依赖插件 {dependency}");
                return false;
            }
            if (dep.State is PluginState.Failed or PluginState.Disabled)
            {
                Fail(runtime, $"依赖插件 {dependency} 不可用");
                return false;
            }
            if (dep.State is not PluginState.Active && !await ActivateAsync(dependency, ct))
            {
                Fail(runtime, $"依赖插件 {dependency} 激活失败");
                return false;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var context = new PluginContext(this, runtime.Descriptor, _options);
        lock (_gate)
        {
            _contexts[pluginId] = context;
            _activating = pluginId;
        }
        try
        {
            var plugin = _factories[pluginId](context);
            _instances[pluginId] = plugin;
            await plugin.InitializeAsync(context, ct);
            runtime.State = PluginState.Active;
            runtime.Error = null;
            runtime.ActivationMs = stopwatch.Elapsed.TotalMilliseconds;
            LauncherLog.Write("kernel", $"插件已激活：{runtime.Descriptor.Name}（{runtime.Descriptor.Id}，{runtime.ActivationMs:F0}ms）");
            Events.Publish(new PluginActivated(runtime.Descriptor.Id));
            return true;
        }
        catch (Exception ex)
        {
            Fail(runtime, ex.Message);
            return false;
        }
        finally
        {
            lock (_gate) _activating = "";
            PluginChanged?.Invoke(runtime);
        }
    }

    public async Task DeactivateAsync(string pluginId, CancellationToken ct = default)
    {
        var runtime = Find(pluginId);
        if (runtime is null || runtime.State is not PluginState.Active) return;
        var context = _contexts[pluginId];
        try
        {
            if (_instances.TryGetValue(pluginId, out var plugin))
            {
                context.BeginShutdown();
                await plugin.ShutdownAsync(ct);
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Write("kernel", $"插件停用出错：{runtime.Descriptor.Name}：{ex.Message}");
        }
        finally
        {
            // 顺序很重要：先撤贡献，再注销契约，最后断订阅
            _contributions.RemoveByPlugin(pluginId);
            _services.RemoveByProvider(pluginId);
            _instances.Remove(pluginId);
            _contexts.Remove(pluginId);
            runtime.State = PluginState.Stopped;
            LauncherLog.Write("kernel", $"插件已停用：{runtime.Descriptor.Name}");
            Events.Publish(new PluginDeactivated(pluginId));
            PluginChanged?.Invoke(runtime);
        }
    }

    // ── 契约解析（懒激活）──

    public T Get<T>() where T : class
    {
        if (_services.TryGet<T>(out var service)) return service;
        if (ProviderOf(typeof(T)) is { State: PluginState.Registered } provider)
        {
            ActivateAsync(provider.Descriptor.Id).GetAwaiter().GetResult();   // 懒激活：第一次要用才起
            if (_services.TryGet<T>(out service)) return service;
        }
        throw new KernelException($"没有插件提供契约 {typeof(T).Name}" +
            (ProviderOf(typeof(T)) is { } p ? $"（{p.Descriptor.Id} 状态：{p.State}）" : ""));
    }

    public bool TryGet<T>(out T? service) where T : class => _services.TryGet(out service);

    public IReadOnlyList<T> GetAll<T>() where T : class => _services.GetAll<T>();

    public PluginRuntime? ProviderOf(Type contract)
    {
        lock (_gate)
            return _plugins.FirstOrDefault(p => p.Descriptor.Provides.Contains(contract));
    }

    // ── 内部 ──

    private PluginRuntime? Find(string id)
    {
        lock (_gate) return _plugins.FirstOrDefault(p => p.Descriptor.Id == id);
    }

    private List<PluginRuntime> Snapshot(PluginKind kind, PluginActivation activation)
    {
        lock (_gate)
            return [.. _plugins.Where(p => p.Descriptor.Kind == kind && p.Descriptor.Activation == activation)];
    }

    private void Fail(PluginRuntime runtime, string message)
    {
        runtime.State = PluginState.Failed;
        runtime.Error = message;
        LauncherLog.Write("kernel", $"插件激活失败：{runtime.Descriptor.Name}（{runtime.Descriptor.Id}）：{message}");
        PluginChanged?.Invoke(runtime);
    }
}

/// <summary>插件激活完成</summary>
public sealed record PluginActivated(string PluginId);

/// <summary>插件已停用</summary>
public sealed record PluginDeactivated(string PluginId);

/// <summary>插件自己的配置分区：plugin-config/&lt;id&gt;.json，改一次存一次</summary>
internal sealed class PluginSettingsFile : ISettingsScope
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;
    private readonly object _gate = new();

    public PluginSettingsFile(string path)
    {
        _path = path;
        _values = Load(path);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);   // 坏配置按空处理
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            lock (_gate) File.WriteAllText(_path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LauncherLog.Write("kernel", $"插件配置写入失败：{ex.Message}");
        }
    }

    public string Get(string key, string fallback = "")
    {
        lock (_gate) return _values.TryGetValue(key, out var value) ? value : fallback;
    }

    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(Get(key), out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        bool.TryParse(Get(key), out var value) ? value : fallback;

    public void Set(string key, string value)
    {
        lock (_gate) _values[key] = value;
        Save();
    }

    public void Set(string key, int value) => Set(key, value.ToString());
    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    public IReadOnlyDictionary<string, string> All
    {
        get
        {
            lock (_gate) return new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }
    }
}

/// <summary>插件上下文实现（内核内部用）</summary>
internal sealed class PluginContext : IPluginContext, IPluginLogger, IPluginPaths
{
    private readonly KernelHost _kernel;
    private readonly KernelOptions _options;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly PluginSettingsFile _settings;
    private bool _shuttingDown;

    public PluginContext(KernelHost kernel, PluginDescriptor descriptor, KernelOptions options)
    {
        _kernel = kernel;
        _options = options;
        Descriptor = descriptor;
        _settings = new PluginSettingsFile(Path.Combine(options.DataRoot, "plugin-config", $"{descriptor.Id}.json"));
        Permissions = descriptor.Builtin ? ["*"] : descriptor.Permissions;
    }

    public PluginDescriptor Descriptor { get; }
    public IKernel Kernel => _kernel;
    public IServiceRegistry Services => _kernel.Services;
    public IEventBus Events => _kernel.Events;
    public IContributionRegistry Contributions => _kernel.Contributions;
    public ISettingsScope Settings => _settings;
    public IPluginLogger Log => this;
    public IPluginPaths Paths => this;
    public IReadOnlyCollection<string> Permissions { get; }

    public string Data => Ensure(Path.Combine(_options.DataRoot, "plugin-data", Descriptor.Id));
    public string Cache => Ensure(Path.Combine(_options.DataRoot, "plugin-cache", Descriptor.Id));

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        var subscription = _kernel.Events.Subscribe(handler);
        Track(subscription);
        return subscription;
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    internal void BeginShutdown()
    {
        _shuttingDown = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    /// <summary>上下文代管的订阅（停用时自动断，插件忘了注销也不会泄漏）</summary>
    internal void Track(IDisposable subscription)
    {
        if (_shuttingDown) subscription.Dispose();
        else _subscriptions.Add(subscription);
    }

    public void Info(string message) => LauncherLog.Write("plugin", $"[{Descriptor.Id}] {message}");
    public void Warn(string message) => LauncherLog.Write("plugin", $"[{Descriptor.Id}] 警告：{message}");
    public void Error(string message, Exception? ex = null) =>
        LauncherLog.Write("plugin", $"[{Descriptor.Id}] 错误：{message}{(ex is null ? "" : $"（{ex.Message}）")}");
}
