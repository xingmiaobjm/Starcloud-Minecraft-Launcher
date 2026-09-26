// 轻量 Markdown 解析（块 + 行内两类 token，纯文本、不依赖任何 UI 类型）。
//
// 为什么需要它：模型回复几乎都带 Markdown（**粗体**、`代码`、- 列表、``` 代码块）。
// 直接塞进 TextBlock 就会把星号和反引号原样显示出来，用户看到的就是「模型输出 \*」。
// 这里只做「够用且不翻车」的子集：标题 / 段落 / 列表 / 引用 / 代码块
// + 粗体 / 斜体 / 删除线 / 行内代码 / 链接 / 反斜杠转义。
// 渲染成 WinUI 内联由 App 侧的 NovaMarkdown 负责（本文件可被单元测试直接调用）。

using System.Text;

namespace Starcloud.Core.Services;

public enum MdBlockKind { Paragraph, Heading, ListItem, Quote, Code }

public enum MdInlineKind { Text, Bold, Italic, Strike, Code, Link }

/// <summary>一段行内内容。<paramref name="Url"/> 只在 <see cref="MdInlineKind.Link"/> 时有值。</summary>
public sealed record MdInline(MdInlineKind Kind, string Text, string? Url = null);

/// <summary>一个块级元素。<paramref name="Level"/> 用于标题（1~6），<paramref name="Marker"/> 用于列表符号。</summary>
public sealed record MdBlock(
    MdBlockKind Kind, IReadOnlyList<MdInline> Inlines, int Level = 0, string Marker = "");

public static class MarkdownDocument
{
    /// <summary>解析成块序列；永远不会抛（坏输入按纯文本处理）</summary>
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        var text = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');

        var buffer = new StringBuilder();          // 连续普通行合成一段
        var code = new List<string>();             // ``` 代码块
        var inCode = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, ParseInlines(buffer.ToString())));
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    blocks.Add(new MdBlock(MdBlockKind.Code, [new MdInline(MdInlineKind.Text, string.Join('\n', code))]));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    Flush();
                    inCode = true;
                }
                continue;
            }
            if (inCode) { code.Add(line); continue; }

            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

            if (HeadingLevel(trimmed) is var (level, headingText) && level > 0)
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Heading, ParseInlines(headingText), level));
                continue;
            }

            if (ListMarker(trimmed) is var (marker, rest) && marker.Length > 0)
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.ListItem, ParseInlines(rest), 0, marker));
                continue;
            }

            if (trimmed.Length > 1 && trimmed[0] == '>' &&
                (trimmed[1] == ' ' || trimmed[1] == '\t'))
            {
                Flush();
                blocks.Add(new MdBlock(MdBlockKind.Quote, ParseInlines(trimmed[2..])));
                continue;
            }

            if (buffer.Length > 0) buffer.Append('\n');   // 段内软换行：保留，渲染成 <LineBreak>
            buffer.Append(line.TrimEnd());
        }

        if (inCode && code.Count > 0)
            blocks.Add(new MdBlock(MdBlockKind.Code, [new MdInline(MdInlineKind.Text, string.Join('\n', code))]));
        Flush();
        return blocks;
    }

    /// <summary>解析一段行内 Markdown（粗体 / 斜体 / 删除线 / 行内代码 / 链接 / 转义）</summary>
    public static IReadOnlyList<MdInline> ParseInlines(string text)
    {
        var result = new List<MdInline>();
        var plain = new StringBuilder();
        var i = 0;

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            result.Add(new MdInline(MdInlineKind.Text, plain.ToString()));
            plain.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            // 反斜杠转义：\* 就是字面的星号，不再是「格式符号」
            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Code, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '*' || c == '_')
            {
                // ** 优先于 *（__ 同理）
                if (i + 1 < text.Length && text[i + 1] == c)
                {
                    var end = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        FlushPlain();
                        result.Add(new MdInline(MdInlineKind.Bold, text[(i + 2)..end]));
                        i = end + 2;
                        continue;
                    }
                }
                var single = text.IndexOf(c, i + 1);
                if (single > i + 1)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Italic, text[(i + 1)..single]));
                    i = single + 1;
                    continue;
                }
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Strike, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }

            if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close + 2)
                    {
                        var label = text[(i + 1)..close];
                        var url = text[(close + 2)..urlEnd].Trim();
                        if (label.Length > 0 && url.Length > 0)
                        {
                            FlushPlain();
                            result.Add(new MdInline(MdInlineKind.Link, label, url));
                            i = urlEnd + 1;
                            continue;
                        }
                    }
                }
            }

            plain.Append(c);
            i++;
        }

        FlushPlain();
        return result;
    }

    private static bool IsEscapable(char c) =>
        c is '*' or '_' or '`' or '~' or '[' or ']' or '(' or ')' or '#' or '>' or '\\' or '-' or '.';

    /// <summary>「## 标题」→ (2, "标题")；不是标题返回 (0, "")</summary>
    private static (int Level, string Text) HeadingLevel(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#' && level < 6) level++;
        if (level == 0) return (0, "");
        if (level >= trimmed.Length || trimmed[level] != ' ') return (0, "");
        return (level, trimmed[(level + 1)..].Trim());
    }

    /// <summary>「- 项」「1. 项」→ ("•", "项") / ("1.", "项")；不是列表返回 ("", "")</summary>
    private static (string Marker, string Text) ListMarker(string trimmed)
    {
        if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') &&
            trimmed[1] == ' ')
            return ("•", trimmed[2..].Trim());

        var digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length &&
            (trimmed[digits] == '.' || trimmed[digits] == ')') && trimmed[digits + 1] == ' ')
            return (trimmed[..(digits + 1)], trimmed[(digits + 2)..].Trim());

        return ("", "");
    }
}
