// LLM 网关契约：Nova 走云端模型时的那条通道（额度 / 模型列表 / 对话 / 流式）。
//
// 与「自接模型」（LauncherSettings.ReadyLlmProfile）是两条路：
//   云端网关 = 我们自己的中转，按账号额度计费；
//   自接模型 = 用户把自己的 OpenAI 兼容服务填进来，不经我们的中转。
// 调用点只认这个契约，换实现（换中转地址、换协议、加本地推理）不动上层。

using System.Runtime.CompilerServices;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Contracts;

public interface ILlmGateway
{
    /// <summary>实现名（设置 / 插件页展示）</summary>
    string DisplayName { get; }

    /// <summary>网关是否可用：开关打开 + 已登录且拿到 Key</summary>
    bool CanUse(StarcloudSession? session);

    /// <summary>用户是否接了自己的模型（这时云端额度不再显示）</summary>
    bool HasOwnModel();

    /// <summary>拉取本周额度；失败返回 null（额度显示是锦上添花，不挡对话）</summary>
    Task<NovaQuota?> FetchQuotaAsync(string apiKey, CancellationToken ct = default);

    /// <summary>可用模型 id 列表（取第一个作为对话模型）</summary>
    Task<IReadOnlyList<string>> FetchModelsAsync(string apiKey, CancellationToken ct = default);

    /// <summary>解析对话实际使用的模型（缓存按 Key 区分）</summary>
    Task<string> ResolveModelAsync(string apiKey, CancellationToken ct = default);

    /// <summary>非流式对话，返回正文</summary>
    Task<string> ChatAsync(string apiKey, IReadOnlyList<object> messages,
        string? model = null, int? maxTokens = null, CancellationToken ct = default);

    /// <summary>流式对话（文本增量 + 工具调用增量）</summary>
    IAsyncEnumerable<SseEvent> StreamChatAsync(string apiKey, IReadOnlyList<object> messages,
        object? tools = null, string? model = null, int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken ct = default);
}
