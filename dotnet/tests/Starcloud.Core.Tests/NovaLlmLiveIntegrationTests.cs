// 自接模型实测（默认不跑，需显式开启）：
//
//   set STARCLOUD_IT_LLM=1                开关
//   set STARCLOUD_IT_LLM_BASE=...         可选，默认读 launcher.settings.json 里当前启用的档案
//   set STARCLOUD_IT_LLM_KEY=...
//   set STARCLOUD_IT_LLM_MODEL=...
//
// 跑真实网络 + 真实 OpenAiSseClient，验证「自接模型（含外置 DeepSeek）能真的收到流式正文」。
// 离线护栏见 NovaLlmRequestTests（本地假服务端证明字段名与 SSE 解析正确）。

using System.Text;
using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Starcloud.Core.Tests;

public class NovaLlmLiveIntegrationTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("STARCLOUD_IT_LLM") == "1";

    [Fact]
    public async Task 真实端点_自接模型能收到流式正文()
    {
        if (!Enabled) return; // 未开启：直接跳过

        var (baseUrl, key, model) = ResolveTarget();
        if (baseUrl.Length == 0 || key.Length == 0 || model.Length == 0)
        {
            output.WriteLine("没有可用的模型档案：设 STARCLOUD_IT_LLM_BASE/KEY/MODEL 或先在启动器里接一个模型");
            return;
        }
        output.WriteLine($"目标 {LlmEndpoint.ChatUrl(baseUrl)} · 模型 {model}");

        using var http = new HttpClient();
        var history = new List<NovaChatTurn>
        {
            new("user", "第一轮：回复两个字"),
            new("assistant", "好的"),
        };
        var text = new StringBuilder();
        var events = 0;
        await foreach (var evt in OpenAiSseClient.StreamChatAsync(
            http, LlmEndpoint.ChatUrl(baseUrl), key, model,
            NovaService.BuildMessages("第二轮：只回复两个字「收到」", history)))
        {
            events++;
            if (evt.ContentDelta is { Length: > 0 } d) text.Append(d);
        }

        output.WriteLine($"事件 {events} 条，正文 {text.Length} 字：{text}");
        Assert.True(events > 0, "一条 SSE 事件都没解析出来（历史上 line[4..] 的 off-by-one 会导致这个）");
        Assert.True(text.Length > 0, "连接成功但没有正文");
    }

    private static (string BaseUrl, string Key, string Model) ResolveTarget()
    {
        var url = Environment.GetEnvironmentVariable("STARCLOUD_IT_LLM_BASE") ?? "";
        var key = Environment.GetEnvironmentVariable("STARCLOUD_IT_LLM_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("STARCLOUD_IT_LLM_MODEL") ?? "";
        if (url.Length > 0) return (url, key, model);

        // 没给环境变量 → 读本机启动器设置里当前启用的档案
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "starcloud-launcher", "launcher.settings.json");
        if (!File.Exists(path)) return ("", "", "");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("nova_llm_profiles", out var profiles) ||
                profiles.ValueKind != JsonValueKind.Array)
                return ("", "", "");
            var activeId = doc.RootElement.TryGetProperty("nova_llm_active_id", out var a) ? a.GetString() : null;
            JsonElement? fallback = null;
            foreach (var p in profiles.EnumerateArray())
            {
                fallback ??= p;
                if (activeId is not null &&
                    p.TryGetProperty("id", out var id) && id.GetString() == activeId)
                    fallback = p;
            }
            if (fallback is not { } profile) return ("", "", "");
            return (
                profile.TryGetProperty("base_url", out var b) ? b.GetString() ?? "" : "",
                profile.TryGetProperty("api_key", out var k) ? k.GetString() ?? "" : "",
                profile.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "");
        }
        catch
        {
            return ("", "", "");
        }
    }
}
