// 插件间依赖解析与冲突检测（规范 2.0 §8.3）
// 输入「已安装插件 + 启用判定」，输出：可加载顺序（依赖先于被依赖者）与阻断原因。
// 纯逻辑，不触碰文件系统与运行时，便于单测；接入点在 PluginService。

namespace Starcloud.Core.Plugins;

/// <summary>依赖解析结果</summary>
public sealed class PluginDependencyResolution
{
    internal PluginDependencyResolution(
        IReadOnlyList<string> loadOrder, IReadOnlyDictionary<string, string> blocked)
    {
        LoadOrder = loadOrder;
        Blocked = blocked;
    }

    /// <summary>按「依赖先于被依赖者」排列的插件 id（仅含未被阻断者）</summary>
    public IReadOnlyList<string> LoadOrder { get; }

    /// <summary>pluginId → 阻止加载的原因（缺失 / 未启用 / 版本不满足 / 循环依赖 / 冲突 / 依赖不可用）</summary>
    public IReadOnlyDictionary<string, string> Blocked { get; }
}

public static class PluginDependencyResolver
{
    /// <summary>
    /// 解析依赖关系。
    /// </summary>
    /// <param name="installed">已安装插件（含未启用者，用于区分「未安装」与「未启用」）</param>
    /// <param name="isEnabled">启用判定；null = 全部视为启用</param>
    public static PluginDependencyResolution Resolve(
        IEnumerable<PluginManifest> installed, Func<string, bool>? isEnabled = null)
    {
        var all = new Dictionary<string, PluginManifest>(StringComparer.Ordinal);
        foreach (var m in installed) all[m.Id] = m;

        var active = all.Values
            .Where(m => isEnabled?.Invoke(m.Id) ?? true)
            .Select(m => m.Id)
            .ToHashSet(StringComparer.Ordinal);

        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
        void Block(string id, string reason)
        {
            if (!blocked.ContainsKey(id)) blocked[id] = reason;
        }

        string[] Deps(string id) => [.. all[id].Dependencies.Keys];

        // 1) 直接原因：未安装 / 未启用 / 版本区间不满足 / 声明的冲突命中
        foreach (var id in active)
        {
            foreach (var (depId, rangeText) in all[id].Dependencies)
            {
                if (!all.TryGetValue(depId, out var dep))
                {
                    Block(id, $"未安装依赖 {depId} {rangeText}");
                    continue;
                }
                if (!active.Contains(depId))
                {
                    Block(id, $"依赖 {depId} 未启用");
                    continue;
                }
                if (!PluginVersionRange.TryParse(rangeText, out var range, out _) ||
                    !range!.Matches(dep.Version))
                {
                    Block(id, $"依赖 {depId} 版本不满足：需要 {rangeText}，已安装 {dep.Version}");
                }
            }
            foreach (var (conflictId, rangeText) in all[id].Conflicts)
            {
                if (!active.Contains(conflictId) || !all.TryGetValue(conflictId, out var other)) continue;
                if (!PluginVersionRange.TryParse(rangeText, out var range, out _) ||
                    !range!.Matches(other.Version)) continue;
                Block(id, $"与 {conflictId} {other.Version} 冲突（已由本插件声明 conflicts）");
            }
        }

        // 2) 循环依赖：DFS 栈内回边即环，环上全部阻断
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1=栈中 2=已完成
        var stack = new List<string>();
        foreach (var root in active.Where(x => !state.ContainsKey(x)).OrderBy(x => x, StringComparer.Ordinal))
            Visit(root);
        void Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            foreach (var depId in Deps(id))
            {
                if (!active.Contains(depId)) continue;
                if (state.TryGetValue(depId, out var depState))
                {
                    if (depState != 1) continue; // 已完成，无需再走
                    var start = stack.IndexOf(depId);
                    var cycle = stack.Skip(start).Append(depId);
                    var text = $"循环依赖：{string.Join(" → ", cycle)}";
                    foreach (var member in stack.Skip(start)) Block(member, text);
                    continue;
                }
                Visit(depId);
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }

        // 3) 传递阻断：依赖了不可用插件的，同样不可加载
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var id in active)
            {
                if (blocked.ContainsKey(id)) continue;
                foreach (var depId in Deps(id))
                {
                    if (!blocked.TryGetValue(depId, out var why)) continue;
                    Block(id, $"依赖 {depId} 当前不可用（{why}）");
                    changed = true;
                    break;
                }
            }
        }

        // 4) 拓扑排序：依赖先出列；顺序确定（同层按 id 升序）
        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in active.Where(x => !blocked.ContainsKey(x))
                     .OrderBy(x => x, StringComparer.Ordinal)) Order(id);
        void Order(string id)
        {
            if (!visited.Add(id)) return;
            foreach (var depId in Deps(id).OrderBy(x => x, StringComparer.Ordinal))
                if (active.Contains(depId) && !blocked.ContainsKey(depId)) Order(depId);
            if (!blocked.ContainsKey(id)) order.Add(id);
        }

        return new PluginDependencyResolution(order, blocked);
    }

    /// <summary>取某插件被阻断的原因（null = 可加载）</summary>
    public static string? BlockReason(PluginDependencyResolution resolution, string pluginId) =>
        resolution.Blocked.TryGetValue(pluginId, out var why) ? why : null;
}
