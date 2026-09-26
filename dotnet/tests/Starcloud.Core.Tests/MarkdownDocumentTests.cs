// Nova 对话正文的 Markdown 解析守卫。
//
// 用户看到的是「模型输出 \*」：模型回复里的 **粗体** / `代码` / - 列表原本直接进 TextBlock，
// 格式符号原样露在气泡里。这里锁住解析结果，App 侧只负责把 token 画成 RichTextBlock 内联。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class MarkdownDocumentTests
{
    [Fact]
    public void 粗体斜体删除线都成token()
    {
        var inlines = MarkdownDocument.ParseInlines("**粗** 与 *斜* 与 ~~删~~");

        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Bold && i.Text == "粗");
        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Italic && i.Text == "斜");
        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Strike && i.Text == "删");
    }

    [Fact]
    public void 反斜杠转义还原成字面字符()
    {
        // 关键回归：\* 不该继续显示成「\*」
        var inlines = MarkdownDocument.ParseInlines(@"\*不是斜体\*");

        var text = string.Concat(inlines.Where(i => i.Kind == MdInlineKind.Text).Select(i => i.Text));
        Assert.Equal("*不是斜体*", text);
        Assert.DoesNotContain(inlines, i => i.Kind == MdInlineKind.Italic);
    }

    [Fact]
    public void 行内代码与链接()
    {
        var inlines = MarkdownDocument.ParseInlines("用 `install_minecraft` 看 [文档](https://example.com/x)");

        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Code && i.Text == "install_minecraft");
        var link = Assert.Single(inlines, i => i.Kind == MdInlineKind.Link);
        Assert.Equal("文档", link.Text);
        Assert.Equal("https://example.com/x", link.Url);
    }

    [Fact]
    public void 无序与有序列表带符号()
    {
        var blocks = MarkdownDocument.Parse("- 第一项\n1. 第二项\n* 第三项");

        Assert.All(blocks, b => Assert.Equal(MdBlockKind.ListItem, b.Kind));
        Assert.Equal(["•", "1.", "•"], blocks.Select(b => b.Marker));
    }

    [Theory]
    [InlineData("# 一级", 1)]
    [InlineData("### 三级", 3)]
    [InlineData("###### 六级", 6)]
    public void 标题层级(string line, int level)
    {
        var block = Assert.Single(MarkdownDocument.Parse(line));

        Assert.Equal(MdBlockKind.Heading, block.Kind);
        Assert.Equal(level, block.Level);
    }

    [Fact]
    public void 井号后面没空格不算标题()
    {
        var block = Assert.Single(MarkdownDocument.Parse("#1.21.1 更新"));

        Assert.Equal(MdBlockKind.Paragraph, block.Kind);
    }

    [Fact]
    public void 代码块整段保留且不解析行内格式()
    {
        var blocks = MarkdownDocument.Parse("```\n**不是粗体**\n--flag\n```");

        var code = Assert.Single(blocks);
        Assert.Equal(MdBlockKind.Code, code.Kind);
        Assert.Equal("**不是粗体**\n--flag", Assert.Single(code.Inlines).Text);
    }

    [Fact]
    public void 引用与段落()
    {
        var blocks = MarkdownDocument.Parse("> 引用一句\n\n普通段落**加粗**");

        Assert.Equal(MdBlockKind.Quote, blocks[0].Kind);
        Assert.Equal(MdBlockKind.Paragraph, blocks[1].Kind);
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.Bold);
    }

    [Fact]
    public void 段内换行保留成同一段()
    {
        var block = Assert.Single(MarkdownDocument.Parse("第一行\n第二行"));

        Assert.Equal(MdBlockKind.Paragraph, block.Kind);
        Assert.Contains("\n", string.Concat(block.Inlines.Select(i => i.Text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("**没闭合")]
    [InlineData("`没闭合")]
    [InlineData("[没闭合](http://")]
    public void 坏输入按纯文本处理不抛异常(string? input)
    {
        var blocks = MarkdownDocument.Parse(input);

        Assert.NotNull(blocks);
    }
}
