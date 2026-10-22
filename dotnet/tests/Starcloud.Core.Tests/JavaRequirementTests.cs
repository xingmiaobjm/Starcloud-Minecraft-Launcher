// 版本 → 所需 Java 主版本的推断规则：修复「1.17/1.19.4 被分配 Java 8」的抽风。
// 元数据声明（javaVersion.majorVersion）优先，缺失时按版本号推断。

using Starcloud.Core.Models;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class JavaRequirementTests
{
    [Theory]
    [InlineData("1.16.5", 8)]
    [InlineData("1.12.2", 8)]
    [InlineData("1.17", 16)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.18", 17)]
    [InlineData("1.19.4", 17)]
    [InlineData("1.20.1", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.20.6", 21)]
    [InlineData("1.21.11", 21)]
    [InlineData("26.3", 25)]
    [InlineData("26.4-snapshot-5", 25)]
    [InlineData("fabric-loader-0.18.2-1.20.1", 17)]
    [InlineData("机械动力航空学整合包", 21)]
    public void 缺少元数据时按版本号推断(string id, int expected)
    {
        Assert.Equal(expected, JavaRequirement.FromVersionId(id));
        // JavaVersionRef 的 majorVersion 缺失时是 0，应走推断而不是回退成 8
        Assert.Equal(expected, JavaRequirement.Resolve(id, new JavaVersionRef()));
    }

    [Fact]
    public void 元数据声明优先于版本号推断()
    {
        var declared = new JavaVersionRef { MajorVersion = 21 };
        Assert.Equal(21, JavaRequirement.Resolve("1.20.1", declared));

        // 明确声明 Java 8 的老版本也要尊重声明
        var legacy = new JavaVersionRef { MajorVersion = 8 };
        Assert.Equal(8, JavaRequirement.Resolve("1.17", legacy));
    }

    [Fact]
    public void 空版本号回退到8()
    {
        Assert.Equal(8, JavaRequirement.FromVersionId(""));
        Assert.Equal(8, JavaRequirement.FromVersionId(null));
    }
}
