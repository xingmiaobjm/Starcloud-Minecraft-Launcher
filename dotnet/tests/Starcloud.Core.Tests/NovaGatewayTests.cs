// Nova 云端中转（NovaGatewayClient）离线测试：额度解析、额度文案、失败语义、通道可用性。
//
// 这里只测「不联网也能确定」的部分（解析 / 判定 / 文案），因为额度是**服务端权威**的：
// 真去请求网关只会测到网络，测不到逻辑。联调（真打网关）请用 docs/designs/2026-09-21-nova-gateway-server-spec.md
// 里的验收清单。

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class NovaGatewayTests
{
    // ── 通道可用性：开关 + 云端通道 + 有 Key，三者缺一不可 ──

    private static StarcloudSession Session(string channel, string key = "sk-live-abc") =>
        new("sc-1", "tester", key, channel, DateTime.Now);

    // ── 云端额度只在「云端模型是当前通道」时才成立 ──

    [Fact]
    public void 接了第三方模型就不再算云端额度()
    {
        var settings = new LauncherSettings { NovaGatewayEnabled = true };
        settings.NovaLlmProfiles.Add(new NovaLlmProfile
        {
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "sk-own-key",
            Model = "deepseek-chat",
        });
        var session = Session(StarcloudIdService.CloudChannel);

        Assert.True(NovaGatewayClient.CanUse(settings, session));      // 通道本身可用
        Assert.True(NovaGatewayClient.HasOwnModel(settings));
        Assert.False(NovaGatewayClient.IsQuotaRelevant(settings, session)); // 但不该再发额度请求 / 显示额度
    }

    [Fact]
    public void 没接自己的模型时云端额度才算()
    {
        var settings = new LauncherSettings { NovaGatewayEnabled = true };

        Assert.False(NovaGatewayClient.HasOwnModel(settings));
        Assert.True(NovaGatewayClient.IsQuotaRelevant(settings, Session(StarcloudIdService.CloudChannel)));
    }

    [Fact]
    public void 本地模型没填Key也算接了模型()
    {
        // 本地推理服务（LM Studio / Ollama）本来就不要 Key：
        // 以前「没 Key = 没接入」，于是自接的本地模型被跳过、请求落到云端，用户只看到云端的 400。
        var settings = new LauncherSettings { NovaGatewayEnabled = true };
        settings.NovaLlmProfiles.Add(new NovaLlmProfile
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            Model = "qwen3:8b",
        });

        Assert.True(NovaGatewayClient.HasOwnModel(settings));
        Assert.False(NovaGatewayClient.IsQuotaRelevant(settings, Session(StarcloudIdService.CloudChannel)));
    }

    [Fact]
    public void 没地址或没模型名都不算接入()
    {
        var settings = new LauncherSettings { NovaGatewayEnabled = true };
        settings.NovaLlmProfiles.Add(new NovaLlmProfile { BaseUrl = "http://127.0.0.1:1234/v1" });

        Assert.False(NovaGatewayClient.HasOwnModel(settings));
        Assert.True(NovaGatewayClient.IsQuotaRelevant(settings, Session(StarcloudIdService.CloudChannel)));
    }

    [Fact]
    public void 第三方请求体里没有云端额度字段()
    {
        var body = OpenAiSseClient.BuildChatRequestJson(
            "deepseek-chat", NovaService.BuildMessages("你好", null),
            tools: null, maxTokens: null, stream: true);

        using var doc = JsonDocument.Parse(body);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "model", "messages", "stream" }, keys);
        Assert.DoesNotContain("quota", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-quota", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 云端通道且开关打开时才可用()
    {
        var settings = new LauncherSettings { NovaGatewayEnabled = true };
        Assert.True(NovaGatewayClient.CanUse(settings, Session(StarcloudIdService.CloudChannel)));
    }

    [Fact]
    public void 关掉开关后不走云端()
    {
        var settings = new LauncherSettings { NovaGatewayEnabled = false };
        Assert.False(NovaGatewayClient.CanUse(settings, Session(StarcloudIdService.CloudChannel)));
    }

    [Fact]
    public void 未登录与空Key都不可用()
    {
        var settings = new LauncherSettings { NovaGatewayEnabled = true };
        Assert.False(NovaGatewayClient.CanUse(settings, null));
        Assert.False(NovaGatewayClient.CanUse(settings, Session(StarcloudIdService.CloudChannel, "  ")));
    }

    // ── 额度对象：比例与耗尽判定 ──

    [Fact]
    public void 额度比例与耗尽判定()
    {
        var half = new NovaQuota(1_000_000, 250_000, 750_000, null);
        Assert.Equal(0.25, half.UsedRatio, 3);
        Assert.Equal(0.75, half.RemainingRatio, 3);
        Assert.False(half.Exhausted);

        var empty = new NovaQuota(1_000_000, 1_000_000, 0, null);
        Assert.True(empty.Exhausted);
        Assert.Equal(1, empty.UsedRatio, 3);
    }

    [Fact]
    public void 超出限额的用量比例被夹在1以内()
    {
        // 服务端多计一次（例如流式中途断线）也不该让进度条越界
        var over = new NovaQuota(100, 180, 0, null);
        Assert.Equal(1, over.UsedRatio, 3);
        Assert.Equal(0, over.RemainingRatio, 3);
    }

    [Fact]
    public void 没有限额时不按比例计算()
    {
        var none = new NovaQuota(0, 10, 0, null);
        Assert.Equal(0, none.UsedRatio, 3);
    }

    // ── token 数值的人话写法（界面上不出现 1234567 这样的长数字） ──

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(12_345, "1.2 万")]
    [InlineData(1_000_000, "100 万")]
    [InlineData(123_456_789, "1.2 亿")]
    public void token数值格式化(long tokens, string expected) =>
        Assert.Equal(expected, NovaGatewayClient.FormatTokens(tokens));

    [Fact]
    public void 额度文案包含已用总量与剩余()
    {
        var q = new NovaQuota(1_000_000, 123_450, 876_550, null);
        Assert.Equal("已用 12.3 万 / 100 万 token", NovaGatewayClient.UsedText(q));
        Assert.Equal("剩余 87.7 万", NovaGatewayClient.RemainingText(q));
        Assert.Equal("本周已用尽", NovaGatewayClient.RemainingText(q with { Used = 1_000_000, Remaining = 0 }));
    }

    [Fact]
    public void 重置时间文案带日期不带时说每周一()
    {
        var reset = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.FromHours(8));
        Assert.Contains("9月28日", NovaGatewayClient.ResetText(reset));
        Assert.Contains("每周一", NovaGatewayClient.ResetText(null));
    }

    // ── /v1/ai/quota 响应解析 ──

    [Fact]
    public void 解析额度JSON()
    {
        const string json = """
        { "limit": 1000000, "used": 250000, "remaining": 750000,
          "reset_at": "2026-09-28T00:00:00+08:00", "plan": "weekly" }
        """;
        var q = NovaGatewayClient.ParseQuotaJson(json);
        Assert.NotNull(q);
        Assert.Equal(1_000_000, q!.Limit);
        Assert.Equal(250_000, q.Used);
        Assert.Equal(750_000, q.Remaining);
        Assert.Equal("weekly", q.Plan);
        Assert.Equal(2026, q.ResetAt!.Value.Year);
    }

    [Theory]
    [InlineData("{}")]                              // 没有任何字段
    [InlineData("{\"limit\": 0, \"used\": 0}")]     // 限额为 0 = 没开通
    [InlineData("not json at all")]
    [InlineData("")]
    public void 解析不出额度时返回空而不是抛异常(string json) =>
        Assert.Null(NovaGatewayClient.ParseQuotaJson(json));

    // ── 响应头额度：客户端不额外发请求就能显示用量 ──

    private static HttpResponseMessage Response(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var resp = new HttpResponseMessage(status);
        foreach (var (name, value) in headers) resp.Headers.TryAddWithoutValidation(name, value);
        return resp;
    }

    [Fact]
    public void 从响应头解析额度()
    {
        using var resp = Response(HttpStatusCode.OK,
            ("X-Quota-Limit", "1000000"),
            ("X-Quota-Used", "400000"),
            ("X-Quota-Remaining", "600000"),
            ("X-Quota-Reset", "2026-09-28T00:00:00+08:00"));

        var q = NovaGatewayClient.ParseQuotaHeaders(resp);
        Assert.NotNull(q);
        Assert.Equal(1_000_000, q!.Limit);
        Assert.Equal(400_000, q.Used);
        Assert.Equal(600_000, q.Remaining);
        Assert.NotNull(q.ResetAt);
    }

    [Fact]
    public void 只有剩余量时反推已用()
    {
        using var resp = Response(HttpStatusCode.OK,
            ("X-Quota-Limit", "1000000"),
            ("X-Quota-Remaining", "250000"));

        var q = NovaGatewayClient.ParseQuotaHeaders(resp);
        Assert.NotNull(q);
        Assert.Equal(750_000, q!.Used);
    }

    [Fact]
    public void 没有额度头时解析为空()
    {
        using var resp = Response(HttpStatusCode.OK);
        Assert.Null(NovaGatewayClient.ParseQuotaHeaders(resp));
    }

    // ── 失败语义：客户端据此决定「讲清楚」还是「静默降级」 ──

    [Fact]
    public async Task 未授权给出去登录的提示()
    {
        using var resp = Response(HttpStatusCode.Unauthorized);
        var ex = await NovaGatewayClient.FailureAsync(resp, default);
        Assert.Equal(401, ex.StatusCode);
        Assert.False(ex.IsQuotaExceeded);
        Assert.Contains("重新登录", ex.Message);
    }

    [Fact]
    public async Task 额度用尽时带上用量与重置时间()
    {
        using var resp = Response(HttpStatusCode.PaymentRequired,
            ("X-Quota-Limit", "1000000"),
            ("X-Quota-Used", "1000000"),
            ("X-Quota-Remaining", "0"));

        var ex = await NovaGatewayClient.FailureAsync(resp, default);
        Assert.True(ex.IsQuotaExceeded);
        Assert.Contains("额度已用尽", ex.Message);
        Assert.Contains("100 万", ex.Message);
        Assert.DoesNotContain("0 / 0", ex.Message);
        Assert.NotNull(ex.Quota);
    }

    [Fact]
    public async Task 额度用尽但没带数值时不报0_0()
    {
        using var resp = Response(HttpStatusCode.PaymentRequired);
        var ex = await NovaGatewayClient.FailureAsync(resp, default);
        Assert.True(ex.IsQuotaExceeded);
        Assert.DoesNotContain("0 / 0", ex.Message);
        Assert.Contains("每周一", ex.Message);
    }

    [Fact]
    public async Task 单纯限流不算额度用尽()
    {
        using var resp = Response(HttpStatusCode.TooManyRequests,
            ("X-Quota-Limit", "1000000"),
            ("X-Quota-Used", "100000"),
            ("X-Quota-Remaining", "900000"));

        var ex = await NovaGatewayClient.FailureAsync(resp, default);
        Assert.False(ex.IsQuotaExceeded);
        Assert.Contains("频繁", ex.Message);
    }

    [Fact]
    public async Task 上游故障带上状态码与错误体摘要()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream timeout", Encoding.UTF8, "text/plain"),
        };
        var ex = await NovaGatewayClient.FailureAsync(resp, default);
        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("502", ex.Message);
        Assert.Contains("upstream timeout", ex.Message);
    }

    // ── 客户端与网关的固定约定（改名即破坏兼容，钉在这里） ──

    [Fact]
    public void 中转地址与逻辑模型名的约定()
    {
        Assert.Equal(StarcloudIdService.AiBaseUrl + "/v1/ai", NovaGatewayClient.CloudBaseUrl);
        Assert.StartsWith("https://", NovaGatewayClient.CloudBaseUrl); // 明文 http 不接受
        Assert.Equal("nova", NovaGatewayClient.DefaultModel);         // 客户端只发逻辑模型名，具体模型由网关选
    }
}
