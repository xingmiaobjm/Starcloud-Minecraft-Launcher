// CurseForge 整合包安装：manifest.json → 逐文件解析直链并下载 → overrides 覆盖。
//
// CurseForge 整合包结构与 Modrinth 的 .mrpack 不同，但对我们的下游是同一件事：
// 都需要「装好 MC 版本 + 加载器，再把包内文件铺到实例根」。所以安装结果直接复用
// <see cref="MrPackInstallResult"/>，加载器联动继续走 App 侧的 MrPackFlow，一行不用改。
//
// manifest.json 关键字段：
//   { "minecraft": { "version": "1.20.1",
//                    "modLoaders": [ { "id": "forge-47.2.0", "primary": true } ] },
//     "name": "...", "version": "1.0.0",
//     "files": [ { "projectID": 394468, "fileID": 8756581, "required": true } ],
//     "overrides": "overrides" }

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>CurseForge 整合包预览信息</summary>
public sealed record CurseForgePackInfo(
    string Name,
    string Version,
    string GameVersion,
    string? LoaderId,
    int FileCount,
    int OverrideFileCount,
    long TotalBytes);

public sealed class CurseForgePackInstaller
{
    private readonly DownloadService _downloader;
    private readonly CurseForgeService _curseForge;

    public CurseForgePackInstaller(DownloadService downloader, CurseForgeService curseForge)
    {
        _downloader = downloader;
        _curseForge = curseForge;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>读取整合包元信息（不落任何游戏文件）</summary>
    public CurseForgePackInfo ReadPackageInfo(string zipPath)
    {
        using var zip = PackZip.Open(zipPath);
        var manifest = ReadManifest(zip)
            ?? throw new InvalidOperationException("不是有效的 CurseForge 整合包（缺少 manifest.json）");
        var overrides = OverridePrefix(manifest);
        var overrideCount = zip.Entries.Count(e =>
            e.FullName.Replace('\\', '/').StartsWith(overrides + "/", StringComparison.OrdinalIgnoreCase));
        return new CurseForgePackInfo(
            string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(zipPath) : manifest.Name!,
            manifest.Version ?? "",
            manifest.Minecraft?.Version ?? "",
            PrimaryLoader(manifest),
            manifest.Files.Count,
            overrideCount,
            manifest.Files.Sum(f => f.FileLength));
    }

    /// <summary>
    /// 安装整合包（installRoot 为空落 .minecraft 根）。
    /// 部分文件可能因为作者未开启分发（allowModDistribution=false）拿不到直链——这类会被跳过并计入
    /// 诊断日志，不会让整包装配失败（与 PCL2/HMCL 的处理一致）。
    /// </summary>
    public async Task<MrPackInstallResult> InstallAsync(
        string zipPath, LauncherSettings settings, string? installRoot = null,
        IProgress<string>? status = null, IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var zip = PackZip.Open(zipPath);
        var manifest = ReadManifest(zip)
            ?? throw new InvalidOperationException("不是有效的 CurseForge 整合包（缺少 manifest.json）");

        var dotMc = installRoot ?? settings.DotMinecraft;
        Directory.CreateDirectory(dotMc);
        var overrides = OverridePrefix(manifest);

        // 1. 解析文件直链（官方 API 拿文件名/直链；拿不到直链的用 CDN 规则兜底）
        status?.Report($"正在解析整合包文件（{manifest.Files.Count} 个）…");
        var batch = new List<(string Url, string Dest, string? Sha1, long Size)>();
        var skipped = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var version = await _curseForge.GetFileAsync(file.ProjectId.ToString(), file.FileId.ToString(), ct);
            if (version?.PrimaryFile is not { } pf || string.IsNullOrWhiteSpace(pf.Url))
            {
                skipped++;
                LauncherLog.Write("curseforge",
                    $"整合包文件无法解析，已跳过：project={file.ProjectId} file={file.FileId}");
                continue;
            }
            // 文件名来自 API 响应，同样当不可信输入处理：压成单段安全名后再落到 mods/
            var name = PackPathGuard.SafeFileName(pf.Filename, "mod.jar");
            batch.Add((pf.Url, Path.Combine(dotMc, "mods", name), pf.Sha1, pf.Size));
        }

        if (batch.Count > 0)
        {
            status?.Report($"正在下载整合包文件（{batch.Count} 个）…");
            await _downloader.DownloadBatchAsync(batch, "整合包文件",
                settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);
        }

        // 2. overrides 覆盖到实例根
        var overrideEntries = zip.Entries
            .Where(e => e.FullName.Replace('\\', '/').StartsWith(overrides + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overrideEntries.Count > 0)
        {
            status?.Report("正在应用 overrides…");
            CopyOverrides(overrideEntries, overrides, dotMc);
        }

        if (skipped > 0) status?.Report($"已跳过 {skipped} 个不可下载文件（作者未开启分发）");

        var dependencies = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(manifest.Minecraft?.Version))
            dependencies["minecraft"] = manifest.Minecraft!.Version!;
        if (ParseLoader(PrimaryLoader(manifest)) is var (loaderName, loaderVersion) && loaderName is not null)
            dependencies[loaderName] = loaderVersion ?? "";

        return new MrPackInstallResult(
            manifest.Minecraft?.Version ?? "", dependencies, batch.Count, batch.Sum(b => Math.Max(b.Size, 0)));
    }

