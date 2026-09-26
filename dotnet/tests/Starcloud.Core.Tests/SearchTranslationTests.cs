// 中文搜索词 → 英文搜索词（SearchTranslationService）测试。
//
// 分两层：
//   ① 离线层（默认跑）：识别中文、判定「可用的英文词」、内置词典命中 —— 全都不联网，
//      所以即使在断网/无代理的 CI 上也稳定；
//   ② 联调层（设 STARCLOUD_IT_TRANSLATE=1 才跑）：真去请求 MC 百科 / 机器翻译，
//      用来确认「后台翻译」这条路真的通（实测响应见 docs/designs/2026-09-21-search-translation-design.md）。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class SearchTranslationTests
{
    private static SearchTranslationService NewService() =>
        new(new DownloadService(() => new LauncherSettings()));

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable("STARCLOUD_IT_TRANSLATE") == "1";

    // ── ① 离线：判定函数 ────────────────────────────────────────────────

    [Theory]
    [InlineData("钠", true)]
    [InlineData("物品管理器", true)]
    [InlineData("Sodium 钠", true)]
    [InlineData("sodium", false)]
    [InlineData("", false)]
    [InlineData("123", false)]
    public void 含中文判定(string text, bool expected) =>
        Assert.Equal(expected, SearchTranslationService.ContainsCjk(text));

    [Theory]
    [InlineData("Sodium", true)]
    [InlineData("Just Enough Items", true)]
    [InlineData("Industrial Craft 2", true)]
    [InlineData("钠", false)]          // 还是中文：不能用
    [InlineData("1.20.1", false)]      // 纯版本号：不是项目名
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void 英文搜索结果判定(string text, bool expected) =>
        Assert.Equal(expected, SearchTranslationService.IsUsableEnglish(text));

    [Fact]
    public void 过长的英文词不采用()
    {
        Assert.False(SearchTranslationService.IsUsableEnglish(new string('a', 81)));
        Assert.True(SearchTranslationService.IsUsableEnglish(new string('a', 80)));
    }

    // ── ① 离线：翻译入口 ───────────────────────────────────────────────

    [Fact]
    public async Task 纯英文查询原样返回且不标记翻译()
    {
        var t = await NewService().TranslateAsync("sodium");
        Assert.Equal("sodium", t.Effective);
        Assert.Equal("sodium", t.Original);
        Assert.False(t.Translated);
        Assert.Null(t.Source);
    }

    [Fact]
    public async Task 空查询原样返回()
    {
        var t = await NewService().TranslateAsync("   ");
        Assert.Equal("", t.Effective);
        Assert.False(t.Translated);
    }

    [Fact]
    public async Task 内置词典命中不联网()
    {
        var t = await NewService().TranslateAsync("钠");
        Assert.Equal("Sodium", t.Effective);
        Assert.Equal("dict", t.Source);
        Assert.True(t.Translated);
    }

    [Fact]
    public async Task 词典命中时首尾空格被裁掉()
    {
        var t = await NewService().TranslateAsync("  机械动力  ");
        Assert.Equal("Create", t.Effective);
    }

    [Theory]
    [InlineData("Sodium 钠", "Sodium")]           // 机翻会叠成「Sodium Sodium」，这里只取英文部分
    [InlineData("create 机械动力", "create")]
    [InlineData("钠 sodium-fabric", "sodium-fabric")]
    public async Task 中英混输只取英文部分不联网(string query, string expected)
    {
        var t = await NewService().TranslateAsync(query);
        Assert.Equal(expected, t.Effective);
        Assert.Equal("latin", t.Source);
        Assert.True(t.Translated);
    }

    [Theory]
    [InlineData("钠 1.20", null)]      // 没有连续两个拉丁字母：不能当成「英文部分」直接拿去搜
    [InlineData("钠", null)]
    [InlineData("sodium", "sodium")]   // 纯英文在 TranslateAsync 入口就原样返回了，走不到这里
    [InlineData("钠 a 1", null)]       // 单个字母算参数不算项目名
    [InlineData("Sodium 钠", "Sodium")]
    [InlineData("钠 JEI", "JEI")]
    public void 中英混输的英文部分判定(string query, string? expected) =>
        Assert.Equal(expected, SearchTranslationService.LatinOnlySegment(query));

    [Fact]
    public void 版本号算参数不算项目名()
    {
        // 「钠 1.20」整体交给百科 / 机翻（有效词仍可能含版本号，这是后续路径的事）
        Assert.Null(SearchTranslationService.LatinOnlySegment("钠 1.20"));
        // 英文 + 版本号：只留英文部分（纯英文查询在入口就原样返回了，走不到这里）
        Assert.Equal("sodium", SearchTranslationService.LatinOnlySegment("sodium 1.20"));
    }

    // ── ② 联调：真去查 MC 百科 / 机器翻译 ───────────────────────────────

    [Theory]
    [InlineData("物品管理", "Just Enough Items")]   // MC 百科收录：[JEI] JEI物品管理器 (Just Enough Items)
    [InlineData("工业时代2", "Industrial Craft 2")] // 同上：[IC2] 工业时代2 (Industrial Craft 2)
    public async Task 联调_未收录在词典的中文词走百科拿到英文名(string query, string expected)
    {
        if (!IntegrationEnabled) return;
        var t = await NewService().TranslateAsync(query);
        Assert.True(t.Translated, $"{query} 应被翻译（实际返回 {t.Effective}）");
        Assert.Equal(expected, t.Effective);
        Assert.Equal("mcmod", t.Source);
    }

    [Fact]
    public async Task 联调_长尾词兜底机器翻译不抛异常()
    {
        if (!IntegrationEnabled) return;
        var t = await NewService().TranslateAsync("把这个词换成英文看看效果");
        // 百科大概率没有这条目，机翻给什么不强求；要求是：要么原文，要么是能拿去搜的英文
        Assert.True(!t.Translated || SearchTranslationService.IsUsableEnglish(t.Effective),
            $"翻译结果不可用：{t.Effective}");
    }

    [Fact]
    public async Task 联调_有超时上限不会拖住搜索()
    {
        if (!IntegrationEnabled) return;
        // 翻译是旁路能力：单步 6 秒超时，百科 + 机翻两步封顶也远小于搜索能接受的等待
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = await NewService().TranslateAsync("一个没有收录的奇怪词条");
        sw.Stop();
        Assert.False(string.IsNullOrWhiteSpace(t.Effective));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"翻译耗时 {sw.Elapsed}");
    }
}
