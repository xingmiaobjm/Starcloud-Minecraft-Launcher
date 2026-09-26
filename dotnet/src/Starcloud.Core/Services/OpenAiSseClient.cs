// OpenAI 兼容 SSE 流式客户端：文本增量 + 工具调用增量累积。
// 服务两条通道：
//   1. 用户自填 Key（OpenAI 兼容 /chat/completions，stream:true）
//   2. StarCloud 云端 /v1/chat（服务端向 OpenAI chat.completion.chunk 兼容格式改造；
//      改造完成前回退解析顶层 delta/text 字段，旧格式不炸）
// 设计：docs 计划 2026-09-14-nova-agent-and-quilt.md 阶段二。

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>一个流式工具调用增量（按 index 累积 id/name/arguments）</summary>
public sealed record SseToolCallDelta(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] string? ArgumentsDelta);

/// <summary>一个 SSE 流事件：文本增量 或 工具调用增量 或 结束标记（三者至少其一）</summary>
public sealed record SseEvent(
    string? ContentDelta,
    IReadOnlyList<SseToolCallDelta>? ToolCallDeltas,
    string? FinishReason)
{
    public static SseEvent Text(string delta) => new(delta, null, null);
    public static SseEvent Done(string? finishReason) => new(null, null, finishReason);
}

/// <summary>累积完成的工具调用（id + name + 完整 arguments JSON 字符串）</summary>
public sealed record CompletedToolCall(string Id, string Name, string Arguments);

/// <summary>
/// 一条对话消息（OpenAI 协议字段名写死在小写）。
/// 之前历史轮是用匿名类型 <c>new { t.Role, t.Content }</c> 拼的，序列化出来是 "Role"/"Content"，
/// 真实服务端大小写敏感 → 一律 400；改成显式类型后 builder 不会再漂。
/// </summary>
public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<ChatToolCall>? ToolCalls = null);

/// <summary>assistant 轮里的一次工具调用（OpenAI 协议形状）</summary>
public sealed record ChatToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ChatToolCallFunction Function);

public sealed record ChatToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

/// <summary>
/// 按 index 累积流式 tool_calls 增量：OpenAI 会把一次调用的 arguments 分多片发送，
/// 必须按 index 拼接后才是完整 JSON。
/// </summary>
public sealed class ToolCallAccumulator
{
    private readonly Dictionary<int, (string? Id, string Name, StringBuilder Args)> _parts = new();

    public void Add(IReadOnlyList<SseToolCallDelta> deltas)
    {
        foreach (var d in deltas)
        {
            if (!_parts.TryGetValue(d.Index, out var part))
                part = (d.Id, d.Name ?? "", new StringBuilder());
            part.Id ??= d.Id;
            if (d.Name is { Length: > 0 } n) part.Name = n; // 某些网关整段重发 name
            if (d.ArgumentsDelta is { Length: > 0 } a) part.Args.Append(a);
            _parts[d.Index] = part;
        }
    }

    /// <summary>取当前全部完成调用；arguments 为空时补 "{}"</summary>
    public IReadOnlyList<CompletedToolCall> Complete() =>
        _parts.OrderBy(p => p.Key)
              .Select(p => new CompletedToolCall(
                  p.Value.Id ?? $"call_{p.Key}",
                  p.Value.Name,
                  p.Value.Args.Length == 0 ? "{}" : p.Value.Args.ToString()))
              .ToList();

    public bool IsEmpty => _parts.Count == 0;
}

/// <summary>
/// 自接模型的请求失败：message 含状态码与响应片段，另附结构化字段供 UI 解释。
/// <see cref="StatusCode"/> = 0 表示请求没连上（地址 / 端口 / 服务未启动）。
/// </summary>
public sealed class LlmStreamException(string message, int statusCode = 0, string? responseBody = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody ?? "";
}

