using Starcloud.Multiplayer.Client;
using Starcloud.Multiplayer.Core.Models;
using Starcloud.Multiplayer.Core.P2P;
using Starcloud.Multiplayer.Core.Protocol;
using Starcloud.Multiplayer.Core.Rooms;
using Starcloud.Multiplayer.MockServer;
using Xunit;

namespace Starcloud.Multiplayer.Tests;

/// <summary>
/// 控制面端到端：**不需要外网、不需要 EasyTier**（P2P 引擎换成内存实现）。
/// 覆盖设计稿验收清单第 2 条：登录 → 好友在线 → 开房 → 大厅可见 → 邀请 → 接受 → 拿到凭据。
/// </summary>
public class ControlPlaneEndToEndTests : IDisposable
{
    private readonly string _workDir;
    private readonly MockServerHandle _server;

    public ControlPlaneEndToEndTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "starcloud-mp-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        var seed = Path.Combine(AppContext.BaseDirectory, "seed.json");
        _server = MockServerHost.StartAsync(seed, Path.Combine(_workDir, "state.json"), 0)
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    private MultiplayerKernel NewKernel(string name, FakeP2PEngine engine) => new(new MpKernelOptions
    {
        ServiceBaseAddress = _server.BaseAddress,
        Platform = new TestPlatformHost(Path.Combine(_workDir, name)),
        EngineFactory = () => engine,
    });

    [Fact]
    public async Task 两个测试账号走完开房邀请接受并拿到进网凭据()
    {
        var hostEngine = new FakeP2PEngine();
        var guestEngine = new FakeP2PEngine();
        await using var host = NewKernel("host", hostEngine);
        await using var guest = NewKernel("guest", guestEngine);

        // 登录（测试通道，不联网）
        var alice = await host.LoginAsync("alice", "test", StarcloudIdentity.TestChannel);
        var bob = await guest.LoginAsync("bob", "test", StarcloudIdentity.TestChannel);
        Assert.NotEqual(alice.UserId, bob.UserId);

        // 好友关系与在线状态
        var friends = await host.FriendsAsync();
        Assert.Contains(friends, f => f.UserId == bob.UserId && f.Online);
        Assert.Contains(await guest.FriendsAsync(), f => f.UserId == alice.UserId);

        // 开房（自检模式：假 MC 服务端顶替游戏）
        var roomId = await host.HostAsync(new HostOptions(Public: true, MinecraftPort: 0, Name: "端到端房间", SelfTest: true));
        Assert.True(RoomCode.IsValidRoomId(roomId));
        Assert.NotNull(hostEngine.Started);
        Assert.True(hostEngine.Started!.IsHost);
        Assert.Equal(RoomCode.NetworkNameFor(roomId), hostEngine.Started!.NetworkName);

        // 公开大厅能看到
        var rooms = await guest.RoomsAsync();
        var listed = Assert.Single(rooms, r => r.RoomId == roomId);
        Assert.Equal("端到端房间", listed.Name);
        Assert.Equal(1, listed.Players);

        // 邀请 → 客人在离线也能收到
        var inviteId = await host.InviteAsync("Bob");
        var invites = await guest.InvitesAsync();
        Assert.Equal(inviteId, Assert.Single(invites).InviteId);

        // 接受 → 拿到凭据并起客人侧的隧道
        var credentials = await guest.AcceptInviteAsync(inviteId);
        Assert.Equal(roomId, credentials.RoomId);
        Assert.Equal(hostEngine.Started!.NetworkName, credentials.NetworkName);
        Assert.Equal(hostEngine.Started!.NetworkSecret, credentials.NetworkSecret);

        Assert.NotNull(guestEngine.Started);
        Assert.False(guestEngine.Started!.IsHost);
        Assert.Equal(P2PDefaults.HostVirtualIp, guestEngine.Started!.PortForwards[0].RemoteHost);
        Assert.Equal(host.Snapshot.MinecraftPort, guestEngine.Started!.PortForwards[0].RemotePort);
        Assert.Equal(MultiplayerSnapshot.PhaseConnected, guest.Snapshot.Phase);

        // 关房后大厅里就没了
        await host.StopSessionAsync();
        Assert.DoesNotContain(await guest.RoomsAsync(), r => r.RoomId == roomId);
    }

    [Fact]
    public async Task 非公开房不进大厅且没邀请进不去()
    {
        var hostEngine = new FakeP2PEngine();
        await using var host = NewKernel("host2", hostEngine);
        await using var guest = NewKernel("guest2", new FakeP2PEngine());

        await host.LoginAsync("alice", "test", StarcloudIdentity.TestChannel);
        await guest.LoginAsync("carol", "test", StarcloudIdentity.TestChannel);

        var roomId = await host.HostAsync(new HostOptions(Public: false, MinecraftPort: 0, SelfTest: true));

        Assert.DoesNotContain(await guest.RoomsAsync(), r => r.RoomId == roomId);

        var error = await Assert.ThrowsAsync<MpServiceException>(() => guest.JoinAsync(roomId));
        Assert.Equal(MpProtocol.ErrForbidden, error.ErrorCode);

        // alice 与 carol 不是好友（seed 里是 alice-bob、bob-carol）：客户端本地就拦住了
        var inviteError = await Assert.ThrowsAsync<InvalidOperationException>(() => host.InviteAsync("carol"));
        Assert.Contains("不在你的好友列表", inviteError.Message);

        // 绕过客户端直接打服务端，这一道闸同样要在
        using var aliceClient = new MpServiceClient(
            _server.BaseAddress, TestChannelIdentity.Derive("alice").ApiKey);
        var carolId = TestChannelIdentity.Derive("carol").UserId;
        var serverError = await Assert.ThrowsAsync<MpServiceException>(() => aliceClient.PostAsync<InviteResult>(
            $"{MpProtocol.BasePath}/invites", new InviteRequest(carolId, roomId), default));
        Assert.Equal(MpProtocol.ErrForbidden, serverError.ErrorCode);
    }

    [Fact]
    public async Task 客人在房间不存在时报错()
    {
        await using var guest = NewKernel("guest3", new FakeP2PEngine());
        await guest.LoginAsync("bob", "test", StarcloudIdentity.TestChannel);

        var error = await Assert.ThrowsAsync<MpServiceException>(() => guest.JoinAsync(RoomCode.NewRoomId()));
        Assert.Equal(MpProtocol.ErrNotFound, error.ErrorCode);
    }

    [Fact]
    public async Task 未登录时调用被拒()
    {
        await using var guest = NewKernel("guest4", new FakeP2PEngine());

        await Assert.ThrowsAsync<InvalidOperationException>(() => guest.FriendsAsync());
    }
}
