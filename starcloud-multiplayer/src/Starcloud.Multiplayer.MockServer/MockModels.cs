using Starcloud.Multiplayer.Core.Models;

namespace Starcloud.Multiplayer.MockServer;

/// <summary>seed.json 里的一名用户（用 starcloud_id 写，服务端派生 user_id / api_key）。</summary>
public sealed record MockUser(string UserId, string DisplayName, string StarcloudId);

/// <summary>在线状态（只存内存；服务重启即全离线）。</summary>
public sealed record Presence(DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? RoomId);

/// <summary>房间。<c>Members</c> 只用于「房间满没满」与展示人数。</summary>
public sealed class RoomRecord
{
    public required string RoomId { get; init; }
    public required string Name { get; init; }
    public required string HostUserId { get; init; }
    public required bool Public { get; init; }
    public required int MinecraftPort { get; init; }
    public required string NetworkName { get; init; }
    public required string NetworkSecret { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public List<string> Members { get; init; } = new();
}

public enum InviteState
{
    Pending,
    Accepted,
    Rejected,
}

/// <summary>一条邀请。指向的房间关掉后由服务端一并作废。</summary>
public sealed class InviteRecord
{
    public required string InviteId { get; init; }
    public required string FromUserId { get; init; }
    public required string ToUserId { get; init; }
    public required string RoomId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public InviteState State { get; set; }
}
