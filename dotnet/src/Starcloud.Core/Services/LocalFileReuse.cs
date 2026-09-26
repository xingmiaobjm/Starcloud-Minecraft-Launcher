// 本地复用：补全 / 安装前先看一眼——同一份文件是不是已经躺在别的实例里了。
//
// 典型场景：装了 9 个版本，其中两个是同一个 MC 版本，client jar 一模一样（十几 MB）。
// 补全时没必要再下一遍：从确认完好的那个实例复制，本地复制是秒级，下载要看网速。
// 同一份 mod / 资源包也可能在另一个实例里已经装过，同理。
//
// 安全线：只认 SHA1。哈希对不上就当没找到，老老实实走下载——绝不拿「文件名像」的文件凑数，
// 也不覆盖目标已存在且校验通过的文件。

using System.Security.Cryptography;

namespace Starcloud.Core.Services;

public sealed class LocalFileReuse
{
    /// <summary>按文件名索引的候选（mods / 资源包 / 同版本 jar 都在里面）</summary>
    private readonly Dictionary<string, List<string>> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>版本 jar 单独一份：不同实例的 jar 文件名不同（<c>versions/&lt;id&gt;/&lt;id&gt;.jar</c>），
    /// 只能靠内容（SHA1）认亲</summary>
    private readonly List<string> _versionJars = [];

    private LocalFileReuse() { }

    /// <summary>扫描一个 .minecraft，建立可复用文件索引（只读目录列表，不算哈希）</summary>
    public static LocalFileReuse Scan(string dotMinecraft)
    {
        var reuse = new LocalFileReuse();
        try
        {
            var versionsDir = Path.Combine(dotMinecraft, "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
                {
                    var id = Path.GetFileName(versionDir);
                    var jar = Path.Combine(versionDir, id + ".jar");
                    if (File.Exists(jar))
                    {
                        reuse._versionJars.Add(jar);
                        reuse.Add(jar);
                    }
                    // 实例内容目录（隔离实例的 mod / 资源包 / 光影 / 数据包）
                    foreach (var contentDir in ContentDirs)
                        reuse.AddDir(Path.Combine(versionDir, contentDir));
                }
            }

            // 共享目录：<.minecraft>/mods、resourcepacks、shaderpacks、datapacks
            foreach (var contentDir in ContentDirs)
                reuse.AddDir(Path.Combine(dotMinecraft, contentDir));
        }
        catch
        {
            // 目录列不出来就当作没有可复用文件：只会退回下载，不影响正确性
        }
        return reuse;
    }

    /// <summary>
    /// 追加启动器自己的资源库（library/mods、library/resourcepacks…）作为来源：
    /// 实例里的 mod 被删了但资源库还留着时，直接复制，不用重新下。
    /// </summary>
    public void AddLibraryDirectory(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root))
                Add(file);
            foreach (var sub in Directory.EnumerateDirectories(root))
                foreach (var file in Directory.EnumerateFiles(sub))
                    Add(file);
        }
        catch
        {
            // 资源库读不到就跳过
        }
    }

    /// <summary>目标文件能不能用本地已有的同内容文件补上；能则复制过去并返回来源路径</summary>
    public bool TryProvide(string destPath, string? expectedSha1, long size, out string? source)
    {
        source = null;
        var candidates = new List<string>();
        if (_byName.TryGetValue(Path.GetFileName(destPath), out var sameName))
            candidates.AddRange(sameName);
        if (IsVersionJar(destPath)) candidates.AddRange(_versionJars);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
            if (PathsEqual(candidate, destPath)) continue;          // 别把自己当来源

            var info = new FileInfo(candidate);
            if (size > 0 && info.Length != size) continue;           // 先比大小：省掉绝大多数哈希
            if (string.IsNullOrEmpty(expectedSha1)) continue;        // 没哈希可比：不冒险
            if (!Sha1Matches(candidate, expectedSha1)) continue;

            try
            {
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(candidate, destPath, overwrite: true);
                source = candidate;
                return true;
            }
            catch
            {
                // 复制失败（占用 / 权限 / 跨盘中断）：退回下载
                return false;
            }
        }
        return false;
    }

    private static readonly string[] ContentDirs = ["mods", "resourcepacks", "shaderpacks", "datapacks"];

    private void AddDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            // 只扫顶层文件：mods 下的子目录不是规范布局，深扫会把索引建得又慢又大
            foreach (var file in Directory.EnumerateFiles(dir))
                Add(file);
        }
        catch
        {
            // 单个目录读不到就跳过
        }
    }

    private void Add(string path)
    {
        var name = Path.GetFileName(path);
        if (!_byName.TryGetValue(name, out var list))
            _byName[name] = list = [];
        list.Add(path);
    }

    /// <summary>是不是 <c>versions/&lt;id&gt;/&lt;id&gt;.jar</c>（唯一需要跨文件名认亲的目标）</summary>
    private static bool IsVersionJar(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase))
            return false;
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return false;
        if (!string.Equals(Path.GetFileNameWithoutExtension(path),
                Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), "versions",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool Sha1Matches(string path, string expected)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(fs))
                .Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
