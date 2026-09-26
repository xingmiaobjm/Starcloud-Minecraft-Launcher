// Starcloud 云端中转：Nova 的默认模型通道。
//
// 形状：Starcloud ID 登录时后端下发一枚 API Key（见 StarcloudIdService），
// 客户端拿它调自家网关 /v1/ai/*，网关再转发到上游模型并把用量记在账号上。
// 额度：每用户每周 100 万 token，服务端记账；客户端只负责「显示 + 降级 + 提示」，
// 不做本地计数（本地计数多端不一致，也挡不住刷）。
//
// 协议：与 OpenAI 兼容（网关负责差异），所以能直接复用 OpenAiSseClient 的解析。
// 服务端实现要求：docs/designs/2026-09-21-nova-gateway-server-spec.md
//
// 降级链里的位置（NovaService）：用户自填 Key → 本中转 → 本地规则兜底。

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>
/// 账号本周额度快照（服务端下发）。Limit/Used/Remaining 单位都是 token。
/// </summary>
public sealed record NovaQuota(
    [property: JsonPropertyName("limit")] long Limit,
    [property: JsonPropertyName("used")] long Used,
    [property: JsonPropertyName("remaining")] long Remaining,
    [property: JsonPropertyName("reset_at")] DateTimeOffset? ResetAt,
    [property: JsonPropertyName("plan")] string? Plan = null)
{
    /// <summary>已用比例（0–1），UI 进度条直接用。</summary>
    public double UsedRatio => Limit <= 0 ? 0 : Math.Clamp((double)Used / Limit, 0, 1);

    /// <summary>本周额度是否已用尽。</summary>
    public bool Exhausted => Remaining <= 0;

    /// <summary>剩余比例（0–1）。</summary>
    public double RemainingRatio => 1 - UsedRatio;
}

/// <summary>
/// 云端中转调用失败。<see cref="StatusCode"/> = 上游 HTTP 状态码（0 = 网络层失败），
/// <see cref="Quota"/> 非空时表示失败同时带回了额度信息（例如 429 超额）。
/// </summary>
public sealed class NovaGatewayException(string message, int statusCode, NovaQuota? quota = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public NovaQuota? Quota { get; } = quota;

    /// <summary>额度用尽：402（需付费）或 429 且响应头明确剩余为 0；单纯限流不算。</summary>
    public bool IsQuotaExceeded => StatusCode == 402 || (StatusCode == 429 && Quota is { Exhausted: true });
}

public static class NovaGatewayClient
{
    /// <summary>云端中转路径前缀（宿主 = StarcloudIdService.CloudBaseUrl）。</summary>
    public const string CloudBaseUrl = StarcloudIdService.CloudBaseUrl + "/v1/ai";

    /// <summary>逻辑模型名：网关按这个名字选上游模型，客户端的预设里不出现具体模型。</summary>
    public const string DefaultModel = "nova";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(180), // 流式长回复；单次请求的内部超时由调用方 ct 控制
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>额度刷新（任何一次调用带回新额度都会触发）→ UI 更新显示。</summary>
    public static event Action<NovaQuota>? QuotaChanged;

    /// <summary>
    /// 这个会话能不能走中转：开关打开 + 云端通道 + 有 Key。
    /// 测试通道的 Key 是本地模拟的假 Key（sk-test-*），打网关只会拿 401，直接跳过。
    /// </summary>
    public static bool CanUse(LauncherSettings settings, StarcloudSession? session) =>
        settings.NovaGatewayEnabled
        && session is { Channel: StarcloudIdService.CloudChannel }
        && !string.IsNullOrWhiteSpace(session.ApiKey);

