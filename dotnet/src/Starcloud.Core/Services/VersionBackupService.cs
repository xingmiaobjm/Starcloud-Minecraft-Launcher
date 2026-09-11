// 版本备份与回档：zip 快照 + backups.json 索引 + 安全网回档

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public sealed class BackupEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("version_id")] public string VersionId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("file_count")] public int FileCount { get; set; }
    /// <summary>zip 相对 BackupRoot 的路径</summary>
    [JsonPropertyName("zip_path")] public string ZipPath { get; set; } = "";
    /// <summary>备份覆盖的相对 .minecraft 根目录路径（如 versions/&lt;id&gt;、versions/&lt;id&gt;/mods、mods）</summary>
    [JsonPropertyName("includes")] public List<string> Includes { get; set; } = [];
}

public sealed class BackupIndex
{
    [JsonPropertyName("entries")] public List<BackupEntry> Entries { get; set; } = [];
}

/// <summary>备份请求：Dirs 为相对目录名（version = 版本目录本体），Isolated 决定内容目录实际位置</summary>
public sealed record VersionBackupRequest(
    string VersionId,
    bool Isolated,
    string Name,
    IReadOnlyList<string> Dirs,
    bool IncludeSaves);

public sealed class VersionBackupService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>备份列表（可按版本过滤，时间倒序）</summary>
    public IReadOnlyList<BackupEntry> List(string dotMc, string? versionId = null) =>
        LoadIndex(dotMc).Entries
            .Where(e => versionId is null ||
                        e.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

    /// <summary>创建备份（zip 内路径 = 相对 .minecraft 根的 posix 路径）</summary>
    public Task<BackupEntry> BackupAsync(string dotMc, VersionBackupRequest request,
        IProgress<string>? status = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var sources = ResolveSources(dotMc, request.VersionId, request.Isolated,
                request.Dirs, request.IncludeSaves);

            var root = InstanceLayout.BackupRoot(dotMc);
            var versionDir = Path.Combine(root, request.VersionId);
            Directory.CreateDirectory(versionDir);

            var fileName = $"{InstanceLayout.SafeFileName(request.Name)}-" +
                           $"{DateTime.Now:yyyy-MM-dd_HHmmss}.zip";
            var finalZip = InstanceLayout.NextAvailablePath(Path.Combine(versionDir, fileName));
            var tmpZip = finalZip + ".tmp";

            status?.Report("正在收集备份内容…");
            var files = new List<(string Rel, string Abs)>();
            foreach (var (prefix, abs) in sources)
                files.AddRange(EnumerateFiles(dotMc, request.VersionId, prefix, abs));
            var totalBytes = files.Sum(f => new FileInfo(f.Abs).Length);

            var written = 0;
            long done = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (rel, abs) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(rel)) continue;
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    using (var src = File.OpenRead(abs))
                    using (var dst = entry.Open())
                        src.CopyTo(dst);
                    written++;
                    done += new FileInfo(abs).Length;
                    if (written % 20 == 0 || written == files.Count)
                        status?.Report($"正在备份 {written}/{files.Count}" +
                                       $"（{FormatSize(done)}/{FormatSize(totalBytes)}）…");
                }
            }
            File.Move(tmpZip, finalZip, overwrite: true);

            var created = new BackupEntry
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                VersionId = request.VersionId,
                Name = request.Name,
                CreatedAt = DateTime.Now,
                SizeBytes = new FileInfo(finalZip).Length,
                FileCount = written,
                ZipPath = Path.GetRelativePath(root, finalZip).Replace('\\', '/'),
                Includes = sources.Select(s => s.Prefix).ToList(),
            };
            var index = LoadIndex(dotMc);
            index.Entries.Add(created);
            SaveIndex(dotMc, index);
            return created;
        }, ct);

    /// <summary>回档：先自动快照当前状态，再按 includes 覆盖还原</summary>
    public Task RestoreAsync(string dotMc, BackupEntry entry, bool isolated,
        IProgress<string>? status = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var root = InstanceLayout.BackupRoot(dotMc);
            var zipPath = Path.Combine(root, entry.ZipPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(zipPath))
                throw new FileNotFoundException($"备份文件不存在：{entry.Name}");

            // 1. 安全网：先快照当前状态（内容与本次备份相同）
            status?.Report("正在备份当前状态（安全网）…");
            await BackupAsync(dotMc, new VersionBackupRequest(
                entry.VersionId, isolated,
                $"回档前自动备份-{DateTime.Now:yyyy-MM-dd_HHmmss}",
                entry.Includes, false), status, ct).ConfigureAwait(false);

            // 2. 清空回档范围内现有内容
            status?.Report("正在清理回档范围…");
            foreach (var prefix in entry.Includes)
            {
                ct.ThrowIfCancellationRequested();
                var rel = prefix.Replace('\\', '/').Trim('/');
                if (rel.Length == 0 || rel.Split('/').Any(s => s is ".." or "")) continue;
                var abs = Path.Combine(dotMc, rel.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
                else if (File.Exists(abs)) File.Delete(abs);
            }

            // 3. 解压覆盖（zip 内路径即落位路径）
            status?.Report("正在还原备份…");
            var rootFull = Path.GetFullPath(dotMc);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = e.FullName.Replace('\\', '/');
                    if (rel.Length == 0 || rel.EndsWith('/')) continue;
                    var segs = rel.Split('/');
                    if (segs.Any(s => s is ".." or "")) continue;
                    var dest = Path.GetFullPath(
                        Path.Combine(dotMc, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(rootFull + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                }
            }
        }, ct);

    /// <summary>删除备份（zip + 索引条目）</summary>
    public void Delete(string dotMc, string entryId)
    {
        var index = LoadIndex(dotMc);
        var entry = index.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return;
        var zipPath = Path.Combine(InstanceLayout.BackupRoot(dotMc),
            entry.ZipPath.Replace('/', Path.DirectorySeparatorChar));
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* 文件占用时保留，索引条目照删 */ }
        index.Entries.Remove(entry);
        SaveIndex(dotMc, index);
    }

    /// <summary>索引损坏/外部手工放入 zip 后的修复入口：按 BackupRoot 下实际 zip 重建</summary>
    public IReadOnlyList<BackupEntry> RebuildIndex(string dotMc)
    {
        var root = InstanceLayout.BackupRoot(dotMc);
        var index = new BackupIndex();
        if (Directory.Exists(root))
        {
            foreach (var zipPath in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories)
                         .OrderByDescending(File.GetLastWriteTime))
            {
                var rel = Path.GetRelativePath(root, zipPath).Replace('\\', '/');
                var segs = rel.Split('/');
                var versionId = segs.Length > 1 ? segs[0] : "";
                var info = new FileInfo(zipPath);
                index.Entries.Add(new BackupEntry
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    VersionId = versionId,
                    Name = Path.GetFileNameWithoutExtension(zipPath),
                    CreatedAt = info.LastWriteTime,
                    SizeBytes = info.Length,
                    FileCount = CountEntries(zipPath),
                    ZipPath = rel,
                    Includes = InferIncludes(zipPath, versionId),
                });
            }
        }
        SaveIndex(dotMc, index);
        return index.Entries.OrderByDescending(e => e.CreatedAt).ToList();
    }

    // ── 内部实现 ──

    /// <summary>把勾选目录名解析为 (zip 前缀, 磁盘绝对路径)；不存在的内容不纳入（避免回档误删）</summary>
    private static List<(string Prefix, string Abs)> ResolveSources(
        string dotMc, string versionId, bool isolated,
        IReadOnlyList<string> dirs, bool includeSaves)
    {
        var tags = new List<string>(dirs);
        if (includeSaves && !tags.Contains("saves", StringComparer.OrdinalIgnoreCase))
            tags.Add("saves");

        var basePrefix = isolated ? $"versions/{versionId}" : "";
        var list = new List<(string Prefix, string Abs)>();
        foreach (var raw in tags)
        {
            var tag = raw.Replace('\\', '/').Trim('/');
            if (tag.Length == 0) continue;

            // "version" = 版本目录整体；"@x" = 相对 .minecraft 根（用于隔离实例附带共享内容）；
            // "versions/..." = 相对 .minecraft 根的显式路径；其余 = 相对实例根
            var prefix = tag.Equals("version", StringComparison.OrdinalIgnoreCase)
                ? $"versions/{versionId}"
                : tag.StartsWith('@')
                    ? tag[1..]
                    : tag.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)
                        ? tag
                        : basePrefix.Length == 0 ? tag : $"{basePrefix}/{tag}";
            if (prefix.Length == 0) continue;

            if (list.Any(x => x.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase))) continue;
            // 已被更上层的范围覆盖则跳过；本范围包含已选中的子范围则去掉子项
            if (list.Any(x => prefix.StartsWith(x.Prefix + "/", StringComparison.OrdinalIgnoreCase))) continue;
            list.RemoveAll(x => x.Prefix.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

            var abs = Path.Combine(dotMc, prefix.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(abs) && !File.Exists(abs)) continue;
            list.Add((prefix, abs));
        }
        return list;
    }

    private static IEnumerable<(string Rel, string Abs)> EnumerateFiles(
        string dotMc, string versionId, string prefix, string abs)
    {
        if (File.Exists(abs))
        {
            if (!IsExcluded(prefix, versionId)) yield return (prefix, abs);
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dotMc, file).Replace('\\', '/');
            if (IsExcluded(rel, versionId)) continue;
            yield return (rel, file);
        }
    }

    private static bool IsExcluded(string rel, string versionId)
    {
        if (rel.StartsWith("starcloud/backups/", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.Split('/').Any(s => s.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                                    s.Equals("crash-reports", StringComparison.OrdinalIgnoreCase)))
            return true;
        return rel.StartsWith($"versions/{versionId}/", StringComparison.OrdinalIgnoreCase)
               && rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    private BackupIndex LoadIndex(string dotMc)
    {
        var path = InstanceLayout.BackupIndexFile(dotMc);
        if (!File.Exists(path)) return new BackupIndex();
        try
        {
            return JsonSerializer.Deserialize<BackupIndex>(File.ReadAllText(path)) ?? new BackupIndex();
        }
        catch
        {
            // 索引损坏 → 重置为空，由「重建索引」找回
            return new BackupIndex();
        }
    }

    private static void SaveIndex(string dotMc, BackupIndex index)
    {
        var path = InstanceLayout.BackupIndexFile(dotMc);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(index, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    private static int CountEntries(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Count(e => !e.FullName.EndsWith('/'));
        }
        catch { return 0; }
    }

    private static List<string> InferIncludes(string zipPath, string versionId)
    {
        var prefixes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var e in zip.Entries)
            {
                var rel = e.FullName.Replace('\\', '/');
                if (rel.Length == 0 || rel.EndsWith('/')) continue;
                var segs = rel.Split('/');
                if (segs[0].Equals("versions", StringComparison.OrdinalIgnoreCase))
                {
                    var id = segs.Length >= 2 ? segs[1] : "";
                    if (id.Length == 0) continue;
                    prefixes.Add(segs.Length <= 3 ? $"versions/{id}" : $"versions/{id}/{segs[2]}");
                }
                else
                {
                    prefixes.Add(segs[0]);
                }
            }
        }
        catch { /* 无法读取时留空，由用户手工重新备份 */ }
        return prefixes.ToList();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
