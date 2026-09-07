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

public sealed class MrPackInstallerService
{
    private readonly DownloadService _downloader;

    public MrPackInstallerService(DownloadService downloader) => _downloader = downloader;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>安装 .mrpack 整合包到 .minecraft 根（mods/config/resourcepacks 等按包内相对路径落位）</summary>
    public async Task<MrPackInstallResult> InstallAsync(
        string mrpackPath, LauncherSettings settings,
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

            var dotMc = settings.DotMinecraft;
            Directory.CreateDirectory(dotMc);

            // 1. 批量下载声明的文件（mod / resourcepack / shader 等，按 index 相对路径落位）
            var files = (IReadOnlyList<MrPackFile>)index.Files
                .Where(f => f.Downloads.Count > 0).ToList();
            var batch = files
                .Select(f => (
                    Url: f.Downloads[0],
                    Dest: Path.Combine(dotMc, f.Path.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1: f.Hashes?.Sha1,
                    Size: f.Size))
                .ToList();
            if (batch.Count > 0)
            {
                status?.Report($"正在下载整合包文件（{batch.Count} 个）…");
                await _downloader.DownloadBatchAsync(batch, "整合包文件",
                    settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);
            }

            // 2. overrides/ 目录内容覆盖到实例根
            var overridesDir = Path.Combine(tempDir, "overrides");
            if (Directory.Exists(overridesDir))
            {
                status?.Report("正在应用 overrides…");
                CopyDirectory(overridesDir, dotMc);
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

    private static void CopyDirectory(string src, string dst)
    {
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(dst, Path.GetRelativePath(src, file));
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
