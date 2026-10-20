// 版本 → JRE 运行时选择：优先用版本 JSON 声明的 javaVersion.majorVersion，
// 缺失时按版本号推断（1.8 及以前 → 8，1.17 → 17，1.18-1.21.x → 21，26.x 新命名 → 25）。

using System.Text.RegularExpressions;

namespace Starcloud.Android.Core.Launch;

public static class RuntimeSelector
{
    /// <summary>选定 JRE 主版本：javaVersion 声明优先（≤8→8，≤17→17，≤21→21，更高→25）；否则按版本号推断</summary>
    public static int SelectJreMajor(string mcVersionId, int? javaMajorVersion)
    {
        if (javaMajorVersion is > 0)
            return javaMajorVersion <= 8 ? 8
                 : javaMajorVersion <= 17 ? 17
                 : javaMajorVersion <= 21 ? 21
                 : 25;
        return ParseMinor(mcVersionId) switch
        {
            null => 25,   // 快照 / 新命名（26.x）/ 特殊版本：按最新运行时
            <= 16 => 8,
            17 => 17,
            <= 21 => 21,
            _ => 25,
        };
    }

    /// <summary>JRE 主版本 → 组件清单名称</summary>
    public static string ComponentNameFor(int jreMajor) =>
        jreMajor <= 8 ? "jre8" : jreMajor <= 17 ? "jre17" : jreMajor <= 21 ? "jre21" : "jre25";

    /// <summary>提取「1.x」中的 minor；非 release 命名（快照、26.x 新命名等）返回 null</summary>
    internal static int? ParseMinor(string versionId)
    {
        var m = Regex.Match(versionId, @"^1\.(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var minor) ? minor : null;
    }
}
