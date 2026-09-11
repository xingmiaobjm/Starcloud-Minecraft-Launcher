// ChunkedFileDownloader.PlanChunks 单元测试：分片区间无重叠、无空洞、总长等于文件大小、片数受限

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class ChunkedFileDownloaderTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>断言：首片从 0 开始、逐片紧邻、各片长度之和 == 文件大小、片数不超过上限</summary>
    private static void AssertCoverage(IReadOnlyList<(long Start, long Length)> plan, long size)
    {
        Assert.Equal(0L, plan[0].Start);

        var chunkSize = plan[0].Length;
        var cursor = 0L;
        foreach (var (start, length) in plan)
        {
            Assert.Equal(cursor, start);                                   // 与上一片紧邻，无重叠无空洞
            Assert.True(length > 0, "分片长度必须为正");
            Assert.True(length <= chunkSize, "单片不得超过首片大小");
            cursor += length;
        }

        Assert.Equal(size, cursor);                                        // 末片正好补齐文件末尾
        Assert.True(plan.Count <= ChunkedFileDownloader.MaxChunks);
    }

    [Fact]
    public void 小文件_单片()
    {
        var plan = ChunkedFileDownloader.PlanChunks(1024);

        var chunk = Assert.Single(plan);
        Assert.Equal((0L, 1024L), chunk);
    }

    [Fact]
    public void 恰好整除_均分且无余片()
    {
        var size = 12 * MiB;

        var plan = ChunkedFileDownloader.PlanChunks(size);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, c => Assert.Equal(ChunkedFileDownloader.ChunkSize, c.Length));
        AssertCoverage(plan, size);
    }

    [Fact]
    public void 有余数_末片为余量()
    {
        var size = 10 * MiB + 123;

        var plan = ChunkedFileDownloader.PlanChunks(size);

        Assert.Equal(3, plan.Count);
        Assert.Equal(ChunkedFileDownloader.ChunkSize, plan[0].Length);
        Assert.Equal(ChunkedFileDownloader.ChunkSize, plan[1].Length);
        Assert.Equal(2 * MiB + 123, plan[2].Length);
        AssertCoverage(plan, size);
    }

    [Fact]
    public void 超过片上限制_扩大片大小并压到上限内()
    {
        var size = 512 * MiB;

        var plan = ChunkedFileDownloader.PlanChunks(size);

        Assert.True(plan.Count <= ChunkedFileDownloader.MaxChunks);
        Assert.True(plan[0].Length > ChunkedFileDownloader.ChunkSize);
        AssertCoverage(plan, size);
    }

    [Fact]
    public void 片大小为整MiB对齐()
    {
        var size = 100 * MiB + 1;

        var plan = ChunkedFileDownloader.PlanChunks(size);

        // 除末片外，片大小都是 1 MiB 的整数倍（Range 请求对偏移更友好）
        for (var i = 0; i < plan.Count - 1; i++)
            Assert.Equal(0, plan[i].Length % MiB);
        AssertCoverage(plan, size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非法大小_返回空计划(long size) =>
        Assert.Empty(ChunkedFileDownloader.PlanChunks(size));

    [Theory]
    [InlineData(1)]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(4 * 1024 * 1024 + 1)]
    [InlineData(64 * 1024 * 1024)]
    [InlineData(70 * 1024 * 1024 + 7)]
    [InlineData(1024 * 1024 * 1024 + 3)]
    public void 各种大小_均满足覆盖性与片上限制(long size)
    {
        var plan = ChunkedFileDownloader.PlanChunks(size);

        Assert.NotEmpty(plan);
        AssertCoverage(plan, size);
    }
}
