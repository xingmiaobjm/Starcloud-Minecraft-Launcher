using Starcloud.Multiplayer.Client.P2P;
using Starcloud.Multiplayer.Core.Models;
using Starcloud.Multiplayer.Core.Net;
using Starcloud.Multiplayer.Core.P2P;
using Starcloud.Multiplayer.Core.Protocol;
using Starcloud.Multiplayer.Core.Rooms;

namespace Starcloud.Multiplayer.Client;

/// <summary>
/// 联机内核：身份 / 好友 / 房间 / 邀请 / 会话状态机 / P2P 引擎编排。
/// <para>
/// 这是本次交付物的门面：**不含任何界面代码**，对外只给数据与
/// <see cref="Snapshot"/>；界面层（将来各端自己写）只做「把点击翻译成一次调用 + 画快照」。
/// </para>
/// </summary>
public sealed class MultiplayerKernel : IAsyncDisposable
{
    /// <summary>心跳间隔；服务端 30s 无心跳视为离线（设计稿 §6）。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PeerRefreshInterval = TimeSpan.FromSeconds(3);

    private readonly MpKernelOptions _options;
    private readonly IPlatformHost _platform;
    private readonly SessionStore _session;
    private readonly EasytierProvisioner _provisioner;
    private readonly IP2PEngine _engine;

    private MpServiceClient? _client;
    private StarcloudIdentity? _identity;
    private RoomCredentials? _credentials;
    private FakeMinecraftServer? _selfTestServer;
    private int? _localPort;
    private MultiplayerSnapshot _snapshot = MultiplayerSnapshot.Idle;

    public MultiplayerKernel(MpKernelOptions options)
    {
        _options = options;
        _platform = options.Platform;
        _session = new SessionStore(options.Platform);
        _provisioner = new EasytierProvisioner(
            options.Platform, options.KernelDirectory, localZipPath: options.KernelZipPath);
        _engine = options.EngineFactory?.Invoke() ?? new EasytierProcessEngine(_provisioner, options.Platform);

        _identity = _session.Load();
        if (_identity is not null)
        {
            _client = new MpServiceClient(options.ServiceBaseAddress, _identity.ApiKey);
            _platform.Log($"已恢复上次会话：{_identity.DisplayName}（{_identity.UserId}）");
        }
    }

    public IPlatformHost Platform => _platform;

    public string ServiceBaseAddress => _options.ServiceBaseAddress;

    public StarcloudIdentity? Identity => _identity;

    public MultiplayerSnapshot Snapshot => _snapshot;

    public bool EngineAvailable => _engine.IsAvailable;

    public string? EngineUnavailableReason => _engine.UnavailableReason;

    public string EngineInstallDirectory => _provisioner.InstallDirectory;

    /// <summary>
    /// easytier-core 可执行文件路径（仅内核已就位时非空）。
    /// 自检用它再拉一个进程当本地会合节点，业务代码用不到。
    /// </summary>
    public string? EngineCoreExecutablePath => _provisioner.IsReady ? _provisioner.CoreExecutablePath : null;

    /// <summary>把 P2P 内核准备好（首次会联网下载）。用注入引擎（测试）时直接返回 true。</summary>
    public async Task<bool> PrepareEngineAsync(CancellationToken ct = default)
    {
        if (_options.EngineFactory is not null) return true;
        if (_engine.IsAvailable) return true;

        var result = await _provisioner.EnsureAsync(ct);
        if (!result.Ok) _platform.Log($"P2P 内核准备失败：{result.Reason}");
        return result.Ok;
    }

    // ── 身份 ──

    public async Task<StarcloudIdentity> LoginAsync(
        string starcloudId, string password, string channel, CancellationToken ct = default)
    {
        if (channel == StarcloudIdentity.CloudChannel)
        {
            _identity = await StarcloudIdCloudClient.LoginAsync(starcloudId, password, ct);
        }
        else
        {
            // 与启动器测试通道同一条规则：密码只做本地长度校验。
            if (password.Length < 4)
                throw new InvalidOperationException("测试通道：密码至少 4 位（任意内容均可）");
            _identity = TestChannelIdentity.Derive(starcloudId);
        }

        _session.Save(_identity);
        _client?.Dispose();
        _client = new MpServiceClient(_options.ServiceBaseAddress, _identity.ApiKey);

        if (_options.RegisterMockKey)
        {
            await _client.TryPostAsync(
                $"{MpProtocol.BasePath}/_mock/keys",
                new { api_key = _identity.ApiKey, user_id = _identity.UserId }, ct);
        }

        await PulseAsync(null, ct);
        return _identity;
    }

