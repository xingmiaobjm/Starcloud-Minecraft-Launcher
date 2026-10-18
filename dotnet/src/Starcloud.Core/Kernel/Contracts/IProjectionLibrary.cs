// 投影管理契约：扫描全局 / 各版本实例的 schematics 目录，跨版本复制、移动、删除。
//
// 投影文件默认躺在 gameDir/schematics（Litematica / WorldEdit 的约定）。
// 版本隔离开启时每个实例的 gameDir 是 versions/<id>/，投影不会共享——
// 这个契约就是为了让玩家在管理界面里一键把投影放到需要的版本里。

namespace Starcloud.Core.Kernel.Contracts;

/// <summary>一条投影文件；<see cref="VersionId"/> 为空表示全局 .minecraft/schematics</summary>
public sealed record ProjectionLibraryFile(
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTime ModifiedAt,
    string? VersionId);

public interface IProjectionLibrary
{
    /// <summary>扫描全部投影（全局 + 各实例目录），按修改时间倒序</summary>
    IReadOnlyList<ProjectionLibraryFile> Scan();

    /// <summary>已安装版本 id（复制目标）</summary>
    IReadOnlyList<string> ListVersions();

    /// <summary>目标目录：null = 全局，否则该版本的 schematics</summary>
    string TargetDirectory(string? versionId);

    /// <summary>复制到目标版本，返回新路径（重名自动加 -2/-3）</summary>
    string CopyTo(string filePath, string? versionId);

    /// <summary>移动到目标版本（源目录相同则原地不动）</summary>
    string MoveTo(string filePath, string? versionId);

    void Delete(string filePath);
}
