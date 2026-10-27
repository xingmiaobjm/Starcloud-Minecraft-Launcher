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
            PackZip.ExtractTo(mrpackPath, tempDir);
            var indexPath = FindIndex(tempDir)
                ?? throw new InvalidOperationException("不是有效的 .mrpack 整合包（缺少 modrinth.index.json）");
            var index = JsonSerializer.Deserialize<MrPackIndex>(PackJson.ReadFileText(indexPath), JsonOpts)
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
            PackZip.ExtractTo(mrpackPath, tempDir);

            var indexPath = FindIndex(tempDir)
                ?? throw new InvalidOperationException("不是有效的 .mrpack 整合包（缺少 modrinth.index.json）");
            var index = JsonSerializer.Deserialize<MrPackIndex>(PackJson.ReadFileText(indexPath), JsonOpts)
                        ?? throw new InvalidOperationException("modrinth.index.json 解析失败");

            var dotMc = installRoot ?? settings.DotMinecraft;
            Directory.CreateDirectory(dotMc);

            // 1. 批量下载声明的文件（mod / resourcepack / shader 等，按 index 相对路径落位）。
            //    env.client == unsupported 的是服务端专属文件，客户端不装（装了部分 mod 直接崩）。
            var files = (IReadOnlyList<MrPackFile>)index.Files
                .Where(f => f.Downloads.Count > 0 && f.Env?.Client != "unsupported").ToList();
            var batch = new List<(string Url, string Dest, string? Sha1, long Size)>(files.Count);
            var alternatives = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                // index 里的 path 是包内作者可写的字段，必须先过路径安全再落盘
                if (PackPathGuard.Resolve(dotMc, f.Path) is not { } dest)
                {
                    LauncherLog.Write("mrpack", $"跳过非法路径条目：{f.Path}");
                    continue;
                }
                batch.Add((f.Downloads[0], dest, f.Hashes?.Sha1, f.Size));
                if (f.Downloads.Count > 1) alternatives[dest] = f.Downloads.Skip(1).ToList();
            }
            if (batch.Count > 0)
            {
                status?.Report($"正在下载整合包文件（{batch.Count} 个）…");
                await _downloader.DownloadBatchAsync(batch, "整合包文件",
                    settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);

                // 主地址没拿到的文件（下载失败/校验不过，目标不存在）用 index 里声明的备选源补拉一次。
                // DownloadBatchAsync 本来就跳过已存在的文件，这里天然只补失败项。
                var missing = batch
                    .Where(b => !File.Exists(b.Dest))
                    .Select(b => (Pick(alternatives, b.Dest), b.Dest, b.Sha1, b.Size))
                    .Where(b => b.Item1 is not null)
                    .Select(b => (b.Item1!, b.Dest, b.Sha1, b.Size))
                    .ToList();
                if (missing.Count > 0)
                {
                    status?.Report($"正在用备用地址补拉 {missing.Count} 个文件…");
                    LauncherLog.Write("mrpack", $"主地址缺失 {missing.Count} 个文件，改走备选源");
                    await _downloader.DownloadBatchAsync(missing, "整合包文件（备用地址）",
                        settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);
                }
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

    /// <summary>大小写不敏感地找 modrinth.index.json（Linux 上包内可能是 Modrinth.index.json）。</summary>
    private static string? FindIndex(string tempDir) =>
        Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(p)
                .Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase));

    /// <summary>递归复制目录内容；逐条过路径安全，非法条目跳过（防归档内的越界相对路径）</summary>
    /// <summary>备选源表里挑一个还没试过的（主地址之外的第一个）；没有备选返回 null</summary>
    private static string? Pick(IReadOnlyDictionary<string, IReadOnlyList<string>> alternatives, string dest)
    {
        if (!alternatives.TryGetValue(dest, out var list)) return null;
        foreach (var url in list)
            if (!string.IsNullOrWhiteSpace(url)) return url;
        return null;
    }

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

    /// <summary>运行环境声明：client=unsupported 表示服务端专属（客户端装了反而崩），跳过</summary>
    [JsonPropertyName("env")]
    public MrPackEnv? Env { get; set; }
}

file sealed class MrPackEnv
{
    [JsonPropertyName("client")]
    public string? Client { get; set; }

    [JsonPropertyName("server")]
    public string? Server { get; set; }
}

file sealed class MrPackHashes
{
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }
}
