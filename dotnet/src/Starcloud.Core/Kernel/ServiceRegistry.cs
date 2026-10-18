// 内核 · 服务注册表：插件按**契约**注册实现，其他插件按契约取用。
//
// 这是"万物皆插件"的核心：页面与功能只认契约（IDownloadEngine、IVersionCatalog…），
// 谁来实现、是内置还是第三方，运行时才知道。
//
// 规则：
//   · 同一个契约允许多个实现（如两个资源源 IModSource），Get 取优先级最高的；
//   · 优先级相同时**后注册的赢**（所以"覆盖内置实现"只要注册得晚一点）；
//   · 每个注册都记着"谁提供的"，插件卸载时按 provider 批量注销。

using System.Diagnostics.CodeAnalysis;

namespace Starcloud.Core.Kernel;

/// <summary>一条注册记录（诊断/插件页展示要用）</summary>
public sealed record RegisteredService(Type Contract, string ProviderId, int Priority, object Instance);

public interface IServiceRegistry
{
    /// <summary>注册一个契约实现（只能由激活中的插件调用）</summary>
    void Add<TContract>(TContract instance, int priority = 0) where TContract : class;

    /// <summary>按契约取优先级最高的实现（不触发懒激活）</summary>
    bool TryGet<TContract>([NotNullWhen(true)] out TContract? service) where TContract : class;

    /// <summary>按契约取全部实现（优先级从高到低）</summary>
    IReadOnlyList<TContract> GetAll<TContract>() where TContract : class;

    /// <summary>
    /// 内核自有服务登记（过渡期专用）：老服务按**具体类**被构造、传给其它服务，
    /// 还不符合"契约必须是接口"的规则，等 P1/P2 逐个改成契约后就不再用这个口子。
    /// 记下来只是为了让 AppServices 能从内核把它们取回来（归属标为"kernel"）。
    /// </summary>
    void AddBuiltin(Type serviceType, object instance);

    /// <summary>按契约取实现；提供者还没激活就先激活它（懒加载的关键）</summary>
    T Get<T>() where T : class;

    /// <summary>当前所有注册（插件页/诊断用）</summary>
    IReadOnlyList<RegisteredService> Snapshot { get; }
}

public sealed class ServiceRegistry : IServiceRegistry
{
    private readonly object _gate = new();
    private readonly List<RegisteredService> _entries = [];
    private readonly Func<string> _currentProvider;

    /// <param name="currentProvider">当前正在激活的插件 id（注册记录用它标注来源）</param>
    public ServiceRegistry(Func<string> currentProvider) => _currentProvider = currentProvider;

    public void Add<TContract>(TContract instance, int priority = 0) where TContract : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var contract = typeof(TContract);
        if (!contract.IsInterface)
            throw new KernelException($"契约必须是接口：{contract.Name}（契约按能力切，不按具体类切）");
        lock (_gate)
            _entries.Add(new RegisteredService(contract, _currentProvider(), priority, instance));
    }

    public void AddBuiltin(Type serviceType, object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate) _entries.Add(new RegisteredService(serviceType, "kernel", 0, instance));
    }

    public T Get<T>() where T : class
    {
        if (TryGet<T>(out var service)) return service;
        throw new KernelException($"内核里没有 {typeof(T).Name}（还没被登记？）");
    }

    public bool TryGet<TContract>([NotNullWhen(true)] out TContract? service) where TContract : class
    {
        lock (_gate)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)      // 后注册优先
            {
                var entry = _entries[i];
                if (entry.Contract != typeof(TContract)) continue;
                var best = entry;
                for (var j = i - 1; j >= 0; j--)
                {
                    var other = _entries[j];
                    if (other.Contract == typeof(TContract) && other.Priority > best.Priority) best = other;
                }
                service = (TContract)best.Instance;
                return true;
            }
        }
        service = null;
        return false;
    }

    public IReadOnlyList<TContract> GetAll<TContract>() where TContract : class
    {
        lock (_gate)
        {
            return [.. _entries
                .Where(e => e.Contract == typeof(TContract))
                .OrderByDescending(e => e.Priority)
                .Select(e => (TContract)e.Instance)];
        }
    }

    public IReadOnlyList<RegisteredService> Snapshot
    {
        get
        {
            lock (_gate) return [.. _entries];
        }
    }

    /// <summary>注销某个插件注册的全部实现（插件卸载时调用）</summary>
    internal void RemoveByProvider(string providerId)
    {
        lock (_gate) _entries.RemoveAll(e => string.Equals(e.ProviderId, providerId, StringComparison.Ordinal));
    }
}
