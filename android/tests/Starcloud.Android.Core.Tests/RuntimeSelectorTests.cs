// JRE 运行时选择测试：声明优先 / 版本号推断 / 组件名映射

using Starcloud.Android.Core.Launch;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class RuntimeSelectorTests
{
    [Theory]
    [InlineData(8, 8)]
    [InlineData(11, 17)]
    [InlineData(17, 17)]
    [InlineData(21, 21)]
    [InlineData(25, 21)]
    public void 版本声明的主版本折算到三档(int declared, int expected)
    {
        Assert.Equal(expected, RuntimeSelector.SelectJreMajor("26.3", declared));
    }

    [Fact]
    public void 缺声明时按版本号minor推断()
    {
        Assert.Equal(8, RuntimeSelector.SelectJreMajor("1.8.9", null));
        Assert.Equal(8, RuntimeSelector.SelectJreMajor("1.12.2", null));
        Assert.Equal(8, RuntimeSelector.SelectJreMajor("1.16.5", null));
        Assert.Equal(17, RuntimeSelector.SelectJreMajor("1.17.1", null));
        Assert.Equal(21, RuntimeSelector.SelectJreMajor("1.20.4", null));
        Assert.Equal(21, RuntimeSelector.SelectJreMajor("1.21", null));
    }

    [Fact]
    public void 快照等非release命名按最新LTS处理()
    {
        Assert.Equal(21, RuntimeSelector.SelectJreMajor("24w14a", null));
        Assert.Equal(21, RuntimeSelector.SelectJreMajor("26.3", null));
    }

    [Theory]
    [InlineData(8, "jre8")]
    [InlineData(17, "jre17")]
    [InlineData(21, "jre21")]
    public void JRE主版本映射组件名(int jreMajor, string componentName)
    {
        Assert.Equal(componentName, RuntimeSelector.ComponentNameFor(jreMajor));
    }
}
