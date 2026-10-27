// 启动指令生成回归：JVM 参数去重与 classpath 参数识别。
//
// 背景：JVM 对 -Xmx 这类参数取「最后一个」生效，所以用户在设置里写了 -Xmx4G 时
// 实际生效的是 4G，而界面显示的还是设置里的数字 —— 两边对不上，用户以为没生效。
// 去重后同名参数只留一条，界面与实际行为就对齐了。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class GameArgumentTests
{
    [Fact]
    public void 用户参数与内置参数同名时只保留用户那条()
    {
        var args = new List<string> { "-Xms1024M", "-Xmx2048M", "-Xmx4G", "-Dfile.encoding=UTF-8" };
        var user = new[] { "-Xmx4G" };

        var result = GameLauncher.DedupeJvmArgs(args, user);

        // -Xmx 只剩一条，且是用户写的那条
        Assert.Single(result, a => a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-Xmx4G", result);
        Assert.DoesNotContain("-Xmx2048M", result);
        // 其它参数原样保留
        Assert.Contains("-Xms1024M", result);
        Assert.Contains("-Dfile.encoding=UTF-8", result);
    }

    [Fact]
    public void 没有用户参数时原样返回()
    {
        var args = new List<string> { "-Xms1024M", "-Xmx2048M" };

        var result = GameLauncher.DedupeJvmArgs(args, Array.Empty<string>());

        Assert.Equal(args, result);
    }

    [Fact]
    public void 用户参数在前时保留用户那条并维持原顺序()
    {
        // 内置参数先加、用户参数后加 —— 这是实际调用顺序
        var args = new List<string> { "-Xmx2048M", "-Xmx4G" };
        var user = new[] { "-Xmx4G" };

        var result = GameLauncher.DedupeJvmArgs(args, user);

        Assert.Single(result, a => a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("-Xmx4G", result[0]);
    }

    [Fact]
    public void 系统属性同名时保留后者()
    {
        var args = new List<string> { "-Dfoo=1", "-Dfoo=2" };
        var user = new[] { "-Dfoo=2" };

        var result = GameLauncher.DedupeJvmArgs(args, user);

        Assert.Single(result);
        Assert.Equal("-Dfoo=2", result[0]);
    }

    [Theory]
    [InlineData("-cp", true)]
    [InlineData("-classpath", true)]
    [InlineData("--class-path", true)]
    [InlineData("-CP", true)]
    [InlineData("-Xmx2G", false)]
    [InlineData("-Djava.library.path=x", false)]
    public void classpath参数识别覆盖三种写法(string arg, bool expected)
    {
        Assert.Equal(expected, GameLauncher.HasClasspathArg(new List<string> { arg }));
    }
}
