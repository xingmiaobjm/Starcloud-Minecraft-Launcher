// 内置投影引擎实现：把既有的 ProjectionService / VoxelExporter 包成 IProjectionEngine。
//
// 这里只做「适配」，不含解析逻辑本身——解析在 ProjectionReader / ProjectionWriter /
// VoxelExporter 里，换实现（例如未来换 GPU 烘焙）时这些文件不动。

using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Core.Services;

/// <summary>内置投影引擎：文件解析 + 体素导出 + 格式互转</summary>
public sealed class ProjectionEngine(Func<LauncherSettings> settings) : IProjectionEngine
{
    private readonly ProjectionService _service = new(settings);

    public string DisplayName => "内置投影引擎";

    public IReadOnlyList<string> Directories() => _service.Directories();

    public IReadOnlyList<ProjectionFile> List() => _service.List();

    public ProjectionInfo Read(string path) => _service.Read(path);

    public ProjectionVoxels ExportVoxels(string path, int maxBlocks = VoxelExporter.MaxBlocks,
        MinecraftAssets? assets = null) => VoxelExporter.Read(path, maxBlocks, assets);

    public void UpdateMetadata(string path, string name, string author, string description) =>
        _service.UpdateMetadata(path, name, author, description);

    public string ConvertToStructureNbt(string source, string dest) =>
        _service.ConvertToStructureNbt(source, dest);

    public string ConvertToLitematic(string source, string dest) =>
        _service.ConvertToLitematic(source, dest);

    public bool CanConvert(ProjectionFormat format) => ProjectionService.CanConvert(format);
}
