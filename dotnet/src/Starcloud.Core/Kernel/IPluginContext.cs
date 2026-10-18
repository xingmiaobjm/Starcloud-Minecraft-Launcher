// 内核 · 插件与内核之间的接口面。
//
// 插件能看到的一切都从 IPluginContext 拿，**不允许静态单例**：
// 否则插件卸载后残留的静态引用会把宿主一起拖住（页面、订阅、服务都泄漏）。

namespace Starcloud.Core.Kernel;

/// <summary>一个插件（内置插件的基类见 <see cref="PluginBase"/>）</summary>
public interface IPlugin
{
    /// <summary>激活：注册契约实现、挂 UI 贡献、订阅事件</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken ct = default);

    /// <summary>停用：撤下贡献、断开订阅、释放资源（必须可逆，重启用不能重复注册）</summary>
    Task ShutdownAsync(CancellationToken ct = default);
}

/// <summary>没有停用逻辑的插件可以直接继承它</summary>
public abstract class PluginBase : IPlugin
{
    protected IPluginContext Context { get; private set; } = null!;

    public virtual Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        Context = context;
        return Task.CompletedTask;
    }

    public virtual Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public interface IPluginContext
{
    PluginDescriptor Descriptor { get; }
    IKernel Kernel { get; }

    /// <summary>注册自己提供的契约实现</summary>
    IServiceRegistry Services { get; }

    /// <summary>订阅/广播事实（插件之间唯一的通信方式）</summary>
    IEventBus Events { get; }

    /// <summary>订阅事件，停用时自动断开（推荐用这个，插件不必自己记句柄）</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;

    /// <summary>挂载 UI 贡献点（页面、导航项、卡片、分区、浮层）</summary>
    IContributionRegistry Contributions { get; }

    /// <summary>插件自己的配置分区（写进 plugin-config/&lt;id&gt;.json）</summary>
    ISettingsScope Settings { get; }

    IPluginLogger Log { get; }
    IPluginPaths Paths { get; }

    /// <summary>本插件被授予的权限（内置插件 = 全量）</summary>
    IReadOnlyCollection<string> Permissions { get; }
}

/// <summary>插件自己的配置分区：字符串读写 + 变更即存</summary>
public interface ISettingsScope
{
    string Get(string key, string fallback = "");
    int GetInt(string key, int fallback = 0);
    bool GetBool(string key, bool fallback = false);
    void Set(string key, string value);
    void Set(string key, int value);
    void Set(string key, bool value);
    IReadOnlyDictionary<string, string> All { get; }
}

public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>插件可用的路径（都在启动器数据目录下，按插件 id 隔离）</summary>
public interface IPluginPaths
{
    /// <summary>插件自己的数据目录（会按需创建）</summary>
    string Data { get; }

    /// <summary>插件自己的缓存目录（可被清理）</summary>
    string Cache { get; }
}

/// <summary>内核层错误（契约缺失、依赖不满足、状态非法）</summary>
public sealed class KernelException(string message) : Exception(message);
