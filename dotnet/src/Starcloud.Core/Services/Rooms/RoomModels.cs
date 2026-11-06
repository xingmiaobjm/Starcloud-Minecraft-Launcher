// 联机房间的契约模型（与 /v1/rooms/* 后端接口一一对应）。
//
// 联机 = 「大厅里挂着一批可加入的房间」：每个房间有 6 位数字联机码（唯一标识）、
// 房主、人数上限、可选密码、以及房主上传的整合包；房间内是临时群聊，退房即散。
//
// 这些类型只描述形状，不做业务判断 —— 校验（6 位码、密码、容量）在 RoomService /
// 服务端两侧，UI 只读字段。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services.Rooms;

/// <summary>房主上传的整合包引用（服务端存对象存储，url 为公开下载地址）。</summary>
public sealed record RoomPackInfo(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string? Sha256,
    /// <summary>modrinth / curseforge / multimc / raw</summary>
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("mc_version")] string McVersion,
    [property: JsonPropertyName("loader")] string? Loader)
{
    /// <summary>包名（大厅卡片展示用；没有时用房间名）。</summary>
    [property: JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>
    /// 对象存储的公开下载地址（服务端下发）。玩家端直接从这里下载，不经过房间服务；
    /// 对象存储带宽有限，大包要给下载进度与重试。
    /// </summary>
    [property: JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>房间/好友的用户摘要（大厅与成员列表共用）。</summary>
public sealed record RoomUser(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl)
{
    /// <summary>首字头像兜底（没有远程头像时 UI 用）。</summary>
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1];
}

/// <summary>大厅里的一行。</summary>
public sealed record RoomSummary(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("owner")] RoomUser Owner,
    [property: JsonPropertyName("members")] int Members,
    [property: JsonPropertyName("max_players")] int MaxPlayers,
    [property: JsonPropertyName("has_password")] bool HasPassword,
    [property: JsonPropertyName("mc_version")] string? McVersion,
    [property: JsonPropertyName("loader")] string? Loader,
    [property: JsonPropertyName("pack")] RoomPackInfo? Pack,
    [property: JsonPropertyName("has_terracotta")] bool HasTerracotta,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("state")] string? State = null)
{
    /// <summary>「3 / 8」这类人数文案。</summary>
    public string MembersText => $"{Members} / {MaxPlayers}";

    /// <summary>版本 + 加载器（缺一不可）的短标签，例如「1.20.1 · Fabric」。</summary>
    public string VersionText
    {
        get
        {
            var loader = string.IsNullOrWhiteSpace(Loader) ? null : Capitalize(Loader!);
            if (string.IsNullOrWhiteSpace(McVersion)) return loader ?? "";
            return loader is null ? McVersion! : $"{McVersion} · {loader}";
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>房间里的一个成员。</summary>
public sealed record RoomMember(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    /// <summary>owner / member</summary>
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("online")] bool Online)
{
    public bool IsOwner => string.Equals(Role, "owner", StringComparison.OrdinalIgnoreCase);
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1];
}

/// <summary>房间详情（进房后与邀请卡片共用）。</summary>
public sealed record RoomDetail(
    [property: JsonPropertyName("room")] RoomSummary Room,
    [property: JsonPropertyName("members")] IReadOnlyList<RoomMember> Members,
    [property: JsonPropertyName("joined")] bool Joined,
    /// <summary>我在这间房里是不是房主。</summary>
    [property: JsonPropertyName("is_owner")] bool IsOwner)
{
    /// <summary>房主账号（成员表里 role=owner 的那位）。</summary>
    public RoomMember? OwnerMember => Members.FirstOrDefault(m => m.IsOwner);
}

/// <summary>进房结果：验密通过后服务端才下发陶瓦联机码（房主上报的那个）。</summary>
public sealed record RoomJoinResult(
    [property: JsonPropertyName("room")] RoomDetail Room,
    /// <summary>陶瓦邀请码；房主还没开房（未上报）时为 null。</summary>
    [property: JsonPropertyName("terracotta_code")] string? TerracottaCode);

/// <summary>房间聊天的一条消息（附件复用好友的 [文件] 协议）。</summary>
public sealed record RoomMessage(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("room_code")] string RoomCode,
    [property: JsonPropertyName("from_user_id")] string FromUserId,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("created_at")] string? CreatedAt);

/// <summary>房间实时事件（SSE）。</summary>
public sealed record RoomEvent(string Type, JsonElement Data)
{
    /// <summary>按事件类型取结构化负载。</summary>
    public T? Parse<T>() where T : class
    {
        try { return Data.Deserialize<T>(RoomService.JsonOpts); }
        catch { return null; }
    }

    public const string MemberJoined = "member_joined";
    public const string MemberLeft = "member_left";
    public const string Kicked = "kicked";
    public const string Message = "message";
    public const string RoomClosed = "room_closed";
    public const string TerracottaReady = "terracotta_ready";
}

/// <summary>建房请求（pack 的 file_id 必须先用 /v1/files 上传拿到）。</summary>
public sealed record CreateRoomRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("max_players")] int MaxPlayers,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("pack")] RoomPackInfo? Pack,
    /// <summary>房间所用游戏版本（1.21.4 这类；大厅卡片的版本标签读它）</summary>
    [property: JsonPropertyName("mc_version")] string? McVersion = null,
    /// <summary>vanilla / forge / fabric / neoform / quilt（白名单外服务端会置 null）</summary>
    [property: JsonPropertyName("loader")] string? Loader = null)
{
    public const int MinPlayers = 2;
    public const int MaxPlayersLimit = 20;
}

/// <summary>6 位数字联机码的规则（服务端生成，客户端只做输入校验与规范化）。</summary>
public static class RoomCodeRules
{
    public const int Length = 6;

    /// <summary>去掉空白与连字符后是否是合法的 6 位数字码。</summary>
    public static bool IsValid(string? code) =>
        Normalize(code) is { Length: Length } digits && digits.All(char.IsAsciiDigit);

    /// <summary>规范化输入：去空白、去连字符、去全角数字转换。</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var sb = new System.Text.StringBuilder(Length);
        foreach (var raw in code.Trim())
        {
            // 全角数字（０-９）转半角：用户在中文输入法下直接敲数字很常见
            var c = raw is >= '０' and <= '９' ? (char)('0' + (raw - '０')) : raw;
            if (char.IsWhiteSpace(c) || c is '-' or '_') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