    /// <summary>
    /// 云端额度要不要算 / 要不要显示：只有「云端模型确实是当前通道」才算，
    /// 即开关打开 + 云端会话 + **用户没有自接模型**。
    /// 接了第三方模型时 Nova 优先用它，就不该再发额度请求、也不该再显示云端额度。
    /// </summary>
    public static bool IsQuotaRelevant(LauncherSettings settings, StarcloudSession? session) =>
        CanUse(settings, session) && !HasOwnModel(settings);

    /// <summary>用户是否已经接入自己的模型（地址 + Key + 模型齐全）</summary>
    public static bool HasOwnModel(LauncherSettings settings) => settings.ReadyLlmProfile() is not null;

    // ── 额度查询：GET /v1/ai/quota ──

    /// <summary>拉取本周额度；失败返回 null（UI 显示「未知」，不打扰用户）。</summary>
    public static async Task<NovaQuota?> FetchQuotaAsync(string apiKey, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{CloudBaseUrl}/quota");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var quota = ParseQuotaJson(json);
            if (quota is not null) QuotaChanged?.Invoke(quota);
            return quota;
        }
        catch
        {
            return null; // 网络失败不抛：额度显示是锦上添花，不能挡住对话
        }
    }

    // ── 文本对话（一次性返回）：POST /v1/ai/chat/completions ──

    /// <summary>非流式对话；返回正文。失败抛 <see cref="NovaGatewayException"/>。</summary>
    public static async Task<string> ChatAsync(
        string apiKey, IReadOnlyList<object> messages, string model = DefaultModel,
        int? maxTokens = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{CloudBaseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.TryAddWithoutValidation("X-Starcloud-Client", "launcher");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { model, messages, max_tokens = maxTokens }, JsonOpts),
            Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        PublishQuota(resp);
        if (!resp.IsSuccessStatusCode)
            throw await FailureAsync(resp, ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    // ── 流式对话（含工具调用）：POST /v1/ai/chat/completions stream=true ──

    /// <summary>
    /// 流式对话：返回 SSE 事件流（文本增量 + 工具调用增量）。
    /// 额度从响应头先读一次（网关每次都会带），随后与上游响应一起流式返回。
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> StreamChatAsync(
        string apiKey, IReadOnlyList<object> messages, object? tools = null,
        string model = DefaultModel, int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{CloudBaseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.TryAddWithoutValidation("X-Starcloud-Client", "launcher");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { model, messages, tools, max_tokens = maxTokens, stream = true }, JsonOpts),
            Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        PublishQuota(resp);
        if (!resp.IsSuccessStatusCode)
            throw await FailureAsync(resp, ct);

        await foreach (var evt in OpenAiSseClient.ParseSseResponseAsync(resp, ct))
            yield return evt;
    }

    /// <summary>
    /// 流式对话的「纯文本」投影：只吐正文增量（不带工具的纯问答走这个）。
    /// </summary>
    public static async IAsyncEnumerable<string> StreamDeltasAsync(
        string apiKey, IReadOnlyList<object> messages, int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in StreamChatAsync(apiKey, messages, tools: null, DefaultModel, maxTokens, ct))
            if (evt.ContentDelta is { Length: > 0 } delta)
                yield return delta;
    }

    // ── 额度解析（响应头优先：不额外发请求） ──

    /// <summary>
    /// 从响应头解析额度：X-Quota-Limit / X-Quota-Used / X-Quota-Remaining / X-Quota-Reset。
    /// 头缺失或不合法 → null（网关可以不带头，客户端就退回定时查询）。
    /// </summary>
    internal static NovaQuota? ParseQuotaHeaders(HttpResponseMessage resp)
    {
        var limit = HeaderLong(resp, "X-Quota-Limit");
        var used = HeaderLong(resp, "X-Quota-Used");
        var remaining = HeaderLong(resp, "X-Quota-Remaining");
        if (limit is null && used is null && remaining is null) return null;

        var l = limit ?? 0;
        var u = used ?? (remaining is { } r && l > 0 ? Math.Max(0, l - r) : 0);
        var rem = remaining ?? Math.Max(0, l - u);
        return new NovaQuota(l, u, rem, HeaderDate(resp, "X-Quota-Reset"), null);
    }

    /// <summary>解析 /quota 的 JSON 响应体。</summary>
    internal static NovaQuota? ParseQuotaJson(string json)
    {
        try
        {
            var quota = JsonSerializer.Deserialize<NovaQuota>(json, JsonOpts);
            return quota is { Limit: > 0 } ? quota : null;
        }
        catch
        {
            return null;
        }
    }

    private static void PublishQuota(HttpResponseMessage resp)
    {
        if (ParseQuotaHeaders(resp) is { } q) QuotaChanged?.Invoke(q);
    }

    private static long? HeaderLong(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v)
        && long.TryParse(v.FirstOrDefault(), out var parsed) ? parsed : null;

    private static DateTimeOffset? HeaderDate(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v)
        && DateTimeOffset.TryParse(v.FirstOrDefault(), out var parsed) ? parsed : null;

    internal static async Task<NovaGatewayException> FailureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        var quota = ParseQuotaHeaders(resp);
        string body;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = ""; }
        var detail = Trim(body);
        var message = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "云端模型未授权：Starcloud ID 登录已失效，请在 Nova 页重新登录。",
            HttpStatusCode.PaymentRequired => QuotaExhaustedText(quota),
            HttpStatusCode.TooManyRequests => quota is { Exhausted: true }
                ? QuotaExhaustedText(quota)
                : "云端模型请求过于频繁，请稍后再试（额度下周自动恢复）。",
            _ => $"云端模型暂时不可用（HTTP {status}）{(detail.Length > 0 ? "：" + detail : "")}",
        };
        return new NovaGatewayException(message, status, quota);
    }

    /// <summary>额度用尽的统一说法（能带上数值就带，带不上也不能说成 0/0）。</summary>
    private static string QuotaExhaustedText(NovaQuota? quota) =>
        quota is { Limit: > 0 }
            ? $"本周云端额度已用尽（已用 {FormatTokens(quota.Used)} / {FormatTokens(quota.Limit)} token）。{ResetText(quota.ResetAt)}"
            : "本周云端额度已用尽。" + ResetText(null);

    private static string Trim(string s) => s.Length <= 160 ? s.ReplaceLineEndings(" ") : s[..160].ReplaceLineEndings(" ") + "…";

    // ── 额度文案（额度卡与失败提示共用同一套口径，避免两处各写一份） ──

    /// <summary>重置时间文案：「额度将在 9月28日 00:00 重置」；没有时间时给「每周一重置」。</summary>
    public static string ResetText(DateTimeOffset? resetAt) =>
        resetAt is { } t ? $"额度将在 {t.ToLocalTime():M月d日 HH:mm} 重置。" : "额度每周一重置。";

    /// <summary>进度文案：「已用 12.3 万 / 100 万 token」。</summary>
    public static string UsedText(NovaQuota q) =>
        $"已用 {FormatTokens(q.Used)} / {FormatTokens(q.Limit)} token";

    /// <summary>剩余文案：「剩余 87.7 万」；耗尽时给「本周已用尽」。</summary>
    public static string RemainingText(NovaQuota q) =>
        q.Exhausted ? "本周已用尽" : $"剩余 {FormatTokens(q.Remaining)}";

    /// <summary>额度快照的补充说明（先显示缓存数值时用得上）。</summary>
    public static string CheckedText(DateTimeOffset? checkedAt) =>
        checkedAt is { } t ? $"更新于 {t.ToLocalTime():M月d日 HH:mm}" : "";

    /// <summary>token 数的粗粒度可读化（1,234,567 → 123.5 万）。</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 100_000_000 => $"{tokens / 100_000_000.0:0.#} 亿",
        >= 10_000 => $"{tokens / 10_000.0:0.#} 万",
        _ => tokens.ToString("N0"),
    };
}
