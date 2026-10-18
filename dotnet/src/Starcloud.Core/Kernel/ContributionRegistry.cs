// 内核 · UI 贡献点注册表：插件把界面挂到宿主的挂载位上。
//
// 与 `docs/plugin-spec-v2.md` §6 的 contributes 完全对齐（内置插件也走这条路），
// 而且**宿主无关**：这里只存"谁在哪个挂载位放了什么"，具体渲染由外壳负责
// （WinUI 外壳读它建 NavigationView 项；Nebula 外壳读它建自己的导航）。
//
// 卸载插件 = RemoveByPlugin(id)，外壳收到 Changed 事件后撤下对应视图。

namespace Starcloud.Core.Kernel;

public enum ContributionKind
{
    /// <summary>左侧主导航项（Footer=true 进底部区）</summary>
    NavItem,

    /// <summary>页面（按 id 创建，懒加载）</summary>
    Page,

    /// <summary>首页卡片</summary>
    HomeCard,

    /// <summary>设置页分区</summary>
    SettingsSection,

    /// <summary>版本详情页分区</summary>
    VersionSection,

    /// <summary>页面内插槽（Target = 插槽 id，如 "version.detail.tabs"）</summary>
    Slot,

    /// <summary>浮层（对话框/面板）</summary>
    Overlay,
}

/// <summary>
/// 一条贡献。大部分字段按种类可选：
/// <list type="bullet">
///   <item><c>Id</c>：宿主用来定位（页面 id 必须唯一）</item>
///   <item><c>Title</c>：显示文案（多语言键或字面量）</item>
///   <item><c>Target</c>：挂载目标（插槽类用它指定插槽 id）</item>
///   <item><c>PageId</c>：导航项指向的页面 id</item>
///   <item><c>Order</c>：排序（小的在前）</item>
///   <item><c>Payload</c>：种类相关的附加数据（如图标字形、声明式 UI 定义）</item>
/// </list>
/// </summary>
public sealed record Contribution(
    ContributionKind Kind,
    string PluginId,
    string Id,
    string Title = "",
    string? Target = null,
    string? PageId = null,
    int Order = 100,
    string? Glyph = null,
    object? Payload = null);

public enum ContributionChangeKind { Added, Removed }

public sealed record ContributionChange(ContributionChangeKind Kind, Contribution Contribution);

public interface IContributionRegistry
{
    void Add(Contribution contribution);

    /// <summary>撤下某个插件的全部贡献（插件停用时调用）</summary>
    void RemoveByPlugin(string pluginId);

    /// <summary>按种类取全部贡献（已按 Order 排序）</summary>
    IReadOnlyList<Contribution> Of(ContributionKind kind, string? target = null);

    event Action<ContributionChange>? Changed;
}

public sealed class ContributionRegistry : IContributionRegistry
{
    private readonly object _gate = new();
    private readonly List<Contribution> _items = [];

    public event Action<ContributionChange>? Changed;

    public void Add(Contribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution.Id.Length == 0)
            throw new KernelException($"贡献点缺少 id（插件 {contribution.PluginId}）");
        lock (_gate)
        {
            // 同一个插件重复挂同一个 id：先撤旧的，避免热重载时重复
            _items.RemoveAll(c => c.PluginId == contribution.PluginId && c.Id == contribution.Id);
            _items.Add(contribution);
        }
        Changed?.Invoke(new ContributionChange(ContributionChangeKind.Added, contribution));
    }

    public void RemoveByPlugin(string pluginId)
    {
        List<Contribution> removed;
        lock (_gate)
        {
            removed = [.. _items.Where(c => string.Equals(c.PluginId, pluginId, StringComparison.Ordinal))];
            _items.RemoveAll(c => string.Equals(c.PluginId, pluginId, StringComparison.Ordinal));
        }
        foreach (var contribution in removed)
            Changed?.Invoke(new ContributionChange(ContributionChangeKind.Removed, contribution));
    }

    public IReadOnlyList<Contribution> Of(ContributionKind kind, string? target = null)
    {
        lock (_gate)
        {
            return [.. _items
                .Where(c => c.Kind == kind && (target is null || string.Equals(c.Target, target, StringComparison.Ordinal)))
                .OrderBy(c => c.Order)
                .ThenBy(c => c.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>当前贡献总数（卸载用例断言"回到基线"要用）</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }
}
