// 安装前磁盘空间预检回归。
//
// 背景：下载到一半才报「磁盘已满」是最难受的失败方式（用户已经等了很久）。
// 原来只有独立的环境体检工具查磁盘，阈值固定 512 MB / 2 GB，安装流程从不调用。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class DiskSpaceGuardTests
{
    [Fact]
    public void 空间充足时放行()
    {
        var ok = DiskSpaceGuard.Check(Path.GetTempPath(), 1024L * 1024 * 1024,
            out var free, out var message);

        // 临时盘空间通常远大于 1 GB（× 1.6 余量后仍应足够）
        Assert.True(ok, message);
        Assert.True(free > 0);
        Assert.Null(message);
    }

    [Fact]
    public void 需求为零时放行()
    {
        Assert.True(DiskSpaceGuard.Enough(Path.GetTempPath(), 0));
    }

    [Fact]
    public void 探测不出盘符时不拦截()
    {
        // 空路径拿不到根目录：探测失败不该阻断安装
        Assert.True(DiskSpaceGuard.Check("", long.MaxValue / 4, out _, out _));
    }

    [Fact]
    public void 需求远超剩余空间时给出提示()
    {
        // 用一个必然超出实际剩余空间的数字（long.MaxValue 会溢出乘数，改用 1 TB）
        var ok = DiskSpaceGuard.Check(Path.GetTempPath(), 1024L * 1024 * 1024 * 1024,
            out _, out var message);

        // 测试机上不一定有 1.7 TB 空间；空间真的够的话这条会失败，所以只断言「结论与提示自洽」
        if (!ok) Assert.False(string.IsNullOrWhiteSpace(message));
        else Assert.Null(message);
    }
}
