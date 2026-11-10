using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Bridge;


public static partial class Program
{

    private static async Task<object?> HandleAccountsListAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                accounts = _accounts.Accounts.Select(AccountPayload),
                current = _accounts.Default?.Uuid,
            };
    }

    private static async Task<object?> HandleAccountsAddOfflineAsync(TextWriter stdout, JsonElement p)
    {
            var name = p.GetProperty("name").GetString() ?? "";
            _accounts.AddOrUpdateOffline(name);
            return new { uuid = AccountStore.OfflineUuid(name), name };
    }

    private static async Task<object?> HandleAccountsSetCurrentAsync(TextWriter stdout, JsonElement p)
    {
            // 首选按类型各记一个（Core 语义）：换离线首选不会清掉正版首选。
            var uuid = p.GetProperty("uuid").GetString()!;
            _accounts.SetDefault(uuid);
            return new { ok = true };
    }

    private static async Task<object?> HandleAccountsRemoveAsync(TextWriter stdout, JsonElement p)
    {
            var uuid = p.GetProperty("uuid").GetString()!;
            _accounts.Remove(uuid);
            return new { ok = true };
    }

    private static async Task<object?> HandleAccountsSetSkinAsync(TextWriter stdout, JsonElement p)
    {
            var uuid = p.GetProperty("uuid").GetString()!;
            string? skinId = p.TryGetProperty("skinId", out var sid) &&
                             sid.ValueKind == JsonValueKind.String
                ? sid.GetString()
                : null;
            string? model = p.TryGetProperty("model", out var md) &&
                            md.ValueKind == JsonValueKind.String
                ? md.GetString()
                : null;
            var account = _accounts.SetSkin(uuid, skinId, model)
                ?? throw new Exception($"账户不存在：{uuid}");
            return new { account = AccountPayload(account) };
    }

    private static async Task<object?> HandleAuthMicrosoftStartAsync(TextWriter stdout, JsonElement p)
    {
            var code = await _msAuth.StartDeviceCodeAsync();
            PendingDeviceCodes[code.DeviceCode] = code;
            return new
            {
                userCode = code.UserCode,
                deviceCode = code.DeviceCode,
                verificationUri = code.VerificationUri,
                interval = code.Interval,
            };
    }

    private static async Task<object?> HandleAuthMicrosoftPollAsync(TextWriter stdout, JsonElement p)
    {
            var deviceCode = p.GetProperty("deviceCode").GetString()!;
            if (!PendingDeviceCodes.TryGetValue(deviceCode, out var code))
                throw new Exception("设备码已失效，请重新开始登录");
            var (state, message, result) = await _msAuth.PollAsync(code);
            if (state == "pending") return new { state, message };
            PendingDeviceCodes.TryRemove(deviceCode, out _);
            if (state == "error" || result is null)
                throw new Exception(message);
            var account = _accounts.AddOrUpdateMicrosoft(result.Uuid, result.Username);
            _accounts.SetSession(result.Uuid,
                new AccountSession(result.AccessToken, result.RefreshToken, result.Xuid));
            if (result.RefreshToken is { Length: > 0 } bridgeRefresh)
            {
                if (_settings.MicrosoftTokenPersist)
                    MicrosoftTokenStore.SaveSession(result.Uuid, bridgeRefresh,
                        result.AccessToken, MicrosoftTokenStore.AccessExpiry(), result.Xuid);
                else
                    MicrosoftTokenStore.SaveNew(result.Uuid, bridgeRefresh);
            }
            return new
            {
                state = "success",
                account = new
                {
                    uuid = account.Uuid,
                    name = account.Username,
                    type = account.Type,
                },
            };
    }

    private static async Task<object?> HandleAuthYggdrasilLoginAsync(TextWriter stdout, JsonElement p)
    {
            var serverUrl = p.GetProperty("url").GetString()!;
            var username = p.GetProperty("username").GetString()!;
            var password = p.GetProperty("password").GetString()!;
            var server = await _yggdrasil.ResolveServerAsync(serverUrl);
            // clientToken 是设备标识（非鉴权 token）：同服务器同用户复用，否则新生成。
            var existing = _accounts.Accounts.FirstOrDefault(a =>
                a.Type == "external" && a.Username == username &&
                (a.ServerBase == server.Base || a.ServerAlias == serverUrl));
            var clientToken = string.IsNullOrEmpty(existing?.ClientToken)
                ? Guid.NewGuid().ToString()
                : AsUuid(existing!.ClientToken!);
            var result = await _yggdrasil.AuthenticateAsync(
                server.Base, username, password, clientToken);
            var account = _accounts.AddOrUpdateExternal(
                result.ProfileId, result.ProfileName, server.Base, serverUrl, server.ServerName);
            _accounts.SetSession(result.ProfileId, new AccountSession(result.AccessToken));
            return new
            {
                state = "success",
                account = new
                {
                    uuid = account.Uuid,
                    name = account.Username,
                    type = account.Type,
                    serverName = account.ServerName,
                },
            };
    }

    private static async Task<object?> HandleStarcloudStateAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                loggedIn = _starcloudId.IsLoggedIn,
                userId = _starcloudId.Current?.UserId,
                displayName = _starcloudId.Current?.DisplayName,
                channel = _starcloudId.Current?.Channel,
                loginAt = _starcloudId.Current?.LoginAt,
            };
    }

    private static async Task<object?> HandleStarcloudLoginAsync(TextWriter stdout, JsonElement p)
    {
            var session = await _starcloudId.LoginAsync(
                p.GetProperty("id").GetString() ?? "",
                p.GetProperty("password").GetString() ?? "");
            StartFriendsEvents(stdout);
            return new
            {
                loggedIn = true,
                userId = session.UserId,
                displayName = session.DisplayName,
                channel = session.Channel,
                loginAt = session.LoginAt,
            };
    }

    private static async Task<object?> HandleStarcloudSendCodeAsync(TextWriter stdout, JsonElement p)
    {
            var retryAfter = await _starcloudId.SendEmailCodeAsync(
                p.GetProperty("email").GetString() ?? "");
            return new { retryAfter };
    }

    private static async Task<object?> HandleStarcloudRegisterAsync(TextWriter stdout, JsonElement p)
    {
            await _starcloudId.RegisterAsync(
                p.GetProperty("id").GetString() ?? "",
                p.GetProperty("password").GetString() ?? "",
                p.GetProperty("email").GetString() ?? "",
                p.GetProperty("code").GetString() ?? "");
            return new { ok = true };
    }

    private static async Task<object?> HandleStarcloudLogoutAsync(TextWriter stdout, JsonElement p)
    {
            _starcloudId.Logout();
            StopFriendsEvents();
            return new { ok = true };
    }

    /// <summary>clientToken 规范是 UUID；老数据可能是不带连字符的 32 位十六进制。</summary>
    private static string AsUuid(string token)
    {
        var hex = token.Replace("-", "");
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return token;
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
    private static object AccountPayload(StoredAccount account) => new
    {
        account.Uuid,
        name = account.Username,
        account.Type,
        isDefault = account.IsDefault,
        account.ServerName,
        account.ServerAlias,
        account.SkinId,
        account.SkinModel,
        // 凭证只在内存：登录过期后 hasSession=false，UI 可提示重新登录。
        hasSession = _accounts.GetSession(account.Uuid) is not null,
    };
}
