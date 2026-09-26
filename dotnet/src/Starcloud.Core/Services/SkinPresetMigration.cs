// 老预置皮肤的清理。
//
// 0.3.1 早期版本的「默认皮肤」是启动器用代码画出来的（id: classic / slim / desert / …）。
// 那一版已经被游戏官方默认皮肤取代（id: steve / alex / ari / …），于是老条目成了"来历不明、
// 画风也怪"的东西留在用户库里，甚至挂在他正在用的账户上。
//
// 这里做一次性清理：只删「来源 = 预置 且 id 不在当前清单里」的条目——用户自己导入的
// （来源 file / url）一律不动；删完顺手清掉账户上的引用，账户就回到自己的默认形象。

using Starcloud.Core.Rendering;

namespace Starcloud.Core.Services;

public static class SkinPresetMigration
{
    /// <summary>预置皮肤在皮肤库里的来源标记</summary>
    public const string PresetSource = "preset";

    /// <summary>
    /// 删掉"不再是预置皮肤"的老条目；返回删除条数（0 = 没什么可清理的）。
    /// </summary>
    public static int PurgeLegacyPresetSkins(SkinLibraryService library, AccountStore accounts)
    {
        var current = SkinPresets.All.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var stale = library.List()
            .Where(e => e.Source == PresetSource && !current.Contains(e.SourceUrl ?? ""))
            .ToList();
        foreach (var entry in stale)
        {
            library.Delete(entry.Id);
            accounts.ClearSkinReference(entry.Id);
        }
        if (stale.Count > 0)
            LauncherLog.Write("skin",
                $"已清理 {stale.Count} 张老预置皮肤（{string.Join("、", stale.Select(e => e.Name))}）");
        return stale.Count;
    }
}
