namespace Starcloud.Multiplayer.Core.Models;

/// <summary>好友（关系由服务端预置，内核只读）。</summary>
public sealed record FriendInfo(
    string UserId,
    string DisplayName,
    bool Online,
    string? RoomId);

/// <summary>房间大厅里的一条房间。</summary>
public sealed record RoomInfo(
    string RoomId,
    string Name,
    HostRef Host,
    int Players,
    int Capacity,
    bool Public,
    int MinecraftPort,
    DateTimeOffset CreatedAt);

/// <summary>房间主人的最小身份信息。</summary>
public sealed record HostRef(string UserId, string DisplayName);

/// <summary>我收到的一条邀请。</summary>
public sealed record InviteInfo(
    string InviteId,
    HostRef From,
    string RoomId,
    string RoomName,
    DateTimeOffset CreatedAt);

/// <summary>拿到手的进网凭据（+ 房主的 MC 端口，客人据此建端口映射）。</summary>
public sealed record RoomCredentials(
    string RoomId,
    string NetworkName,
    string NetworkSecret,
    string HostUserId,
    int MinecraftPort);

/// <summary>内核对外唯一的状态出口：界面层只画它，不碰内部对象。</summary>
public sealed record MultiplayerSnapshot(
    string Phase,
    string? FailureReason,
    string? RoomId,
    string? RoomName,
    bool IsHost,
    int? MinecraftPort,
    int? LocalPort,
    string? ConnectionPath,
    IReadOnlyList<P2P.PeerInfo> Peers)
{
    public const string PhaseIdle = "idle";
    public const string PhasePreparing = "preparing";
    public const string PhaseHosting = "hosting";
    public const string PhaseJoining = "joining";
    public const string PhaseConnected = "connected";
    public const string PhaseFailed = "failed";

    public static MultiplayerSnapshot Idle { get; } = new(
        PhaseIdle, null, null, null, false, null, null, null, Array.Empty<P2P.PeerInfo>());
}
