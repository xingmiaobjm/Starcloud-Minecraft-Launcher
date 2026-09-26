// Markdown → RichTextBlock 渲染（附加属性用法）：
//
//     <RichTextBlock ctrl:NovaMarkdown.Text="{x:Bind Content, Mode=OneWay}" … />
//
// 解析在 Core 的 MarkdownDocument 里（可单测），这里只负责把 token 变成 WinUI 内联。
// 模型回复里的 **粗体**、`代码`、- 列表、``` 代码块因此显示成排版，而不是一堆星号。

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
// FontStyle / TextDecorations 在 Windows.UI.Text；FontWeights 在 Microsoft.UI.Text（两个都要）
using Windows.UI.Text;

namespace Starcloud.App.Views.Controls;

public static class NovaMarkdown
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(NovaMarkdown),
        new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject target) =>
        (string)target.GetValue(TextProperty);

    public static void SetText(DependencyObject target, string value) =>
        target.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBlock block) Render(block, e.NewValue as string);
    }

    /// <summary>把一段 Markdown 渲染进目标 RichTextBlock（清空后重建）</summary>
    public static void Render(RichTextBlock target, string? markdown)
    {
        var baseSize = target.FontSize > 0 ? target.FontSize : 14;
        target.Blocks.Clear();

        foreach (var block in MarkdownDocument.Parse(markdown))
        {
            switch (block.Kind)
            {
                case MdBlockKind.Code:
                    target.Blocks.Add(BuildCodeBlock(block, baseSize));
                    break;

                case MdBlockKind.Heading:
                    target.Blocks.Add(BuildHeading(block, baseSize));
                    break;

                case MdBlockKind.ListItem:
                {
                    var p = new Paragraph
                    {
                        Margin = new Thickness(14, 0, 0, 3),
                        TextIndent = -14,      // 悬挂缩进：换行后对齐到文字，而不是对齐到符号
                    };
                    p.Inlines.Add(new Run { Text = block.Marker + " " });
                    AddInlines(p, block.Inlines, baseSize);
                    target.Blocks.Add(p);
                    break;
                }

                case MdBlockKind.Quote:
                {
                    var p = new Paragraph { Margin = new Thickness(4, 0, 0, 4) };
                    p.Inlines.Add(new Run { Text = "│ " });
                    AddInlines(p, block.Inlines, baseSize);
                    foreach (var inline in p.Inlines) inline.FontStyle = FontStyle.Italic;
                    target.Blocks.Add(p);
                    break;
                }

                default:
                {
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                    AddInlines(p, block.Inlines, baseSize);
                    target.Blocks.Add(p);
                    break;
                }
            }
        }
    }

    private static Paragraph BuildHeading(MdBlock block, double baseSize)
    {
        var size = block.Level switch
        {
            1 => baseSize + 6,
            2 => baseSize + 4,
            3 => baseSize + 2,
            _ => baseSize + 1,
        };
        var p = new Paragraph { Margin = new Thickness(0, 4, 0, 6) };
        AddInlines(p, block.Inlines, size);
        foreach (var inline in p.Inlines)
        {
            inline.FontWeight = FontWeights.Bold;
            if (inline.FontSize <= 0) inline.FontSize = size;
        }
        return p;
    }

    private static Paragraph BuildCodeBlock(MdBlock block, double baseSize)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = Math.Max(baseSize - 1, 11),
        };
        var lines = (block.Inlines.Count > 0 ? block.Inlines[0].Text : "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            p.Inlines.Add(new Run { Text = lines[i] });
        }
        return p;
    }

    private static void AddInlines(Paragraph p, IReadOnlyList<MdInline> inlines, double size)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold:
                {
                    var run = NewRun(inline.Text, size);
                    run.FontWeight = FontWeights.Bold;
                    p.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Italic:
                {
                    var run = NewRun(inline.Text, size);
                    run.FontStyle = FontStyle.Italic;
                    p.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Strike:
                {
                    var run = NewRun(inline.Text, size);
                    run.TextDecorations = TextDecorations.Strikethrough;
                    p.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Code:
                {
                    var run = NewRun(inline.Text, size);
                    run.FontFamily = new FontFamily("Consolas");
                    p.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Link:
                    p.Inlines.Add(BuildLink(inline, size));
                    break;

                default:
                    AddTextWithLineBreaks(p, inline.Text, size);
                    break;
            }
        }
    }

    private static Hyperlink BuildLink(MdInline inline, double size)
    {
        var link = new Hyperlink();
        if (Uri.TryCreate(inline.Url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            link.NavigateUri = uri;
        link.Inlines.Add(NewRun(inline.Text, size));
        return link;
    }

    /// <summary>段内软换行：\n 渲染成真正的换行，而不是丢掉</summary>
    private static void AddTextWithLineBreaks(Paragraph p, string text, double size)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) p.Inlines.Add(NewRun(lines[i], size));
        }
    }

    private static Run NewRun(string text, double size)
    {
        var run = new Run { Text = text };
        if (size > 0) run.FontSize = size;
        return run;
    }
}
