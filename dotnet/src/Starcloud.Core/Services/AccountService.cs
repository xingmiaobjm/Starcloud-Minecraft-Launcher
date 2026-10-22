// 账户存储：微软设备码登录 + 第三方（Yggdrasil 外置登录）+ 离线账户（单人 / 局域网）。
// 鉴权 token（accessToken/refreshToken）仅存内存会话，绝不落盘；
// 磁盘只保存身份信息（uuid/用户名/服务器地址/设备标识 clientToken）。
// 离线身份的 UUID 走官方 nameUUIDFromBytes("OfflinePlayer:<name>") 算法，与各启动器互通。

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
    public string Type { get; set; } = "microsoft";

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    // ── 第三方（external）账户专属 ──

    /// <summary>解析后的认证服务器真实 base（Yggdrasil API 根）</summary>
    [JsonPropertyName("server_base")]
    public string? ServerBase { get; set; }

    /// <summary>用户填写的原始地址（可能为别名跳转前的 URL）</summary>
    [JsonPropertyName("server_alias")]
    public string? ServerAlias { get; set; }

    /// <summary>认证服务器展示名（meta.serverName）</summary>
    [JsonPropertyName("server_name")]
    public string? ServerName { get; set; }

    /// <summary>Yggdrasil 设备标识（非鉴权 token，终身固定复用）</summary>
    [JsonPropertyName("client_token")]
    public string? ClientToken { get; set; }

    // ── 皮肤（多皮肤管理：账户只引用皮肤库里的条目） ──

    /// <summary>该账户使用的皮肤库条目 id（null = 没指定，按账户默认头像渲染）</summary>
    [JsonPropertyName("skin_id")]
    public string? SkinId { get; set; }

    /// <summary>账户级的型号覆盖："auto" / "classic" / "slim"（null = 跟随皮肤条目）</summary>
    [JsonPropertyName("skin_model")]
    public string? SkinModel { get; set; }
}

/// <summary>
/// 一次应用会话内的账户凭证（仅内存，进程退出即失效，绝不序列化）。
/// token 以 CryptProtectMemory 密文驻留，<see cref="AccessToken"/> 取用时才短暂解出明文。
/// </summary>
public sealed class AccountSession : IDisposable
{
    private readonly SecureToken _access;
    private readonly SecureToken? _refresh;

    public AccountSession(string accessToken, string? refreshToken = null, string? xuid = null)
    {
        _access = new SecureToken(accessToken);
        _refresh = string.IsNullOrEmpty(refreshToken) ? null : new SecureToken(refreshToken);
        Xuid = xuid;
    }

    /// <summary>取用明文 access token（每次调用短暂解密；长期驻留的是密文）</summary>
    public string AccessToken => _access.Reveal();

    public string? RefreshToken => _refresh?.Reveal();

    public string? Xuid { get; }

    public bool HasAccessToken => _access.HasValue;

    /// <summary>明文长度（不触发解密）</summary>
    public int AccessTokenLength => _access.Length;

    public void Dispose()
    {
        _access.Dispose();
        _refresh?.Dispose();
    }
}

public sealed class AccountStore
{
    private readonly List<StoredAccount> _accounts = [];
    private readonly Dictionary<string, AccountSession> _sessions = []; // uuid → 会话凭证

    public IReadOnlyList<StoredAccount> Accounts => _accounts;

    /// <summary>离线账户类型标识</summary>
    public const string OfflineType = "offline";

