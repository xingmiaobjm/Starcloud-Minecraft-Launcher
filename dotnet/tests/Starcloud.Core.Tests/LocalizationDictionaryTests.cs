// 英文词典（Strings/en-US.json）守卫。
//
// 为什么值得单独测：Loc.Load 用 JsonSerializer.Deserialize<Dictionary<string,string>> 读词典，
// **遇到重复键会整体抛异常**，被 catch 吞掉之后整本词典变成空表——表现是"切到英文没反应"，
// 而且没有任何提示。曾经真的发生过（文件尾部追加词条时重复了 6 条）。
// 这里把「键不重复 + UTF-8 可解析」钉成测试，避免以后再悄悄踩。

using System.Text.Json;
using Xunit;

namespace Starcloud.Core.Tests;

public class LocalizationDictionaryTests
{
    [Fact]
    public void 英文词典可解析且键不重复()
    {
        var path = FindDictionary();
        if (path is null) return; // 单独拷贝测试工程跑时不强求

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!seen.Add(prop.Name)) duplicates.Add(prop.Name);
            Assert.False(string.IsNullOrWhiteSpace(prop.Value.GetString()),
                $"词条 {prop.Name} 的译文为空");
        }

        Assert.True(duplicates.Count == 0,
            "英文词典存在重复键（会让整本词典静默失效）：" + string.Join("、", duplicates));
    }

    /// <summary>从测试输出目录往上找仓库里的词典文件；找不到返回 null</summary>
    private static string? FindDictionary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Starcloud.App", "Strings", "en-US.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
