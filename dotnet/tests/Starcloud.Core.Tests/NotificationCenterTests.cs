// 通知中心单元测试：写入顺序 / 空白忽略 / 级别 / 容量上限 / 清空
// 服务是进程内单例，每个用例先清空再断言。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class NotificationCenterTests
{
    private static NotificationCenter Fresh()
    {
        var nc = NotificationCenter.Instance;
        nc.Clear();
        return nc;
    }

    [Fact]
    public void Notify_NewestFirst_WithIncreasingIds()
    {
        var nc = Fresh();

        nc.Info("第一条");
        nc.Info("第二条");

        var snapshot = nc.Snapshot();
        Assert.Equal("第二条", snapshot[0].Message);
        Assert.Equal("第一条", snapshot[1].Message);
        Assert.True(snapshot[0].Id > snapshot[1].Id);
    }

    [Fact]
    public void Notify_BlankMessage_IsIgnored()
    {
        var nc = Fresh();

        nc.Info("   ");
        nc.Warn("");

        Assert.Equal(0, nc.Count);
    }

    [Theory]
    [InlineData(NoticeLevel.Info)]
    [InlineData(NoticeLevel.Warning)]
    [InlineData(NoticeLevel.Error)]
    public void LevelHelpers_RecordMatchingLevel(NoticeLevel level)
    {
        var nc = Fresh();

        switch (level)
        {
            case NoticeLevel.Info: nc.Info("信息"); break;
            case NoticeLevel.Warning: nc.Warn("警告"); break;
            default: nc.Error("错误"); break;
        }

        Assert.Equal(level, Assert.Single(nc.Snapshot()).Level);
    }

    [Fact]
    public void Clear_ReturnsCountAndEmptiesHistory()
    {
        var nc = Fresh();
        nc.Info("a");
        nc.Info("b");

        Assert.Equal(2, nc.Clear());
        Assert.Equal(0, nc.Count);
        Assert.Equal(0, nc.Clear());
    }

    [Fact]
    public void Notify_BeyondCapacity_DropsOldest()
    {
        var nc = Fresh();

        for (var i = 1; i <= 205; i++) nc.Info($"第 {i} 条");

        var snapshot = nc.Snapshot();
        Assert.Equal(200, snapshot.Count);
        Assert.Equal("第 205 条", snapshot[0].Message);
        Assert.Equal("第 6 条", snapshot[^1].Message);
    }
}