public static class OpenAiSseClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 兜底：即使以后有人塞进 PascalCase 的匿名对象，也会被压成 snake_case（Content → content），
        // 不再变成服务端的 400。ChatMessage 上的 JsonPropertyName 优先于此策略。
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 唯一的对话请求体构造入口：<c>{model, messages, tools?, max_tokens?, stream?}</c>。
    /// 三条通道（自填 Key 流式 / 非流式 / 云端中转）共用，字段名只在这里定义一次。
    /// </summary>
    public static string BuildChatRequestJson(
        string model, IReadOnlyList<object> messages, object? tools = null,
        int? maxTokens = null, bool stream = true) =>
        JsonSerializer.Serialize(new
        {
            model,
            messages,
            tools,
            max_tokens = maxTokens,
            stream = stream ? true : (bool?)null,
        }, JsonOpts);

    /// <summary>
    /// OpenAI 兼容流式对话：<c>POST {url}</c>，body = {model, messages, tools?, stream:true}。
    /// 失败抛 <see cref="LlmStreamException"/>（message 含状态码与响应片段）。
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> StreamChatAsync(
        HttpClient http, string url, string apiKey, string model,
        IReadOnlyList<object> messages, object? tools = null,
        int? maxTokens = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var body = BuildChatRequestJson(model, messages, tools, maxTokens, stream: true);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new LlmStreamException(
                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(err)}",
                (int)resp.StatusCode, err);
        }
        await foreach (var evt in ParseSseResponseAsync(resp, ct))
            yield return evt;
    }

    /// <summary>
    /// 解析 SSE 响应为事件流。非 event-stream 响应按完整 JSON 兜底：
    /// 先取 choices[0].message.content，再回退顶层 text/delta 字段（云端旧格式）。
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ParseSseResponseAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType != "text/event-stream")
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var text = ExtractFallbackText(doc.RootElement);
            if (!string.IsNullOrEmpty(text)) yield return SseEvent.Text(text);
            yield return SseEvent.Done(null);
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:")) continue;
            // "data: {...}" → 取下标 6；"data:{...}" → 取下标 5。
            // 原来写的是 line[4] == ' '（下标 4 恒为 ':'），于是 data 一直带着前导冒号，
            // JSON 解析次次失败被 catch 吞掉 —— 流式通道实际一条事件都没产出过。
            var data = (line.StartsWith("data: ", StringComparison.Ordinal) ? line[6..] : line[5..]).Trim();
            if (data == "[DONE]") yield break;
            SseEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                evt = ParseChunk(doc.RootElement);
            }
            catch { /* 忽略格式错误的行 */ }
            if (evt is not null) yield return evt;
        }
    }

    /// <summary>解析单个 chat.completion.chunk；云端旧格式（顶层 delta/text）回退为纯文本事件</summary>
    public static SseEvent? ParseChunk(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];

            // 结束标记
            if (first.TryGetProperty("finish_reason", out var fr) &&
                fr.ValueKind == JsonValueKind.String)
                return SseEvent.Done(fr.GetString());

            string? content = null;
            if (first.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var dc) &&
                    dc.ValueKind == JsonValueKind.String)
                    content = dc.GetString();

                if (delta.TryGetProperty("tool_calls", out var tcs) &&
                    tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
                {
                    var calls = new List<SseToolCallDelta>();
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var fn = tc.TryGetProperty("function", out var f) ? f : default;
                        calls.Add(new SseToolCallDelta(
                            tc.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0,
                            tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                                ? id.GetString() : null,
                            fn.ValueKind == JsonValueKind.Object &&
                              fn.TryGetProperty("name", out var n) &&
                              n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                            fn.ValueKind == JsonValueKind.Object &&
                              fn.TryGetProperty("arguments", out var a) &&
                              a.ValueKind == JsonValueKind.String ? a.GetString() : null));
                    }
                    return new SseEvent(content, calls, null);
                }
            }
            else if (first.TryGetProperty("message", out var msg) &&
                     msg.TryGetProperty("content", out var mc) &&
                     mc.ValueKind == JsonValueKind.String)
            {
                content = mc.GetString(); // 非流式响应混入（服务端未开流）
            }
            return content is { Length: > 0 } ? SseEvent.Text(content) : null;
        }

        // 云端旧格式回退：顶层 delta/text 字符串
        var fallback = ExtractFallbackText(root);
        return fallback is { Length: > 0 } ? SseEvent.Text(fallback) : null;
    }

    private static string? ExtractFallbackText(JsonElement root)
    {
        if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
            return d.GetString();
        if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString();
        return null;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
