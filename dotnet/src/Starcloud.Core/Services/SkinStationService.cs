// 皮肤站衣柜（第三方账号换肤）。
//
// 背景：Yggdrasil 只规定认证与会话，**皮肤库是各皮肤站自己的开放 API**（Blessing Skin /
// LittleSkin 一系）。正版账号那边皮肤归微软账号管、本机改不了，所以第三方账号走这条路：
// 把皮肤站上"我的皮肤"列出来，点一张就让它成为当前皮肤，游戏里（authlib-injector 注入后）看到的就是它。
//
// 两条路，能选中就选中，选不了就搬过去：
//   ① `PUT /api/v1/user/profile/skin` 带 `{"tid": <皮肤 id>}` —— 皮肤站若支持"直接设为当前"，
//      一步到位（皮肤不下载、不重传，账号分数也不浪费）；
//   ② 不支持就回退：把那张皮肤的 PNG 从站上下下来，走标准换肤接口上传回账号。
//      接口口径各家有差异，回退路径保证"能换成”，代价是多一次下载 + 上传。
//
// 合规：accessToken 只作参数（内存会话里来的，不落盘、不写日志）；密码不经过这里。

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>皮肤站上的一张皮肤（衣柜条目）</summary>
public sealed record StationSkin(string Id, string Name, string Model, string? Url)
{
    /// <summary>纤细手臂（皮肤站用 steve / alex 表示版型）</summary>
    public bool Slim => Model is "alex" or "slim";
}

public sealed class SkinStationService
{
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _handler;

