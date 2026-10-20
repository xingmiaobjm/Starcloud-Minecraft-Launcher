// Starcloud ID 账号体系：登录换取会话令牌，再用令牌换取云 AI 助手 API Key。
// 只保留云端通道（测试通道的本地模拟账号已移除）。
//
// 协议（后端 /v1/auth/*）：
//   登录   POST /v1/auth/login    {id,password} → {token,user_id,display_name,avatar_url}
//   换 Key POST /v1/auth/apikeys  Bearer token  → {api_key}（幂等：已有有效 Key 直接返回旧 Key）
//   token 只能消费一次，拿到的 api_key 才是长期凭证，必须持久化到本地。

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>登录会话（ApiKey 用于访问云 AI 助手）</summary>
public sealed record StarcloudSession(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("login_at")] DateTime LoginAt);

public sealed class StarcloudIdService
{
    /// <summary>会话记录的通道标识（新会话一律 cloud；旧会话文件里的值只用于展示）</summary>
    public const string CloudChannel = "cloud";

    /// <summary>账号服务地址：登录 / 注册 / 换 API Key</summary>
    public const string AuthBaseUrl = "https://starcloudfur.cn";

    /// <summary>云 AI 助手网关地址（OpenAI 兼容，Nova 的 chat / models / quota 都挂在这下面）</summary>
    public const string AiBaseUrl = "https://starcloudfur.cn";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public StarcloudSession? Current { get; private set; }

    public StarcloudIdService() => Current = LoadSession();

    public bool IsLoggedIn => Current is not null;

    /// <summary>退出登录：清掉内存里的会话与本地会话文件。</summary>
    public void Logout()
    {
        Current = null;
        if (File.Exists(LauncherPaths.StarcloudIdFile))
            File.Delete(LauncherPaths.StarcloudIdFile);
    }

    /// <summary>登录 Starcloud ID：拿会话令牌，再换（或取回）云 AI API Key。</summary>
    public async Task<StarcloudSession> LoginAsync(
        string starcloudId, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(starcloudId))
            throw new InvalidOperationException("请输入 Starcloud ID");
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("请输入密码");

        var session = await CloudLoginAsync(starcloudId.Trim(), password, ct);
        Current = session;
        Persist();
        return session;
    }

    /// <summary>注册新的 Starcloud ID（POST /v1/auth/register）。</summary>
    public async Task RegisterAsync(string starcloudId, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(starcloudId))
            throw new InvalidOperationException("请输入 Starcloud ID");
        if (password.Length < 6)
            throw new InvalidOperationException("密码至少 6 位");

        using var http = new HttpClient { BaseAddress = new Uri(AuthBaseUrl) };
        var resp = await http.PostAsync("/v1/auth/register",
            new StringContent(JsonSerializer.Serialize(new { id = starcloudId.Trim(), password }),
                Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
            throw await ServerErrorAsync(resp, ct, "注册失败，请稍后再试");
    }

    // ── 登录：POST /v1/auth/login 换 token → POST /v1/auth/apikeys 换 api_key ──
    private static async Task<StarcloudSession> CloudLoginAsync(string id, string password, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(AuthBaseUrl) };

        var loginResp = await http.PostAsync("/v1/auth/login",
            new StringContent(JsonSerializer.Serialize(new { id, password }),
                Encoding.UTF8, "application/json"), ct);
        if (!loginResp.IsSuccessStatusCode)
            throw await ServerErrorAsync(loginResp, ct, "登录失败，请检查账号或密码");

        var loginJson = await loginResp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(loginJson);
        var token = (doc.RootElement.TryGetProperty("token", out var tk) ? tk.GetString() : null)
                    ?? throw new InvalidOperationException("登录响应缺少 token");
        var userId = doc.RootElement.TryGetProperty("user_id", out var uid) ? uid.GetString() ?? "" : "";
        var displayName = doc.RootElement.TryGetProperty("display_name", out var dn)
            ? dn.GetString() ?? id : id;

        // 令牌只能消费一次；这里立刻换成长期 API Key
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/apikeys");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var keyResp = await http.SendAsync(req, ct);
        if (!keyResp.IsSuccessStatusCode)
            throw await ServerErrorAsync(keyResp, ct, "获取 API Key 失败");

        var keyJson = await keyResp.Content.ReadAsStringAsync(ct);
        using var keyDoc = JsonDocument.Parse(keyJson);
        var apiKey = (keyDoc.RootElement.TryGetProperty("api_key", out var ak) ? ak.GetString() : null)
                     ?? throw new InvalidOperationException("API Key 创建失败");

        return new StarcloudSession(userId, displayName, apiKey, CloudChannel, DateTime.Now);
    }

    /// <summary>后端错误体统一是 {"error":{"code","message"}}，message 已中文化，直接给用户看。</summary>
    private static async Task<Exception> ServerErrorAsync(
        HttpResponseMessage resp, CancellationToken ct, string fallback)
    {
        string? message = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var m) &&
                m.ValueKind == JsonValueKind.String)
                message = m.GetString();
        }
        catch
        {
            // 非 JSON / 空响应：用兜底文案
        }
        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(message) ? fallback : message);
    }

    // ── 会话持久化 ──
    private static StarcloudSession? LoadSession()
    {
        try
        {
            if (!File.Exists(LauncherPaths.StarcloudIdFile)) return null;
            return JsonSerializer.Deserialize<StarcloudSession>(
                File.ReadAllText(LauncherPaths.StarcloudIdFile), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private void Persist()
    {
        if (Current is null) return;
        LauncherPaths.EnsureDataDir();
        File.WriteAllText(LauncherPaths.StarcloudIdFile,
            JsonSerializer.Serialize(Current, JsonOpts));
    }
}
