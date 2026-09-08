// 安装器：原版（客户端 jar + 库 + 资源）与 Fabric（loader profile 合并）

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

public sealed class InstallerService
{
    private readonly DownloadService _downloader;

    public InstallerService(DownloadService downloader) => _downloader = downloader;

    public string VersionDir(string dotMinecraft, string id) =>
        Path.Combine(dotMinecraft, "versions", id);

    public string VersionJsonPath(string dotMinecraft, string id) =>
        Path.Combine(VersionDir(dotMinecraft, id), $"{id}.json");

    public string VersionJarPath(string dotMinecraft, string id) =>
        Path.Combine(VersionDir(dotMinecraft, id), $"{id}.jar");

    public bool IsInstalled(string dotMinecraft, string id) =>
        File.Exists(VersionJsonPath(dotMinecraft, id));

    /// <summary>加载版本 JSON（含一层 inheritsFrom 合并，如 Fabric 继承原版）</summary>
    public async Task<MinecraftVersion> LoadVersionAsync(string dotMinecraft, string id,
        CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(VersionJsonPath(dotMinecraft, id), ct);
        var version = JsonSerializer.Deserialize<MinecraftVersion>(json)
                      ?? throw new InvalidOperationException($"版本 {id} 解析失败");
        if (!string.IsNullOrEmpty(version.InheritsFrom))
        {
            var parent = await LoadVersionAsync(dotMinecraft, version.InheritsFrom, ct);
            return MergeVersions(parent, version);
        }
        return version;
    }

    /// <summary>子版本（如 Fabric）合并到父版本：库/参数追加，mainClass 覆盖</summary>
    public static MinecraftVersion MergeVersions(MinecraftVersion parent, MinecraftVersion child)
    {
        parent.MainClass = string.IsNullOrEmpty(child.MainClass) ? parent.MainClass : child.MainClass;
        parent.Libraries.AddRange(child.Libraries);
        if (child.Arguments is not null)
        {
            parent.Arguments ??= new VersionArguments();
            parent.Arguments.Game.AddRange(child.Arguments.Game);
            parent.Arguments.Jvm.InsertRange(0, child.Arguments.Jvm);
        }
        parent.InheritsFrom = null;
        return parent;
    }

    /// <summary>安装原版：client json + jar + 库 + 资源</summary>
    public async Task InstallVanillaAsync(
        string versionId, string versionUrl, LauncherSettings settings,
        IProgress<BatchProgress>? progress, CancellationToken ct = default)
    {
        var dotMc = settings.DotMinecraft;
        Directory.CreateDirectory(VersionDir(dotMc, versionId));

        // 1. 版本 JSON
        var versionJson = await _downloader.GetStringAsync(versionUrl, ct);
        var version = JsonSerializer.Deserialize<MinecraftVersion>(versionJson)
                      ?? throw new InvalidOperationException($"版本 {versionId} 解析失败");
        await File.WriteAllTextAsync(VersionJsonPath(dotMc, versionId), versionJson, ct);

        // 2. 安装内容（jar + 库 + 资源）
        await InstallVersionContentAsync(version, versionId, settings, progress, ct);
    }

    /// <summary>安装 Fabric：拉 loader profile 合并保存，再装全量内容</summary>
    public async Task InstallFabricAsync(string mcVersion, string loaderVersion,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct = default)
    {
        var profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
        var profileJson = await _downloader.GetStringAsync(profileUrl, ct);
        var profile = JsonSerializer.Deserialize<FabricProfile>(profileJson)
                      ?? throw new InvalidOperationException("Fabric profile 解析失败");

        var dotMc = settings.DotMinecraft;
        var id = profile.Id;
        Directory.CreateDirectory(VersionDir(dotMc, id));

        // 读取原版版本 JSON 并合并
        var baseJson = await File.ReadAllTextAsync(VersionJsonPath(dotMc, mcVersion), ct);
        var baseVersion = JsonSerializer.Deserialize<MinecraftVersion>(baseJson)
                          ?? throw new InvalidOperationException($"原版 {mcVersion} 解析失败");
        var merged = MergeVersions(baseVersion, profile.ToVersion(id));

        await File.WriteAllTextAsync(
            VersionJsonPath(dotMc, id),
            JsonSerializer.Serialize(merged, SettingsJson.Options), ct);

        await InstallVersionContentAsync(merged, id, settings, progress, ct);
    }

