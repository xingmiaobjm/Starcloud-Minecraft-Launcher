using System.Text.Json;
using Starcloud.Multiplayer.Core.Models;
using Starcloud.Multiplayer.Core.Protocol;
using Starcloud.Multiplayer.Core.Rooms;

namespace Starcloud.Multiplayer.MockServer;

/// <summary>操作结果：要么有值，要么带一个契约错误码。</summary>
public sealed record StoreResult<T>(T? Value, string? ErrorCode, string? Message)
{
    public bool Ok => ErrorCode is null;

    public static StoreResult<T> Success(T value) => new(value, null, null);

    public static StoreResult<T> Fail(string code, string message) => new(default, code, message);
}

/// <summary>
/// mock 服务端的状态：用户 / 好友 / 在线 / 房间 / 邀请。
/// 好友关系来自 seed.json（只读）；房间与邀请落盘到 <c>state.json</c>；在线状态只在内存里。
/// </summary>
public sealed class MockStore
{
    public const int MaxPlayersPerRoom = 8;

    /// <summary>心跳超过这个时长就视为离线（设计稿 §6：15s 一次心跳、30s 过期）。</summary>
    public static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly Dictionary<string, MockUser> _users = new();
    private readonly Dictionary<string, string> _userIdByApiKey = new();
    private readonly HashSet<string> _friendPairs = new();
    private readonly Dictionary<string, Presence> _presence = new();
    private readonly Dictionary<string, RoomRecord> _rooms = new();
    private readonly List<InviteRecord> _invites = new();

    public MockStore(string seedFile, string statePath)
    {
        _statePath = statePath;
        LoadSeed(seedFile);
        LoadState();
    }

    public IReadOnlyCollection<MockUser> Users
    {
        get
        {
            lock (_gate) return _users.Values.ToList();
        }
    }

    // ── 身份 ──

    /// <summary>Bearer api_key → user_id。测试通道的 key 由 seed 里的 starcloud_id 派生而来。</summary>
    public string? ResolveUser(string apiKey)
    {
        lock (_gate)
        {
            return _userIdByApiKey.TryGetValue(apiKey, out var userId) ? userId : null;
        }
    }

    /// <summary>仅 mock 提供的登记入口：云端通道下客户端登录后把 api_key 登记进来。</summary>
    public bool RegisterKey(string apiKey, string userId)
    {
        lock (_gate)
        {
            if (!_users.ContainsKey(userId)) return false;
            _userIdByApiKey[apiKey] = userId;
            return true;
        }
    }

    // ── 在线 ──

