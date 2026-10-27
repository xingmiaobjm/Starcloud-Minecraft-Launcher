// 第三方（Yggdrasil / authlib-injector）认证服务客户端：服务器解析、账密登录、皮肤地址。
// 合规：认证服务器地址一律来自用户输入，本仓库不内置任何服务器地址；
// 密码只作方法参数，不落盘、不写日志。

using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>解析后的认证服务器：Base 为 Yggdrasil API 根（ trailing / 已去除）</summary>
public sealed record YggServer(string Base, string? ServerName);

/// <summary>账密登录结果：accessToken 仅内存会话使用，绝不持久化</summary>
public sealed record YggAuthResult(string AccessToken, string ClientToken, string ProfileId, string ProfileName);

public sealed class YggdrasilService
{
    private readonly HttpClient _http;

    public YggdrasilService(HttpMessageHandler? handler = null)
        => _http = handler is null ? new HttpClient() : new HttpClient(handler);

    /// <summary>
    /// 解析用户填写的认证服务器地址：GET 该地址，若返回 JSON 含 serverLocation（别名跳转）
    /// 则以其为真实 base 继续跟随（限 3 跳防环）；否则视为 Yggdrasil 根，
    /// meta.serverName 作为展示名。失败抛中文异常由调用方展示。
    /// </summary>
    public async Task<YggServer> ResolveServerAsync(string inputUrl, CancellationToken ct = default)
    {
        var url = inputUrl.Trim();
        if (url.Length == 0)
            throw new InvalidOperationException("请填写认证服务器地址");
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        for (var hop = 0; hop < 3; hop++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync(url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"认证服务器无法访问：{ex.Message}", ex);
            }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"认证服务器无法访问（HTTP {(int)resp.StatusCode}）");
                JsonDocument doc;
                try { doc = JsonDocument.Parse(body); }
                catch (JsonException)
                {
                    throw new InvalidOperationException("该地址不是有效的 Yggdrasil 认证服务器");
                }
                using (doc)
                {
                    // 别名跳转：authlib-injector 约定根地址返回 serverLocation 指向真实 API base
                    if (doc.RootElement.TryGetProperty("serverLocation", out var loc) &&
                        loc.GetString() is { Length: > 0 } real)
                    {
                        url = real;
                        continue;
                    }
                    var name = doc.RootElement.TryGetProperty("meta", out var meta) &&
                               meta.TryGetProperty("serverName", out var sn)
                        ? sn.GetString()
                        : null;
                    return new YggServer(url.TrimEnd('/'),
                        string.IsNullOrWhiteSpace(name) ? null : name);
                }
            }
        }
        throw new InvalidOperationException("认证服务器别名跳转超过 3 层，疑似配置错误");
    }

    /// <summary>Yggdrasil 账密登录：POST {base}/authserver/authenticate。
    /// 无 selectedProfile 时取 availableProfiles 首个（单角色服务器）。</summary>
    public async Task<YggAuthResult> AuthenticateAsync(
        string baseUrl, string username, string password, string clientToken,
        CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync($"{baseUrl}/authserver/authenticate",
                new StringContent(JsonSerializer.Serialize(new
                {
                    // 规范硬性要求：agent.name 必须**正好**是 "Minecraft"。
                    // 写自家名字（"MinecraftLauncher"）会被皮肤站一类的服务端直接拒：
                    // LittleSkin 报的就是 "agent.name must be equal to Minecraft"。
                    agent = new { name = "Minecraft", version = 1 },
                    username,
                    password,
                    clientToken,
                    requestUser = false,
                }), Encoding.UTF8, "application/json"), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接认证服务器：{ex.Message}", ex);
        }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                throw new InvalidOperationException(
                    $"认证服务器返回了无法识别的内容（HTTP {(int)resp.StatusCode}）");
            }
            using (doc)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var msg = doc.RootElement.TryGetProperty("errorMessage", out var em)
                        ? em.GetString() : null;
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg)
                        ? $"登录失败（HTTP {(int)resp.StatusCode}）"
                        : msg!);
                }
                var token = doc.RootElement.GetProperty("accessToken").GetString()!;
                var client = doc.RootElement.GetProperty("clientToken").GetString()!;
                var profile = doc.RootElement.TryGetProperty("selectedProfile", out var sp) &&
                              sp.ValueKind == JsonValueKind.Object
                    ? sp
                    : doc.RootElement.TryGetProperty("availableProfiles", out var aps) &&
                      aps.ValueKind == JsonValueKind.Array && aps.GetArrayLength() > 0
                        ? aps[0]
                        : default;
                if (profile.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("该账号在认证服务器上没有 Minecraft 角色");
                return new YggAuthResult(
                    token, client,
                    profile.GetProperty("id").GetString()!,
                    profile.GetProperty("name").GetString()!);
            }
        }
    }

    /// <summary>
    /// 查询角色皮肤地址：GET {base}/sessionserver/session/minecraft/profile/{uuid} →
    /// properties[].textures（base64）→ textures.SKIN.url。任何失败返回 null（皮肤是尽力而为）。
    /// </summary>
    public async Task<string?> GetSkinUrlAsync(string baseUrl, string uuid, CancellationToken ct = default)
        => (await GetTexturesAsync(baseUrl, uuid, ct)).SkinUrl;

    /// <summary>
    /// 按角色名查 UUID：POST {base}/api/profiles/minecraft（Yggdrasil 规范子集）。
    /// OAuth 建号时只有角色名、没有账密，用这个接口补齐账户所需的 UUID。
    /// 找不到 / 请求失败返回 null，由调用方给可读提示。
    /// </summary>
    public async Task<string?> LookupProfileUuidAsync(
        string baseUrl, string name, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsync($"{baseUrl}/api/profiles/minecraft",
                new StringContent(JsonSerializer.Serialize(new[] { name }), Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out var n) ||
                    !string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                return item.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>会话接口解出的纹理地址：SKIN.url 与 CAPE.url（任一缺失为 null）。</summary>
    public async Task<(string? SkinUrl, string? CapeUrl)> GetTexturesAsync(
        string baseUrl, string uuid, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"{baseUrl}/sessionserver/session/minecraft/profile/{uuid}", ct);
            if (!resp.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return (null, null);
            foreach (var p in props.EnumerateArray())
            {
                if (!p.TryGetProperty("name", out var n) || n.GetString() != "textures" ||
                    !p.TryGetProperty("value", out var v))
                    continue;
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(v.GetString() ?? ""));
                using var tex = JsonDocument.Parse(decoded);
                if (!tex.RootElement.TryGetProperty("textures", out var tx)) return (null, null);

                string? UrlOf(string slot) =>
                    tx.TryGetProperty(slot, out var e) &&
                    e.TryGetProperty("url", out var u) ? u.GetString() : null;
                return (UrlOf("SKIN"), UrlOf("CAPE"));
            }
            return (null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null); }
    }
}
