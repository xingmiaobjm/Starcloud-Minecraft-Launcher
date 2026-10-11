using System.Security.Cryptography;
using System.Text;
using Starcloud.Multiplayer.Core.Models;

namespace Starcloud.Multiplayer.Core.Rooms;

/// <summary>
/// 测试通道身份派生。规则与启动器的 <c>StarcloudIdService.TestLoginAsync</c> **完全一致**：
/// 只有算出来一样，本地 mock 服务端才能在没联网的情况下认出你是谁。
/// </summary>
public static class TestChannelIdentity
{
    public static StarcloudIdentity Derive(string starcloudId, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(starcloudId))
            throw new ArgumentException("Starcloud ID 不能为空", nameof(starcloudId));

        var userId = "sc-" + Sha256Hex(starcloudId.ToLowerInvariant())[..12];
        var apiKey = "sk-test-" + Sha256Hex($"starcloud:{userId}:{starcloudId}")[..32];
        return new StarcloudIdentity(
            userId,
            displayName ?? starcloudId,
            apiKey,
            StarcloudIdentity.TestChannel,
            DateTimeOffset.Now);
    }

    /// <summary>任意 token 派生出的 user_id（云端通道由服务端权威给出，这里只用于测试通道）。</summary>
    public static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
