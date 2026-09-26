// Nova「自接 / 本地模型」接入测试（离线，裸 TcpListener 假服务端）：
//   ① 请求体必须用 OpenAI 小写字段名（role / content / tool_call_id）。
//      历史轮曾写成匿名类型 new { t.Role, t.Content } → 序列化成 "Role" / "Content"，
//      真实服务端（DeepSeek / Ollama / vLLM / LM Studio）一律按 400 拒绝：
//      症状就是「第一句能聊，第二句开始报 400」。这个用例是那条 400 的回归护栏。
//   ② 空内容的历史轮不进请求（部分服务端对空 content 直接 400）。
//   ③ 接入地址归一化：省略 /v1、误粘 /chat/completions、漏写 http:// 都要能用。
//   ④ 上游 4xx 必须带着状态码与响应体上抛，UI 才有东西可解释。

using System.Net;
using System.Net.Sockets;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class NovaLlmRequestTests
{
    private static readonly IReadOnlyList<NovaChatTurn> History =
    [
        new("user", "第一轮提问"),
        new("assistant", "第一轮回答"),
    ];

    // ── ① 字段名 ────────────────────────────────────────────────

    [Fact]
    public void 请求体用OpenAI小写字段名_历史轮不能是PascalCase()
    {
        var body = OpenAiSseClient.BuildChatRequestJson(
            "qwen3", NovaService.BuildMessages("第二轮提问", History),
            tools: null, maxTokens: null, stream: true);

        Assert.Contains("\"role\":\"user\"", body);
        Assert.Contains("\"content\":\"第一轮提问\"", body);
        Assert.DoesNotContain("\"Role\"", body);
        Assert.DoesNotContain("\"Content\"", body);
    }

    [Fact]
    public void 工具消息也用小写字段名()
    {
        var messages = new List<object>
        {
            new { role = "assistant", content = (string?)null },
            new { role = "tool", tool_call_id = "call_1", content = "结果" },
        };
        var body = OpenAiSseClient.BuildChatRequestJson("m", messages, tools: null, maxTokens: 128, stream: false);

        Assert.Contains("\"tool_call_id\":\"call_1\"", body);
        Assert.Contains("\"max_tokens\":128", body);
        Assert.DoesNotContain("\"ToolCallId\"", body);
        Assert.DoesNotContain("\"MaxTokens\"", body);
        Assert.DoesNotContain("\"stream\"", body); // 非流式不写 stream 字段
    }

    // ── ② 空内容历史轮 ──────────────────────────────────────────

    [Fact]
    public void 空内容的历史轮不进请求()
    {
        var history = new List<NovaChatTurn>
        {
            new("user", "第一次提问"),
            new("assistant", "   "),   // 计划卡/中断轮会留下空正文
            new("user", "第二次提问"),
        };
        var body = OpenAiSseClient.BuildChatRequestJson(
            "m", NovaService.BuildMessages("第三次提问", history), null, null, stream: true);

        Assert.DoesNotContain("\"content\":\"   \"", body);
        Assert.Contains("第一次提问", body);
        Assert.Contains("第二次提问", body);
    }

    // ── ③ 地址归一化 ────────────────────────────────────────────

    [Theory]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1")]
    [InlineData("127.0.0.1:11434", "http://127.0.0.1:11434/v1")]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434/v1")]
    [InlineData("http://127.0.0.1:1234/v1/chat/completions", "http://127.0.0.1:1234/v1")]
    [InlineData("  https://open.bigmodel.cn/api/paas/v4  ", "https://open.bigmodel.cn/api/paas/v4")]
    public void 接入地址归一化(string raw, string expected) =>
        Assert.Equal(expected, LlmEndpoint.NormalizeBaseUrl(raw));

    [Fact]
    public void 归一化后的对话地址()
    {
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions",
            LlmEndpoint.ChatUrl("127.0.0.1:11434"));
        Assert.Equal("http://127.0.0.1:11434/v1/models", LlmEndpoint.ModelsUrl("127.0.0.1:11434"));
    }

    // ── ④ 端到端：严格服务端（PascalCase 即 400） ────────────────

    [Fact]
    public async Task 第二轮对话_严格服务端不再回400()
    {
        using var server = new FakeOpenAiServer();
        using var http = DirectHttp();

        var text = new StringBuilder();
        await foreach (var evt in OpenAiSseClient.StreamChatAsync(
            http, server.ChatUrl, "sk-test", "qwen3",
            NovaService.BuildMessages("第二轮提问", History)))
            if (evt.ContentDelta is { Length: > 0 } d) text.Append(d);

        Assert.Equal("pong", text.ToString());
        Assert.Equal(1, server.Requests);
        Assert.DoesNotContain("\"Role\"", server.LastBody);
        Assert.DoesNotContain("\"Content\"", server.LastBody);
    }

    /// <summary>
    /// SSE 解析（纯解析，不过网络）：`data: {...}` 必须取出完整 JSON。
    /// 曾经取的是 line[4..]（下标 4 恒为 ':'），JSON 解析次次失败被吞掉 →
    /// 所有 OpenAI 兼容服务端的流式回复都会变成「一个字都没有」。
    /// </summary>
    [Theory]
    [InlineData("data: ")]   // 标准写法（冒号后有空格）
    [InlineData("data:")]    // 少数网关不写空格
    public async Task SSE事件能被解析出正文(string prefix)
    {
        var sse = prefix + "{\"id\":\"1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"," +
                  "\"content\":\"pong\"},\"finish_reason\":null}]}\n\n" +
                  prefix + "[DONE]\n\n";
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };

        var got = new StringBuilder();
        await foreach (var evt in OpenAiSseClient.ParseSseResponseAsync(resp, CancellationToken.None))
            if (evt.ContentDelta is { Length: > 0 } d) got.Append(d);

        Assert.Equal("pong", got.ToString());
    }

    [Fact]
    public async Task 上游400_异常带状态码与响应体()
    {
        using var server = new FakeOpenAiServer(alwaysBadRequest: true);
        using var http = DirectHttp();

        var ex = await Assert.ThrowsAsync<LlmStreamException>(async () =>
        {
            await foreach (var _ in OpenAiSseClient.StreamChatAsync(
                http, server.ChatUrl, "sk-test", "不存在的模型",
                NovaService.BuildMessages("你好", null)))
            {
            }
        });

        Assert.Contains("400", ex.Message);
        Assert.Contains("Model Not Exist", ex.Message);
    }

    [Fact]
    public async Task 探测服务端能列出模型()
    {
        using var server = new FakeOpenAiServer(models: ["qwen3:8b", "llama3.1"]);
        using var http = DirectHttp();

        var result = await LlmEndpoint.ProbeAsync(http, server.BaseUrl, "sk-test");

        Assert.True(result.Ok);
        Assert.Equal(new[] { "qwen3:8b", "llama3.1" }, result.Models);
    }

    [Fact]
    public async Task 探测连不上时给出可执行提示()
    {
        using var http = DirectHttp();
        var freePort = FakeOpenAiServer.FreePort();

        var result = await LlmEndpoint.ProbeAsync(http, $"http://127.0.0.1:{freePort}", "sk-test");

        Assert.False(result.Ok);
        Assert.Equal(0, result.StatusCode);
        Assert.Contains("连不上", result.Message);
    }

    // ── 失败说明：用户看得懂 + 有下一步 ──────────────────────────

    [Theory]
    [InlineData(400, "{\"error\":{\"message\":\"Model Not Exist\"}}", "模型名")]
    [InlineData(401, "{\"error\":{\"message\":\"invalid api key\"}}", "Key")]
    [InlineData(404, "not found", "地址")]
    [InlineData(429, "rate limit", "限流")]
    [InlineData(500, "", "上游")]
    public void 失败说明带状态码和下一步(int status, string body, string keyword) =>
        Assert.Contains(keyword, LlmEndpoint.Explain(status, body, "http://127.0.0.1:11434/v1"));

    // ── 测试基础设施 ────────────────────────────────────────────

    /// <summary>不依赖系统代理的直连 HttpClient（本机可能有回环代理截走请求）。</summary>
    private static HttpClient DirectHttp() =>
        new(new HttpClientHandler { UseProxy = false, Proxy = null });

    /// <summary>
    /// 假 OpenAI 兼容服务端：收到 PascalCase 字段名（"Role" / "Content"）时按真实服务端那样回 400。
    /// 用裸 TcpListener 而不是 HttpListener：后者非管理员下需要 URL ACL。
    /// </summary>
    private sealed class FakeOpenAiServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _alwaysBadRequest;
        private readonly IReadOnlyList<string> _models;
        private int _requests;
        private string _lastBody = "";

        public FakeOpenAiServer(bool alwaysBadRequest = false, IReadOnlyList<string>? models = null)
        {
            _alwaysBadRequest = alwaysBadRequest;
            _models = models ?? [];
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
        public string ChatUrl => $"{BaseUrl}/chat/completions";
        public int Requests => Volatile.Read(ref _requests);
        public string LastBody => _lastBody;

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();

                    // 按字节读请求（不能用 StreamReader 按「字符数」读正文：
                    // 正文里有中文时 UTF-8 字节数 > 字符数，会一直等不到足够的数据而挂住）
                    var headerBytes = new List<byte>(1024);
                    var one = new byte[1];
                    while (headerBytes.Count < 64 * 1024)
                    {
                        if (await stream.ReadAsync(one) <= 0) return;
                        headerBytes.Add(one[0]);
                        if (headerBytes.Count >= 4 &&
                            headerBytes[^4] == 13 && headerBytes[^3] == 10 &&
                            headerBytes[^2] == 13 && headerBytes[^1] == 10)
                            break;
                    }
                    var headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
                    var requestLine = headerText.Split("\r\n")[0];

                    var contentLength = 0;
                    foreach (var line in headerText.Split("\r\n"))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line[15..].Trim(), out contentLength);

                    var bodyBytes = new byte[contentLength];
                    var read = 0;
                    while (read < contentLength)
                    {
                        var n = await stream.ReadAsync(bodyBytes.AsMemory(read, contentLength - read));
                        if (n <= 0) break;
                        read += n;
                    }
                    var body = Encoding.UTF8.GetString(bodyBytes, 0, read);
                    if (read > 0) _lastBody = body;

                    Interlocked.Increment(ref _requests);

                    if (requestLine.StartsWith("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        var list = string.Join(",", _models.Select(m => $"{{\"id\":\"{m}\",\"object\":\"model\"}}"));
                        await ReplyAsync(stream, 200, "application/json", $"{{\"object\":\"list\",\"data\":[{list}]}}");
                        return;
                    }

                    // 真实服务端的判定：OpenAI 协议字段名大小写敏感，PascalCase 直接 400
                    if (_alwaysBadRequest || body.Contains("\"Role\"", StringComparison.Ordinal))
                    {
                        await ReplyAsync(stream, 400, "application/json",
                            "{\"error\":{\"message\":\"Model Not Exist\",\"type\":\"invalid_request_error\"}}");
                        return;
                    }

                    await ReplyAsync(stream, 200, "text/event-stream",
                        "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"m\"," +
                        "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"pong\"}," +
                        "\"finish_reason\":null}]}\n\ndata: [DONE]\n\n");
                }
                catch
                {
                    // 客户端提前断开：忽略
                }
            }
        }

        private async Task ReplyAsync(NetworkStream stream, int status, string contentType, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\n" +
                       $"Content-Type: {contentType}\r\n" +
                       $"Content-Length: {payload.Length}\r\n" +
                       "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
    }
}
