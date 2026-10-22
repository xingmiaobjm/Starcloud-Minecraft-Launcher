// 启动前预检：完整性检查 + 缺失文件自动补全（对应旧 Rust ensure_installed / needs_download）
// 检查深度：jar/库/natives 存在性 + SHA1；资源仅存在性（量可达数 GB，不做内容校验）

using System.Security.Cryptography;
using System.Text.Json;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

/// <summary>单个完整性问题（Kind：jar/lib/natives/index/asset）</summary>
public sealed record PreflightIssue(string Kind, string Path, string? Detail);

/// <summary>补全失败的文件与原因</summary>
public sealed record PreflightFailedFile(string Path, string Reason);

/// <summary>预检结果：问题清单 + 版本 JSON 状态</summary>
public sealed class PreflightReport
{
    public List<PreflightIssue> Issues { get; } = [];
    public bool VersionJsonBroken { get; internal set; }

    /// <summary>官方清单里该版本 JSON 的下载地址（vanilla 可自动恢复；Fabric 类为 null）</summary>
    public string? ManifestJsonUrl { get; internal set; }

    public bool NeedsRepair => Issues.Count > 0 || VersionJsonBroken;
    public bool CanAutoRestoreJson => VersionJsonBroken && ManifestJsonUrl is not null;
}

/// <summary>补全失败：个别文件重试穷尽后仍缺失或校验不通过</summary>
public sealed class PreflightException(IReadOnlyList<PreflightFailedFile> failedFiles)
    : Exception($"有 {failedFiles.Count} 个游戏文件补全失败")
{
    public IReadOnlyList<PreflightFailedFile> FailedFiles { get; } = failedFiles;
}

public sealed class LaunchPreflightService
{
    private readonly InstallerService _installer;
    private readonly DownloadService _downloader;
    private readonly ManifestService _manifest;

    public LaunchPreflightService(InstallerService installer, DownloadService downloader,
        ManifestService manifest)
    {
        _installer = installer;
        _downloader = downloader;
        _manifest = manifest;
    }