    /// <summary>
    /// 离线身份 UUID：等价于 Java 的 <c>UUID.nameUUIDFromBytes("OfflinePlayer:&lt;name&gt;".getBytes(UTF_8))</c>
    /// （MD5 派生的 v3 UUID，带连字符的标准写法）。所有主流启动器都按同一算法算离线 UUID，
    /// 因此本地存档里的玩家数据、服务器白名单在换启动器后仍能对上同一个人。
    /// </summary>
    public static string OfflineUuid(string username)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + (username ?? "")));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version = 3（name-based, MD5）
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant = RFC 4122
        // 三个多字节字段按大端解释：Java 的 UUID.toString() 就是这样打印哈希字节的
        return new Guid(
                unchecked((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]),
                unchecked((short)((bytes[4] << 8) | bytes[5])),
                unchecked((short)((bytes[6] << 8) | bytes[7])),
                bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15])
            .ToString();
    }

    /// <summary>离线用户名合法性：官方允许 3-16 位字母 / 数字 / 下划线</summary>
    public static bool IsValidOfflineName(string username) =>
        !string.IsNullOrWhiteSpace(username) && username.Length is >= 3 and <= 16 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

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
        // 历史遗留条目迁移：离线账户缺 uuid（早期版本只存了名字）时按名字补算
        var migrated = false;
        foreach (var a in store._accounts.Where(a => a.Type == OfflineType &&
                                                     string.IsNullOrWhiteSpace(a.Uuid)))
        {
            a.Uuid = OfflineUuid(a.Username);
            migrated = true;
        }
        if (migrated) store.Save();
        return store;
    }

    public void Save()
    {
        LauncherPaths.EnsureDataDir();
        File.WriteAllText(LauncherPaths.AccountsFile,
            JsonSerializer.Serialize(_accounts, SettingsJson.Options));
    }

    /// <summary>添加或更新微软账户身份（按 uuid 去重），凭证另经 SetSession 存内存</summary>
    public StoredAccount AddOrUpdateMicrosoft(string uuid, string username)
    {
        var existing = _accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (existing is not null)
        {
            existing.Username = username;
            existing.Type = "microsoft";
        }
        else
        {
            existing = new StoredAccount
            {
                Uuid = uuid,
                Username = username,
                Type = "microsoft",
                IsDefault = _accounts.All(a => a.Type != "microsoft" || !a.IsDefault),
            };
            _accounts.Add(existing);
        }
        Save();
        return existing;
    }

    /// <summary>添加或更新第三方（Yggdrasil）账户身份；clientToken 首次生成后终身固定</summary>
    public StoredAccount AddOrUpdateExternal(
        string uuid, string username, string serverBase, string serverAlias, string? serverName)
    {
        var existing = _accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (existing is not null)
        {
            existing.Username = username;
            existing.Type = "external";
            existing.ServerBase = serverBase;
            existing.ServerAlias = serverAlias;
            existing.ServerName = serverName;
        }
        else
        {
            existing = new StoredAccount
            {
                Uuid = uuid,
                Username = username,
                Type = "external",
                ServerBase = serverBase,
                ServerAlias = serverAlias,
                ServerName = serverName,
                // 设备标识按规范写成标准 UUID（带连字符）：严格校验的认证服务器会直接拒收非 UUID
                ClientToken = Guid.NewGuid().ToString(),
                IsDefault = _accounts.All(a => a.Type != "external" || !a.IsDefault),
            };
            _accounts.Add(existing);
        }
        if (string.IsNullOrEmpty(existing.ClientToken))
            existing.ClientToken = Guid.NewGuid().ToString();
        Save();
        return existing;
    }

    /// <summary>
    /// 添加或更新离线账户身份（按用户名派生 UUID 去重）。离线身份无凭证可存，
    /// 启动时用占位令牌走本地 / 局域网游戏，不参与任何在线验证。
    /// </summary>
    public StoredAccount AddOrUpdateOffline(string username)
    {
        var name = (username ?? "").Trim();
        if (!IsValidOfflineName(name))
            throw new ArgumentException("离线用户名需为 3-16 位字母、数字或下划线");

        var uuid = OfflineUuid(name);
        var existing = _accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (existing is not null)
        {
            existing.Username = name;
            existing.Type = OfflineType;
        }
        else
        {
            existing = new StoredAccount
            {
                Uuid = uuid,
                Username = name,
                Type = OfflineType,
                IsDefault = _accounts.All(a => a.Type != OfflineType || !a.IsDefault),
            };
            _accounts.Add(existing);
        }
        Save();
        return existing;
    }

    /// <summary>写入会话凭证（仅内存；进程退出即失效）</summary>
    public void SetSession(string uuid, AccountSession session) => _sessions[uuid] = session;

    /// <summary>读取会话凭证；无则返回 null（本次会话尚未登录）</summary>
    public AccountSession? GetSession(string uuid) =>
        _sessions.TryGetValue(uuid, out var s) ? s : null;

    public void Remove(string uuid)
    {
        MicrosoftTokenStore.Remove(uuid); // 正版登录凭证一并清掉
        var type = _accounts.FirstOrDefault(a => a.Uuid == uuid)?.Type;
        _accounts.RemoveAll(a => a.Uuid == uuid);
        _sessions.Remove(uuid);
        // 删掉的是该类型首选时，用同类型第一个补位；其它类型的首选不受影响
        if (type is not null && _accounts.Any(a => a.Type == type) &&
            !_accounts.Any(a => a.IsDefault && a.Type == type))
            _accounts.First(a => a.Type == type).IsDefault = true;
        Save();
    }

    /// <summary>设为首选。首选按账户类型各记一个：换离线首选不会清掉正版首选，
    /// 否则一键启动切到另一种模式时会找不到该类型的账户。</summary>
    public void SetDefault(string uuid)
    {
        var target = _accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (target is null) return;
        foreach (var a in _accounts)
            if (a.Type == target.Type) a.IsDefault = a.Uuid == uuid;
        Save();
    }

    /// <summary>给账户指定皮肤库条目（skinId = null 表示取消指定）</summary>
    public StoredAccount? SetSkin(string uuid, string? skinId, string? model = null)
    {
        var account = _accounts.FirstOrDefault(a => a.Uuid == uuid);
        if (account is null) return null;
        account.SkinId = string.IsNullOrWhiteSpace(skinId) ? null : skinId;
        account.SkinModel = string.IsNullOrWhiteSpace(skinId) ? null : SkinModels.Normalize(model);
        Save();
        return account;
    }

    /// <summary>皮肤被库里删掉后清引用（避免账户指向不存在的皮肤）</summary>
    public int ClearSkinReference(string skinId)
    {
        var affected = 0;
        foreach (var a in _accounts.Where(a => a.SkinId == skinId))
        {
            a.SkinId = null;
            a.SkinModel = null;
            affected++;
        }
        if (affected > 0) Save();
        return affected;
    }

    public StoredAccount? Default =>
        _accounts.FirstOrDefault(a => a.IsDefault) ?? _accounts.FirstOrDefault();

    /// <summary>指定类型的首选账户；该类尚无标记首选时退化为该类型第一个</summary>
    public StoredAccount? DefaultOf(string type) =>
        _accounts.FirstOrDefault(a => a.IsDefault && a.Type == type) ??
        _accounts.FirstOrDefault(a => a.Type == type);
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

    /// <summary>
    /// 使用本地保存的 refresh_token 换一份新会话（正版登录 30 天免登录）。
    /// 失败抛异常（invalid_grant 已被 ExplainError 翻译成“授权已失效”），由调用方清掉本地凭证。
    /// </summary>
    public async Task<LoginResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = MbiScope,
            }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!TryParse(body, out var doc))
            throw new InvalidOperationException($"微软返回了无法解析的响应（HTTP {(int)resp.StatusCode}）");
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(ExplainError(error.GetString() ?? "", doc.RootElement));
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"刷新登录凭证失败（HTTP {(int)resp.StatusCode}）");
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("刷新响应缺少 access_token");
            var rotated = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() ?? refreshToken : refreshToken;
            LauncherLog.Write("auth", "已用本地 refresh_token 续期，重新兑换 Xbox / Minecraft 令牌");
            return await AuthenticateWithMicrosoftTokenAsync(accessToken, rotated, ct);
        }
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
