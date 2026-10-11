using Starcloud.Multiplayer.Core.Rooms;
using Xunit;

namespace Starcloud.Multiplayer.Tests;

public class RoomCodeTests
{
    [Fact]
    public void 房间号是8位且只含约定字母表()
    {
        for (var i = 0; i < 200; i++)
        {
            var roomId = RoomCode.NewRoomId();
            Assert.Equal(RoomCode.RoomIdLength, roomId.Length);
            Assert.All(roomId, c => Assert.Contains(c, RoomCode.Alphabet));
        }
    }

    [Fact]
    public void 字母表去掉了易混字符()
    {
        foreach (var ambiguous in new[] { '0', 'O', '1', 'I', 'L' })
            Assert.DoesNotContain(ambiguous, RoomCode.Alphabet);
    }

    [Fact]
    public void 连续生成不重复()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => RoomCode.NewRoomId()).ToHashSet();
        Assert.Equal(500, ids.Count);
    }

    [Fact]
    public void 网络名由房间号派生()
    {
        var roomId = RoomCode.NewRoomId();
        Assert.Equal("sc-mp-" + roomId, RoomCode.NetworkNameFor(roomId));
    }

    [Fact]
    public void 非法房间号拒绝派生网络名()
    {
        Assert.Throws<ArgumentException>(() => RoomCode.NetworkNameFor("abc"));
        Assert.Throws<ArgumentException>(() => RoomCode.NetworkNameFor("AAAAAAA0"));   // 含被排除的 0
        Assert.False(RoomCode.IsValidRoomId("AAAAAAAO"));                              // 含被排除的 O
        Assert.False(RoomCode.IsValidRoomId(null));
    }

    [Fact]
    public void 网络密钥是32位且合法()
    {
        var secret = RoomCode.NewNetworkSecret();
        Assert.Equal(RoomCode.NetworkSecretLength, secret.Length);
        Assert.True(RoomCode.IsValidNetworkSecret(secret));
        Assert.False(RoomCode.IsValidNetworkSecret(secret[..31]));
    }
}
