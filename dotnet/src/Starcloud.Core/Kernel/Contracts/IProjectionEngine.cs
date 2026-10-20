// 投影引擎契约：解析投影文件、导出渲染用体素、元数据回写、格式互转。
//
// 和「投影管理」（IProjectionLibrary，管文件）分开：
//   管理 = 把文件放到哪个版本；
//   引擎 = 这个文件里到底是什么（尺寸、方块、可渲染体素）。
// 3D 预览、统计、转换都走这条契约，换实现（比如换成 GPU 侧烘焙）不影响调用点。

using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Core.Kernel.Contracts;

public interface IProjectionEngine
{
    /// <summary>实现名（设置 / 插件页展示）</summary>
    string DisplayName { get; }

    /// <summary>投影目录（schematics / config/litematica；只返回路径，不创建）</summary>
    IReadOnlyList<string> Directories();

    /// <summary>扫描默认投影目录（schematics / config/litematica），按修改时间倒序</summary>
    IReadOnlyList<ProjectionFile> List();

    /// <summary>解析单个投影（尺寸、方块数、调色板统计）；大文件请在后台线程调用</summary>
    ProjectionInfo Read(string path);

    /// <summary>导出体素（带真实纹理；超过上限自动降采样）。assets 为空时退回近似色</summary>
    ProjectionVoxels ExportVoxels(string path, int maxBlocks = VoxelExporter.MaxBlocks,
        MinecraftAssets? assets = null);

    /// <summary>改 .litematic 的名称 / 作者 / 描述</summary>
    void UpdateMetadata(string path, string name, string author, string description);

    /// <summary>litematic → 结构 nbt</summary>
    string ConvertToStructureNbt(string source, string dest);

    /// <summary>结构 nbt → litematic</summary>
    string ConvertToLitematic(string source, string dest);

    /// <summary>该格式是否支持互转（.schematic 数字 ID 旧格式只读）</summary>
    bool CanConvert(ProjectionFormat format);
}
