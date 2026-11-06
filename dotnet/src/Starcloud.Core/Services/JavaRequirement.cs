// 版本 → 所需 Java 主版本的统一判定。
//
// 版本 JSON 的 javaVersion.majorVersion 是首选来源，但第三方整合包 / 裁剪过的 JSON
// 经常只写 component 或整个省略该字段；旧实现直接回退成 8，会出现「1.17 / 1.19.4
// 也被分配 Java 8」的抽风。这里改为：声明有效则用声明，否则按 MC 版本号推断。
using System.Text.RegularExpressions;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

public static class JavaRequirement
{
    // 不锚定行首：加载器实例名形如 fabric-loader-0.18.2-1.20.1，原版号在中间
    private static readonly Regex Release = new(@"1\.(\d+)(?:\.(\d+))?", RegexOptions.Compiled);
    private static readonly Regex ModernYear = new(@"^(\d{2})\.", RegexOptions.Compiled);

    /// <summary>
    /// 所需 Java 主版本：javaVersion 声明 &gt; 0 时直接采用（含元数据里明确写的 8），
    /// 否则按版本号推断。
    /// </summary>
    public static int Resolve(string? versionId, JavaVersionRef? declared)
    {
        if (declared is { MajorVersion: > 0 }) return declared.MajorVersion;
        return FromVersionId(versionId);
    }

    /// <summary>
    /// 按版本 id 推断最低 Java 主版本：
    ///   1.20.5+            → 21
    ///   1.18 – 1.20.4      → 17
    ///   1.17               → 16
    ///   1.16 及更早         → 8
    ///   26.x 及以后（新命名）→ 25
    ///   快照 / 无法识别      → 21（新快照所需运行时；声明存在时以声明为准）
    /// 加载器实例（fabric-loader-…-1.20.1 等）按其中的原版版本号判断。
    /// </summary>
    public static int FromVersionId(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return 8;
        var id = versionId.Trim();

        // 加载器 / 实例名里可能带原版号：取第一个形如 1.x[.y] 的片段
        var release = Release.Match(id);
        if (release.Success)
        {
            var minor = int.Parse(release.Groups[1].Value);
            var patch = release.Groups[2].Success ? int.Parse(release.Groups[2].Value) : 0;
            if (minor >= 21) return 21;
            if (minor == 20) return patch >= 5 ? 21 : 17;
            if (minor >= 18) return 17;
            if (minor == 17) return 16;
            return 8;
        }

        // 26.x 这类新命名（26.3、26.4-snapshot-5）
        var modern = ModernYear.Match(id);
        if (modern.Success && int.TryParse(modern.Groups[1].Value, out var year) && year >= 26)
            return 25;

        // 快照（24w14a）等：给较新的运行时，真跑不起来会有明确的类版本错误提示
        return 21;
    }

    /// <summary>
    /// 该版本能用的 Java **最高**主版本（null = 没有上限）。
    /// <para>只看下限是不够的：老版本用新 Java 不是"能跑但慢"，而是直接崩——
    /// 老版本的 ASM/Forge 无法解析 Java 9+ 的类文件，OptiFine 更是要求特定版本。
    /// 常见组合（社区长期验证的兼容边界）：</para>
    ///   · 1.5.2 及更早              → 最高 8
    ///   · LaunchWrapper（Forge ≤1.12）→ 最高 8（见下方说明）
    ///   · 1.8 – 1.11 + OptiFine     → 恰好 8（Java 9+ 崩溃）
    ///   · 1.12 + OptiFine           → 最高 8
    /// </summary>
    /// <param name="mainClass">版本 JSON 的 mainClass；用于识别 LaunchWrapper 系实例</param>
    public static int? MaxSupported(string? versionId, bool hasOptiFine, string? mainClass = null)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;
        var release = Release.Match(versionId.Trim());
        if (!release.Success) return null;   // 24w14a / 26.3 这类：不设上限

        var minor = int.Parse(release.Groups[1].Value);
        var patch = release.Groups[2].Success ? int.Parse(release.Groups[2].Value) : 0;

        // LaunchWrapper 时代（Forge / LiteLoader / OptiFine，1.12 及更早）：
        // 它假定系统类加载器是 URLClassLoader，而 Java 9+ 的 AppClassLoader 不是，
        // 会直接抛 ClassCastException 起不来。这是硬限制，不是版本没有下限判断能发现的。
        // （lwjgl3ify / Cleanroom 会把 mainClass 换成自己的 Bootstrap，所以不会误伤它们。）
        if (!string.IsNullOrEmpty(mainClass) &&
            mainClass.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase) && minor <= 12)
            return 8;

        // 1.5.2 及更早：Java 9+ 的类文件版本与旧 ASM 不兼容
        if (minor < 6 || (minor == 5 && patch <= 2)) return 8;

        if (hasOptiFine)
        {
            // 1.8 – 1.11：OptiFine 的补丁强绑定 Java 8，9+ 会崩在 tweaker 初始化
            if (minor is >= 8 and <= 11) return 8;
            // 1.12：同样是 Java 8 上限（1.12.2 + OptiFine + Java 9+ 已知崩溃）
            if (minor == 12) return 8;
        }
        return null;
    }

    /// <summary>
    /// 选定的 Java 主版本是否超出该版本能接受的范围；超了返回给用户看的说明，正常返回 null。
    /// </summary>
    public static string? TooNewWarning(string? versionId, bool hasOptiFine, int actualMajor,
        string? mainClass = null)
    {
        if (MaxSupported(versionId, hasOptiFine, mainClass) is not { } max || actualMajor <= max) return null;
        if (!string.IsNullOrEmpty(mainClass) &&
            mainClass.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase))
        {
            return $"这个版本用的是 LaunchWrapper 启动器（Forge 1.12 及更早），它只支持 Java {max}。"
                + $"当前选的 Java {actualMajor} 会直接抛类加载器类型错误——请换 Java {max}";
        }
        return hasOptiFine
            ? $"这个版本搭配 OptiFine 时最高只能用 Java {max}，当前选的 Java {actualMajor} 会让游戏在启动阶段崩溃——请换 Java {max}"
            : $"这个版本最高只能用 Java {max}，当前选的 Java {actualMajor} 会让游戏无法启动——请换 Java {max}";
    }
}