    public void Logout()
    {
        _session.Clear();
        _client?.Dispose();
        _client = null;
        _identity = null;
    }

    // ── 查询 ──

    public async Task<IReadOnlyList<FriendInfo>> FriendsAsync(CancellationToken ct = default)
    {
        var client = RequireClient();
        return await client.GetAsync<List<FriendInfo>>($"{MpProtocol.BasePath}/friends", ct);
    }

    public async Task<IReadOnlyList<RoomInfo>> RoomsAsync(CancellationToken ct = default)
    {
        var client = RequireClient();
        return await client.GetAsync<List<RoomInfo>>($"{MpProtocol.BasePath}/rooms", ct);
    }

    public async Task<IReadOnlyList<InviteInfo>> InvitesAsync(CancellationToken ct = default)
    {
        var client = RequireClient();
        return await client.GetAsync<List<InviteInfo>>($"{MpProtocol.BasePath}/invites", ct);
    }

    // ── 开房 / 进房 ──

    public async Task<string> HostAsync(HostOptions options, CancellationToken ct = default)
    {
        var client = RequireClient();
        RequireEngine();
        await StopSessionAsync(ct);

        var minecraftPort = options.MinecraftPort;
        if (options.SelfTest)
        {
            // 自检模式：用假 MC 服务端顶替真实游戏占住端口。
            _selfTestServer = FakeMinecraftServer.Start(minecraftPort);
            minecraftPort = _selfTestServer.Port;
            _platform.Log($"自检模式：假 Minecraft 服务端已监听 {minecraftPort}");
        }
        else if (!await LocalPorts.IsTcpPortOpenAsync("127.0.0.1", minecraftPort, TimeSpan.FromSeconds(1), ct))
        {
            throw new InvalidOperationException(
                $"本机 {minecraftPort} 端口没有服务在监听。请先在 Minecraft 里「对局域网开放」，再来开房。");
        }

        var secret = RoomCode.NewNetworkSecret();
        _snapshot = _snapshot with { Phase = MultiplayerSnapshot.PhasePreparing };

        CreateRoomResult? created = null;
        for (var attempt = 0; attempt < 5 && created is null; attempt++)
        {
            var roomId = RoomCode.NewRoomId();
            try
            {
                created = await client.PostAsync<CreateRoomResult>($"{MpProtocol.BasePath}/rooms",
                    new CreateRoomRequest(
                        roomId, options.Name ?? string.Empty, options.Public, minecraftPort,
                        RoomCode.NetworkNameFor(roomId), secret), ct);
            }
            catch (MpServiceException ex) when (ex.ErrorCode == MpProtocol.ErrRoomExists)
            {
                // 撞号就换一个重来。
            }
        }

        if (created is null)
        {
            await CleanupSelfTestServerAsync();
            throw new InvalidOperationException("连续 5 次都撞上已占用的房间号，请重试");
        }

        var networkName = RoomCode.NetworkNameFor(created.RoomId);
        try
        {
            await _engine.StartAsync(new P2POptions
            {
                NetworkName = networkName,
                NetworkSecret = secret,
                IsHost = true,
                MinecraftPort = minecraftPort,
                RpcPort = LocalPorts.FindFreeTcpPort(),
                PublicServers = _options.PublicServers ?? P2PDefaults.PublicServers,
            }, ct);
        }
        catch (Exception)
        {
            // 网络起不来就别留一个进不去的房间。
            await client.DeleteAsync<SimpleOk>($"{MpProtocol.BasePath}/rooms/{created.RoomId}", ct);
            await CleanupSelfTestServerAsync();
            _snapshot = MultiplayerSnapshot.Idle;
            throw;
        }

        _credentials = new RoomCredentials(
            created.RoomId, networkName, secret, _identity!.UserId, minecraftPort);
        _localPort = null;
        _snapshot = new MultiplayerSnapshot(
            MultiplayerSnapshot.PhaseHosting, null, created.RoomId,
            options.Name ?? $"{_identity.DisplayName} 的房间", true, minecraftPort, null, null,
            Array.Empty<PeerInfo>());

        await PulseAsync(created.RoomId, ct);
        return created.RoomId;
    }

