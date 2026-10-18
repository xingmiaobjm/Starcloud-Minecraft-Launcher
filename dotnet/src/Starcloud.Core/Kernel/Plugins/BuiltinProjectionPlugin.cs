// 内置投影管理插件：把 ProjectionLibrary 按 IProjectionLibrary 契约注册进内核。
//
// 管理界面（Views/ProjectionManagerPage）通过契约取用，换实现 = 注册一个优先级更高的插件。

using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Core.Kernel.Plugins;

/// <summary>内置投影管理插件：扫描 / 跨版本复制 / 移动 / 删除投影文件</summary>
public sealed class BuiltinProjectionPlugin(Func<LauncherSettings> settings) : PluginBase
{
    public const string PluginId = "starcloud.projections";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "投影管理",
        Version = "1.0.0",
        Description = "扫描全局与各版本实例的投影文件，可把投影复制/移动到指定版本",
        Kind = PluginKind.Feature,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(IProjectionLibrary)],
        Permissions = ["fs.read:minecraft", "fs.write:minecraft"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        context.Services.Add<IProjectionLibrary>(new ProjectionLibrary(settings));
        return Task.CompletedTask;
    }
}
