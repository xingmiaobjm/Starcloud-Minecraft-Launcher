// 内置投影引擎插件：把 ProjectionEngine 按 IProjectionEngine 契约注册进内核。
//
// 与 BuiltinProjectionPlugin（投影管理，管文件放哪个版本）分开注册：
// 引擎是「文件里是什么」，管理是「文件放哪里」，两块可以各自替换。

using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Plugins;

/// <summary>内置投影引擎插件：解析 / 体素导出 / 元数据回写 / 格式互转</summary>
public sealed class BuiltinProjectionEnginePlugin(Func<LauncherSettings> settings) : PluginBase
{
    public const string PluginId = "starcloud.projection-engine";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "投影引擎",
        Version = "1.0.0",
        Description = "解析投影文件（litematic / 结构 nbt / schematic），导出带真实纹理的体素，支持格式互转",
        Kind = PluginKind.Engine,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(IProjectionEngine)],
        Permissions = ["fs.read:minecraft"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        context.Services.Add<IProjectionEngine>(new ProjectionEngine(settings));
        return Task.CompletedTask;
    }
}