    public async Task<string> JoinAsync(string roomIdOrFriend, CancellationToken ct = default)
    {
        var client = RequireClient();
        RequireEngine();
        await StopSessionAsync(ct);

        var roomId = roomIdOrFriend;
        if (!RoomCode.IsValidRoomId(roomIdOrFriend))
        {
            var friend = await FindFriendAsync(roomIdOrFriend, ct);
            if (string.IsNullOrEmpty(friend.RoomId))
                throw new InvalidOperationException($"{friend.DisplayName} 当前不在任何房间里");
            roomId = friend.RoomId!;
        }

        _snapshot = _snapshot with { Phase = MultiplayerSnapshot.PhaseJoining };
        var joined = await client.PostAsync<JoinRoomResult>(
            $"{MpProtocol.BasePath}/rooms/{roomId}/join", null, ct);

        await StartGuestEngineAsync(joined, ct);
        return joined.RoomId;
    }

    public async Task<string> InviteAsync(string friendNameOrId, CancellationToken ct = default)
    {
        var client = RequireClient();
        if (_credentials is null)
            throw new InvalidOperationException("你当前不在房间里：先 host 开房，再邀请好友");

        var friend = await FindFriendAsync(friendNameOrId, ct);
        var result = await client.PostAsync<InviteResult>($"{MpProtocol.BasePath}/invites",
            new InviteRequest(friend.UserId, _credentials.RoomId), ct);
        return result.InviteId;
    }

    public async Task<RoomCredentials> AcceptInviteAsync(string inviteId, CancellationToken ct = default)
    {
        var client = RequireClient();
        RequireEngine();
        await StopSessionAsync(ct);

        _snapshot = _snapshot with { Phase = MultiplayerSnapshot.PhaseJoining };
        var joined = await client.PostAsync<JoinRoomResult>(
            $"{MpProtocol.BasePath}/invites/{inviteId}/accept", null, ct);

        await StartGuestEngineAsync(joined, ct);
        return _credentials!;
    }

    public async Task RejectInviteAsync(string inviteId, CancellationToken ct = default)
    {
        var client = RequireClient();
        await client.PostAsync<SimpleOk>($"{MpProtocol.BasePath}/invites/{inviteId}/reject", null, ct);
    }

    public async Task StopSessionAsync(CancellationToken ct = default)
    {
        if (_credentials is not null && _snapshot.IsHost && _client is not null)
        {
            try
            {
                await _client.DeleteAsync<SimpleOk>($"{MpProtocol.BasePath}/rooms/{_credentials.RoomId}", ct);
            }
            catch (Exception ex) when (ex is MpServiceException or HttpRequestException)
            {
                _platform.Log($"关房失败（忽略，房间会随心跳过期）：{ex.Message}");
            }
        }

        await _engine.StopAsync(ct);
        await CleanupSelfTestServerAsync();

        _credentials = null;
        _localPort = null;
        _snapshot = MultiplayerSnapshot.Idle;
    }

    // ── 状态 ──

    /// <summary>刷新一次：上报心跳 + 读对端。`status` 命令与前台驻留循环都用它。</summary>
    public async Task RefreshAsync(bool includePeers, CancellationToken ct = default)
    {
        await PulseAsync(_credentials?.RoomId, ct);

        if (_credentials is null) return;

        if (!await _engine.IsAliveAsync(ct))
        {
            _snapshot = _snapshot with
            {
                Phase = MultiplayerSnapshot.PhaseFailed,
                FailureReason = "P2P 内核已退出",
            };
            return;
        }

        if (!includePeers) return;

        try
        {
            var peers = await _engine.GetPeersAsync(ct);
            _snapshot = _snapshot with { Peers = peers, ConnectionPath = ResolvePath(peers) };
        }
        catch (Exception ex)
        {
            _platform.Log($"读对端列表失败：{ex.Message}");
        }
    }

