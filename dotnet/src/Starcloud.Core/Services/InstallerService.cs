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

    /// <summary>
    /// 生成不冲突的版本 id：已存在时追加 -2、-3…，允许本地并存多个相同版本。
    /// </summary>
    public string UniqueVersionId(string dotMinecraft, string id)
    {
        if (!IsInstalled(dotMinecraft, id)) return id;
        for (var i = 2; ; i++)
            if (!IsInstalled(dotMinecraft, $"{id}-{i}")) return $"{id}-{i}";
    }

    /// <summary>
    /// 同一版本已安装了几份（<c>id</c> 本身，加上 <c>id-2</c>、<c>id-3</c>… 这些副本）。
    /// 只认纯数字后缀，<c>26.3-rc-3</c> 这种不算副本。
    /// </summary>
    public int CountInstalledCopies(string dotMinecraft, string id)
    {
        var dir = Path.Combine(dotMinecraft, "versions");
        if (!Directory.Exists(dir)) return 0;
        var count = 0;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (!File.Exists(Path.Combine(sub, name + ".json"))) continue;
            if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }
            if (name.Length > id.Length + 1 &&
                name.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name[(id.Length + 1)..], out _))
                count++;
        }
        return count;
    }

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
        var jsonPath = VersionJsonPath(dotMc, versionId);
        await File.WriteAllTextAsync(jsonPath, versionJson, ct);

        // 同名版本的第二份起，目录名是 1.20.1-2，下载到的 JSON 里 id 还写着 1.20.1。
        // 不同步的话这份副本在详情页/启动器眼里仍是原版，等于"装了多份但只有一份有名字"。
        if (!string.Equals(version.Id, versionId, StringComparison.Ordinal))
            InstanceLayout.PatchJsonId(jsonPath, versionId);

        // 2. 安装内容（jar + 库 + 资源）
        await InstallVersionContentAsync(version, versionId, settings, progress, ct);
    }

    /// <summary>安装 Fabric：拉 loader profile 合并保存，再装全量内容</summary>
    public Task InstallFabricAsync(string mcVersion, string loaderVersion,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct = default,
        string? targetId = null) =>
        InstallLoaderProfileAsync(mcVersion, loaderVersion, settings, progress, ct,
            "meta.fabricmc.net", "v2", targetId);

    /// <summary>安装 Quilt：meta v3 profile 与 Fabric v2 同构，复用同一合并管线</summary>
    public Task InstallQuiltAsync(string mcVersion, string loaderVersion,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct = default,
        string? targetId = null) =>
        InstallLoaderProfileAsync(mcVersion, loaderVersion, settings, progress, ct,
            "meta.quiltmc.org", "v3", targetId);

    private async Task InstallLoaderProfileAsync(string mcVersion, string loaderVersion,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct,
        string metaHost, string metaApi, string? targetId = null)
    {
        var profileUrl = $"https://{metaHost}/{metaApi}/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
        var profileJson = await _downloader.GetStringAsync(profileUrl, headers: null, ct: ct, trackSourceHealth: false);
        var profile = JsonSerializer.Deserialize<FabricProfile>(profileJson)
                      ?? throw new InvalidOperationException("Fabric profile 解析失败");

        var dotMc = settings.DotMinecraft;
        // 同名版本并存：id 冲突时自动追加 -2、-3…（merged 元数据沿用新 id）
        // 补全/重装已有实例时沿用原目录名覆盖，不再生成 -2 副本
        var id = targetId ?? UniqueVersionId(dotMc, profile.Id);
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

        // 库（含规则过滤 + 当前平台 natives）
        var versionDir = VersionDir(dotMc, id);
        var nativesDir = Path.Combine(versionDir, "natives");
        var nativesJobs = new List<(DownloadableFile File, string Dest, ExtractSpec? Extract)>();
        foreach (var lib in version.Libraries.Where(l => RulesAllow(l.Rules)))
        {
            var dl = lib.Downloads;
            // Mojang 库带 downloads.artifact；Fabric/Quilt 只有 Maven 坐标（name + url），
            // 两种情况都要落到 libraries 下并加入下载队列，否则启动时 classpath 缺 loader。
            if (MavenArtifacts.Resolve(lib) is { } artifact)
            {
                files.Add((artifact.Url, Path.Combine(dotMc, "libraries", (artifact.Path ?? "").Replace('/', Path.DirectorySeparatorChar)),
                    artifact.Sha1, artifact.Size));
            }
            if (NativesOf(dl) is { } native)
            {
                nativesJobs.Add((native,
                    Path.Combine(dotMc, "libraries", native.Path ?? ""), lib.Extract));
            }
            else if (dl?.Artifact is { Url: not "" } modernNative && IsModernNative(lib.Name))
            {
                // 1.19+ 把 natives 拆成独立库条目（name 以 :natives-linux 等结尾），
                // 不再是 downloads.classifiers。artifact 已在上面入下载队列，
                // 这里补一条解压任务，否则 java.library.path 指向空目录。
                nativesJobs.Add((modernNative,
                    Path.Combine(dotMc, "libraries", modernNative.Path ?? ""), lib.Extract));
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

        // 磁盘空间预检：下载到一半才报「磁盘已满」是最难受的失败方式（用户已经等了很久）。
        // 资源对象动辄几千个，这里按总量一次性判断。
        var expectedBytes = files.Sum(f => Math.Max(f.Size, 0));
        if (!DiskSpaceGuard.Check(dotMc, expectedBytes, out _, out var spaceMsg))
            throw new InvalidOperationException(spaceMsg);

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

    /// <summary>解压 natives jar 中的本机库（.dll/.so/.dylib）到 natives 目录（受 extract.exclude 过滤）</summary>
    public static void ExtractNatives(string nativesJarPath, string nativesDir, ExtractSpec? extract)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(nativesJarPath);
        foreach (var entry in zip.Entries)
        {
            if (!PlatformInfo.IsNativeArtifact(entry.FullName)) continue;
            if (extract?.Exclude?.Any(e => entry.FullName.Contains(e)) == true) continue;
            var target = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>新版清单的 natives 库条目：name 以 `:natives-&lt;当前平台 classifier&gt;` 结尾</summary>
    private static bool IsModernNative(string name)
    {
        foreach (var candidate in PlatformInfo.NativesClassifierCandidates)
        {
            if (name.EndsWith(":" + candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>从 classifiers 里取当前平台的 natives（按候选键顺序回退）；无对应平台键时返回 null</summary>
    public static DownloadableFile? NativesOf(LibraryDownloads? downloads,
        IReadOnlyList<string>? candidates = null)
    {
        if (downloads?.Classifiers is not { } classifiers) return null;
        foreach (var key in candidates ?? PlatformInfo.NativesClassifierCandidates)
            if (classifiers.TryGetValue(key, out var file)) return file;
        return null;
    }

    /// <summary>Minecraft 库规则判定（默认按当前平台 os.name；osName 显式传入便于跨平台单测）</summary>
    public static bool RulesAllow(List<OsRule>? rules, string? osName = null)
    {
        osName ??= PlatformInfo.MojangOsName;
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            // 带 features 的规则：启动器未启用任何 feature，不匹配（杜绝 --demo 等试玩参数）
            if (rule.Features is { Count: > 0 }) continue;
            var matches = rule.Os is null || rule.Os.Name is null ||
                          rule.Os.Name.Equals(osName, StringComparison.OrdinalIgnoreCase);
            // 32 位专用规则在 64 位进程上不匹配（如 -Xss1M）
            if (matches && rule.Os?.Arch == "x86" && Environment.Is64BitProcess) matches = false;
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
