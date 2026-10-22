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
}
