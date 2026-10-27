// Blessing Skin 6 站点接入（LittleSkin 2026-10 已升到 BS 6.0.2）。
//
// BS6 把站点 API 整体换成了 OAuth2 保护的新接口（auth:oauth + scope）：
//   GET  /api/closet                    衣柜（ Closet.Read ）
//   GET  /api/players                   角色列表（ Player.Read ）
//   PUT  /api/players/{name}/textures   换肤    （ Player.ReadWrite ）
// 旧 /api/v1/user/skins（Yggdrasil 令牌直用）在 BS6 上已下线 → 404。
//
// 授权：标准 OAuth2 授权码 + PKCE。本地在 127.0.0.1:27320 起一个一次性监听接回调，
// 打开系统浏览器让用户在皮肤站上确认，拿到 code 后换 token（client_secret 走机密客户端）。
//
// 凭证：token 只存本地（<dotMc>/starcloud/bs6-tokens.json），不进账号云档案、不写日志。

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Starcloud.Core.Services;

/// <summary>BS6 OAuth 令牌（含刷新令牌与过期时间）</summary>
public sealed record Bs6Tokens(string AccessToken, string RefreshToken, DateTime ExpiresAt);

public sealed class BlessingSkin6Service
{
    // LittleSkin 上为本启动器注册的 OAuth 应用（机密客户端）。
    // secret 打进桌面客户端本就是可提取的，属于 BS6 机密客户端模式的固有限制。
    public const string LittleSkinClientId = "1653";
    public const string LittleSkinClientSecret = "mrVkw44pW6ez2qw3uYgVRJpMVUM7mKCQTXBXelpF";

    public const string RedirectUri = "http://127.0.0.1:27320/callback";
    public const string Scopes = "Closet.Read Closet.ReadWrite Player.Read Player.ReadWrite";
    private const int CallbackPort = 27320;

    private readonly HttpClient _http;

