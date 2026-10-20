// 内置 LLM 网关实现：把既有的 NovaGatewayClient（纯静态类）包成 ILlmGateway。
//
// 全部转发，不含逻辑——协议细节仍在 NovaGatewayClient 里，
// 换实现（本地推理、别家中转）时只换这里的注册，不动 NovaService。

using System.Runtime.CompilerServices;
using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Services;

namespace Starcloud.Core.Services;

/// <summary>内置 LLM 网关：云端中转（额度 / 模型列表 / 对话 / 流式）</summary>
public sealed class NovaLlmGateway(Func<LauncherSettings> settings) : ILlmGateway
{
    public string DisplayName => "Starcloud 云端网关";

    public bool CanUse(StarcloudSession? session) => NovaGatewayClient.CanUse(settings(), session);

    public bool HasOwnModel() => NovaGatewayClient.HasOwnModel(settings());

    public Task<NovaQuota?> FetchQuotaAsync(string apiKey, CancellationToken ct = default) =>
        NovaGatewayClient.FetchQuotaAsync(apiKey, ct);

    public Task<IReadOnlyList<string>> FetchModelsAsync(string apiKey, CancellationToken ct = default) =>
        NovaGatewayClient.FetchModelsAsync(apiKey, ct);

    public Task<string> ResolveModelAsync(string apiKey, CancellationToken ct = default) =>
        NovaGatewayClient.ResolveModelAsync(apiKey, ct);

    public Task<string> ChatAsync(string apiKey, IReadOnlyList<object> messages,
        string? model = null, int? maxTokens = null, CancellationToken ct = default) =>
        NovaGatewayClient.ChatAsync(apiKey, messages, model, maxTokens, ct);

    public IAsyncEnumerable<SseEvent> StreamChatAsync(string apiKey, IReadOnlyList<object> messages,
        object? tools = null, string? model = null, int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken ct = default) =>
        NovaGatewayClient.StreamChatAsync(apiKey, messages, tools, model, maxTokens, ct);
}
