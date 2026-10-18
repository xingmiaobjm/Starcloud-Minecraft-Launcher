// 投影库实现：把"投影放在哪、怎么搬到另一个版本"这件事收敛到一处。
//
// 目录约定：
//   全局      <dotMinecraft>/schematics
//   版本隔离  <dotMinecraft>/versions/<id>/schematics（gameDir = versions/<id>/）
//   版本共享  <dotMinecraft>/schematics（与全局同一处）
//
// 复制/移动只做文件系统操作，不碰 NBT 内容；重名自动加 -2/-3，绝不覆盖玩家的文件。

using Starcloud.Core.Kernel.Contracts;

namespace Starcloud.Core.Services.Projection;

public sealed class ProjectionLibrary(Func<LauncherSettings> settings) : IProjectionLibrary
{
    /// <summary>支持的投影扩展名（与 ProjectionReader.DetectFormat 对齐）</summary>
    private static readonly string[] Extensions = [".litematic", ".nbt", ".schematic", ".schem"];

    public IReadOnlyList<ProjectionLibraryFile> Scan()
    {
        var s = settings();
        var byPath = new Dictionary<string, ProjectionLibraryFile>(StringComparer.OrdinalIgnoreCase);

        void Add(string dir, string? versionId)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                if (Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) < 0) continue;
                var info = new FileInfo(path);
                byPath[path] = new ProjectionLibraryFile(path, info.Name, info.Length, info.LastWriteTime, versionId);
            }
        }

        Add(Path.Combine(s.DotMinecraft, "schematics"), null);
        foreach (var versionId in ListVersions())
            Add(Path.Combine(InstanceLayout.GameRoot(s.DotMinecraft, versionId, s.VersionIsolated), "schematics"), versionId);

        return byPath.Values.OrderByDescending(f => f.ModifiedAt).ToList();
    }

    public IReadOnlyList<string> ListVersions()
    {
        var root = Path.Combine(settings().DotMinecraft, "versions");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(id => id is { Length: > 0 } && File.Exists(Path.Combine(root, id, id + ".json")))
            .Cast<string>()
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string TargetDirectory(string? versionId)
    {
        var s = settings();
        return versionId is null
            ? Path.Combine(s.DotMinecraft, "schematics")
            : Path.Combine(InstanceLayout.GameRoot(s.DotMinecraft, versionId, s.VersionIsolated), "schematics");
    }

    public string CopyTo(string filePath, string? versionId)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("投影文件不存在", filePath);
        var dir = TargetDirectory(versionId);
        Directory.CreateDirectory(dir);

        var dest = UniquePath(dir, Path.GetFileNameWithoutExtension(filePath), Path.GetExtension(filePath));
        File.Copy(filePath, dest, overwrite: false);
        return dest;
    }

    public string MoveTo(string filePath, string? versionId)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("投影文件不存在", filePath);
        var dir = TargetDirectory(versionId);
        // 同一目录（含"全局 → 全局"）：什么都不用做
        if (string.Equals(Path.GetDirectoryName(filePath), dir, StringComparison.OrdinalIgnoreCase))
            return filePath;

        var dest = CopyTo(filePath, versionId);
        File.Delete(filePath);
        return dest;
    }

    public void Delete(string filePath)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    private static string UniquePath(string dir, string name, string ext)
    {
        var path = Path.Combine(dir, name + ext);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name}-{i}{ext}");
        return path;
    }
}