    public StoreResult<PresenceResult> TouchPresence(string userId, string? roomId)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var firstSeen = _presence.TryGetValue(userId, out var previous) ? previous.FirstSeen : now;
            _presence[userId] = new Presence(firstSeen, now, roomId);
            return StoreResult<PresenceResult>.Success(
                new PresenceResult(userId, (int)(now - firstSeen).TotalSeconds));
        }
    }

    // ── 好友 ──

    public StoreResult<IReadOnlyList<FriendInfo>> Friends(string userId)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var result = new List<FriendInfo>();
            foreach (var pair in _friendPairs)
            {
                var parts = pair.Split('|');
                var other = parts[0] == userId ? parts[1] : parts[1] == userId ? parts[0] : null;
                if (other is null) continue;

                var online = _presence.TryGetValue(other, out var presence)
                             && now - presence.LastSeen < PresenceTimeout;
                result.Add(new FriendInfo(
                    other,
                    _users.TryGetValue(other, out var user) ? user.DisplayName : other,
                    online,
                    online ? presence!.RoomId : null));
            }

            result.Sort((a, b) => string.CompareOrdinal(a.UserId, b.UserId));
            return StoreResult<IReadOnlyList<FriendInfo>>.Success(result);
        }
    }

    // ── 房间 ──

    public StoreResult<IReadOnlyList<RoomInfo>> PublicRooms()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var list = _rooms.Values
                .Where(r => r.Public && IsHostLive(r, now))
                .OrderByDescending(r => r.CreatedAt)
                .Select(ToRoomInfo)
                .ToList();
            return StoreResult<IReadOnlyList<RoomInfo>>.Success(list);
        }
    }

    public StoreResult<CreateRoomResult> CreateRoom(string userId, CreateRoomRequest request)
    {
        lock (_gate)
        {
            if (!RoomCode.IsValidRoomId(request.RoomId))
                return StoreResult<CreateRoomResult>.Fail(MpProtocol.ErrInvalidRequest, "房间号不合法");
            if (!RoomCode.IsValidNetworkSecret(request.NetworkSecret))
                return StoreResult<CreateRoomResult>.Fail(MpProtocol.ErrInvalidRequest, "网络密钥不合法");
            if (!string.Equals(request.NetworkName, RoomCode.NetworkNameFor(request.RoomId), StringComparison.Ordinal))
                return StoreResult<CreateRoomResult>.Fail(MpProtocol.ErrInvalidRequest, "网络名与房间号不匹配");
            if (request.MinecraftPort is <= 0 or > 65535)
                return StoreResult<CreateRoomResult>.Fail(MpProtocol.ErrInvalidRequest, "Minecraft 端口不合法");
            if (_rooms.ContainsKey(request.RoomId))
                return StoreResult<CreateRoomResult>.Fail(MpProtocol.ErrRoomExists, "房间号已被占用，请重试");

            // 同一时间只允许一个人开一个房：换房等于先关旧房。
            var existing = _rooms.Values.FirstOrDefault(r => r.HostUserId == userId);
            if (existing is not null) _rooms.Remove(existing.RoomId);

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? $"{_users.GetValueOrDefault(userId)?.DisplayName ?? userId} 的房间"
                : request.Name.Trim();

            var room = new RoomRecord
            {
                RoomId = request.RoomId,
                Name = name,
                HostUserId = userId,
                Public = request.Public,
                MinecraftPort = request.MinecraftPort,
                NetworkName = request.NetworkName,
                NetworkSecret = request.NetworkSecret,
                CreatedAt = DateTimeOffset.Now,
                Members = { userId },
            };
            _rooms[room.RoomId] = room;
            Save();

            return StoreResult<CreateRoomResult>.Success(new CreateRoomResult(room.RoomId, room.MinecraftPort));
        }
    }

    public StoreResult<SimpleOk> CloseRoom(string userId, string roomId)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
                return StoreResult<SimpleOk>.Fail(MpProtocol.ErrNotFound, "房间不存在");
            if (room.HostUserId != userId)
                return StoreResult<SimpleOk>.Fail(MpProtocol.ErrNotHost, "只有房主能关房");

            _rooms.Remove(roomId);
            // 房间没了，指向它的待处理邀请也一并作废。
            _invites.RemoveAll(i => i.RoomId == roomId && i.State == InviteState.Pending);
            Save();
            return StoreResult<SimpleOk>.Success(new SimpleOk(true));
        }
    }

    public StoreResult<JoinRoomResult> JoinRoom(string userId, string roomId)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrNotFound, "房间不存在");
            if (!IsHostLive(room, DateTimeOffset.Now))
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrRoomClosed, "房主已离线，房间已关闭");
            if (!room.Public && room.HostUserId != userId && !IsInvited(userId, roomId))
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrForbidden, "这是非公开房间，需要房主邀请");
            if (!room.Members.Contains(userId) && room.Members.Count >= MaxPlayersPerRoom)
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrRoomFull, "房间已满");

            if (!room.Members.Contains(userId)) room.Members.Add(userId);
            Save();
            return StoreResult<JoinRoomResult>.Success(ToJoinResult(room));
        }
    }

    // ── 邀请 ──

    public StoreResult<IReadOnlyList<InviteInfo>> PendingInvites(string userId)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var list = _invites
                .Where(i => i.ToUserId == userId && i.State == InviteState.Pending)
                .Where(i => _rooms.TryGetValue(i.RoomId, out var room) && IsHostLive(room, now))
                .OrderByDescending(i => i.CreatedAt)
                .Select(i =>
                {
                    var room = _rooms[i.RoomId];
                    return new InviteInfo(
                        i.InviteId,
                        new HostRef(i.FromUserId, _users.GetValueOrDefault(i.FromUserId)?.DisplayName ?? i.FromUserId),
                        i.RoomId,
                        room.Name,
                        i.CreatedAt);
                })
                .ToList();
            return StoreResult<IReadOnlyList<InviteInfo>>.Success(list);
        }
    }

    public StoreResult<InviteResult> CreateInvite(string userId, InviteRequest request)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(request.RoomId, out var room))
                return StoreResult<InviteResult>.Fail(MpProtocol.ErrNotFound, "房间不存在");
            if (room.HostUserId != userId)
                return StoreResult<InviteResult>.Fail(MpProtocol.ErrNotHost, "只有房主能邀请");
            if (!_users.ContainsKey(request.ToUserId))
                return StoreResult<InviteResult>.Fail(MpProtocol.ErrNotFound, "对方不是已知用户");
            if (!AreFriends(userId, request.ToUserId))
                return StoreResult<InviteResult>.Fail(MpProtocol.ErrForbidden, "只能邀请好友");

            var invite = new InviteRecord
            {
                InviteId = "inv-" + Guid.NewGuid().ToString("N")[..12],
                FromUserId = userId,
                ToUserId = request.ToUserId,
                RoomId = request.RoomId,
                CreatedAt = DateTimeOffset.Now,
                State = InviteState.Pending,
            };
            _invites.Add(invite);
            Save();
            return StoreResult<InviteResult>.Success(new InviteResult(invite.InviteId));
        }
    }

    public StoreResult<JoinRoomResult> AcceptInvite(string userId, string inviteId)
    {
        lock (_gate)
        {
            var invite = _invites.FirstOrDefault(i => i.InviteId == inviteId);
            if (invite is null)
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrNotFound, "邀请不存在");
            if (invite.ToUserId != userId)
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrForbidden, "这条邀请不是给你的");
            if (invite.State != InviteState.Pending)
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrNotFound, "邀请已处理过");
            if (!_rooms.TryGetValue(invite.RoomId, out var room) || !IsHostLive(room, DateTimeOffset.Now))
                return StoreResult<JoinRoomResult>.Fail(MpProtocol.ErrRoomClosed, "房间已关闭");

            invite.State = InviteState.Accepted;
            if (!room.Members.Contains(userId)) room.Members.Add(userId);
            Save();
            return StoreResult<JoinRoomResult>.Success(ToJoinResult(room));
        }
    }

    public StoreResult<SimpleOk> RejectInvite(string userId, string inviteId)
    {
        lock (_gate)
        {
            var invite = _invites.FirstOrDefault(i => i.InviteId == inviteId);
            if (invite is null)
                return StoreResult<SimpleOk>.Fail(MpProtocol.ErrNotFound, "邀请不存在");
            if (invite.ToUserId != userId)
                return StoreResult<SimpleOk>.Fail(MpProtocol.ErrForbidden, "这条邀请不是给你的");

            invite.State = InviteState.Rejected;
            Save();
            return StoreResult<SimpleOk>.Success(new SimpleOk(true));
        }
    }

    // ── 内部 ──

    private bool IsInvited(string userId, string roomId) =>
        _invites.Any(i => i.ToUserId == userId && i.RoomId == roomId && i.State == InviteState.Accepted);

    private bool AreFriends(string a, string b) => _friendPairs.Contains(PairKey(a, b));

    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    /// <summary>房主心跳还在，房间才算「活着」（房主掉线即自动失效）。</summary>
    private bool IsHostLive(RoomRecord room, DateTimeOffset now) =>
        _presence.TryGetValue(room.HostUserId, out var presence)
        && now - presence.LastSeen < PresenceTimeout;

    private RoomInfo ToRoomInfo(RoomRecord room) => new(
        room.RoomId,
        room.Name,
        new HostRef(room.HostUserId, _users.GetValueOrDefault(room.HostUserId)?.DisplayName ?? room.HostUserId),
        room.Members.Count,
        MaxPlayersPerRoom,
        room.Public,
        room.MinecraftPort,
        room.CreatedAt);

    private static JoinRoomResult ToJoinResult(RoomRecord room) => new(
        room.RoomId,
        room.NetworkName,
        room.NetworkSecret,
        room.HostUserId,
        room.MinecraftPort);

    private void LoadSeed(string seedFile)
    {
        if (!File.Exists(seedFile))
            throw new FileNotFoundException($"找不到 seed 文件：{seedFile}", seedFile);

        var seed = JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(seedFile), MpJson.Options)
                   ?? throw new InvalidDataException("seed 文件解析为空");

        var nameByStarcloudId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in seed.Users ?? new List<SeedUser>())
        {
            var identity = TestChannelIdentity.Derive(item.StarcloudId, item.DisplayName);
            _users[identity.UserId] = new MockUser(identity.UserId, identity.DisplayName, item.StarcloudId);
            _userIdByApiKey[identity.ApiKey] = identity.UserId;
            nameByStarcloudId[item.StarcloudId] = identity.UserId;
        }

        foreach (var pair in seed.Friendships ?? new List<List<string>>())
        {
            if (pair.Count < 2) continue;
            if (!nameByStarcloudId.TryGetValue(pair[0], out var a)) continue;
            if (!nameByStarcloudId.TryGetValue(pair[1], out var b)) continue;
            _friendPairs.Add(PairKey(a, b));
        }
    }

    private void LoadState()
    {
        if (!File.Exists(_statePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), MpJson.Options);
            if (state is null) return;
            foreach (var room in state.Rooms ?? new List<RoomRecord>()) _rooms[room.RoomId] = room;
            foreach (var invite in state.Invites ?? new List<InviteRecord>()) _invites.Add(invite);
        }
        catch (Exception)
        {
            // 落盘文件坏掉不该拦住 mock 服务端启动：丢掉它，从干净状态开始。
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var state = new PersistedState(_rooms.Values.ToList(), _invites);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, MpJson.Options));
        }
        catch (Exception)
        {
            // 落盘只是方便重启后还在；失败不影响本次会话。
        }
    }

    private sealed record SeedFile(List<SeedUser>? Users, List<List<string>>? Friendships);

    private sealed record SeedUser(string StarcloudId, string DisplayName);

    private sealed record PersistedState(List<RoomRecord>? Rooms, List<InviteRecord>? Invites);
}
