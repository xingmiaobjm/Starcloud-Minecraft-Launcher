// XAML 绑定守卫：x:Bind 的默认模式是 **OneTime**。
//
// 真踩过的坑：Nova 对话模板里写 Visibility="{x:Bind TextVisible}"，流式增量把 Content 填满、
// TextVisible 变成 Visible，但一次性绑定不会重算 —— 气泡一直藏着，直到下一条消息让容器重建
// 才突然露面。用户看到的就是「发消息不回复，再发一条才显示」。
//
// 这条测试不需要跑 UI：直接读 XAML，找出「绑定了运行时会变的属性却没写 Mode=OneWay/TwoWay」的地方。

using System.Text.RegularExpressions;
using Xunit;

namespace Starcloud.Core.Tests;

public class XamlBindingGuardTests
{
    /// <summary>
    /// 会在运行时变化、且被 x:Bind 绑过的属性（按类分组）。
    /// 新增可变属性时，这里加一条 —— 加漏了不要紧，下面还有一条「模板里必须真的用上 OneWay」的检查兜底。
    /// </summary>
    private static readonly string[][] MutableByType =
    [
        // Starcloud.App.Services.NovaChatMessage
        ["Content", "TextVisible", "PendingVisible", "BubbleVisible", "TimeVisible", "IsPending",
         "Plan", "HasPlanVisible", "PlanButtonsVisible", "PlanStateVisible", "PlanStateText",
         "ToolStatus", "ToolStatusVisible"],
    ];

    [Fact]
    public void 可变属性不能用x_Bind的默认OneTime模式()
    {
        var path = FindAppFile(Path.Combine("Views", "Controls", "NovaChatView.xaml"));
        if (path is null) return; // 单独拷贝测试工程跑时不强求

        var mutable = MutableByType.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
        var text = File.ReadAllText(path);
        var violations = new List<string>();

        foreach (Match m in Regex.Matches(text, "\\{x:Bind\\s+([^}\"]+)\\}"))
        {
            var binding = m.Groups[1].Value.Trim();
            var prop = binding.Split(',', ' ')[0].Trim();
            if (!mutable.Contains(prop)) continue;
            if (binding.Contains("Mode=OneWay") || binding.Contains("Mode=TwoWay")) continue;
            violations.Add(prop);
        }

        Assert.True(violations.Count == 0,
            "这些属性会变却没写 Mode=OneWay（OneTime 绑定不会重算，UI 会停在旧值）："
            + string.Join("、", violations.Distinct()));
    }

    [Fact]
    public void Nova对话模板_气泡与等待动画都按OneWay显示()
    {
        var path = FindAppFile(Path.Combine("Views", "Controls", "NovaChatView.xaml"));
        if (path is null) return;

        var text = File.ReadAllText(path);

        // 正文气泡：内容到了就得显形（回归「发消息不回复」）
        Assert.Contains("{x:Bind BubbleVisible, Mode=OneWay}", text);
        Assert.Contains("{x:Bind TextVisible, Mode=OneWay}", text);
        Assert.Contains("{x:Bind Content, Mode=OneWay}", text);
        // 等待首个增量时的三点动画
        Assert.Contains("{x:Bind PendingVisible, Mode=OneWay}", text);
        // 气泡内的时间戳跟着正文出现
        Assert.Contains("{x:Bind TimeVisible, Mode=OneWay}", text);
        // 确认清单卡与执行状态（执行完按钮必须收起来）
        Assert.Contains("{x:Bind HasPlanVisible, Mode=OneWay}", text);
        Assert.Contains("{x:Bind PlanButtonsVisible, Mode=OneWay}", text);
        Assert.Contains("{x:Bind PlanStateVisible, Mode=OneWay}", text);
    }

    /// <summary>
    /// 工具调用只出「一行小提示」，不能写进对话气泡/正文。
    /// 老实现是 onToolStarted 里往 msg.Content 拼「→ 正在执行 search_modrinth {"query":…}」，
    /// 于是内部函数名和参数 JSON 全留在对话里，还会跟着历史回喂给模型。
    /// </summary>
    [Fact]
    public void 工具动作提示走独立状态行不进对话正文()
    {
        var xaml = FindAppFile(Path.Combine("Views", "Controls", "NovaChatView.xaml"));
        var runtime = FindAppFile(Path.Combine("Services", "NovaRuntime.cs"));
        if (xaml is null || runtime is null) return;

        var xamlText = File.ReadAllText(xaml);
        Assert.Contains("{x:Bind ToolStatusVisible, Mode=OneWay}", xamlText);
        Assert.Contains("{x:Bind ToolStatus, Mode=OneWay}", xamlText);

        var code = File.ReadAllText(runtime);
        Assert.DoesNotContain("msg.Content += $\"\\n→", code);   // 正文里不再拼动作行
        Assert.DoesNotContain("正在执行", code);
        Assert.Contains("msg.ToolStatus = ToolActionLabel(tool)", code);
    }

    /// <summary>从测试输出目录往上找 App 工程里的文件；找不到返回 null</summary>
    private static string? FindAppFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Starcloud.App", relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
