// 内核 · 事件总线：插件与内核之间**唯一**的通信方式。
//
// 纪律：插件之间不许直接引用对方的类型，只许
//   ① 通过契约（注册表）调用；② 通过事件广播事实。
// 这样一个插件被换掉/卸载时，其他插件不会被编译期依赖拽住。
//
// 分发是同步的（内核里的事都很快），但**单个订阅者抛异常不影响其他人**：
// 异常被吞掉并转成日志回调（插件崩了不该连坐整个启动器）。

using System.Collections.Concurrent;

namespace Starcloud.Core.Kernel;

public interface IEventBus
{
    /// <summary>订阅某类事件；返回的对象 Dispose 即退订</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;

    /// <summary>广播事件（同步分发，逐个隔离异常）</summary>
    void Publish<TEvent>(TEvent payload) where TEvent : notnull;

    /// <summary>当前某类事件的订阅者数量（卸载用例断言"订阅回到基线"要用）</summary>
    int SubscriberCount<TEvent>() where TEvent : notnull;
}

public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Subscription>> _handlers = new();
    private readonly Action<string>? _onError;

    public EventBus(Action<string>? onError = null) => _onError = onError;

    private sealed class Subscription(Action<object> invoke) : IDisposable
    {
        public Action<object> Invoke { get; } = invoke;
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        var list = _handlers.GetOrAdd(typeof(TEvent), _ => []);
        Subscription subscription;
        lock (list)
        {
            subscription = new Subscription(payload => handler((TEvent)payload));
            list.Add(subscription);
        }
        return new Unsubscriber(list, subscription);
    }

    public void Publish<TEvent>(TEvent payload) where TEvent : notnull
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list)) return;
        Subscription[] snapshot;
        lock (list)
        {
            list.RemoveAll(s => s.Disposed);
            snapshot = [.. list];
        }
        foreach (var subscription in snapshot)
        {
            if (subscription.Disposed) continue;
            try
            {
                subscription.Invoke(payload);
            }
            catch (Exception ex)
            {
                _onError?.Invoke($"事件 {typeof(TEvent).Name} 的订阅者抛异常：{ex.Message}");
            }
        }
    }

    public int SubscriberCount<TEvent>() where TEvent : notnull
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list)) return 0;
        lock (list)
        {
            list.RemoveAll(s => s.Disposed);
            return list.Count;
        }
    }

    private sealed class Unsubscriber(List<Subscription> list, Subscription subscription) : IDisposable
    {
        public void Dispose()
        {
            lock (list) list.Remove(subscription);
            subscription.Dispose();
        }
    }
}
