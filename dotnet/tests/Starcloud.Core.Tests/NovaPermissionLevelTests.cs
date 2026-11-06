using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>许可分级：关闭 / 变更前确认 / 自主模式 / 完全访问（操控电脑、接管游戏用）。</summary>
public class NovaPermissionLevelTests
{
    [Fact]
    public void 未授权的档位是关闭()
    {
        var store = new NovaPermissionStore();
        Assert.Equal(NovaPermissionLevel.Off, store.Level("computer-never-granted"));
        Assert.False(store.IsOn("computer-never-granted"));
        Assert.False(store.AllowsAutoRun("computer-never-granted"));
        Assert.False(store.IsFullAccess("computer-never-granted"));
    }

    [Theory]
    [InlineData(NovaPermissionLevel.Off, false, false, false)]
    [InlineData(NovaPermissionLevel.Confirm, true, false, false)]
    [InlineData(NovaPermissionLevel.Auto, true, true, false)]
    [InlineData(NovaPermissionLevel.Full, true, true, true)]
    public void 档位到布尔判定的映射(NovaPermissionLevel level, bool authorized,
        bool autoRun, bool fullAccess)
    {
        // 用记录自身推导，避免碰磁盘上的真实许可文件
        var p = new NovaPermission("probe", level >= NovaPermissionLevel.Confirm,
            level >= NovaPermissionLevel.Confirm ? DateTime.Now : null, (int)level);
        var resolved = p.Resolve();
        Assert.Equal(level, resolved);
        Assert.Equal(authorized, resolved >= NovaPermissionLevel.Confirm);
        Assert.Equal(autoRun, resolved >= NovaPermissionLevel.Auto);
        Assert.Equal(fullAccess, resolved == NovaPermissionLevel.Full);
    }

    [Fact]
    public void 旧文件只有on字段时按变更前确认处理()
    {
        // 旧版本写的是 {"key":"recording","on":true}，没有 level
        var legacyOn = new NovaPermission("recording", true, DateTime.Now);
        Assert.Equal(NovaPermissionLevel.Confirm, legacyOn.Resolve());

        var legacyOff = new NovaPermission("recording", false, null);
        Assert.Equal(NovaPermissionLevel.Off, legacyOff.Resolve());
    }

    [Fact]
    public void 档位文案齐全()
    {
        Assert.Equal("关闭", NovaPermissionStore.LevelText(NovaPermissionLevel.Off));
        Assert.Equal("变更前确认", NovaPermissionStore.LevelText(NovaPermissionLevel.Confirm));
        Assert.Equal("自主模式", NovaPermissionStore.LevelText(NovaPermissionLevel.Auto));
        Assert.Equal("完全访问", NovaPermissionStore.LevelText(NovaPermissionLevel.Full));
    }
}
