// 换肤上传：把库里的皮肤同步到「账号本身」，让别人也能看到。
//
// · 微软（正版）：官方 API PUT /minecraft/profile/skins（multipart，variant + file），
//   用本次会话内存里的 Minecraft access token（令牌不落盘，缺了就要求就地登录）。
// · 第三方（Yggdrasil / authlib）：标准接口 PUT /api/v1/user/profile/skin（JSON + base64 文件）。
//   并非所有认证服务器都实现换肤，接口返回 404/405 时如实告知「该服务器不支持」。
// · 离线账户：账号侧没有可上传的对象，只能本机生效（见 OfflineSkinServer）。

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

public sealed class SkinUploadService
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly HttpClient _http;

    /// <param name="handler">测试注入用；生产走共享 HttpClient（连接复用）</param>
    public SkinUploadService(HttpMessageHandler? handler = null)
        => _http = handler is null ? Shared : new HttpClient(handler);

    /// <summary>
    /// 换肤请求体的序列化选项。默认编码器会把 base64 里的 `+` 转义成 `\u002B`
    /// （System.Text.Json 的默认行为），皮肤白白多传一圈转义字节，还可能被简陋的服务端读歪，
    /// 所以这里放宽转义——请求体本来就是给 JSON API 吃的，不用担心嵌进 HTML。
    /// </summary>
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>微软换肤端点（Minecraft Services）</summary>
    public const string MicrosoftSkinEndpoint =
        "https://api.minecraftservices.com/minecraft/profile/skins";

    /// <summary>把皮肤 PNG 上传到微软账号；成功返回一句结果说明，失败抛异常（消息可直接展示）</summary>
    public async Task<string> UploadMicrosoftAsync(string accessToken, byte[] png,
        string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("本次会话没有可用的登录凭证，请到账户页重新登录后再换肤");
        var variant = SkinModels.Normalize(model) == SkinModels.Slim ? "slim" : "classic";

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(variant), "variant");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "skin.png");

        using var req = new HttpRequestMessage(HttpMethod.Put, MicrosoftSkinEndpoint)
        {
            Content = content,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeMicrosoftFailure((int)resp.StatusCode, body));
        LauncherLog.Write("skin", $"已上传微软皮肤（variant={variant}，{png.Length} 字节）");
        return $"已同步到微软账号（{variant}），重新进入游戏即可看到";
    }

    /// <summary>
    /// 把皮肤上传到第三方认证服务器（Yggdrasil 标准换肤）。
    /// 服务器未实现换肤时会返回 404 / 405 / 501，这里翻译成可读说明。
    /// </summary>
    public async Task<string> UploadExternalAsync(string serverBase, string accessToken, byte[] png,
        string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("本次会话没有可用的登录凭证，请到账户页重新登录后再换肤");
        var baseUrl = (serverBase ?? "").TrimEnd('/');
        if (baseUrl.Length == 0) throw new InvalidOperationException("该账户没有记录认证服务器地址");

        var slim = SkinModels.Normalize(model) == SkinModels.Slim;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = slim ? "slim" : "",
            ["file"] = Convert.ToBase64String(png),
        }, PayloadJson);
        using var req = new HttpRequestMessage(HttpMethod.Put,
            baseUrl + "/api/v1/user/profile/skin")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeExternalFailure((int)resp.StatusCode, body));
        LauncherLog.Write("skin", $"已上传第三方皮肤（{baseUrl}，{png.Length} 字节）");
        return "已同步到认证服务器，重新进入游戏即可看到";
    }

    /// <summary>读微软档案里的当前皮肤（换肤后回读一次，确认服务端已更新）</summary>
    public async Task<(string? Url, string? Variant)> ReadMicrosoftSkinAsync(
        string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("skins", out var skins) ||
                skins.ValueKind != JsonValueKind.Array || skins.GetArrayLength() == 0)
                return (null, null);
            var skin = skins.EnumerateArray().FirstOrDefault(s =>
                !s.TryGetProperty("state", out var state) || state.GetString() == "ACTIVE");
            if (skin.ValueKind != JsonValueKind.Object) skin = skins[0];
            var url = skin.TryGetProperty("url", out var u) ? u.GetString() : null;
            var variant = skin.TryGetProperty("variant", out var v) ? v.GetString() : null;
            return (url, variant);
        }
        catch
        {
            return (null, null); // 只读校验，失败不影响换肤本身
        }
    }

    private static string DescribeMicrosoftFailure(int status, string body)
    {
        var detail = ExtractDetail(body);
        return status switch
        {
            400 => "微软拒绝了这张皮肤：请确认是 64×64（或 64×32 / 等比放大）的 PNG" +
                   (detail.Length > 0 ? $"（{detail}）" : ""),
            401 => "登录状态已失效，请到账户页重新登录后再换肤",
            403 => "该微软账户没有 Minecraft: Java 版档案，无法换肤",
            429 => "换肤请求过于频繁，稍等几分钟再试",
            _ => $"换肤失败（HTTP {status}）" + (detail.Length > 0 ? $"：{detail}" : ""),
        };
    }

    private static string DescribeExternalFailure(int status, string body)
    {
        var detail = ExtractDetail(body);
        return status switch
        {
            401 or 403 => "认证服务器拒绝了这次换肤（登录状态可能已失效），请重新登录后再试",
            404 or 405 or 501 => "该认证服务器没有实现换肤接口（标准 Yggdrasil 才有 /api/v1/user/profile/skin）",
            400 => "认证服务器认为皮肤不合法" + (detail.Length > 0 ? $"：{detail}" : ""),
            _ => $"换肤失败（HTTP {status}）" + (detail.Length > 0 ? $"：{detail}" : ""),
        };
    }

    /// <summary>从错误体里挖出可读原因（Yggdrasil 用 errorMessage，微软用 error/errorMessage）</summary>
    private static string ExtractDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "errorMessage", "error_description", "error", "message" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) &&
                    v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text)
                    return text;
            }
        }
        catch
        {
            // 非 JSON 错误体：直接截断展示
            return body.Length > 120 ? body[..120] : body;
        }
        return "";
    }
}
