// SC 实验室 · 投影社区的游戏术语词典：中文黑话 → 英文检索词。
//
// 为什么必须自己维护：通用翻译（MyMemory / 谷歌）会把游戏黑话译歪——
// 实测「世吞」被音译成人名「Seton」，而正确检索词是 "world eater"。
// 词典命中就直接用它（精确 > 包含，长词优先），未命中再交给通用翻译。

namespace Starcloud.Core.Services.Lab;

public static class CommunityGlossary
{
    /// <summary>中文术语 → 英文检索词（按词长降序匹配，保证「无沟世吞」先于「世吞」命中）</summary>
    private static readonly (string Cn, string En)[] Terms =
    [
        ("无沟世吞", "trenchless world eater"),
        ("世吞", "world eater"),
        ("吞噬者", "world eater"),
        ("苦力怕农场", "creeper farm"),
        ("刷怪塔", "mob farm"),
        ("经验农场", "xp farm"),
        ("树场", "tree farm"),
        ("史莱姆农场", "slime farm"),
        ("金农场", "gold farm"),
        ("猪灵农场", "piglin gold farm"),
        ("铁傀儡农场", "iron farm"),
        ("村民打折机", "villager trading hall"),
        ("交易大厅", "trading hall"),
        ("袭击者农场", "raid farm"),
        ("守卫者农场", "guardian farm"),
        ("凋灵骷髅农场", "wither skeleton farm"),
        ("珍珠炮", "pearl cannon"),
        ("轰炸机", "bomber"),
        ("盾构机", "tunnel bore"),
        ("全自动", "fully automatic"),
        ("半自动", "semi automatic"),
        ("红石", "redstone"),
        ("粘液块", "slime block"),
        ("活塞", "piston"),
        ("飞行器", "flying machine"),
        ("鱼塔", "fish farm"),
        ("海带农场", "kelp farm"),
        ("熔炉组", "furnace array"),
        ("仓库", "storage system"),
        ("分类机", "sorting system"),
        ("建筑", "build"),
        ("雕像", "statue"),
    ];

    /// <summary>
    /// 查询词 → 英文检索词。命中词典返回对应英文；未命中返回 null（调用方再走通用翻译）。
    /// 支持"整个查询就是术语"（精确优先），以及"查询包含术语"（如「双倍速世吞」）。
    /// </summary>
    public static string? ToSearchTerm(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return null;

        foreach (var (cn, en) in Terms)
            if (string.Equals(q, cn, StringComparison.Ordinal)) return en;

        foreach (var (cn, en) in Terms)
            if (q.Contains(cn, StringComparison.Ordinal)) return en;

        return null;
    }
}
