// 通知中心：历史通知的内存流水（时间 + 级别 + 文案）。
// 任务收尾、插件通知等统一经 Notify 写入，由任务中心面板按时间倒序展示；
// 仅内存、进程退出即清空（与任务中心同一生命周期约定）。

using System.Collections.ObjectModel;

namespace Starcloud.Core.Services;

/// <summary>通知级别（界面上的信息 / 警告 / 错误标签）</summary>
public enum NoticeLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>一条历史通知；Id 单调递增，供 UI 按 Id 复用行控件</summary>
public sealed record AppNotice(long Id, DateTimeOffset Time, NoticeLevel Level, string Message);

/// <summary>通知中心（单例）：Notify 写入 → Changed 通知 UI；保留最近 200 条，最新的在最前。</summary>
public sealed class NotificationCenter
{
    public static NotificationCenter Instance { get; } = new();

    private const int MaxKept = 200;
    private readonly object _gate = new();
    private readonly List<AppNotice> _items = [];
    private long _nextId = 1;

    /// <summary>任意写入 / 清空后触发（UI 侧应节流刷新）</summary>
    public event Action? Changed;

    public void Info(string message) => Notify(NoticeLevel.Info, message);

    public void Warn(string message) => Notify(NoticeLevel.Warning, message);

    public void Error(string message) => Notify(NoticeLevel.Error, message);

    /// <summary>写入一条通知（空白文案忽略；超出容量丢弃最旧的）</summary>
    public void Notify(NoticeLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_gate)
        {
            _items.Insert(0, new AppNotice(_nextId++, DateTimeOffset.Now, level, message.Trim()));
            if (_items.Count > MaxKept) _items.RemoveAt(_items.Count - 1);
        }
        Changed?.Invoke();
    }

    /// <summary>当前通知快照（最新在前）</summary>
    public ReadOnlyCollection<AppNotice> Snapshot()
    {
        lock (_gate) return _items.AsReadOnly();
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>清空历史通知，返回清掉的条数</summary>
    public int Clear()
    {
        int removed;
        lock (_gate)
        {
            removed = _items.Count;
            _items.Clear();
        }
        if (removed > 0) Changed?.Invoke();
        return removed;
    }
}
