// 导出 .mrpack 整合包：目录发现 → SHA1 反查 Modrinth → files[] + overrides 打包

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>导出时的候选目录（扫描实例根得到）</summary>
public sealed record ExportDirCandidate(string Name, bool IsStandard, int FileCount, long SizeBytes);

/// <summary>导出请求</summary>
public sealed record ExportRequest(
    string GameRoot,
    string TargetZipPath,
    string Name,
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<string> Dirs);

/// <summary>导出结果</summary>
public sealed record ExportResult(
    int DownloadableFiles, int OverriddenFiles, int SkippedFiles, long TotalBytes, string? LoaderWarning);

public sealed class ModPackExportService(ModrinthService mods)
{
    /// <summary>运行期/公共目录，不作为导出候选</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "libraries", "assets", "versions", "logs", "crash-reports", "natives",
        "starcloud", ".fabric", ".mixin.out", "backups",
    };

    /// <summary>标准内容目录（勾选时默认选中）</summary>
    private static readonly string[] StandardDirs =
        ["mods", "config", "resourcepacks", "shaderpacks", "datapacks"];

    /// <summary>系统噪音文件，不导出</summary>
    private static readonly string[] NoiseNames = [".DS_Store", "Thumbs.db", "desktop.ini"];

    private static readonly string[] LoaderKeys =
        ["fabric-loader", "quilt-loader", "forge", "neoforge"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>扫描实例根，列出可选导出目录（标准目录恒列出，其余取实际存在者）</summary>
    public List<ExportDirCandidate> DiscoverDirs(string gameRoot)
    {
        var names = new List<string>(StandardDirs);
        if (Directory.Exists(gameRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(gameRoot))
            {
                var name = Path.GetFileName(dir);
                if (Excluded.Contains(name)) continue;
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                names.Add(name);
            }
        }

        var result = new List<ExportDirCandidate>();
        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var files = EnumerateContent(gameRoot, [name], out _);
            var isStandard = StandardDirs.Contains(name, StringComparer.OrdinalIgnoreCase);
            result.Add(new ExportDirCandidate(name, isStandard, files.Count, files.Sum(f => f.Size)));
        }
        return result;
    }

    /// <summary>按勾选目录导出 .mrpack（识别出的走 files[]，其余打进 overrides/）</summary>
    public Task<ExportResult> ExportAsync(ExportRequest request,
        IProgress<string>? status = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "starcloud-export", Guid.NewGuid().ToString("N"));
            var overridesRoot = Path.Combine(tempDir, "overrides");
            Directory.CreateDirectory(overridesRoot);
            try
            {
                status?.Report("正在扫描导出内容…");
                var files = EnumerateContent(request.GameRoot, request.Dirs, out var skipped);

                status?.Report($"正在计算校验值（{files.Count} 个文件）…");
                var sha1s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    using var stream = File.OpenRead(file.Abs);
                    sha1s[file.Rel] = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
                }

                status?.Report("正在识别 Modrinth 资源…");
                Dictionary<string, ModrinthVersion> known = [];
                try
                {
                    known = await mods.GetVersionsByHashesAsync(
                        sha1s.Values.Distinct().ToList(), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 反查失败 → 全部打进 overrides，不阻断导出 */ }

                var indexFiles = new List<ExportFile>();
                var overridden = 0;
                long totalBytes = 0;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    totalBytes += file.Size;
                    var sha1 = sha1s[file.Rel];
                    if (known.TryGetValue(sha1, out var version)
                        && version.PrimaryFile is { Url.Length: > 0 } primary)
                    {
                        indexFiles.Add(new ExportFile
                        {
                            Path = file.Rel,
                            Hashes = new ExportHashes { Sha1 = sha1 },
                            Downloads = [primary.Url],
                            FileSize = primary.Size > 0 ? primary.Size : file.Size,
                        });
                    }
                    else
                    {
                        var dest = Path.Combine(overridesRoot,
                            file.Rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(file.Abs, dest, overwrite: true);
                        overridden++;
                    }
                }

                var index = new ExportIndex
                {
                    FormatVersion = 1,
                    GameVersion = request.GameVersion,
                    Name = request.Name,
                    VersionId = "1.0.0",
                    Dependencies = new Dictionary<string, string>(request.Dependencies),
                    Files = indexFiles,
                };
                File.WriteAllText(Path.Combine(tempDir, "modrinth.index.json"),
                    JsonSerializer.Serialize(index, JsonOpts));

                status?.Report("正在打包…");
                var tmpZip = request.TargetZipPath + ".tmp";
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
                ZipFile.CreateFromDirectory(tempDir, tmpZip,
                    CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(tmpZip, request.TargetZipPath, overwrite: true);

                var warning = LoaderKeys.Any(request.Dependencies.ContainsKey)
                    ? null
                    : "未识别到加载器依赖（Fabric/Forge/NeoForge），导入方需自行选择加载器";
                return new ExportResult(indexFiles.Count, overridden, skipped, totalBytes, warning);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
            }
        }, ct);

    /// <summary>枚举勾选目录下的待导出文件（相对 GameRoot 的 posix 路径；噪音文件计入 skipped）</summary>
    private static List<ContentFile> EnumerateContent(
        string gameRoot, IReadOnlyList<string> dirs, out int skipped)
    {
        var list = new List<ContentFile>();
        var count = 0;
        foreach (var dir in dirs)
        {
            var name = dir.Replace('\\', '/').Trim('/');
            if (name.Length == 0) continue;
            var abs = Path.Combine(gameRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(abs))
            {
                count++;
                if (!IsNoise(name)) list.Add(new ContentFile(name, abs, new FileInfo(abs).Length));
                continue;
            }
            if (!Directory.Exists(abs)) continue;
            foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
            {
                count++;
                var rel = Path.GetRelativePath(gameRoot, file).Replace('\\', '/');
                if (IsNoise(rel)) continue;
                list.Add(new ContentFile(rel, file, new FileInfo(file).Length));
            }
        }
        skipped = count - list.Count;
        return list;
    }

    private static bool IsNoise(string rel)
    {
        if (NoiseNames.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase)) return true;
        return rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
               || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || rel.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ContentFile(string Rel, string Abs, long Size);
}

// ── modrinth.index.json 写出 DTO ──

file sealed class ExportIndex
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("game")]
    public string Game { get; set; } = "minecraft";

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = "";

    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ExportFile> Files { get; set; } = [];
}

file sealed class ExportFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("hashes")]
    public ExportHashes Hashes { get; set; } = new();

    [JsonPropertyName("downloads")]
    public List<string> Downloads { get; set; } = [];

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }
}

file sealed class ExportHashes
{
    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";
}