    public SkinStationService(HttpMessageHandler? handler = null)
    {
        _handler = handler;
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }
            : new HttpClient(handler);
    }

    /// <summary>
    /// 列出该账号在皮肤站上的皮肤（衣柜）。失败抛可读异常（消息可直接展示给用户）。
    /// </summary>
    public async Task<IReadOnlyList<StationSkin>> ListAsync(
        string serverBase, string accessToken, CancellationToken ct = default)
    {
        var baseUrl = Base(serverBase);
        EnsureToken(accessToken);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/v1/user/skins?page=1&page_size=100");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await SendAsync(req, "读取皮肤站皮肤列表", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var doc = Parse(body, (int)resp.StatusCode, "读取皮肤站皮肤列表");
        using (doc)
        {
            var array = FindArray(doc.RootElement);
            if (array is null) return [];
            var list = new List<StationSkin>();
            foreach (var item in array.Value.EnumerateArray())
            {
                var id = Str(item, "tid") ?? Str(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new StationSkin(
                    id!,
                    Str(item, "name") ?? $"皮肤 {id}",
                    Str(item, "model") ?? "steve",
                    Str(item, "url")));
            }
            return list;
        }
    }

    /// <summary>
    /// 把衣柜里的一张设为当前皮肤；返回一句可展示的结果说明。
    /// </summary>
    public async Task<string> ApplyAsync(string serverBase, string accessToken,
        StationSkin skin, CancellationToken ct = default)
    {
        var baseUrl = Base(serverBase);
        EnsureToken(accessToken);

        // ① 直接"设为当前"（皮肤站支持的话最省事）
        var direct = await TrySelectAsync(baseUrl, accessToken, skin, ct);
        if (direct is not null) return direct;

        // ② 回退：下载这张皮肤 → 走标准换肤接口上传回账号
        var png = await TryFetchPngAsync(skin.Url, ct)
            ?? throw new InvalidOperationException(
                "皮肤站没有给出这张皮肤的下载地址（或下载失败），也没提供“直接设为当前”的接口，换不了");
        if (!SkinTexture.TryDecode(png, out _, out var error))
            throw new InvalidOperationException($"皮肤站上的这张皮肤不是合法纹理：{error}");

        // 回退上传复用同一套网络栈（同一个 handler），测试里也能注入假响应
        var upload = new SkinUploadService(_handler);
        var message = await upload.UploadExternalAsync(
            baseUrl, accessToken, png, skin.Slim ? SkinModels.Slim : SkinModels.Classic, ct);
        LauncherLog.Write("skin", $"皮肤站衣柜：已把「{skin.Name}」搬回账号（{baseUrl}）");
        return message + "（皮肤站不支持“直接选中”，已把这张皮肤搬回你的账号）";
    }

    /// <summary>
    /// 取皮肤站上某张皮肤的 PNG（列表缩略图与回退上传都用它）。取不到返回 null，不抛异常。
    /// </summary>
    public async Task<byte[]?> TryFetchPngAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return await _http.GetByteArrayAsync(url, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LauncherLog.Write("skin", $"皮肤站取图失败 {url}：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 试一次"设为当前"。服务端不支持这种写法返回 null（交给回退路径）；
    /// 凭证类错误（401/403）直接抛，因为回退也一样过不去。
    /// </summary>
    private async Task<string?> TrySelectAsync(string baseUrl, string accessToken,
        StationSkin skin, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/v1/user/profile/skin")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { tid = skin.Id }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"连接皮肤站失败：{ex.Message}", ex);
        }
        using (resp)
        {
            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                // 有些皮肤站用 HTTP 200 + {"code":1,"message":"..."} 表示失败
                if (JsonError(body) is { Length: > 0 } reason)
                    throw new InvalidOperationException(reason);
                LauncherLog.Write("skin", $"皮肤站衣柜：已选中「{skin.Name}」（{baseUrl}）");
                return $"已把「{skin.Name}」设为当前皮肤，重新进入游戏即可看到";
            }
            if (status is 401 or 403) throw new InvalidOperationException(LoginExpired);
            if (status is 400 or 404 or 405 or 501) return null;   // 写法不被支持 → 走回退
            throw new InvalidOperationException(
                $"皮肤站拒绝了这次换肤（HTTP {status}）" + Detail(body));
        }
    }

    private const string LoginExpired = "皮肤站的登录状态已失效，请在本页重新登录该账号后再试";

    private static string Base(string serverBase)
    {
        var baseUrl = (serverBase ?? "").TrimEnd('/');
        if (baseUrl.Length == 0) throw new InvalidOperationException("该账户没有记录认证服务器地址");
        return baseUrl;
    }

    private static void EnsureToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException(
                "本次运行还没有该账户的登录凭证（令牌不落盘），请先在本页重新登录");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, string what, CancellationToken ct)
    {
        try
        {
            var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode is 401 or 403)
            {
                resp.Dispose();
                throw new InvalidOperationException(LoginExpired);
            }
            if ((int)resp.StatusCode is 404 or 405 or 501)
            {
                resp.Dispose();
                throw new InvalidOperationException(NotASkinStation);
            }
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var status = (int)resp.StatusCode;
                resp.Dispose();
                throw new InvalidOperationException($"{what}失败（HTTP {status}）" + Detail(body));
            }
            return resp;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{what}失败：{ex.Message}", ex);
        }
    }

    private const string NotASkinStation =
        "这个认证服务器没有开放皮肤站接口（Blessing Skin / LittleSkin 这类皮肤站才有）";

    /// <summary>解析 JSON；HTTP 200 但业务码非 0 也算失败</summary>
    private static JsonDocument Parse(string body, int status, string what)
    {
        if (JsonError(body) is { Length: > 0 } reason)
            throw new InvalidOperationException(reason);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{what}失败：皮肤站返回了无法识别的内容（HTTP {status}）");
        }
    }

    /// <summary>Blessing Skin 一系的业务错误码：HTTP 200 + code != 0 + message</summary>
    private static string JsonError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty("code", out var code)) return "";
            if (code.ValueKind != JsonValueKind.Number || code.GetInt32() == 0) return "";
            var msg = new[] { "message", "msg", "error" }
                .Select(k => Str(doc.RootElement, k))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return string.IsNullOrWhiteSpace(msg) ? "皮肤站返回了错误" : msg!;
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>
    /// 从响应里挖出皮肤数组：`data` 直接是数组，或 `data.data`（分页包裹）都认。
    /// </summary>
    private static JsonElement? FindArray(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return null;
        if (data.ValueKind == JsonValueKind.Array) return data;
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Array)
            return inner;
        return null;
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static string Detail(string body)
    {
        var reason = JsonError(body);
        if (reason.Length > 0) return $"：{reason}";
        if (string.IsNullOrWhiteSpace(body)) return "";
        var trimmed = body.Length > 120 ? body[..120] : body;
        return $"：{trimmed}";
    }
}