    private async Task InstallVersionContentAsync(MinecraftVersion version, string id,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var dotMc = settings.DotMinecraft;

        // client jar
        var client = version.Downloads?.Client
                     ?? throw new InvalidOperationException("版本缺少 client 下载信息");
        var files = new List<(string Url, string Dest, string? Sha1, long Size)>
        {
            (client.Url, VersionJarPath(dotMc, id), client.Sha1, client.Size),
        };

        // 库（含规则过滤 + windows natives）
        var versionDir = VersionDir(dotMc, id);
        var nativesDir = Path.Combine(versionDir, "natives");
        var nativesJobs = new List<(DownloadableFile File, string Dest, ExtractSpec? Extract)>();
        foreach (var lib in version.Libraries.Where(l => RulesAllow(l.Rules)))
        {
            var dl = lib.Downloads;
            if (dl?.Artifact is { Url: not "" } artifact)
            {
                files.Add((artifact.Url, Path.Combine(dotMc, "libraries", artifact.Path ?? ""),
                    artifact.Sha1, artifact.Size));
            }
            if (dl?.Classifiers is not null &&
                dl.Classifiers.TryGetValue("natives-windows", out var native))
            {
                nativesJobs.Add((native,
                    Path.Combine(dotMc, "libraries", native.Path ?? ""), lib.Extract));
            }
        }

        // 资源索引
        if (version.AssetIndex is { } index)
        {
            var indexDir = Path.Combine(dotMc, "assets", "indexes");
            Directory.CreateDirectory(indexDir);
            var indexPath = Path.Combine(indexDir, $"{index.Id}.json");
            if (!File.Exists(indexPath))
                await _downloader.DownloadFileAsync(index.Url, indexPath, index.Sha1,
                    settings.DownloadMaxRetries, ct);

            var indexJson = await File.ReadAllTextAsync(indexPath, ct);
            var assetIndex = JsonSerializer.Deserialize<AssetIndexFile>(indexJson);
            if (assetIndex is not null)
            {
                var objectsDir = Path.Combine(dotMc, "assets", "objects");
                foreach (var (name, obj) in assetIndex.Objects)
                {
                    var hashPrefix = obj.Hash[..2];
                    files.Add((
                        $"https://resources.download.minecraft.net/{hashPrefix}/{obj.Hash}",
                        Path.Combine(objectsDir, hashPrefix, obj.Hash),
                        obj.Hash, obj.Size));
                }
            }
        }

        // 批量下载
        await _downloader.DownloadBatchAsync(files, "下载游戏文件",
            settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);

        // natives 下载 + 解压
        if (nativesJobs.Count > 0)
        {
            Directory.CreateDirectory(nativesDir);
            var nativeFiles = nativesJobs
                .Select(j => (j.File.Url, j.Dest, (string?)j.File.Sha1, j.File.Size)).ToList();
            await _downloader.DownloadBatchAsync(nativeFiles.Select(f => (f.Item1, f.Item2, f.Item3, f.Item4)).ToList(),
                "下载本地库", settings.DownloadConcurrency, settings.DownloadMaxRetries, progress, ct);
            foreach (var job in nativesJobs)
            {
                ExtractNatives(job.Dest, nativesDir, job.Extract);
            }
        }
    }

    /// <summary>解压 natives jar 中的 dll 到 natives 目录（受 extract.exclude 过滤）</summary>
    public static void ExtractNatives(string nativesJarPath, string nativesDir, ExtractSpec? extract)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(nativesJarPath);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (extract?.Exclude?.Any(e => entry.FullName.Contains(e)) == true) continue;
            var target = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>Minecraft 库规则判定（os: windows）</summary>
    public static bool RulesAllow(List<OsRule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            var matches = rule.Os is null || rule.Os.Name is null ||
                          rule.Os.Name.Equals("windows", StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;
            if (rule.Action == "allow") allowed = true;
            else if (rule.Action == "deny") allowed = false;
        }
        return allowed;
    }
}

internal static class FabricProfileExtensions
{
    public static MinecraftVersion ToVersion(this FabricProfile p, string id) => new()
    {
        Id = id,
        MainClass = p.MainClass,
        Arguments = p.Arguments,
        Libraries = p.Libraries,
        Type = p.Type,
        ReleaseTime = p.ReleaseTime ?? p.Time,
    };
}
