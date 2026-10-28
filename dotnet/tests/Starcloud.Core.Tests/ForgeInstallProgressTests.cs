// Forge / NeoForge 安装的输出翻译回归。
//
// 背景：官方安装器输出的是「Extracting json」「Building Processors」「Remapping jar... 50%」
// 这类英文行，直接甩给用户等于没有进度。参照 PCL 的 ForgelikeInjectorLine 做了映射。
// 另外成功判定不只看 exit code —— 官方安装器在部分路径下 exit code 不可靠，
// 真正信号是输出末尾的 "true"（PCL 同样按这个判定）。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class ForgeInstallProgressTests
{
    [Theory]
    [InlineData("Extracting json", "正在解压版本信息")]
    [InlineData("Downloading libraries", "正在下载依赖库")]
    [InlineData("Building Processors", "正在构建处理器")]
    [InlineData("Task: DOWNLOAD_MOJMAPS", "正在下载官方映射")]
    [InlineData("Task: MERGE_MAPPING", "正在合并映射")]
    [InlineData("Processing Complete", "处理完成")]
    [InlineData("Injecting profile", "正在写入版本配置")]
    public void 安装器输出翻译成中文阶段(string raw, string expected)
    {
        var text = ForgeInstallerService.DescribeInstallStage(raw);

        Assert.StartsWith(expected, text);
    }

    [Fact]
    public void 重映射行带出百分比()
    {
        var text = ForgeInstallerService.DescribeInstallStage("Remapping jar... 50%");

        Assert.Contains("50%", text);
        Assert.Contains("重映射", text);
    }

    [Fact]
    public void 未知行原样透传()
    {
        // 错误信息之类不能被改写，否则用户看不懂
        var text = ForgeInstallerService.DescribeInstallStage("java.lang.OutOfMemoryError: Java heap space");

        Assert.Equal("java.lang.OutOfMemoryError: Java heap space", text);
    }
}
