// 调度中心任务总线单元测试：登记去重 / 状态收尾 / 清除已完成 / 运行中计数
// 服务是进程内单例（UI 与 Core 共用同一份任务列表），因此每个用例先清空再断言。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>与 <see cref="NotificationCenterTests"/> 同集合：任务收尾会写通知中心，二者必须串行。</summary>
[Collection("进程内单例")]
public class TaskCenterServiceTests
{
    private static TaskCenterService Fresh()
    {
        var tc = TaskCenterService.Instance;
        tc.ClearAll();
        NotificationCenter.Instance.Clear();
        return tc;
    }

    [Fact]
    public void Begin_SameTitleWhileRunning_ReusesExistingTask()
    {
        var tc = Fresh();

        var first = tc.Begin("资源下载");
        var second = tc.Begin("资源下载");

        Assert.Same(first, second);
        Assert.Single(tc.Snapshot());
    }

    [Fact]
    public void Begin_AfterPreviousFinished_CreatesNewTask()
    {
        var tc = Fresh();

        var first = tc.Begin("资源下载");
        tc.Complete(first);
        var second = tc.Begin("资源下载");

        Assert.NotSame(first, second);
        Assert.Equal(2, tc.Snapshot().Count);
        Assert.Equal(1, tc.RunningCount);
    }

    [Fact]
    public void Complete_SetsStateAndDefaultDetail()
    {
        var tc = Fresh();
        var task = tc.Begin("安装 1.21.4");

        tc.Complete(task);

        Assert.Equal(AppTaskState.Completed, task.State);
        Assert.Equal("完成", task.Detail);
        Assert.Equal(0, tc.RunningCount);
    }

    [Fact]
    public void Fail_KeepsReasonInDetail()
    {
        var tc = Fresh();
        var task = tc.Begin("安装 1.21.4");

        tc.Fail(task, "下载失败：连接超时");

        Assert.Equal(AppTaskState.Failed, task.State);
        Assert.Equal("下载失败：连接超时", task.Detail);
        Assert.Equal(0, tc.RunningCount);
    }

    [Fact]
    public void Cancel_FinishedTask_IsNoOp()
    {
        var tc = Fresh();
        var task = tc.Begin("安装 1.21.4");
        tc.Complete(task);

        tc.Cancel(task);

        Assert.Equal(AppTaskState.Completed, task.State);
    }

    [Fact]
    public void Complete_AppendsInfoNotice()
    {
        var tc = Fresh();
        var task = tc.Begin("搜索 Java");

        tc.Complete(task, "扫描完成");

        var notice = Assert.Single(NotificationCenter.Instance.Snapshot());
        Assert.Equal(NoticeLevel.Info, notice.Level);
        Assert.Equal("搜索 Java 完成（扫描完成）", notice.Message);
    }

    [Fact]
    public void Fail_AppendsErrorNotice()
    {
        var tc = Fresh();
        var task = tc.Begin("安装 1.21.4");

        tc.Fail(task, "连接超时");

        var notice = Assert.Single(NotificationCenter.Instance.Snapshot());
        Assert.Equal(NoticeLevel.Error, notice.Level);
        Assert.Equal("安装 1.21.4 失败（连接超时）", notice.Message);
    }

    [Fact]
    public void Complete_Twice_AppendsSingleNotice()
    {
        var tc = Fresh();
        var task = tc.Begin("资源下载");

        tc.Complete(task);
        tc.Complete(task);

        Assert.Single(NotificationCenter.Instance.Snapshot());
    }

    [Fact]
    public void Snapshot_NewestFirst()
    {
        var tc = Fresh();
        var first = tc.Begin("第一个");
        tc.Complete(first);
        var second = tc.Begin("第二个");
        tc.Complete(second);

        var snapshot = tc.Snapshot();

        Assert.Equal(second.Id, snapshot[0].Id);
        Assert.Equal(first.Id, snapshot[1].Id);
    }
}