    public BlessingSkin6Service(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }
            : new HttpClient(handler);
    }

    /// <summary>站点 API 大版本：GET /api 返回 blessing_skin（如 "6.0.2"）；拿不到返回 null</summary>
    public async Task<int?> ProbeMajorAsync(string root, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{root}/api", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("blessing_skin", out var v) ||
                v.ValueKind != JsonValueKind.String)
                return null;
            var text = v.GetString() ?? "";
            var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            var major = digits.Split('.')[0];
            return int.TryParse(major, out var n) ? n : null;
        }
        catch
        {
            return null;   // 探测失败不当错误：调用方走旧版实现
        }
    }

    /// <summary>
    /// 跑一次 OAuth2 授权码 + PKCE：起本地回调监听 → 打开系统浏览器 → 等用户确认 → 换令牌。
    /// </summary>
    /// <param name="openBrowser">打开授权页的回调（调用方用 Process.Start）</param>
    public async Task<Bs6Tokens> AuthorizeAsync(string root, string clientId, string clientSecret,
        Action<Uri> openBrowser, CancellationToken ct = default)
    {
        var verifier = PkceVerifier();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Guid.NewGuid().ToString("N")[..16];

        var listenerTask = WaitForCallbackCodeAsync(state, ct);

        var authorize = $"{root}/oauth/authorize" +
                        $"?client_id={Uri.EscapeDataString(clientId)}" +
                        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                        "&response_type=code" +
                        $"&scope={Uri.EscapeDataString(Scopes)}" +
                        $"&state={state}" +
                        $"&code_challenge={challenge}&code_challenge_method=S256";
        openBrowser(new Uri(authorize));

        var code = await listenerTask.WaitAsync(TimeSpan.FromMinutes(5), ct);
        return await TokenRequestAsync(root, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = RedirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier,
        }, ct);
    }

    /// <summary>用刷新令牌换新令牌（BS6 的 access token 会过期，refresh 长期有效）。</summary>
    public async Task<Bs6Tokens> RefreshAsync(string root, string clientId, string clientSecret,
        string refreshToken, CancellationToken ct = default)
    {
        return await TokenRequestAsync(root, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
        }, ct);
    }

    /// <summary>衣柜：BS6 返回的结构有包装差异，这里做宽容解析（找带 tid/name 的对象数组）。</summary>
    public async Task<IReadOnlyList<StationSkin>> ListClosetAsync(
        string root, string accessToken, string category = "skin", CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(
            Authed(HttpMethod.Get,
                $"{root}/api/closet?category={Uri.EscapeDataString(category)}&perPage=12",
                accessToken), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        LauncherLog.Write("station", $"衣柜响应：HTTP {(int)resp.StatusCode}，长度 {body.Length}");
        using var doc = JsonDocument.Parse(body);
        var array = FindSkinArray(doc.RootElement);
        if (array is null)
        {
            LauncherLog.Write("station", $"衣柜解析失败：{body[..Math.Min(body.Length, 300)]}");
            throw new InvalidOperationException("皮肤站返回的衣柜数据无法识别（不是预期的结构）");
        }
        var skinArray = array.Value;

        var list = new List<StationSkin>();
        var seen = new HashSet<string>();
        foreach (var item in skinArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Str(item, "tid") ?? Str(item, "id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            // BS6 条目没有 url 字段：纹理地址按 BS 约定从 hash 拼（{站点}/textures/{hash}）
            var url = Str(item, "url") ?? Str(item, "texture_url");
            if (string.IsNullOrEmpty(url))
            {
                var hash = Str(item, "hash");
                if (!string.IsNullOrEmpty(hash)) url = $"{root}/textures/{hash}";
            }
            list.Add(new StationSkin(
                id!,
                Str(item, "name") ?? Str(item, "item_name") ?? $"皮肤 {id}",
                Str(item, "model") ?? Str(item, "type") ?? "steve",
                url,
                category));
        }
        LauncherLog.Write("station", $"衣柜解析：{list.Count} 张（{root}）");
        return list;
    }

    /// <summary>该账号在皮肤站上的角色列表（名字 + 角色 tid + 数字 pid）。</summary>
    public async Task<IReadOnlyList<(string Name, string? Tid, string? Pid)>> ListPlayersAsync(
        string root, string accessToken, CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(
            Authed(HttpMethod.Get, $"{root}/api/players", accessToken), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // 角色数组的可能位置：根直接是数组 / data 数组 / data.players 数组。
        // 对数组元素调 TryGetProperty 会抛 "requires an element of type 'Object'"，逐层判型。
        JsonElement playersEl;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            playersEl = doc.RootElement;
        else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty("data", out var dataEl))
            playersEl = dataEl.ValueKind == JsonValueKind.Array
                ? dataEl
                : dataEl.ValueKind == JsonValueKind.Object &&
                  dataEl.TryGetProperty("players", out var pl) &&
                  pl.ValueKind == JsonValueKind.Array
                    ? pl
                    : throw new InvalidOperationException("皮肤站返回的角色列表无法识别");
        else
            throw new InvalidOperationException("皮肤站返回的角色列表无法识别");

        var list = new List<(string Name, string? Tid, string? Pid)>();
        foreach (var p in playersEl.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var name = Str(p, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var tid = Num(p, "tid");
            var pid = Num(p, "pid");
            list.Add((name!, tid, pid));
        }
        return list;
    }

    /// <summary>
    /// 把衣柜里的一张（tid）设为指定角色的当前皮肤。
    /// 路由参数是角色的数字 <c>pid</c>（传名字会被当整型查询 → 500）。
    /// </summary>
    public async Task SetSkinAsync(string root, string accessToken, string playerPid,
        string tid, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { skin = int.Parse(tid) });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var req = Authed(HttpMethod.Put,
            $"{root}/api/players/{Uri.EscapeDataString(playerPid)}/textures", accessToken);
        req.Content = content;
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"换肤失败（HTTP {(int)resp.StatusCode}）{Detail(text)}");
        }
    }

    /// <summary>把衣柜里的一张披风（tid）设为角色当前披风（BS6：PUT textures cape 字段）。</summary>
    public async Task SetCapeAsync(string root, string accessToken, string playerPid,
        string tid, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { cape = int.Parse(tid) });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var req = Authed(HttpMethod.Put,
            $"{root}/api/players/{Uri.EscapeDataString(playerPid)}/textures", accessToken);
        req.Content = content;
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"披风设置失败（HTTP {(int)resp.StatusCode}）{Detail(text)}");
        }
    }

    // ── 内部 ──

    private async Task<Bs6Tokens> TokenRequestAsync(string root,
        Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync($"{root}/oauth/token", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"换取令牌失败（HTTP {(int)resp.StatusCode}）{Detail(body)}");

        using var doc = JsonDocument.Parse(body);
        var access = Str(doc.RootElement, "access_token");
        var refresh = Str(doc.RootElement, "refresh_token") ?? "";
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidOperationException("皮肤站返回的令牌缺少 access_token");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) &&
                        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var secs)
            ? secs : 0;
        return new Bs6Tokens(access, refresh, DateTime.UtcNow.AddSeconds(expiresIn));
    }

    /// <summary>本地回调监听：等浏览器带着 code 打回 127.0.0.1:27320/callback。</summary>
    private static async Task<string> WaitForCallbackCodeAsync(string state, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        listener.Start();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var client = await listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();
                var line = await ReadRequestLineAsync(stream, ct);
                if (line is null) continue;

                // 应答一个极简完成页（浏览器收到就显示"授权完成"）
                var page = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n" +
                    "<!doctype html><meta charset='utf-8'><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
                    "<h2>授权完成</h2>可以关闭这个页面，回到启动器继续。</body>");
                await stream.WriteAsync(page, ct);

                var code = QueryValue(line, "code");
                var err = QueryValue(line, "error");
                if (!string.IsNullOrEmpty(err))
                    throw new InvalidOperationException($"皮肤站授权被拒绝或失败：{err}");
                if (!string.IsNullOrEmpty(code))
                {
                    var got = QueryValue(line, "state");
                    if (got != state)
                        throw new InvalidOperationException("授权回调 state 校验失败，请重试");
                    return code;
                }
                // 其它请求（favicon 之类）继续等真正的回调
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (sb.Length < 8192)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            var idx = sb.ToString().IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0) return sb.ToString(0, idx);   // "GET /path?query HTTP/1.1"
        }
        return null;
    }

    private static string? QueryValue(string requestLine, string key)
    {
        var q = requestLine.IndexOf('?');
        if (q < 0) return null;
        var end = requestLine.IndexOf(' ', q);
        var query = end > q ? requestLine.Substring(q + 1, end - q - 1) : requestLine[(q + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
        }
        return null;
    }

    private static string PkceVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Base64Url(bytes);   // 64 字符，符合 [A-Za-z0-9-._~] 43~128
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpRequestMessage Authed(HttpMethod method, string url, string accessToken)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    private static string? Str(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            // tid 等字段在 BS6 里是数字：返回原文本（之前只认字符串，数字 id 被当成空 → 全部跳过）
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static string? Num(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
    }

    private static string Detail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var msg = Str(doc.RootElement, "message") ?? Str(doc.RootElement, "error");
            return string.IsNullOrWhiteSpace(msg) ? "" : $"：{msg}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>深度找第一个"对象数组"：元素里带 tid 或（name 且 url），兼容各种包装。</summary>
    private static JsonElement? FindSkinArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return LooksLikeSkins(root) ? root : null;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array && LooksLikeSkins(prop.Value))
                return prop.Value;
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                if (FindSkinArray(prop.Value) is { } found)
                    return found;
        }
        return null;
    }

    private static bool LooksLikeSkins(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) return false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            var hasId = item.TryGetProperty("tid", out _) || item.TryGetProperty("id", out _);
            var hasName = item.TryGetProperty("name", out _) || item.TryGetProperty("item_name", out _);
            return hasId && hasName;
        }
        return false;
    }
}


