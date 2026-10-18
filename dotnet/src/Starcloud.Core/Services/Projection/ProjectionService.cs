// 「实验室」投影文件服务：扫描投影目录、解析信息、编辑元数据、格式转换。
//
// 扫描范围（对齐 Litematica / WorldEdit 的常见约定）：
//   · <.minecraft>/schematics        —— Litematica 与 WorldEdit 的默认投影目录
//   · <.minecraft>/config/litematica —— Litematica 备用位置
// 列表只做"文件名 + 大小 + 时间"的轻量读取；解析（含解包统计）在用户选中文件时才做。

using Starcloud.Core.Services.Projection;

namespace Starcloud.Core.Services;

/// <summary>投影文件列表项（轻量：不解析内容）</summary>
public sealed record ProjectionFile(string Path, string Name, long SizeBytes, DateTime ModifiedAt)
{
    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024:F1} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";
}

public sealed class ProjectionService(Func<LauncherSettings> settings)
{
    /// <summary>投影目录（存在即列出，不存在返回空表；不创建——避免给没装模组的用户凭空建目录）</summary>
    public List<string> Directories()
    {
        var dotMc = settings().DotMinecraft;
        return
        [
            Path.Combine(dotMc, "schematics"),
            Path.Combine(dotMc, "config", "litematica"),
        ];
    }

    /// <summary>列出两个目录下的全部投影文件（.litematic/.nbt/.schematic/.schem），按修改时间倒序</summary>
    public List<ProjectionFile> List()
    {
        List<ProjectionFile> result = [];
        foreach (var dir in Directories())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext is not (".litematic" or ".nbt" or ".schematic" or ".schem"))
                        continue;
                    var info = new FileInfo(path);
                    result.Add(new ProjectionFile(path, Path.GetFileNameWithoutExtension(path),
                        info.Length, info.LastWriteTime));
                }
            }
            catch
            {
                // 目录读不到就跳过（权限/占用），不影响另一个目录
            }
        }
        return result.OrderByDescending(f => f.ModifiedAt).ToList();
    }

    /// <summary>解析一个投影文件（选中时才调用；大投影的解包统计在调用方放后台线程）</summary>
    public ProjectionInfo Read(string path) => ProjectionReader.Read(path);

    /// <summary>改 .litematic 元数据（名称 / 作者 / 描述）并落盘</summary>
    public void UpdateMetadata(string path, string name, string author, string description) =>
        ProjectionWriter.UpdateLitematicMetadata(path, name, author, description);

    /// <summary>litematic → 结构 nbt（目标路径由调用方从保存对话框取得）</summary>
    public string ConvertToStructureNbt(string source, string dest) =>
        ProjectionWriter.LitematicToStructureNbt(source, dest);

    /// <summary>结构 nbt → litematic</summary>
    public string ConvertToLitematic(string source, string dest) =>
        ProjectionWriter.StructureNbtToLitematic(source, dest);

    /// <summary>该格式是否支持转换（.schematic 数字 ID 旧格式只读）</summary>
    public static bool CanConvert(ProjectionFormat format) =>
        format is ProjectionFormat.Litematic or ProjectionFormat.StructureNbt;
}
