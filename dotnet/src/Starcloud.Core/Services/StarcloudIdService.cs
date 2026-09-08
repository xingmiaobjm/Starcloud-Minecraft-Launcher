// Starcloud ID 账号体系：登录换取会话，登录成功后后台为用户创建云 AI 助手 API Key。
// 测试通道在本地模拟鉴权与发 Key（无需后端）；云端通道预留 HTTP 协议实现。

using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    public const string TestChannel = "test";
    public const string CloudChannel = "cloud";

    /// <summary>云 AI 助手服务地址（云端通道；测试通道不访问网络）</summary>
    public const string CloudBaseUrl = "https://ai.starcloud.example";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public StarcloudSession? Current { get; private set; }

    public StarcloudIdService() => Current = LoadSession();

    public bool IsLoggedIn => Current is not null;

    /// <summary>
    /// 登录 Starcloud ID。登录成功后后台为该用户创建（或取回）云 AI API Key。
    /// 测试通道：本地模拟服务端流程；同一 ID 多次登录得到同一 Key（模拟服务端存储）。
    /// </summary>
    public async Task<StarcloudSession> LoginAsync(
        string starcloudId, string password, string channel = TestChannel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(starcloudId))
            throw new InvalidOperationException("请输入 Starcloud ID");
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("请输入密码");

        var session = channel == CloudChannel
            ? await CloudLoginAsync(starcloudId, password, ct)
            : await TestLoginAsync(starcloudId, password, ct);

        Current = session;
        Persist();
        return session;
    }

    public void Logout()
    {
        Current = null;
        if (File.Exists(LauncherPaths.StarcloudIdFile))
            File.Delete(LauncherPaths.StarcloudIdFile);
    }

    /// <summary>
    /// 注册新的 Starcloud ID。测试通道本地模拟（进程内去重）；云端通道调用 /v1/auth/register。
    /// </summary>
    public async Task RegisterAsync(
        string starcloudId, string password, string channel = TestChannel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(starcloudId))
            throw new InvalidOperationException("请输入 Starcloud ID");
        if (password.Length < 6)
            throw new InvalidOperationException("密码至少 6 位");

        if (channel == CloudChannel)
            await CloudRegisterAsync(starcloudId, password, ct);
        else
            await TestRegisterAsync(starcloudId, password, ct);
    }

    // ── 测试通道：本地模拟注册（同进程内同 ID 不可重复注册） ──
    private static readonly HashSet<string> TestRegisteredIds = new();

    private static async Task TestRegisterAsync(string id, string password, CancellationToken ct)
    {
        await Task.Delay(500, ct); // 模拟网络往返
        if (!TestRegisteredIds.Add(id.ToLowerInvariant()))
            throw new InvalidOperationException("该 Starcloud ID 已被注册（测试通道）");
    }

    // ── 云端通道：POST /v1/auth/register ──
    private static async Task CloudRegisterAsync(string id, string password, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(CloudBaseUrl) };
        var resp = await http.PostAsync("/v1/auth/register",
            new StringContent(JsonSerializer.Serialize(new { id, password }),
                Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"注册失败（{(int)resp.StatusCode}）");
    }

    // ── 测试通道：本地模拟「登录 → 后台创建 API Key」 ──
    private static async Task<StarcloudSession> TestLoginAsync(string id, string password, CancellationToken ct)
    {
        await Task.Delay(500, ct); // 模拟网络往返
        if (password.Length < 4)
            throw new InvalidOperationException("测试通道：密码至少 4 位（任意内容均可）");

        var userId = "sc-" + Sha256Hex(id.ToLowerInvariant())[..12];
        var apiKey = "sk-test-" + Sha256Hex($"starcloud:{userId}:{id}")[..32];
        return new StarcloudSession(userId, id, apiKey, TestChannel, DateTime.Now);
    }

    // ── 云端通道：POST /v1/auth/login 换 token → POST /v1/auth/apikeys 创建 API Key ──
    private static async Task<StarcloudSession> CloudLoginAsync(string id, string password, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(CloudBaseUrl) };

        var loginResp = await http.PostAsync("/v1/auth/login",
            new StringContent(JsonSerializer.Serialize(new { id, password }),
                Encoding.UTF8, "application/json"), ct);
        loginResp.EnsureSuccessStatusCode();
        var loginJson = await loginResp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(loginJson);
        var token = doc.RootElement.GetProperty("token").GetString()
                    ?? throw new InvalidOperationException("登录响应缺少 token");
        var userId = doc.RootElement.GetProperty("user_id").GetString() ?? "";
        var displayName = doc.RootElement.TryGetProperty("display_name", out var dn)
            ? dn.GetString() ?? id : id;

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/apikeys");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var keyResp = await http.SendAsync(req, ct);
        keyResp.EnsureSuccessStatusCode();
        var keyJson = await keyResp.Content.ReadAsStringAsync(ct);
        using var keyDoc = JsonDocument.Parse(keyJson);
        var apiKey = keyDoc.RootElement.GetProperty("api_key").GetString()
                     ?? throw new InvalidOperationException("API Key 创建失败");

        return new StarcloudSession(userId, displayName, apiKey, CloudChannel, DateTime.Now);
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

    private static string Sha256Hex(string s)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
