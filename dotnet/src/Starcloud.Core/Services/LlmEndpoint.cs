// 自接模型（含本机 Ollama / LM Studio / vLLM）的接入地址处理与连通性自检。
//
// 为什么单独一块：
//   1. 接入地址是用户手输的，写法五花八门（漏 http://、漏 /v1、直接粘 /chat/completions）；
//      归一化放在这里，保存与请求两条路共用同一份规则。
//   2. 接错了要能立刻说清楚。HTTP 4xx 的原文（Model Not Exist / invalid api key）必须回给用户，
//      否则只剩一句「没匹配到本地功能」，没人能定位问题。

using System.Net.Http.Headers;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>接入自检结果：Ok=false 时 <see cref="Message"/> 是给用户看的原因与下一步。</summary>
public sealed record LlmProbeResult(bool Ok, int StatusCode, IReadOnlyList<string> Models, string Message)
{
    public static LlmProbeResult Success(IReadOnlyList<string> models) =>
        new(true, 200, models, models.Count == 0
            ? "连接正常，但这个地址没有列出可用模型，请确认模型名"
            : $"连接正常，可用模型 {models.Count} 个");
}

public static class LlmEndpoint
{
    /// <summary>
    /// 接入地址归一化：补协议 → 去尾斜杠 → 去掉误粘的 /chat/completions → 根路径补 /v1。
    /// 本机地址（127.0.0.1 / localhost / ::1）默认 http，其余默认 https。
    /// </summary>
    public static string NormalizeBaseUrl(string? raw)
    {
        var url = (raw ?? "").Trim();
        if (url.Length == 0) return "";

        if (!url.Contains("://", StringComparison.Ordinal))
            url = (IsLocalHost(CandidateHost(url)) ? "http://" : "https://") + url;

        url = url.TrimEnd('/');

        // 用户常常把完整对话地址粘进来（…/v1/chat/completions），这里回退到挂载点
        foreach (var suffix in new[] { "/chat/completions", "/completions" })
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                url = url[..^suffix.Length].TrimEnd('/');

        // 只给了主机名（api.openai.com / 127.0.0.1:11434）→ 补 OpenAI 兼容挂载点 /v1
        var path = PathOf(url);
        if (path.Length == 0) url += "/v1";
        return url;
    }

    public static string ChatUrl(string? baseUrl) => NormalizeBaseUrl(baseUrl) + "/chat/completions";

    public static string ModelsUrl(string? baseUrl) => NormalizeBaseUrl(baseUrl) + "/models";

    /// <summary>
    /// 拉一次 <c>GET {base}/models</c> 做自检：地址、端口、Key、可用模型名一次看清。
    /// 探测只用于「用户按下测试按钮」，不参与每轮对话。
    /// </summary>
    public static async Task<LlmProbeResult> ProbeAsync(
        HttpClient http, string? baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (normalized.Length == 0)
            return new LlmProbeResult(false, 0, [], "请先填写接入地址（例如 http://127.0.0.1:11434/v1）");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(normalized));
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var resp = await http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                return new LlmProbeResult(false, (int)resp.StatusCode, [], Explain((int)resp.StatusCode, body, normalized));

            var models = ParseModelIds(body);
            return LlmProbeResult.Success(models);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LlmProbeResult(false, 0, [], Explain(0, "超时（10 秒）", normalized));
        }
        catch (Exception ex)
        {
            return new LlmProbeResult(false, 0, [], Explain(0, ex.Message, normalized));
        }
    }

    /// <summary>
    /// HTTP 失败的用户可读说明：状态码 + 上游原文 + 该去哪儿改。
    /// <paramref name="status"/> = 0 表示请求根本没连上（地址 / 端口 / 服务没起）。
    /// </summary>
    public static string Explain(int status, string? body, string? baseUrl)
    {
        var target = NormalizeBaseUrl(baseUrl);
        var detail = Summarize(body);
        var head = status switch
        {
            0 => $"连不上模型服务：{detail}",
            400 => "模型服务拒绝了这次请求（HTTP 400）：模型名不存在、模型不支持工具调用、或参数不被支持",
            401 or 403 => "API Key 无效或没有权限（HTTP 401）",
            404 => "接入地址不对（HTTP 404）：地址或路径写错",
            429 => "请求被限流（HTTP 429）：稍后再试，或检查账户额度",
            >= 500 => "模型上游出错（HTTP 5xx）：不是本地配置问题，稍后再试",
            _ => $"模型服务返回 HTTP {status}",
        };

        var next = status switch
        {
            0 => $"确认地址和端口：当前是 {target}；本机模型要先启动服务（Ollama 默认 127.0.0.1:11434）。",
            400 => "点「测试连接」拉一次模型列表，用列表里的名字；本机小模型多半不支持工具调用，改用云端模型或换大模型。",
            401 or 403 => "重新复制 Key 再保存；本机 Ollama 不需要 Key，留空即可。",
            404 => $"确认路径：当前是 {target}，OpenAI 兼容服务通常要在末尾带 /v1。",
            429 => "等一会儿再发，或换用云端模型。",
            >= 500 => "稍后重试，或换用云端模型。",
            _ => "检查接入地址、Key 与模型名。",
        };

        var raw = detail.Length > 0 && status != 0 ? $"\n服务端原文：{detail}" : "";
        return $"{head}{raw}\n{next}";
    }

    /// <summary>从 <c>/models</c> 响应里取模型 id；兼容 <c>data[].id</c> 与 Ollama 的 <c>models[].name</c>。</summary>
    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ids;
            foreach (var key in new[] { "data", "models" })
            {
                if (!doc.RootElement.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    foreach (var nameKey in new[] { "id", "name", "model" })
                    {
                        if (!item.TryGetProperty(nameKey, out var v) || v.ValueKind != JsonValueKind.String) continue;
                        var id = v.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
                        break;
                    }
                }
            }
        }
        catch
        {
            // 非 JSON（网关返回 HTML 等）→ 当作没列出模型，交给调用方提示
        }
        return ids;
    }

    /// <summary>响应体摘要：优先取 error.message，其次原文前 200 字（压掉换行，避免撑爆气泡）。</summary>
    public static string Summarize(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return Cut(err.GetString());
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return Cut(m.GetString());
            }
            if (doc.RootElement.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String)
                return Cut(top.GetString());
        }
        catch
        {
            // 非 JSON：直接用原文
        }
        return Cut(text);
    }

    private static string Cut(string? text)
    {
        var s = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 200 ? s : s[..200] + "…";
    }

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]"
        || host.StartsWith("127.", StringComparison.Ordinal)
        || host.StartsWith("192.168.", StringComparison.Ordinal)
        || host.StartsWith("10.", StringComparison.Ordinal);

    /// <summary>从「还没补协议」的字符串里抠出主机名，用于判断本机地址。</summary>
    private static string CandidateHost(string url)
    {
        var slash = url.IndexOf('/');
        var hostPort = slash < 0 ? url : url[..slash];
        var colon = hostPort.LastIndexOf(':');
        return colon > 0 ? hostPort[..colon] : hostPort;
    }

    private static string PathOf(string url)
    {
        try { return (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "/").Trim('/'); }
        catch { return url.Trim('/'); }
    }
}
