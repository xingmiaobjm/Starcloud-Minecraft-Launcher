// Nova Agent 执行循环：LLM 通过 OpenAI function calling 真正动手配置启动器。
// 循环规则（信任递进，产品基石不变）：
//   ≤6 轮：只读工具立即执行、结果以 role:"tool" 回喂续跑；
//   写操作工具一律攒成 NovaPlan 确认清单（用户点头才执行），本轮到此为止——
//   执行结果由 UI 侧确认后回喂新一轮对话出总结。
// 设计：.trae/documents/2026-09-14-nova-agent-and-quilt.md 阶段三。

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>Agent 循环产出的一条事件（UI 据此刷状态行 / 确认卡）</summary>
public abstract record AgentEvent;

/// <summary>文本增量（直接进对话气泡）</summary>
public sealed record AgentTextDelta(string Delta) : AgentEvent;

/// <summary>一个只读工具开始执行（UI 显示「正在执行 xxx…」状态行）</summary>
public sealed record AgentToolStarted(string Tool, string ArgsPreview) : AgentEvent;

/// <summary>写操作提案就绪：Plan 交给 UI 渲染确认卡，用户点头后执行</summary>
public sealed record AgentPlanProposed(NovaPlan Plan, string Text) : AgentEvent;

/// <summary>本轮对话结束（无更多工具调用；Text 为收尾文本）</summary>
public sealed record AgentFinal(string Text) : AgentEvent;

public sealed class NovaAgentLoop
{
    private const int MaxRounds = 6;

    private readonly NovaService _nova;
    private readonly NovaToolRegistry _tools;

    public NovaAgentLoop(NovaService nova, NovaToolRegistry tools)
    {
        _nova = nova;
        _tools = tools;
    }

    /// <summary>Agent 行为约束：在 Nova 人格之上追加工具使用守则。</summary>
    internal const string AgentPromptSuffix =
        "\n你可以调用工具帮用户真正完成配置。规则：" +
        "1. 只读工具（搜索/列表/读取）可以直接调用；" +
        "2. 安装、修改设置等写操作先想清楚参数再调用，调用即代表你已把影响写进回复让用户确认；" +
        "3. 版本号、项目 ID 一律来自工具结果，绝不臆造；" +
        "4. 一次说清楚要做什么，不要连续空转；" +
        "5. 工具失败时如实说明原因，给用户手动步骤；" +
        "6. 工具很多，只调必要的那个；工具返回的原文不要贴给用户，" +
        "提炼成一句结论（例如「1.21.1 的钠已装到实例 X」），需要用户点头的写进确认清单；" +
        "7. 回复仍然遵守简短要求：1~2 句，最多 3 条要点。";

    /// <summary>
    /// 跑一轮 Agent 对话：流式文本 + 工具调用。
    /// 没有任何可用 LLM 通道时不产出任何事件，调用方回退原有纯文本链。
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string message, LauncherSettings settings,
        IReadOnlyList<NovaChatTurn>? history,
        int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(history, message);
        var tools = _tools.ToOpenAiJson();
        var text = new StringBuilder();

        for (var round = 0; round < MaxRounds; round++)
        {
            var acc = new ToolCallAccumulator();
            var gotAny = false;

            await foreach (var evt in _nova.AgentLlmStreamAsync(messages, tools, maxTokens, settings, ct))
            {
                gotAny = true;
                if (evt.ContentDelta is { Length: > 0 } d)
                {
                    text.Append(d);
                    yield return new AgentTextDelta(d);
                }
                if (evt.ToolCallDeltas is { Count: > 0 } deltas)
                    acc.Add(deltas);
            }

            if (!gotAny)
            {
                if (round == 0) yield break; // 无可用通道 → 上层回退
                yield return new AgentFinal(text.ToString());
                yield break;
            }

            if (acc.IsEmpty)
            {
                yield return new AgentFinal(text.ToString());
                yield break;
            }

            // 把 assistant 的工具调用请求记回 messages（OpenAI 协议要求）
            var calls = acc.Complete();
            messages.Add(new ChatMessage(
                "assistant",
                text.Length > 0 ? text.ToString() : null,
                ToolCalls: calls.Select(c => new ChatToolCall(
                    c.Id, "function", new ChatToolCallFunction(c.Name, c.Arguments))).ToArray()));
            text.Clear();

            // 只读立即执行；写操作攒进确认清单
            var writes = new List<(NovaTool Tool, CompletedToolCall Call)>();
            foreach (var call in calls)
            {
                var tool = _tools.Find(call.Name);
                if (tool is null)
                {
                    messages.Add(new ChatMessage("tool", $"未知工具 {call.Name}", call.Id));
                    continue;
                }

                if (tool.ReadOnly)
                {
                    yield return new AgentToolStarted(tool.Name, Preview(call.Arguments));
                    string result;
                    try { result = await _tools.ExecuteAsync(call.Name, call.Arguments, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = $"执行失败：{ex.Message}"; }
                    messages.Add(new ChatMessage("tool", result, call.Id));
                }
                else
                {
                    writes.Add((tool, call));
                }
            }

            // 本轮全是只读 → 带着工具结果继续下一轮，让模型作答
            if (writes.Count == 0) continue;

            // 有写操作 → 出确认清单，本轮终止（执行后由 UI 回喂总结）
            var plan = new NovaPlan(
                Guid.NewGuid().ToString("N")[..12],
                writes.Count == 1 ? writes[0].Tool.DisplayName : $"执行 {writes.Count} 项配置动作",
                writes.Select(w => new NovaActionItem(
                    w.Tool.DisplayName, Preview(w.Call.Arguments, 200))).ToList(),
                async ct2 =>
                {
                    var results = new StringBuilder();
                    foreach (var (tool, call) in writes)
                        results.AppendLine(await _tools.ExecuteAsync(tool.Name, call.Arguments, ct2));
                    return results.ToString().Trim();
                });
            yield return new AgentPlanProposed(plan, text.ToString());
            yield break;
        }

        yield return new AgentFinal(text.ToString() + "\n（工具轮次用完，先到这里；还需要什么请继续说。）");
    }

    private static List<object> BuildMessages(IReadOnlyList<NovaChatTurn>? history, string message)
    {
        var msgs = new List<object>
        {
            new ChatMessage("system", NovaService.SystemPrompt + AgentPromptSuffix),
        };
        if (history is { Count: > 0 })
            msgs.AddRange(history
                .Where(t => !string.IsNullOrWhiteSpace(t.Content)) // 空正文轮会被服务端 400
                .Select(t => new ChatMessage(t.Role, t.Content)));
        msgs.Add(new ChatMessage("user", message));
        return msgs;
    }

    private static string Preview(string arguments, int max = 120)
    {
        var s = (arguments ?? "").Trim();
        if (s.Length == 0) return "{}";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
