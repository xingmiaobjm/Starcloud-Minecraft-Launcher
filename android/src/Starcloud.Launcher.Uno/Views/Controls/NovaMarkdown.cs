// Markdown → StackPanel + TextBlock 渲染（附加属性用法）：
//
//     <StackPanel ctrl:NovaMarkdown.Text="{x:Bind Content, Mode=OneWay}" … />
//
// 解析在 Core 的 MarkdownDocument 里（可单测），这里只负责把 token 变成 UI 元素。
// 模型回复里的 **粗体**、`代码`、- 列表、``` 代码块因此显示成排版，而不是一堆星号。
// Uno Skia 没实现 RichTextBlock（Blocks / Paragraph / Block.Margin 均不可用），
// 所以每个块级元素输出一个 TextBlock：字体字重走 App.xaml 的隐式 TextBlock 样式
// （与对话正文同源），代码里只设与正文不同的部分——标题字号、等宽字体与块间距。

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
// 解析层是纯逻辑：沿用桌面端 Core 命名空间（安卓壳的纯逻辑层保留 Starcloud.Core.Services）
using Starcloud.Core.Services;
// FontStyle / TextDecorations 在 Windows.UI.Text；FontWeights 在 Microsoft.UI.Text（两个都要）
using Windows.UI.Text;

namespace Starcloud.Launcher.Uno.Views.Controls;

public static class NovaMarkdown
{
    // 标题 / 代码字号的推算基准：与 App.xaml 隐式 TextBlock 样式（正文 14 号）一致
    private const double BaseSize = 14;

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(NovaMarkdown),
        new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject target) =>
        (string)target.GetValue(TextProperty);

    public static void SetText(DependencyObject target, string value) =>
        target.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StackPanel panel) Render(panel, e.NewValue as string);
    }

    /// <summary>把一段 Markdown 渲染进目标 StackPanel（清空后重建，一个块级元素一个 TextBlock）</summary>
    public static void Render(StackPanel target, string? markdown)
    {
        // 链接色随主题取（先取好，行内构建时直接用）
        var linkBrush = LookupLinkBrush(target.ActualTheme);
        target.Children.Clear();

        foreach (var block in MarkdownDocument.Parse(markdown))
        {
            switch (block.Kind)
            {
                case MdBlockKind.Code:
                    target.Children.Add(BuildCodeBlock(block));
                    break;

                case MdBlockKind.Heading:
                    target.Children.Add(BuildHeading(block, linkBrush));
                    break;

                case MdBlockKind.ListItem:
                {
                    // 原 Paragraph 的悬挂缩进（TextIndent）在 TextBlock 上没有对应属性，
                    // 退化为整体左缩进：换行后对齐到缩进线而不是符号之后
                    var tb = NewText();
                    tb.Margin = new Thickness(14, 0, 0, 3);
                    tb.Inlines.Add(new Run { Text = block.Marker + " " });
                    AddInlines(tb, block.Inlines, linkBrush);
                    target.Children.Add(tb);
                    break;
                }

                case MdBlockKind.Quote:
                {
                    var tb = NewText();
                    tb.Margin = new Thickness(4, 0, 0, 4);
                    tb.FontStyle = FontStyle.Italic;   // 引用整段斜体：设在 TextBlock 上，Run 经继承生效
                    tb.Inlines.Add(new Run { Text = "│ " });
                    AddInlines(tb, block.Inlines, linkBrush);
                    target.Children.Add(tb);
                    break;
                }

                default:
                {
                    var tb = NewText();
                    tb.Margin = new Thickness(0, 0, 0, 6);
                    AddInlines(tb, block.Inlines, linkBrush);
                    target.Children.Add(tb);
                    break;
                }
            }
        }
    }

    private static TextBlock BuildHeading(MdBlock block, Brush linkBrush)
    {
        var size = block.Level switch
        {
            1 => BaseSize + 6,
            2 => BaseSize + 4,
            3 => BaseSize + 2,
            _ => BaseSize + 1,
        };
        var tb = NewText();
        tb.Margin = new Thickness(0, 4, 0, 6);
        tb.FontSize = size;
        tb.FontWeight = FontWeights.Bold;   // 标题整体加粗：设在 TextBlock 上，Run 经继承生效
        AddInlines(tb, block.Inlines, linkBrush);
        return tb;
    }

    private static TextBlock BuildCodeBlock(MdBlock block)
    {
        var tb = NewText();
        tb.Margin = new Thickness(0, 2, 0, 8);
        tb.FontFamily = new FontFamily("Consolas");
        tb.FontSize = Math.Max(BaseSize - 1, 11);
        // 代码块是纯文本（内容自带 \n）：单个 Run 塞入，由文本管线折行（同 LogsPage 的做法）
        tb.Inlines.Add(new Run { Text = block.Inlines.Count > 0 ? block.Inlines[0].Text : "" });
        return tb;
    }

    private static void AddInlines(TextBlock tb, IReadOnlyList<MdInline> inlines, Brush linkBrush)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold:
                {
                    var run = NewRun(inline.Text);
                    run.FontWeight = FontWeights.Bold;
                    tb.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Italic:
                {
                    var run = NewRun(inline.Text);
                    run.FontStyle = FontStyle.Italic;
                    tb.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Strike:
                {
                    var run = NewRun(inline.Text);
                    run.TextDecorations = TextDecorations.Strikethrough;
                    tb.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Code:
                {
                    var run = NewRun(inline.Text);
                    run.FontFamily = new FontFamily("Consolas");
                    tb.Inlines.Add(run);
                    break;
                }

                case MdInlineKind.Link:
                    // Hyperlink 内联在 Uno Skia 不可用：用下划线 + 强调色复现链接观感
                    tb.Inlines.Add(BuildLink(inline, linkBrush));
                    break;

                default:
                    // 段内软换行：\n 保留在 Run 文本里，由文本管线折行
                    tb.Inlines.Add(NewRun(inline.Text));
                    break;
            }
        }
    }

    private static Run BuildLink(MdInline inline, Brush linkBrush)
    {
        var run = NewRun(inline.Text);
        run.TextDecorations = TextDecorations.Underline;
        run.Foreground = linkBrush;
        return run;
    }

    /// <summary>正文 TextBlock：必须换行（原 Wrap 由调用点设在 RichTextBlock 上，TextBlock 各自补齐）</summary>
    private static TextBlock NewText() => new() { TextWrapping = TextWrapping.Wrap };

    private static Run NewRun(string text) => new() { Text = text };

    /// <summary>
    /// 取链接用的强调色画刷。注意：直接 <c>Application.Current.Resources[key]</c> 不会走
    /// ThemeDictionaries 的当前主题解析（同 LogsPage 的实测），所以按元素所在主题去对应字典查。
    /// </summary>
    private static Brush LookupLinkBrush(ElementTheme theme)
    {
        var res = Application.Current.Resources;
        var themeKey = theme == ElementTheme.Dark ? "Dark" : "Light";
        if (res.ThemeDictionaries.TryGetValue(themeKey, out var themeObj) &&
            themeObj is ResourceDictionary themeRes &&
            themeRes.TryGetValue("AccentTextFillColorPrimaryBrush", out var themed) &&
            themed is Brush themedBrush)
            return themedBrush;
        // 双主题兜底：浅色深琥珀 / 深色品牌黄（与 App.xaml 的取值一致）
        return res["AccentTextFillColorPrimaryBrush"] as Brush
            ?? new SolidColorBrush(theme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 255, 211, 34)
                : Windows.UI.Color.FromArgb(255, 179, 133, 0));
    }
}
