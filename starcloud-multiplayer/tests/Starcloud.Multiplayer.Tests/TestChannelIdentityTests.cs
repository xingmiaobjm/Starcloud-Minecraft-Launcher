using Starcloud.Multiplayer.Core.Models;
using Starcloud.Multiplayer.Core.Rooms;
using Xunit;

namespace Starcloud.Multiplayer.Tests;

public class TestChannelIdentityTests
{
    // 期望值由启动器那套规则独立算得（sha256(小写 ID) 前 12 位 + sha256("starcloud:<userId>:<ID>") 前 32 位）。
    // 一旦有人改动派生规则，这条测试就会红：两边必须始终一致。
    [Fact]
    public void 派生结果与启动器规则一致()
    {
        var identity = TestChannelIdentity.Derive("alice");

        Assert.Equal("sc-2bd806c97f0e", identity.UserId);
        Assert.Equal("sk-test-7f3fde06a7dc22370a1cb4287e3effb8", identity.ApiKey);
        Assert.Equal(StarcloudIdentity.TestChannel, identity.Channel);
        Assert.Equal("alice", identity.DisplayName);
    }

    [Fact]
    public void 同一ID永远得到同一身份()
    {
        Assert.Equal(TestChannelIdentity.Derive("Bob").ApiKey, TestChannelIdentity.Derive("Bob").ApiKey);
    }

    [Fact]
    public void 大小写不影响user_id()
    {
        Assert.Equal(TestChannelIdentity.Derive("bob").UserId, TestChannelIdentity.Derive("BOB").UserId);
    }

    [Fact]
    public void 测试通道的Key被标记为测试Key()
    {
        Assert.True(TestChannelIdentity.Derive("carol").IsTestKey);
        Assert.False(TestChannelIdentity.Derive("carol").IsCloud);
    }

    [Fact]
    public void 空ID直接报错()
    {
        Assert.Throws<ArgumentException>(() => TestChannelIdentity.Derive("  "));
    }
}
