// 存档库聚合：跨版本（共享根 + 各隔离版本根）扫描全部存档
// 设计：docs/superpowers/specs/2026-09-10-saves-cross-version-design.md

namespace Starcloud.Core.Services;

/// <summary>跨版本存档聚合：把共享根与各隔离版本根下的 saves 合并成一份列表</summary>
public sealed class SaveLibraryService(ManifestService manifest, SaveService saves)
{
    /// <summary>已装版本 Id 列表（供存档页版本筛选下拉）</summary>
    public List<string> ListVersionIds(string dotMc) =>
        manifest.ListInstalled(dotMc).Select(v => v.Id).ToList();

    /// <summary>共享根 + 各隔离版本根下的全部存档，按 LastPlayed 倒序</summary>
    public List<SaveInfo> ListAll(string dotMc, LauncherSettings settings)
    {
        // 共享根：所有非隔离版本（含未按版本隔离的存档）都落在这里
        var result = saves.ListSaves(dotMc, "", false);

        // 逐版本：仅隔离版本有自己的 saves，非隔离跳过避免与共享根重复
        var store = new InstanceSettingsStore(dotMc);
        foreach (var id in ListVersionIds(dotMc))
        {
            if (!InstanceLayout.IsIsolated(store.Get(id), settings)) continue;
            var root = InstanceLayout.GameRoot(dotMc, id, true);
            result.AddRange(saves.ListSaves(root, id, true));
        }

        // 各来源内部已排序，合并后需再排一次
        result.Sort((a, b) => (b.LastPlayed ?? DateTime.MinValue).CompareTo(a.LastPlayed ?? DateTime.MinValue));
        return result;
    }
}
