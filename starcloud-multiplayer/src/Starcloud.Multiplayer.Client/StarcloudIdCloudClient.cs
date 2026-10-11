using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Starcloud.Multiplayer.Core.Models;

namespace Starcloud.Multiplayer.Client;

/// <summary>
/// Starcloud ID 云端通道登录：<c>POST /v1/auth/login</c> 换 token → <c>POST /v1/auth/apikeys</c> 换 ApiKey。
/// 与启动器走同一套协议，但这里是独立实现（不引用启动器程序集）。
/// </summary>
internal static class StarcloudIdCloudClient
{
    public static async Task<StarcloudIdentity> LoginAsync(string id, string password, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(StarcloudIdentity.CloudBaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        using var loginResponse = await http.PostAsJsonAsync(
            "/v1/auth/login", new { id, password }, ct);
        if (!loginResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Starcloud ID 登录失败（{(int)loginResponse.StatusCode}）");

        using var loginDoc = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(ct));
        var token = loginDoc.RootElement.GetProperty("token").GetString()
                    ?? throw new InvalidOperationException("登录响应缺少 token");
        var userId = loginDoc.RootElement.TryGetProperty("user_id", out var uid) ? uid.GetString() ?? id : id;
        var displayName = loginDoc.RootElement.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? id : id;

        using var keyRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/apikeys");
        keyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var keyResponse = await http.SendAsync(keyRequest, ct);
        if (!keyResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"创建 API Key 失败（{(int)keyResponse.StatusCode}）");

        using var keyDoc = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync(ct));
        var apiKey = keyDoc.RootElement.GetProperty("api_key").GetString()
                     ?? throw new InvalidOperationException("创建 API Key 失败：响应缺少 api_key");

        return new StarcloudIdentity(
            userId, displayName, apiKey, StarcloudIdentity.CloudChannel, DateTimeOffset.Now);
    }
}
