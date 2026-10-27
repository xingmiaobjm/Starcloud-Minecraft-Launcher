// 下载源健康度评分回归。
//
// 背景：源池原来只按「有没有被降级」排序 —— 一个「能连但很慢」的镜像会一直排在最前面，
// 整个下载都被它拖住，用户看到的就是「时快时慢」。现在池排序在降级标记之上
// 加了一层健康度评分（实测吞吐 + 失败次数 + 限流次数）。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class DownloadSourceHealthTests
{
    private static DownloadSourceRegistry Registry()
    {
        var settings = new LauncherSettings { DownloadSource = "auto", DownloadCustomMirrorUrl = "" };
        return new DownloadSourceRegistry(() => settings);
    }

    private static int IndexOf(IReadOnlyList<SourceKind> pool, SourceKind kind)
    {
        for (var i = 0; i < pool.Count; i++)
            if (pool[i] == kind) return i;
        return -1;
    }

    [Fact]
    public void 没有测速样本的源评分为零()
    {
        var registry = Registry();

        // 没测过速 → 不参与排序（交给分片阶段的实测择优）
        Assert.Equal(0, registry.ScoreOf(SourceKind.Bmclapi));
        Assert.Equal(0, registry.ScoreOf(SourceKind.Fastmirror));
    }

    [Fact]
    public void 测速更快的源评分更高()
    {
        var registry = Registry();

        // 1 MB/s 与 4 MB/s 的样本
        registry.RecordSpeed(SourceKind.Bmclapi, 1_048_576, 1000);   // 1 MB/s
        registry.RecordSpeed(SourceKind.Fastmirror, 4_194_304, 1000); // 4 MB/s

        Assert.True(registry.ScoreOf(SourceKind.Fastmirror) > registry.ScoreOf(SourceKind.Bmclapi),
            "更快的源应当得分更高");
    }

    [Fact]
    public void 失败与限流会扣分()
    {
        var registry = Registry();

        registry.RecordSpeed(SourceKind.Bmclapi, 4_194_304, 1000);
        var clean = registry.ScoreOf(SourceKind.Bmclapi);

        // 连续失败达到降级阈值（3 次）
        for (var i = 0; i < 5; i++) registry.RecordFailure(SourceKind.Bmclapi);
        var afterFailures = registry.ScoreOf(SourceKind.Bmclapi);

        Assert.True(afterFailures < clean, "失败应当扣分");

        // 限流同样扣分
        registry.RecordThrottled(SourceKind.Bmclapi);
        Assert.True(registry.ScoreOf(SourceKind.Bmclapi) < afterFailures, "限流应当扣分");
    }

    [Fact]
    public void 池排序把慢源排在快源之后()
    {
        var registry = Registry();

        registry.RecordSpeed(SourceKind.Bmclapi, 262_144, 1000);      // 0.25 MB/s：慢
        registry.RecordSpeed(SourceKind.Fastmirror, 8_388_608, 1000);  // 8 MB/s：快

        var pool = registry.RacingPool();

        // 官方恒在最后
        Assert.Equal(SourceKind.Official, pool[^1]);
        // 快源在慢源之前
        Assert.True(IndexOf(pool, SourceKind.Fastmirror) < IndexOf(pool, SourceKind.Bmclapi),
            $"池顺序不对：{string.Join(" → ", pool)}");
    }

    [Fact]
    public void 降级的源排在健康源之后()
    {
        var registry = Registry();

        registry.RecordSpeed(SourceKind.Bmclapi, 8_388_608, 1000);
        registry.RecordSpeed(SourceKind.Fastmirror, 8_388_608, 1000);
        for (var i = 0; i < 5; i++) registry.RecordFailure(SourceKind.Bmclapi);

        var pool = registry.RacingPool();

        Assert.True(IndexOf(pool, SourceKind.Fastmirror) < IndexOf(pool, SourceKind.Bmclapi),
            "降级源应当靠后");
    }

    [Fact]
    public void 成功会刷新样本的新鲜度()
    {
        var registry = Registry();

        registry.RecordSpeed(SourceKind.Bmclapi, 4_194_304, 1000);
        // 样本刚写入时「新鲜度」还没生效（LastSuccessAt 还没被成功请求刷新），
        // 成功之后新鲜度补上，分数回升 —— 这是预期：源确实还活着。
        registry.RecordSuccess(SourceKind.Bmclapi);

        Assert.True(registry.ScoreOf(SourceKind.Bmclapi) > 0, "成功之后分数应回到正值");
        Assert.True(!registry.IsDegraded(SourceKind.Bmclapi), "成功应当解除降级");
    }
}
