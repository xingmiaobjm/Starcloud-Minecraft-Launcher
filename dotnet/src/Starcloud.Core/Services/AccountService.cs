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

/// <summary>微软设备码登录（设备码 → 轮询 → XBL → XSTS → Minecraft API）
/// 走 login.live.com 的 legacy 设备码端点：Minecraft 官方 client_id 不属于任何 Entra
/// 租户，打 v2 devicecode/token 端点会 AADSTS700016。</summary>
public sealed class MicrosoftAuthService
{
    private const string ClientId = "00000000402b5328"; // Minecraft Launcher 官方
    private const string DeviceCodeEndpoint = "https://login.live.com/oauth20_connect.srf";
    private const string TokenEndpoint = "https://login.live.com/oauth20_token.srf";
    private const string MbiScope = "service::user.auth.xboxlive.com::MBI_SSL";
    private readonly HttpClient _http = new();

    public sealed record DeviceCodeInfo(string UserCode, string DeviceCode, string VerificationUri, int Interval);

    public sealed record LoginResult(string Username, string Uuid, string AccessToken,
        string? RefreshToken, string? Xuid = null);

    public async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            DeviceCodeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = MbiScope,
                ["response_type"] = "device_code",
            }), ct);
        if (!resp.IsSuccessStatusCode) throw await AuthExceptionAsync(resp, "获取设备码失败", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var info = new DeviceCodeInfo(
            doc.RootElement.GetProperty("user_code").GetString()!,
            doc.RootElement.GetProperty("device_code").GetString()!,
            doc.RootElement.GetProperty("verification_uri").GetString()!,
            doc.RootElement.GetProperty("interval").GetInt32());
        LauncherLog.Write("auth", $"设备码已获取 user_code={info.UserCode} interval={info.Interval}s uri={info.VerificationUri}");
        return info;
    }

    /// <summary>单次轮询：pending / success / error</summary>
    public async Task<(string State, string Message, LoginResult? Result)> PollAsync(
        DeviceCodeInfo code, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = code.DeviceCode,
                ["grant_type"] = "device_code",
            }), ct);
        // 未授权时 legacy 端点返回非 2xx + JSON error，必须先读体
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!TryParse(body, out var doc))
            throw new InvalidOperationException($"微软返回了无法解析的响应（HTTP {(int)resp.StatusCode}）");
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var err = error.GetString() ?? "";
                if (err is "authorization_pending") return ("pending", "等待用户授权", null);
                if (err is "slow_down") return ("pending", "请求过快，降速轮询", null);
                var explained = ExplainError(err, doc.RootElement);
                LauncherLog.Write("auth", $"轮询失败 HTTP {(int)resp.StatusCode} {explained}");
                return ("error", explained, null);
            }
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() : null;
            LauncherLog.Write("auth", $"用户已授权，MSA 令牌已兑现（len={accessToken.Length}）");
            // 设备码此刻已被承兑，不能再复用；认证链失败必须作为终态返回，
            // 否则调用方会拿同一个 device_code 重试并得到「授权已失效」而掩盖真实原因。
            LoginResult result;
            try
            {
                result = await AuthenticateWithMicrosoftTokenAsync(accessToken, refreshToken, ct);
            }
            catch (Exception ex)
            {
                LauncherLog.Write("auth", $"认证链失败 {ex.GetType().Name}: {ex.Message}");
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                return ("error", ex.Message, null);
            }
            LauncherLog.Write("auth", $"登录成功 {result.Username} ({result.Uuid})");
            return ("success", "登录成功", result);
        }
    }

    /// <summary>用 refresh_token 续期并重走认证链；refresh_token 失效返回 null（需重新设备码登录），
    /// 网络异常原样抛出由调用方决定降级策略。</summary>
    public async Task<LoginResult?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = MbiScope,
            }), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("error", out var error)) return null;
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString() : refreshToken;
        return await AuthenticateWithMicrosoftTokenAsync(accessToken, newRefresh, ct);
    }

    /// <summary>查询 Minecraft 档案：显式带 Bearer 令牌；令牌刚下发时服务端可能尚未传播，
    /// 收到 401 则退避重试。</summary>
    private async Task<JsonDocument> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            var profile = await _http.SendAsync(req, ct);
            if (profile.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException("该微软账户没有 Minecraft: Java 版（未购买或未迁移）");
            if (profile.IsSuccessStatusCode)
                return JsonDocument.Parse(await profile.Content.ReadAsStringAsync(ct));
            if (profile.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt < 3)
            {
                LauncherLog.Write("auth", $"档案查询第 {attempt} 次 401（令牌尚未传播），1 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            throw await AuthExceptionAsync(profile, "获取 Minecraft 档案失败", ct);
        }
    }

    private static bool TryParse(string body, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    /// <summary>把微软的 error / error_description 换成中文可读提示</summary>
    private static string ExplainError(string err, JsonElement root)
    {
        var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        var zh = err switch
        {
            "authorization_declined" => "你在验证页面拒绝了本次授权",
            "expired_token" or "code_expired" => "验证码已过期，请重新登录",
            "bad_verification_code" => "验证码无效，请重新登录",
            "invalid_grant" => "授权已失效，请重新登录",
            _ => null,
        };
        if (zh is not null) return zh;
        return string.IsNullOrWhiteSpace(desc) ? $"登录失败：{err}" : $"登录失败：{err}（{desc}）";
    }

    /// <summary>非 2xx 时把响应体里的 error / error_description 提成异常消息</summary>
    private static async Task<Exception> AuthExceptionAsync(
        HttpResponseMessage resp, string fallback, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (TryParse(body, out var doc))
        {
            using (doc)
            {
                var root = doc.RootElement;
                // XBL / XSTS 的失败体是 XErr 而非 error
                if (root.TryGetProperty("XErr", out var xe) && xe.TryGetInt64(out var xerr))
                    return new InvalidOperationException(
                        $"{fallback}：XErr {xerr}（HTTP {(int)resp.StatusCode}）");
                var err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                if (!string.IsNullOrWhiteSpace(err) || !string.IsNullOrWhiteSpace(desc))
                    return new InvalidOperationException(
                        $"{fallback}：{ExplainError(err, root)}（HTTP {(int)resp.StatusCode}）");
            }
        }
        return new InvalidOperationException($"{fallback}（HTTP {(int)resp.StatusCode}）");
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
                    // legacy live.com 令牌用 t= 前缀；d= 只适用于自注册 Azure 应用的 v2 令牌
                    RpsTicket = $"t={msAccessToken}",
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT",
            }), Encoding.UTF8, "application/json"), ct);
        if (!xbl.IsSuccessStatusCode) throw await AuthExceptionAsync(xbl, "Xbox Live 认证失败", ct);
        using var xblDoc = JsonDocument.Parse(await xbl.Content.ReadAsStringAsync(ct));
        var xblToken = xblDoc.RootElement.GetProperty("Token").GetString()!;
        var uhs = xblDoc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0]
            .GetProperty("uhs").GetString()!;
        LauncherLog.Write("auth", $"XBL 认证通过 uhs={uhs}");

        // XSTS
        var xsts = await _http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
            new StringContent(JsonSerializer.Serialize(new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            }), Encoding.UTF8, "application/json"), ct);
        if (!xsts.IsSuccessStatusCode)
        {
            using var xstsErrDoc = JsonDocument.Parse(await xsts.Content.ReadAsStringAsync(ct));
            var xerr = xstsErrDoc.RootElement.TryGetProperty("XErr", out var xe) ? xe.GetInt64() : 0L;
            throw new InvalidOperationException(xerr switch
            {
                2148916233 => "该微软账户尚未创建 Xbox Live 账号，请先前往 xbox.com 创建",
                2148916235 => "该微软账户所在地区不支持 Xbox Live",
                2148916238 => "该微软账户为未成年人账户，需要家长同意后才能登录",
                _ => $"Xbox 认证失败（XErr {xerr}）",
            });
        }
        using var xstsDoc = JsonDocument.Parse(await xsts.Content.ReadAsStringAsync(ct));
        var xstsToken = xstsDoc.RootElement.GetProperty("Token").GetString()!;
        LauncherLog.Write("auth", "XSTS 认证通过");

        // Minecraft 登录
        var mc = await _http.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new StringContent(JsonSerializer.Serialize(new
            {
                identityToken = $"XBL3.0 x={uhs};{xstsToken}",
            }), Encoding.UTF8, "application/json"), ct);
        if (!mc.IsSuccessStatusCode) throw await AuthExceptionAsync(mc, "Minecraft 登录失败", ct);
        using var mcDoc = JsonDocument.Parse(await mc.Content.ReadAsStringAsync(ct));
        var mcToken = mcDoc.RootElement.GetProperty("access_token").GetString()!;

        // Profile —— 必须显式带 Authorization: Bearer，且令牌下发后有传播延迟，401 时重试
        using var profileDoc = await GetProfileAsync(mcToken, ct);
        var username = profileDoc.RootElement.GetProperty("name").GetString()!;
        var uuid = profileDoc.RootElement.GetProperty("id").GetString()!;

        // uhs 即该用户的 xuid（十进制字符串），随启动参数 --xuid 传递
        return new LoginResult(username, uuid, mcToken, refreshToken, uhs);
    }
}
