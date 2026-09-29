// 插件 2.0 语义化版本区间（规范 2.0 §3 engines.starcloud / §8.3 dependencies）
// 支持 npm 风格区间：* / x、精确版本、^、~、以及比较符组合（空格或逗号分隔）。
// 只比较数字段（忽略预发布/构建元数据尾缀），与 PluginManifest.VersionAtLeast 保持一致。

using System.Text.RegularExpressions;

namespace Starcloud.Core.Plugins;

public sealed class PluginVersionRange
{
    private readonly List<(string Op, int[] Version)> _comparators = [];

    /// <summary>原始区间文本</summary>
    public string Raw { get; private init; } = "";

    /// <summary>解析区间文本；失败给出原因</summary>
    public static bool TryParse(string? text, out PluginVersionRange? range, out string error)
    {
        range = null;
        error = "";
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) { error = "版本区间为空"; return false; }

        var parsed = new PluginVersionRange { Raw = raw };
        var tokens = raw.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token is "*" or "x" or "X" or "latest")
            {
                parsed._comparators.Add(("*", [0]));
                continue;
            }

            var op = "=";
            var body = token;
            if (token.StartsWith(">=") || token.StartsWith("<=")) { op = token[..2]; body = token[2..]; }
            else if (token[0] is '^' or '~') { op = token[..1]; body = token[1..]; }
            else if (token[0] is '>' or '<' or '=') { op = token[..1]; body = token[1..]; }

            if (!TryParseVersion(body, out var version, out var versionError))
            {
                error = $"版本区间 {token} 非法：{versionError}";
                return false;
            }

            switch (op)
            {
                case "^":
                    parsed._comparators.Add((">=", version));
                    parsed._comparators.Add(("<", CaretUpperBound(version)));
                    break;
                case "~":
                    parsed._comparators.Add((">=", version));
                    parsed._comparators.Add(("<", TildeUpperBound(version, body)));
                    break;
                default:
                    parsed._comparators.Add((op, version));
                    break;
            }
        }

        if (parsed._comparators.Count == 0) { error = "版本区间为空"; return false; }
        range = parsed;
        return true;
    }

    /// <summary>给定版本是否落在区间内</summary>
    public bool Matches(string version)
    {
        if (!TryParseVersion(version, out var segments, out _)) return false;
        return _comparators.All(c => Compare(c.Op, segments, c.Version));
    }

    /// <summary>解析版本号为数字段数组（忽略 -预发布 / +构建 尾缀）</summary>
    public static bool TryParseVersion(string? text, out int[] segments, out string error)
    {
        segments = [];
        error = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0) { error = "版本号为空"; return false; }
        var core = t.Split('-', '+')[0];
        if (!Regex.IsMatch(core, @"^\d+(\.\d+)*$"))
        {
            error = $"版本号格式非法：{text}";
            return false;
        }
        segments = core.Split('.').Select(int.Parse).ToArray();
        return true;
    }

    // ── 内部 ──

    private static bool Compare(string op, int[] actual, int[] required)
    {
        if (op == "*") return true;
        var diff = CompareSegments(actual, required);
        return op switch
        {
            "=" => diff == 0,
            ">" => diff > 0,
            ">=" => diff >= 0,
            "<" => diff < 0,
            "<=" => diff <= 0,
            _ => false,
        };
    }

    private static int CompareSegments(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    /// <summary>^1.2.3 → &lt;2.0.0；^0.2.3 → &lt;0.3.0；^0.0.3 → &lt;0.0.4（npm 语义）</summary>
    private static int[] CaretUpperBound(int[] v)
    {
        var major = v.Length > 0 ? v[0] : 0;
        var minor = v.Length > 1 ? v[1] : 0;
        if (major > 0) return [major + 1, 0, 0];
        if (minor > 0) return [0, minor + 1, 0];
        var patch = v.Length > 2 ? v[2] : 0;
        return [0, 0, patch + 1];
    }

    /// <summary>~1.2.3 → &lt;1.3.0；~1 → &lt;2.0.0</summary>
    private static int[] TildeUpperBound(int[] v, string body)
    {
        var major = v.Length > 0 ? v[0] : 0;
        if (!body.Contains('.')) return [major + 1, 0, 0];
        var minor = v.Length > 1 ? v[1] : 0;
        return [major, minor + 1, 0];
    }
}
