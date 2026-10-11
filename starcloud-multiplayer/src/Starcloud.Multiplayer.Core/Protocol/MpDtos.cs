using Starcloud.Multiplayer.Core.Models;

namespace Starcloud.Multiplayer.Core.Protocol;

// ── 请求体 ──

/// <summary>心跳上报：当前在哪个房间（不在房间时为 null）。</summary>
public sealed record PresenceRequest(string? RoomId);

/// <summary>
/// 开房登记：**房间号由房主本地生成**（服务端只做格式校验、唯一性校验与存储），
/// 因为 <c>network_name = sc-mp-&lt;room_id&gt;</c> 必须在开房那一刻就已经确定。
/// 撞号时服务端回 <c>room_exists</c>，客户端换一个重试。
/// </summary>
public sealed record CreateRoomRequest(
    string RoomId,
    string Name,
    bool Public,
    int MinecraftPort,
    string NetworkName,
    string NetworkSecret);

/// <summary>邀请好友进房。</summary>
public sealed record InviteRequest(string ToUserId, string RoomId);

// ── 响应体（data 段）──

public sealed record PresenceResult(string UserId, int OnlineSeconds);

/// <summary>开房结果：回显房间号与端口（房主据此建网络与展示）。</summary>
public sealed record CreateRoomResult(string RoomId, int MinecraftPort);

/// <summary>进房结果：进网凭据 + 房主的 MC 端口（客人据此建端口映射）。</summary>
public sealed record JoinRoomResult(
    string RoomId,
    string NetworkName,
    string NetworkSecret,
    string HostUserId,
    int MinecraftPort);

public sealed record InviteResult(string InviteId);

public sealed record SimpleOk(bool Done);

/// <summary>服务端的错误体（HTTP 非 2xx 时）。</summary>
public sealed record MpError(string Error, string Message);
