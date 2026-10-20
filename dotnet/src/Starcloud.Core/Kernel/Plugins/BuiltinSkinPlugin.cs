// 内置皮肤库插件：把 SkinLibraryService 按 ISkinLibrary 契约注册进内核。
//
// 皮肤文件放在启动器数据目录（LauncherPaths.SkinsDir），账户只记 skin_id；
// 换实现（云皮肤库、共享目录）时只换注册，账户与预览代码不动。

using Starcloud.Core.Kernel.Contracts;

namespace Starcloud.Core.Kernel.Plugins;

/// <summary>内置皮肤库插件：导入 / 改名 / 换型号 / 删除</summary>
public sealed class BuiltinSkinPlugin(ISkinLibrary skins) : PluginBase
{
    public const string PluginId = "starcloud.skins";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "皮肤库",
        Version = "1.0.0",
        Description = "管理本地皮肤：导入 PNG、改名、切换纤细/经典型号、删除",
        Kind = PluginKind.Engine,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(ISkinLibrary)],
        Permissions = ["fs.read:app", "fs.write:app"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        context.Services.Add<ISkinLibrary>(skins);
        return Task.CompletedTask;
    }
}