    /// <summary>前台驻留：定时心跳 + 刷对端，直到 cts 取消（CLI 的 host/join 用它）。</summary>
    public async Task RunUntilCancelledAsync(CancellationToken ct)
    {
        var lastHeartbeat = DateTimeOffset.MinValue;
        var lastPeerRefresh = DateTimeOffset.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now;
                var includePeers = _snapshot.Phase is MultiplayerSnapshot.PhaseHosting
                    or MultiplayerSnapshot.PhaseConnected;

                if (now - lastHeartbeat >= HeartbeatInterval)
                {
                    await RefreshAsync(includePeers, ct);
                    lastHeartbeat = now;
                    if (includePeers) lastPeerRefresh = now;
                }
                else if (includePeers && now - lastPeerRefresh >= PeerRefreshInterval)
                {
                    await RefreshAsync(includePeers: true, ct);
                    lastPeerRefresh = now;
                }

                if (_snapshot.Phase == MultiplayerSnapshot.PhaseFailed) return;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 的正常路径。
        }
        finally
        {
            await StopSessionAsync(CancellationToken.None);
        }
    }

    /// <summary>隧道自检：向客人本地映射端口发一次 Minecraft 服务器列表探测。</summary>
    public async Task<PingResult> ProbeTunnelAsync(CancellationToken ct = default)
    {
        if (_credentials is null)
            throw new InvalidOperationException("当前不在房间里");
        if (_snapshot.IsHost)
            throw new InvalidOperationException("你在这个房间里是房主，隧道自检要在客人侧做");

        var port = _localPort ?? _credentials.MinecraftPort;
        return await MinecraftPing.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync(CancellationToken.None);
        await _engine.DisposeAsync();
        _client?.Dispose();
    }

    // ── 内部 ──

    private async Task StartGuestEngineAsync(JoinRoomResult joined, CancellationToken ct)
    {
        var localPort = LocalPorts.FindFreeTcpPort(joined.MinecraftPort);

        try
        {
            await _engine.StartAsync(new P2POptions
            {
                NetworkName = joined.NetworkName,
                NetworkSecret = joined.NetworkSecret,
                IsHost = false,
                MinecraftPort = joined.MinecraftPort,
                RpcPort = LocalPorts.FindFreeTcpPort(),
                PublicServers = _options.PublicServers ?? P2PDefaults.PublicServers,
                // 客人侧把本地端口映射到房主的固定虚拟地址：Minecraft 连 127.0.0.1:<localPort> 即可。
                PortForwards = new[]
                {
                    PortForward.Tcp(localPort, P2PDefaults.HostVirtualIp, joined.MinecraftPort),
                    PortForward.Udp(localPort, P2PDefaults.HostVirtualIp, joined.MinecraftPort),
                },
            }, ct);
        }
        catch (Exception)
        {
            _snapshot = MultiplayerSnapshot.Idle;
            throw;
        }

        _credentials = new RoomCredentials(
            joined.RoomId, joined.NetworkName, joined.NetworkSecret, joined.HostUserId, joined.MinecraftPort);
        _localPort = localPort;
        _snapshot = new MultiplayerSnapshot(
            MultiplayerSnapshot.PhaseConnected, null, joined.RoomId, null, false,
            joined.MinecraftPort, localPort, null, Array.Empty<PeerInfo>());

        await PulseAsync(joined.RoomId, ct);
    }

    private async Task<FriendInfo> FindFriendAsync(string nameOrId, CancellationToken ct)
    {
        var friends = await FriendsAsync(ct);
        var friend = friends.FirstOrDefault(f =>
            string.Equals(f.DisplayName, nameOrId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.UserId, nameOrId, StringComparison.OrdinalIgnoreCase));

        return friend ?? throw new InvalidOperationException($"「{nameOrId}」不在你的好友列表里");
    }

    private async Task PulseAsync(string? roomId, CancellationToken ct)
    {
        if (_client is null) return;
        try
        {
            await _client.PostAsync<PresenceResult>(
                $"{MpProtocol.BasePath}/presence", new PresenceRequest(roomId), ct);
        }
        catch (Exception ex) when (ex is MpServiceException or HttpRequestException)
        {
            _platform.Log($"心跳上报失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 判定与对端的连接路径：客人认房主（按房主机名匹配），房主取任意一个非本机对端。
    /// 读不出来就保持 Unknown——宁可不说，也不假装直连。
    /// </summary>
    private string? ResolvePath(IReadOnlyList<PeerInfo> peers)
    {
        if (_credentials is null) return null;

        var counterpart = _snapshot.IsHost
            ? peers.Where(p => !p.IsLocal).OrderBy(p => p.Cost).FirstOrDefault()
            : peers.FirstOrDefault(p =>
                string.Equals(p.Hostname, P2PDefaults.HostHostnameFor(_credentials.MinecraftPort),
                    StringComparison.OrdinalIgnoreCase));

        if (counterpart is null) return null;
        return counterpart.Path switch
        {
            ConnectionPath.Local => "local",
            ConnectionPath.Direct => "direct",
            ConnectionPath.Relay => "relay",
            _ => "unknown",
        };
    }

    private async Task CleanupSelfTestServerAsync()
    {
        if (_selfTestServer is null) return;
        await _selfTestServer.DisposeAsync();
        _selfTestServer = null;
    }

    private MpServiceClient RequireClient() =>
        _client ?? throw new InvalidOperationException("尚未登录 Starcloud ID（先 login）");

    private void RequireEngine()
    {
        if (!_engine.IsAvailable) throw new InvalidOperationException(_engine.UnavailableReason ?? "P2P 引擎不可用");
    }
}
