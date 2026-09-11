// 智能内存推荐：物理内存 + 模组数量 + 光影 → 建议最大内存
// 设计：docs/superpowers/specs/2026-09-11-launch-reliability-design.md §8.2

namespace Starcloud.Core.Services;

/// <summary>内存推荐（可解释、保守）：以物理内存为基线，按模组数与光影适度上浮，512 MB 对齐</summary>
public static class MemoryAdvisor
{
    /// <summary>推荐最大内存（MB）；<paramref name="totalPhysicalMb"/> 非法时用 SystemMemory 兜底</summary>
    public static int SuggestMaxMb(long totalPhysicalMb, int modCount, bool hasShaders)
    {
        var total = Normalize(totalPhysicalMb);
        var upper = Math.Max(2048L, total * 3 / 4);
        var baseline = Math.Clamp(total / 4, 2048L, upper);
        var max = Math.Clamp((long)(baseline * ModBoost(modCount) * (hasShaders ? 1.25 : 1.0)),
            2048L, upper);
        return (int)(max / 512 * 512);
    }

    /// <summary>一句话解释推荐依据，如「16 GB 内存 + 187 个模组 + 光影 → 建议 6144 MB」</summary>
    public static string Explain(long totalPhysicalMb, int modCount, bool hasShaders)
    {
        var total = Normalize(totalPhysicalMb);
        var parts = new List<string> { $"{total / 1024.0:0.#} GB 内存" };
        if (modCount > 0) parts.Add($"{modCount} 个模组");
        if (hasShaders) parts.Add("光影");
        return $"{string.Join(" + ", parts)} → 建议 {SuggestMaxMb(total, modCount, hasShaders)} MB";
    }

    /// <summary>探测某 GameRoot 的模组数量与光影目录是否存在；失败返回 (0, false)</summary>
    public static (int ModCount, bool HasShaders) Probe(string gameRoot)
    {
        try
        {
            var mods = 0;
            var modsDir = Path.Combine(gameRoot, "mods");
            if (Directory.Exists(modsDir))
                mods = Directory.EnumerateFiles(modsDir, "*.jar").Count();

            var shaderDir = Path.Combine(gameRoot, "shaderpacks");
            var hasShaders = Directory.Exists(shaderDir)
                && Directory.EnumerateFileSystemEntries(shaderDir).Any();
            return (mods, hasShaders);
        }
        catch { return (0, false); }
    }

    private static long Normalize(long totalPhysicalMb)
    {
        if (totalPhysicalMb > 0) return totalPhysicalMb;
        var detected = SystemMemory.Snapshot().TotalMb;
        return detected > 0 ? detected : 8192;
    }

    private static double ModBoost(int modCount) => modCount switch
    {
        > 300 => 1.6,
        > 150 => 1.35,
        > 60 => 1.15,
        _ => 1.0,
    };
}