    /// <summary>
    /// 加载器 id（形如 "forge-47.2.0" / "neoforge-21.1.9" / "fabric-0.15.11"）→
    /// MrPackFlow 认识的依赖键与版本号。
    /// </summary>
    public static (string? Name, string? Version) ParseLoader(string? loaderId)
    {
        if (string.IsNullOrWhiteSpace(loaderId)) return (null, null);
        var text = loaderId.Trim();
        var dash = text.IndexOf('-');
        var prefix = (dash > 0 ? text[..dash] : text).ToLowerInvariant();
        var version = dash > 0 ? text[(dash + 1)..] : "";
        var name = prefix switch
        {
            "forge" => "forge",
            "neoforge" => "neoforge",
            "fabric" or "fabricloader" or "fabric-loader" => "fabric-loader",
            "quilt" or "quiltloader" => "quilt-loader",
            _ => null,
        };
        return (name, string.IsNullOrWhiteSpace(version) ? null : version);
    }

    private static string? PrimaryLoader(CurseForgeManifest manifest)
    {
        var loaders = manifest.Minecraft?.ModLoaders;
        if (loaders is null || loaders.Count == 0) return null;
        return loaders.FirstOrDefault(l => l.Primary)?.Id ?? loaders[0].Id;
    }

    private static string OverridePrefix(CurseForgeManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.Overrides) ? "overrides" : manifest.Overrides!.Trim('/');

    private static CurseForgeManifest? ReadManifest(ZipArchive zip)
    {
        // 大小写不敏感 + 去 BOM：真实整合包里 Manifest.json / 带 BOM 的 manifest.json 都出现过
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<CurseForgeManifest>(PackJson.ReadText(stream), JsonOpts);
    }

    /// <summary>
    /// 把 overrides 内容铺到实例根；条目路径逐条校验，拒绝绝对路径与 `..` 穿越
    /// （整合包是第三方产物，不能当可信输入）。
    /// </summary>
    private static void CopyOverrides(
        IReadOnlyList<ZipArchiveEntry> entries, string prefix, string dotMc)
    {
        var root = Path.GetFullPath(dotMc);
        foreach (var entry in entries)
        {
            if (entry.Name.Length == 0) continue; // 目录项
            var relative = entry.FullName.Replace('\\', '/');
            relative = relative[(prefix.Length + 1)..];
            if (relative.Length == 0) continue;
            var parts = relative.Split('/');
            if (parts.Any(p => p == ".." || p.Length == 0) || relative.Contains(':')) continue;

            var dest = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }
}

// ── manifest.json DTO ──

internal sealed class CurseForgeManifest
{
    [JsonPropertyName("minecraft")] public CurseForgeMinecraft? Minecraft { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("files")] public List<CurseForgeManifestFile> Files { get; set; } = [];
    [JsonPropertyName("overrides")] public string? Overrides { get; set; }
}

internal sealed class CurseForgeMinecraft
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("modLoaders")] public List<CurseForgeLoader> ModLoaders { get; set; } = [];
}

internal sealed class CurseForgeLoader
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
}

internal sealed class CurseForgeManifestFile
{
    [JsonPropertyName("projectID")] public long ProjectId { get; set; }
    [JsonPropertyName("fileID")] public long FileId { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("fileLength")] public long FileLength { get; set; }
}
