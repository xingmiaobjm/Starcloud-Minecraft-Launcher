// 账户存储：离线账户（uuid 按 OfflinePlayer 规则）+ 微软设备码登录（官方启动器 client_id）

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public sealed class StoredAccount
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "offline";

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }
}

public sealed class AccountStore
{
    private readonly List<StoredAccount> _accounts = [];

    public IReadOnlyList<StoredAccount> Accounts => _accounts;

    public static AccountStore Load()
    {
        LauncherPaths.EnsureDataDir();
        var store = new AccountStore();
        if (File.Exists(LauncherPaths.AccountsFile))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<StoredAccount>>(
                    File.ReadAllText(LauncherPaths.AccountsFile));
                if (list is not null) store._accounts.AddRange(list);
            }
            catch
            {
                /* 损坏则从空开始 */
            }
        }
        return store;
    }

    public void Save()
    {
        LauncherPaths.EnsureDataDir();
        File.WriteAllText(LauncherPaths.AccountsFile,
            JsonSerializer.Serialize(_accounts, SettingsJson.Options));
    }

    public StoredAccount AddOffline(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("玩家名不能为空");
        var uuid = GameLauncher.OfflineUuid(username).ToString("N");
        var existing = _accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (existing is not null) return existing;
        var account = new StoredAccount
        {
            Uuid = uuid,
            Username = username.Trim(),
            Type = "offline",
            IsDefault = _accounts.Count == 0,
        };
        _accounts.Add(account);
        Save();
        return account;
    }

    /// <summary>添加或更新微软账户（按 uuid 去重），并设为默认</summary>
    public StoredAccount AddOrUpdateMicrosoft(string uuid, string username,
        string accessToken, string? refreshToken)
    {
        var existing = _accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (existing is not null)
        {
            existing.Username = username;
            existing.AccessToken = accessToken;
            existing.RefreshToken = refreshToken;
            existing.Type = "microsoft";
        }
        else
        {
            existing = new StoredAccount
            {
                Uuid = uuid,
                Username = username,
                Type = "microsoft",
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                IsDefault = _accounts.Count == 0,
            };
            _accounts.Add(existing);
        }
        Save();
        return existing;
    }

    public void Remove(string uuid)
    {
        _accounts.RemoveAll(a => a.Uuid == uuid);
        if (_accounts.Count > 0 && _accounts.All(a => !a.IsDefault))
            _accounts[0].IsDefault = true;
        Save();
    }

    public void SetDefault(string uuid)
    {
        foreach (var a in _accounts) a.IsDefault = a.Uuid == uuid;
        Save();
    }

    public StoredAccount? Default =>
        _accounts.FirstOrDefault(a => a.IsDefault) ?? _accounts.FirstOrDefault();
}

/// <summary>微软设备码登录（设备码 → 轮询 → XBL → XSTS → Minecraft API）</summary>
public sealed class MicrosoftAuthService
{
    private const string ClientId = "00000000402b5328"; // Minecraft Launcher 官方
    private readonly HttpClient _http = new();

    public sealed record DeviceCodeInfo(string UserCode, string DeviceCode, string VerificationUri, int Interval);

    public sealed record LoginResult(string Username, string Uuid, string AccessToken,
        string? RefreshToken, string? Xuid = null);

    public async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = "XboxLive.signin offline_access",
            }), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return new DeviceCodeInfo(
            doc.RootElement.GetProperty("user_code").GetString()!,
            doc.RootElement.GetProperty("device_code").GetString()!,
            doc.RootElement.GetProperty("verification_uri").GetString()!,
            doc.RootElement.GetProperty("interval").GetInt32());
    }

    /// <summary>单次轮询：pending / success / error</summary>
    public async Task<(string State, string Message, LoginResult? Result)> PollAsync(
        DeviceCodeInfo code, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = code.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var err = error.GetString() ?? "";
            if (err is "authorization_pending") return ("pending", "等待用户授权", null);
            if (err is "slow_down") return ("pending", "请求过快，降速轮询", null);
            return ("error", err, null);
        }
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString() : null;
        var result = await AuthenticateWithMicrosoftTokenAsync(accessToken, refreshToken, ct);
        return ("success", "登录成功", result);
    }

    /// <summary>用 refresh_token 续期并重走认证链；refresh_token 失效返回 null（需重新设备码登录），
    /// 网络异常原样抛出由调用方决定降级策略。</summary>
    public async Task<LoginResult?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = "XboxLive.signin offline_access",
            }), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("error", out var error)) return null;
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString() : refreshToken;
        return await AuthenticateWithMicrosoftTokenAsync(accessToken, newRefresh, ct);
    }

    private async Task<LoginResult> AuthenticateWithMicrosoftTokenAsync(
        string msAccessToken, string? refreshToken, CancellationToken ct)
    {
        // XBL
        var xbl = await _http.PostAsync("https://user.auth.xboxlive.com/user/authenticate",
            new StringContent(JsonSerializer.Serialize(new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = $"d={msAccessToken}",
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT",
            }), Encoding.UTF8, "application/json"), ct);
        xbl.EnsureSuccessStatusCode();
        using var xblDoc = JsonDocument.Parse(await xbl.Content.ReadAsStringAsync(ct));
        var xblToken = xblDoc.RootElement.GetProperty("Token").GetString()!;
        var uhs = xblDoc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0]
            .GetProperty("uhs").GetString()!;

        // XSTS
        var xsts = await _http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
            new StringContent(JsonSerializer.Serialize(new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            }), Encoding.UTF8, "application/json"), ct);
        xsts.EnsureSuccessStatusCode();
        using var xstsDoc = JsonDocument.Parse(await xsts.Content.ReadAsStringAsync(ct));
        var xstsToken = xstsDoc.RootElement.GetProperty("Token").GetString()!;

        // Minecraft 登录
        var mc = await _http.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new StringContent(JsonSerializer.Serialize(new
            {
                identityToken = $"XBL3.0 x={uhs};{xstsToken}",
            }), Encoding.UTF8, "application/json"), ct);
        mc.EnsureSuccessStatusCode();
        using var mcDoc = JsonDocument.Parse(await mc.Content.ReadAsStringAsync(ct));
        var mcToken = mcDoc.RootElement.GetProperty("access_token").GetString()!;

        // Profile
        var profile = await _http.GetAsync("https://api.minecraftservices.com/minecraft/profile", ct);
        profile.EnsureSuccessStatusCode();
        using var profileDoc = JsonDocument.Parse(await profile.Content.ReadAsStringAsync(ct));
        var username = profileDoc.RootElement.GetProperty("name").GetString()!;
        var uuid = profileDoc.RootElement.GetProperty("id").GetString()!;

        // uhs 即该用户的 xuid（十进制字符串），随启动参数 --xuid 传递
        return new LoginResult(username, uuid, mcToken, refreshToken, uhs);
    }
}
