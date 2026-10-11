using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Multiplayer.Core.Protocol;

/// <summary>
/// 联机服务端 HTTP 契约的常量。**这些值是跨端冻结的**：鸿蒙侧的 ArkTS 实现必须逐字节对齐，
/// 改动等于破坏兼容。
/// </summary>
public static class MpProtocol
{
    /// <summary>联机服务端路径前缀（云端挂在 Starcloud ID 同一域名下）。</summary>
    public const string BasePath = "/v1/multiplayer";

    /// <summary>客户端标识头（与启动器对 Nova 网关的做法一致，便于服务端区分来源）。</summary>
    public const string ClientHeader = "X-Starcloud-Client";

    public const string ClientHeaderValue = "multiplayer-kernel";

    // ── 错误码（机器可读，落在响应体的 error 字段）──
    public const string ErrUnauthorized = "unauthorized";
    public const string ErrForbidden = "forbidden";
    public const string ErrNotFound = "not_found";
    public const string ErrRoomClosed = "room_closed";
    public const string ErrRoomFull = "room_full";
    public const string ErrRoomExists = "room_exists";
    public const string ErrInvalidRequest = "invalid_request";
    public const string ErrNotHost = "not_host";
    public const string ErrServerError = "server_error";
}

/// <summary>
/// 统一响应信封：成功 <c>{"ok":true,"data":…}</c>，失败
/// <c>{"ok":false,"error":"&lt;码&gt;","message":"&lt;说明&gt;"}</c>。
/// </summary>
public sealed record MpEnvelope<T>(bool Ok, T? Data, string? Error, string? Message);

/// <summary>序列化约定：snake_case（契约里的字段名一律小写下划线）。</summary>
public static class MpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
