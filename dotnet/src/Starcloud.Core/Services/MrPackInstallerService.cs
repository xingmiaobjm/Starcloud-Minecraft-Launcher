// Modrinth .mrpack 整合包自动安装：解析 modrinth.index.json → 下载文件 → 复制 overrides

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>整合包安装结果（含所需游戏版本与加载器依赖，供调用方联动安装）</summary>
public sealed record MrPackInstallResult(
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    int DownloadedFiles,
    long TotalBytes);

/// <summary>本地 .mrpack 的元信息（导入前预览用）</summary>
public sealed record MrPackPackageInfo(
    string Name,
    string VersionId,
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    int DownloadFileCount,
    int OverrideFileCount,
    long TotalBytes,
    int FormatVersion);

public sealed class MrPackInstallerService
{
    private readonly DownloadService _downloader;

    public MrPackInstallerService(DownloadService downloader) => _downloader = downloader;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>读取 .mrpack 元信息（解压到临时目录后统计，不落地任何游戏文件）</summary>
    public MrPackPackageInfo ReadPackageInfo(string mrpackPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "starcloud-mrpack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ZipFile.ExtractToDirectory(mrpackPath, tempDir, overwriteFiles: true);
            var indexPath = Path.Combine(tempDir, "modrinth.index.json");
            if (!File.Exists(indexPath))
                throw new InvalidOperationException("不是有效的 .mrpack 整合包（缺少 modrinth.index.json）");
            var index = JsonSerializer.Deserialize<MrPackIndex>(File.ReadAllText(indexPath), JsonOpts)
                        ?? throw new InvalidOperationException("modrinth.index.json 解析失败");

            var overrideCount = 0;
            foreach (var name in new[] { "overrides", "client-overrides" })
            {
                var dir = Path.Combine(tempDir, name);
                if (Directory.Exists(dir))
                    overrideCount += Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
            }

            return new MrPackPackageInfo(
                string.IsNullOrWhiteSpace(index.Name) ? Path.GetFileNameWithoutExtension(mrpackPath) : index.Name,
                index.VersionId ?? "",
                index.Dependencies.GetValueOrDefault("minecraft") ?? "",
                index.Dependencies,
                index.Files.Count(f => f.Downloads.Count > 0),
                overrideCount,
                index.Files.Sum(f => Math.Max(f.Size, 0)),
                index.FormatVersion);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
        }
    }

    /// <summary>安装 .mrpack 整合包（installRoot 为空时落 .minecraft 根；支持装进隔离实例目录）</summary>
    public async Task<MrPackInstallResult> InstallAsync(
        string mrpackPath, LauncherSettings settings, string? installRoot = null,
        IProgress<string>? status = null, IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "starcloud-mrpack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            status?.Report("正在解压整合包…");
            ZipFile.ExtractToDirectory(mrpackPath, tempDir, overwriteFiles: true);

            var indexPath = Path.Combine(tempDir, "modrinth.index.json");
            if (!File.Exists(indexPath))
                throw new InvalidOperationException("不是有效的 .mrpack 整合包（缺少 modrinth.index.json）");
            var index = JsonSerializer.Deserialize<MrPackIndex>(File.ReadAllText(indexPath), JsonOpts)
                        ?? throw new InvalidOperationException("modrinth.index.json 解析失败");

            var dotMc = installRoot ?? settings.DotMinecraft;
            Directory.CreateDirectory(dotMc);

            // 1. 批量下载声明的文件（mod / resourcepack / shader 等，按 index 相对路径落位）
            var files = (IReadOnlyList<MrPackFile>)index.Files
                .Where(f => f.Downloads.Count > 0).ToList();
            var batch = new List<(string Url, string Dest, string? Sha1, long Size)>(files.Count);
            foreach (var f in files)
            {
                // index 里的 path 是包内作者可写的字段，必须先过路径安全再落盘
                if (PackPathGuard.Resolve(dotMc, f.Path) is not { } dest)
                {
                    LauncherLog.Write("mrpack", $"跳过非法路径条目：{f.Path}");
                    continue;
                }
                batch.Add((f.Downloads[0], dest, f.Hashes?.Sha1, f.Size));
            }
            if (batch.Count > 0)
            {
                status?.Report($"正在下载整合包文件（{batch.Count} 个）…");
                await _downloader.DownloadBatchAsync(batch, "整合包文件",
                    settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);
            }

            // 2. overrides/ 与 client-overrides/ 覆盖到实例根（客户端专用覆盖后写，优先级更高）
            foreach (var name in new[] { "overrides", "client-overrides" })
            {
                var dir = Path.Combine(tempDir, name);
                if (!Directory.Exists(dir)) continue;
                status?.Report($"正在应用 {name}…");
                CopyDirectory(dir, dotMc);
            }

            return new MrPackInstallResult(
                index.Dependencies.GetValueOrDefault("minecraft") ?? "",
                index.Dependencies, batch.Count, batch.Sum(b => Math.Max(b.Size, 0)));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
        }
    }

    /// <summary>递归复制目录内容；逐条过路径安全，非法条目跳过（防归档内的越界相对路径）</summary>
    private static void CopyDirectory(string src, string dst)
    {
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            if (PackPathGuard.Resolve(dst, rel) is { } target) Directory.CreateDirectory(target);
            else LauncherLog.Write("mrpack", $"跳过非法目录：{rel}");
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (PackPathGuard.Resolve(dst, rel) is not { } dest)
            {
                LauncherLog.Write("mrpack", $"跳过非法文件：{rel}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

// ── modrinth.index.json DTO ──

file sealed class MrPackIndex
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("versionId")]
    public string? VersionId { get; set; }

    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = [];

    [JsonPropertyName("files")]
    public List<MrPackFile> Files { get; set; } = [];
}

file sealed class MrPackFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("hashes")]
    public MrPackHashes? Hashes { get; set; }

    [JsonPropertyName("downloads")]
    public List<string> Downloads { get; set; } = [];

    [JsonPropertyName("fileSize")]
    public long Size { get; set; }
}

file sealed class MrPackHashes
{
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }
}
