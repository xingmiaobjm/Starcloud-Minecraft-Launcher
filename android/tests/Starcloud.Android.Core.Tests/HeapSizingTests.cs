// 堆分级测试：各内存档位的推荐堆上下限

using Starcloud.Android.Core.Memory;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class HeapSizingTests
{
    [Theory]
    [InlineData(1024, 256, 512)]    // 低内存设备：上限 512M
    [InlineData(2048, 256, 512)]    // 档位边界含上界
    [InlineData(2049, 384, 768)]    // 越过边界进下一档
    [InlineData(3072, 384, 768)]
    [InlineData(3073, 512, 1024)]
    [InlineData(4096, 512, 1024)]
    [InlineData(4097, 768, 1536)]
    [InlineData(6144, 768, 1536)]
    [InlineData(6145, 1024, 2048)]
    [InlineData(8192, 1024, 2048)]
    [InlineData(8193, 1536, 3072)]  // 大内存平板：上限 3G
    [InlineData(16384, 1536, 3072)]
    public void 按设备内存档位推荐堆大小(int totalMemoryMb, int expectedMin, int expectedMax)
    {
        var (min, max) = HeapSizing.Recommend(totalMemoryMb);

        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
        Assert.True(min < max, "堆下限必须小于上限（min = max / 2）");
    }
}