/// <summary>BS6 OAuth 令牌的本地持久化（<dotMc>/starcloud/bs6-tokens.json，按账号隔离）。</summary>
public static class Bs6TokenStore
{
    private static readonly object Gate = new();

    public static string FilePath(string dotMc) =>
        Path.Combine(dotMc, "starcloud", "bs6-tokens.json");

    public static Bs6Tokens? Get(string dotMc, string key)
    {
        try
        {
            var file = FilePath(dotMc);
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty(key, out var node)) return null;
            var access = node.TryGetProperty("access", out var a) ? a.GetString() : null;
            var refresh = node.TryGetProperty("refresh", out var r) ? r.GetString() : null;
            var expires = node.TryGetProperty("expires", out var e) &&
                          DateTime.TryParse(e.GetString(), out var dt) ? dt : DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(access)) return null;
            return new Bs6Tokens(access!, refresh ?? "", expires);
        }
        catch
        {
            return null;   // 损坏视为无令牌：走重新授权
        }
    }

    public static void Set(string dotMc, string key, Bs6Tokens tokens)
    {
        lock (Gate)
        {
            var file = FilePath(dotMc);
            Dictionary<string, JsonObject> profiles = [];
            try
            {
                if (File.Exists(file))
                {
                    var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                    foreach (var (k, v) in root)
                        if (v is JsonObject o) profiles[k] = o;
                }
            }
            catch { /* 损坏重建 */ }

            profiles[key] = new JsonObject
            {
                ["access"] = tokens.AccessToken,
                ["refresh"] = tokens.RefreshToken,
                ["expires"] = tokens.ExpiresAt.ToString("O"),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = new JsonObject();
            foreach (var kv in profiles) json[kv.Key] = kv.Value;
            var temp = file + ".tmp";
            File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            File.Move(temp, file, overwrite: true);
        }
    }
}
