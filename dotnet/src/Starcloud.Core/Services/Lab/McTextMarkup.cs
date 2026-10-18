// SC 实验室 · 彩色文字的「标记语法」（编辑界面按 Markdown 的手感来，但只保留颜色）。
//
// 语法（写在编辑界面提示里，改动需两边同步）：
//   {#RRGGBB 内容}   —— 内联片段着色（3 位缩写也认，如 {#f55 红字}）
//   {#RRGGBB}        —— 从这往后着色，直到下一个标记或 {/}
//   {/}              —— 复位为默认样式（无标记文本按界面上选的默认渐变/单色处理）
//   原版色名亦可：{#red 红字}（16 色名，见 McText.LegacyColors 的英文名表）
//
// 解析产出"片段列表"（文本 + 颜色），既用于生成 § 代码，也用于界面上的实时预览渲染。

using System.Text;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services.Lab;

/// <summary>一段带颜色（或无色 = 跟随默认样式）的文本</summary>
public sealed record MarkupSpan(string Text, string? Hex);

public static partial class McTextMarkup
{
    [GeneratedRegex(@"\{#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[a-zA-Z_]+)(\s|\})", RegexOptions.Compiled)]
    private static partial Regex MarkerRegex();

    /// <summary>原版 16 色的英文名 → hex（供 {#red} 这类写法使用）</summary>
    private static readonly Dictionary<string, string> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000", ["dark_blue"] = "#0000AA", ["dark_green"] = "#00AA00",
        ["dark_aqua"] = "#00AAAA", ["dark_red"] = "#AA0000", ["dark_purple"] = "#AA00AA",
        ["gold"] = "#FFAA00", ["gray"] = "#AAAAAA", ["dark_gray"] = "#555555",
        ["blue"] = "#5555FF", ["green"] = "#55FF55", ["aqua"] = "#55FFFF",
        ["red"] = "#FF5555", ["light_purple"] = "#FF55FF", ["yellow"] = "#FFFF55",
        ["white"] = "#FFFFFF",
        ["cyan"] = "#55FFFF", ["magenta"] = "#FF55FF", ["light_blue"] = "#5555FF",
        ["lime"] = "#55FF55", ["orange"] = "#FFAA00", ["pink"] = "#FF55FF",
    };

    /// <summary>
    /// 解析标记文本为片段列表。无法识别的花括号原样当普通文本（编辑期用户还在打字时不报错，
    /// 只是"预览里显示原文"，比弹错误友好）。
    /// </summary>
    public static List<MarkupSpan> Parse(string text)
    {
        var spans = new List<MarkupSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        var current = new StringBuilder();
        string? currentColor = null;
        var i = 0;

        void Flush()
        {
            if (current.Length == 0) return;
            spans.Add(new MarkupSpan(current.ToString(), currentColor));
            current.Clear();
        }

        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                // {/} 复位
                if (i + 2 < text.Length && text[i + 1] == '/' && text[i + 2] == '}')
                {
                    Flush();
                    currentColor = null;
                    i += 3;
                    continue;
                }

                // {#color 内容} 或 {#color}
                var match = MarkerRegex().Match(text, i);
                if (match.Success && match.Index == i)
                {
                    var color = ResolveColor(match.Groups[1].Value);
                    if (color is not null)
                    {
                        var bodyStart = i + match.Length;
                        // 内联形态：标记后紧跟内容，直到匹配的 '}'；没有闭合 '}' 时按"开启色"处理
                        var close = FindClosingBrace(text, bodyStart);
                        if (close >= 0)
                        {
                            Flush();
                            var body = text[bodyStart..close];
                            if (body.Length > 0) spans.Add(new MarkupSpan(body, color));
                            currentColor = null;
                            i = close + 1;
                            continue;
                        }
                        Flush();
                        currentColor = color;
                        i = bodyStart;
                        continue;
                    }
                }
            }

            current.Append(text[i]);
            i++;
        }
        Flush();
        return spans;
    }

    /// <summary>标记文本里有没有可用的颜色标记（界面上用来提示"未使用标记，按默认样式输出"）</summary>
    public static bool HasMarkup(string text) =>
        MarkerRegex().IsMatch(text) || text.Contains("{/}", StringComparison.Ordinal);

    /// <summary>
    /// 片段列表 → § 代码。无色片段按默认样式渲染：
    /// 默认渐变（逐字符精确）或默认单色；<paramref name="defaultHex"/> 为单色时的颜色。
    /// </summary>
    public static string ToSectionCode(
        IReadOnlyList<MarkupSpan> spans,
        bool exactGradient = true,
        string gradientFrom = "#55FFFF",
        string gradientTo = "#FF55FF",
        string? defaultHex = null,
        string styleCodes = "")
    {
        var sb = new StringBuilder();
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            var body = plain.ToString();
            plain.Clear();
            sb.Append(defaultHex is { } hex
                ? McText.Solid(body, hex, legacyPalette: !exactGradient)
                : McText.Gradient(body, gradientFrom, gradientTo, exact: exactGradient));
        }

        foreach (var span in spans)
        {
            if (span.Hex is null)
            {
                plain.Append(span.Text);
                continue;
            }
            FlushPlain();
            sb.Append(McText.Solid(span.Text, span.Hex, legacyPalette: !exactGradient));
        }
        FlushPlain();
        return styleCodes + sb;
    }

    /// <summary>预览用：片段 → 纯文本与颜色序列（界面渲染成带色 Run）</summary>
    public static List<MarkupSpan> PreviewSpans(string text) => Parse(text);

    private static string? ResolveColor(string token)
    {
        if (token.Length is 3 or 6 &&
            token.All(c => Uri.IsHexDigit(c)))
        {
            return "#" + token;
        }
        return ColorNames.TryGetValue(token, out var hex) ? hex : null;
    }

    /// <summary>从 from 起找配对的 '}'（嵌套花括号按深度计数；找不到返回 -1）</summary>
    private static int FindClosingBrace(string text, int from)
    {
        var depth = 1;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
            // 换行视为"不闭合"（用户换行通常意味着这是"开启色"而不是内联片段）
            else if (text[i] == '\n') return -1;
        }
        return -1;
    }
}
