// 内置 LLM 网关插件：把 NovaLlmGateway 按 ILlmGateway 契约注册进内核。
//
// 与「自接模型」并存：这个插件管云端中转，用户自己的模型走 LauncherSettings 里的 profile，
// 两条路互不替代（HasOwnModel 为真时云端只做兜底）。

using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Plugins;

/// <summary>内置 LLM 网关插件：额度、模型列表、对话、流式</summary>
public sealed class BuiltinLlmGatewayPlugin(Func<LauncherSettings> settings) : PluginBase
{
    public const string PluginId = "starcloud.llm-gateway";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "LLM 网关",
        Version = "1.0.0",
        Description = "Nova 的云端模型通道：额度查询、模型列表、对话与流式响应",
        Kind = PluginKind.Engine,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(ILlmGateway)],
        Permissions = ["net.outbound"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        context.Services.Add<ILlmGateway>(new NovaLlmGateway(settings));
        return Task.CompletedTask;
    }
}
