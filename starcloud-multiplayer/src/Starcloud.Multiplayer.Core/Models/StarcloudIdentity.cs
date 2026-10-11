namespace Starcloud.Multiplayer.Core.Models;

/// <summary>
/// Starcloud ID 会话。字段与启动器的 <c>StarcloudSession</c> 一致（同一套云端协议），
/// 但本内核独立存储、独立实现，不读不写启动器的数据目录。
/// </summary>
public sealed record StarcloudIdentity(
    string UserId,
    string DisplayName,
    string ApiKey,
    string Channel,
    DateTimeOffset LoginAt)
{
    /// <summary>测试通道：不联网，本地确定性派生（同一 ID 永远得到同一 user_id / api_key）。</summary>
    public const string TestChannel = "test";

    /// <summary>云端通道：走真实的 Starcloud ID 服务。</summary>
    public const string CloudChannel = "cloud";

    /// <summary>Starcloud ID 云端地址（与启动器一致）。</summary>
    public const string CloudBaseUrl = "https://api.starcloudfur.cn";

    public bool IsCloud => Channel == CloudChannel;

    /// <summary>测试通道的假 Key（以 sk-test- 开头）不可用于真实云端。</summary>
    public bool IsTestKey => ApiKey.StartsWith("sk-test-", StringComparison.Ordinal);
}