    /// <summary>启动前完整性检查（只查不动）。版本 JSON 缺失/损坏时其余项无法检查，直接返回。</summary>
    public async Task<PreflightReport> CheckAsync(string dotMinecraft, string versionId,
        CancellationToken ct = default)
    {
        var report = new PreflightReport();

        MinecraftVersion version;
        try
        {
            version = await _installer.LoadVersionAsync(dotMinecraft, versionId, ct);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException
                or JsonException or InvalidOperationException)
        {
            report.VersionJsonBroken = true;
            try
            {
                var manifest = await _manifest.GetManifestAsync(forceRefresh: false, ct);
                report.ManifestJsonUrl =
                    manifest.Versions.FirstOrDefault(v => v.Id == versionId)?.Url;
            }
            catch
            {
                /* 断网且无缓存：无法自动恢复 */
            }
            return report;
        }

        // 客户端 jar
        if (version.Downloads?.Client is { Url: not "" } client)
        {
            // 与启动器同口径：实例目录同名 jar 优先，其次目录内客户端 jar、JSON id 兜底
            var jarPath = GameLauncher.ResolveClientJar(dotMinecraft, versionId, version.Id);
            if (jarPath is null || !await Sha1MatchesAsync(jarPath, client.Sha1, ct))
                report.Issues.Add(new PreflightIssue("jar",
                    _installer.VersionJarPath(dotMinecraft, versionId), null));
        }

        // 库 + natives（与安装器同口径：规则过滤、有下载信息才检查）
        foreach (var lib in version.Libraries.Where(l => InstallerService.RulesAllow(l.Rules)))
        {
            var dl = lib.Downloads;
            // Mojang 用 downloads.artifact；Fabric/Quilt 只有 Maven 坐标，统一交给 MavenArtifacts
            if (MavenArtifacts.Resolve(lib) is { Path: { Length: > 0 } relative })
            {
                var libPath = Path.Combine(dotMinecraft, "libraries",
                    relative.Replace('/', Path.DirectorySeparatorChar));
                if (!await Sha1MatchesAsync(libPath, lib.Sha1 ?? dl?.Artifact?.Sha1, ct))
                    report.Issues.Add(new PreflightIssue("lib", libPath, null));
            }
            if (InstallerService.NativesOf(dl) is { Path: not null } native)
            {
                var nativePath = Path.Combine(dotMinecraft, "libraries", native.Path);
                if (!await Sha1MatchesAsync(nativePath, native.Sha1, ct))
                    report.Issues.Add(new PreflightIssue("natives", nativePath, null));
            }
        }

        // 资源索引：缺失或损坏时资源无法枚举，只报 index
        if (version.AssetIndex is { Id: not "" } index)
        {
            var indexPath = Path.Combine(dotMinecraft, "assets", "indexes", $"{index.Id}.json");
            if (!File.Exists(indexPath))
            {
                report.Issues.Add(new PreflightIssue("index", indexPath, null));
            }
            else
            {
                try
                {
                    var assetIndex = JsonSerializer.Deserialize<AssetIndexFile>(
                        await File.ReadAllTextAsync(indexPath, ct));
                    if (assetIndex is not null)
                    {
                        var objectsDir = Path.Combine(dotMinecraft, "assets", "objects");
                        foreach (var obj in assetIndex.Objects.Values.DistinctBy(o => o.Hash))
                        {
                            var assetPath = Path.Combine(objectsDir, obj.Hash[..2], obj.Hash);
                            if (!File.Exists(assetPath))
                                report.Issues.Add(new PreflightIssue("asset", assetPath, obj.Hash));
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    report.Issues.Add(new PreflightIssue("index", indexPath, null));
                }
            }
        }

        return report;
    }

    /// <summary>按预检结果自动补全缺失/损坏文件；个别文件重试穷尽仍不可用时抛 PreflightException。</summary>
    public async Task EnsureReadyAsync(string dotMinecraft, string versionId, PreflightReport report,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct = default)
    {
        // 1. 版本 JSON 自动恢复（vanilla），恢复后重新完整检查（此前其余项未能检查）
        if (report.VersionJsonBroken && report.CanAutoRestoreJson)
        {
            await _downloader.DownloadFileAsync(report.ManifestJsonUrl!,
                _installer.VersionJsonPath(dotMinecraft, versionId),
                sha1: null, settings.DownloadMaxRetries, ct);
            report = await CheckAsync(dotMinecraft, versionId, ct);
            if (report.VersionJsonBroken) // 恢复后仍解析失败：交给 UI 层报错
                throw new InvalidOperationException(
                    $"版本 {versionId} 的 JSON 恢复后仍无法解析，请到下载页重新安装");
        }

        var version = await _installer.LoadVersionAsync(dotMinecraft, versionId, ct);

        // 2. 损坏文件先删除：体积相同但内容损坏的文件会被批量下载按"已存在"跳过
        foreach (var issue in report.Issues)
            if (File.Exists(issue.Path))
                File.Delete(issue.Path);

        var files = new List<(string Url, string Dest, string? Sha1, long Size)>();
        var nativeJobs = new List<(string Dest, ExtractSpec? Extract)>();

        // 3. jar / 库 / natives 任务（索引单独处理）
        foreach (var issue in report.Issues)
        {
            switch (issue.Kind)
            {
                case "jar" when version.Downloads?.Client is { Url: not "" } client:
                    files.Add((client.Url, _installer.VersionJarPath(dotMinecraft, versionId),
                        client.Sha1, client.Size));
                    break;
                case "lib" or "natives":
                    AddLibTask(version, dotMinecraft, issue.Path, files, nativeJobs);
                    break;
            }
        }

        // 4. 资源：索引缺失时先重下索引再全量补；否则按 hash 精确补
        if (report.Issues.Any(i => i.Kind == "index") &&
            version.AssetIndex is { Id: not "", Url: not "" } index)
        {
            var indexPath = Path.Combine(dotMinecraft, "assets", "indexes", $"{index.Id}.json");
            await _downloader.DownloadFileAsync(index.Url, indexPath, index.Sha1,
                settings.DownloadMaxRetries, ct);
            var assetIndex = JsonSerializer.Deserialize<AssetIndexFile>(
                await File.ReadAllTextAsync(indexPath, ct));
            if (assetIndex is not null)
                foreach (var hash in assetIndex.Objects.Values.Select(o => o.Hash).Distinct())
                    AddAssetTask(dotMinecraft, hash, files);
        }
        else
        {
            foreach (var hash in report.Issues.Where(i => i.Kind == "asset").Select(i => i.Detail!))
                AddAssetTask(dotMinecraft, hash, files);
        }

        // 5. 批量下载（复用并发 + 重试 + 下载后 SHA1 校验）；失败后重扫收集失败清单
        try
        {
            await _downloader.DownloadBatchAsync(files, "补全游戏文件",
                settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);
        }
        catch (Exception)
        {
            var failed = await CollectFailuresAsync(files);
            if (failed.Count > 0) throw new PreflightException(failed);
            throw;
        }

        // 6. natives 解压
        if (nativeJobs.Count > 0)
        {
            var nativesDir = Path.Combine(_installer.VersionDir(dotMinecraft, versionId), "natives");
            Directory.CreateDirectory(nativesDir);
            foreach (var job in nativeJobs)
                InstallerService.ExtractNatives(job.Dest, nativesDir, job.Extract);
        }
    }

    /// <summary>按库文件路径反查版本模型，构造下载与 natives 解压任务（路径不匹配则跳过）</summary>
    private static void AddLibTask(MinecraftVersion version, string dotMinecraft, string issuePath,
        List<(string Url, string Dest, string? Sha1, long Size)> files,
        List<(string Dest, ExtractSpec? Extract)> nativeJobs)
    {
        foreach (var lib in version.Libraries.Where(l => InstallerService.RulesAllow(l.Rules)))
        {
            var dl = lib.Downloads;
            if (MavenArtifacts.Resolve(lib) is { Path: { Length: > 0 } relative } artifact &&
                Path.Combine(dotMinecraft, "libraries",
                        relative.Replace('/', Path.DirectorySeparatorChar))
                    .Equals(issuePath, StringComparison.OrdinalIgnoreCase))
            {
                files.Add((artifact.Url, issuePath, artifact.Sha1, artifact.Size));
            }
            if (InstallerService.NativesOf(dl) is { Path: not null } native &&
                Path.Combine(dotMinecraft, "libraries", native.Path)
                    .Equals(issuePath, StringComparison.OrdinalIgnoreCase))
            {
                files.Add((native.Url, issuePath, native.Sha1, native.Size));
                nativeJobs.Add((issuePath, lib.Extract));
            }
        }
    }

    private static void AddAssetTask(string dotMinecraft, string hash,
        List<(string Url, string Dest, string? Sha1, long Size)> files)
    {
        var prefix = hash[..2];
        files.Add((
            $"https://resources.download.minecraft.net/{prefix}/{hash}",
            Path.Combine(dotMinecraft, "assets", "objects", prefix, hash),
            hash, 0));
    }

    /// <summary>下载中断后重扫任务清单，收集仍不达标的文件</summary>
    private static async Task<List<PreflightFailedFile>> CollectFailuresAsync(
        List<(string Url, string Dest, string? Sha1, long Size)> files)
    {
        var failed = new List<PreflightFailedFile>();
        foreach (var f in files)
        {
            if (!await Sha1MatchesAsync(f.Dest, f.Sha1, CancellationToken.None))
                failed.Add(new PreflightFailedFile(f.Dest, "下载后仍缺失或校验不通过"));
        }
        return failed;
    }

    private static async Task<bool> Sha1MatchesAsync(string path, string? expected,
        CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrEmpty(expected)) return true; // 无校验值，存在即可
        await using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA1.HashData(fs));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
