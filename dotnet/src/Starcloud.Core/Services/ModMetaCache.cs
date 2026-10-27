// jar 元数据缓存：155 个 mod 每次进详情页都要重新打开 zip 读 mods.toml / fabric.mod.json，
// 还要顺带找图标 —— 单次 1~2 秒，切换页面时非常明显。
// 这里按「路径 + 修改时间 + 大小」判活，进程内 + 落盘两级；文件没动就直接复用上次的解析结果。

using System.Text.Json;

namespace Starcloud.Core.Services;

public static class ModMetaCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Map = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    internal sealed record Entry(long Size, DateTime ModifiedUtc, ModFileInfo Info);

    private static string FilePath => Path.Combine(LauncherPaths.CacheDir, "mod-meta-cache.json");

    public static bool TryGet(string path, FileInfo file, out ModFileInfo info)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (Map.TryGetValue(path, out var entry) &&
                entry.Size == file.Length && entry.ModifiedUtc == file.LastWriteTimeUtc)
            {
                info = entry.Info;
                return true;
            }
        }
        info = null!;
        return false;
    }

    public static void Put(string path, FileInfo file, ModFileInfo info)
    {
        lock (Gate)
        {
            Map[path] = new Entry(file.Length, file.LastWriteTimeUtc, info);
        }
    }

    /// <summary>批量扫描结束后落盘一次（不要每个 jar 都写）。</summary>
    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LauncherPaths.CacheDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Map));
            }
            catch { /* 缓存写失败不影响功能 */ }
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
            if (map is null) return;
            foreach (var (key, value) in map) Map[key] = value;
        }
        catch { /* 缓存损坏从零开始 */ }
    }
}
