// 内置跨设备存档同步插件：把 SaveSyncService 按 ISaveSync 契约注册进内核。
//
// 链路（DeviceLinkService）负责发现与加密连接，本插件只暴露"推/拉/列清单"三件事；
// 换同步策略（整实例同步、云盘中转）只换这条契约的实现。

using Starcloud.Core.Kernel.Contracts;

namespace Starcloud.Core.Kernel.Plugins;

/// <summary>内置存档同步插件：推送到对端 / 从对端拉回 / 列出对端世界</summary>
public sealed class BuiltinSaveSyncPlugin(ISaveSync saveSync) : PluginBase
{
    public const string PluginId = "starcloud.save-sync";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "跨设备存档同步",
        Version = "1.0.0",
        Description = "把存档推到另一台设备或从对端拉回，冲突按新者胜、旧者改名保留",
        Kind = PluginKind.Engine,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(ISaveSync)],
        Permissions = ["net.outbound", "fs.read:minecraft", "fs.write:minecraft"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        context.Services.Add<ISaveSync>(saveSync);
        return Task.CompletedTask;
    }
}
