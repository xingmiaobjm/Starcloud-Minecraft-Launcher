// Starcloud ID 账号体系：登录换取会话令牌，再用令牌换取云 AI 助手 API Key。
// 只保留云端通道（测试通道的本地模拟账号已移除）。
//
// 协议（后端 /v1/auth/*）：
//   注册   POST /v1/auth/email/code {email} → {ok,retry_after}（同邮箱 60 秒一次、每天 5 次）
//          POST /v1/auth/register   {id,password,email,code} → 201 空响应（验证码 6 位、10 分钟有效）
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

    /// <summary>登录态变化（登录 / 退出）：好友长连接等常驻服务据此启停。</summary>
    public event Action? SessionChanged;

    public StarcloudIdService() => Current = LoadSession();

    public bool IsLoggedIn => Current is not null;

    /// <summary>退出登录：清掉内存里的会话与本地会话文件。</summary>
    public void Logout()
    {
        Current = null;
        if (File.Exists(LauncherPaths.StarcloudIdFile))
            File.Delete(LauncherPaths.StarcloudIdFile);
        SessionChanged?.Invoke();
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
        SessionChanged?.Invoke();
        return session;
    }

    /// <summary>
    /// 发送邮箱验证码（POST /v1/auth/email/code），返回服务端建议的冷却秒数。
    /// 邮箱归一化（全角转半角、去空格与零宽字符）由服务端负责，这里只去掉首尾空白。
    /// </summary>
    public async Task<int> SendEmailCodeAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim();
        if (!LooksLikeEmail(email))
            throw new InvalidOperationException("请输入有效的邮箱地址");

        using var http = new HttpClient { BaseAddress = new Uri(AuthBaseUrl) };
        var resp = await http.PostAsync("/v1/auth/email/code",
            new StringContent(JsonSerializer.Serialize(new { email }),
                Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
            throw await ServerErrorAsync(resp, ct, "验证码发送失败，请稍后再试");

        // retry_after 只用于界面倒计时，解析不出来就按 60 秒算
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("retry_after", out var ra) &&
                ra.TryGetInt32(out var seconds) && seconds > 0)
                return seconds;
        }
        catch
        {
            // 响应体非 JSON：用默认冷却
        }
        return 60;
    }

    /// <summary>注册新的 Starcloud ID（先发邮箱验证码，再 POST /v1/auth/register）。</summary>
    public async Task RegisterAsync(
        string starcloudId, string password, string email, string code, CancellationToken ct = default)
    {
        starcloudId = starcloudId.Trim();
        email = email.Trim();
        code = code.Trim();

        if (starcloudId.Length == 0)
            throw new InvalidOperationException("请输入 Starcloud ID");
        if (starcloudId.Length > 64)
            throw new InvalidOperationException("Starcloud ID 不能超过 64 个字符");
        if (password.Length < 6)
            throw new InvalidOperationException("密码至少 6 位");
        if (!LooksLikeEmail(email))
            throw new InvalidOperationException("请输入有效的邮箱地址");
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
            throw new InvalidOperationException("验证码为 6 位数字");

        using var http = new HttpClient { BaseAddress = new Uri(AuthBaseUrl) };
        var resp = await http.PostAsync("/v1/auth/register",
            new StringContent(JsonSerializer.Serialize(new { id = starcloudId, password, email, code }),
                Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
            throw await ServerErrorAsync(resp, ct, "注册失败，请稍后再试");
    }

    /// <summary>粗校验：只挡明显不像邮箱的输入，真正的规则由服务端判定。</summary>
    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;
        if (email.IndexOf('@', at + 1) >= 0) return false;
        var dot = email.IndexOf('.', at + 1);
        return dot > at + 1 && dot < email.Length - 1;
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
