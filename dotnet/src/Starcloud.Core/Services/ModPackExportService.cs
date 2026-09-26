// 整合包导出：Modrinth .mrpack / CurseForge / MultiMC·Prism 三种格式 + 高级导出选项。
//
// 三种格式的差别只在「元数据怎么写、内容怎么落位」，所以流程统一为：
//   1. 按勾选目录 + 排除规则枚举内容文件
//   2. 反查来源（Modrinth 用 SHA1，CurseForge 用 murmur2 指纹；MultiMC 自包含不反查）
//   3. 命中的写进索引文件，未命中的按格式落到 overrides / .minecraft
//
// 打包用 ZipArchive 逐条写入（而不是先复制到临时目录再 CreateFromDirectory），
// 这样既能省掉一整轮磁盘复制，也能按请求的压缩级别逐个条目控制。

using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>导出格式</summary>
public enum ModPackExportFormat
{
    Modrinth,
    CurseForge,
    MultiMc,
}

/// <summary>导出时的候选目录（扫描实例根得到）</summary>
public sealed record ExportDirCandidate(
    string Name, bool IsStandard, int FileCount, long SizeBytes, string? Hint = null);

/// <summary>导出请求（Format 及各高级项走 init 属性，便于按需覆盖）</summary>
public sealed record ExportRequest(
    string GameRoot,
    string TargetZipPath,
    string Name,
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<string> Dirs)
{
    public ModPackExportFormat Format { get; init; } = ModPackExportFormat.Modrinth;

    /// <summary>包版本号（Modrinth versionId / manifest.version / MultiMC 包版本）</summary>
    public string VersionId { get; init; } = "1.0.0";

    public string Author { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>排除规则：含 "/" 时匹配整条相对路径，否则匹配任意一层目录名（支持 * 与 ?）</summary>
    public IReadOnlyList<string> ExcludeRules { get; init; } = [];

    public CompressionLevel Compression { get; init; } = CompressionLevel.Optimal;

    /// <summary>是否带上 options.txt / servers.dat 等实例设置</summary>
    public bool IncludeGameSettings { get; init; } = true;

    /// <summary>反查不到来源的文件：true 打包进包内，false 直接丢弃</summary>
    public bool UnresolvedAsOverrides { get; init; } = true;
}

/// <summary>导出结果（DownloadableFiles = 靠下载还原的文件，OverriddenFiles = 包内实文件）</summary>
public sealed record ExportResult(
    int DownloadableFiles, int OverriddenFiles, int SkippedFiles, long TotalBytes, string? LoaderWarning)
{
    public int TotalFiles => DownloadableFiles + OverriddenFiles;
}

public sealed class ModPackExportService(ModrinthService mods, CurseForgeService curseForge)
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

    /// <summary>实例设置类根文件（IncludeGameSettings 开启时一并导出）</summary>
    private static readonly string[] GameSettingFiles =
        ["options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat", "servers.dat_old"];

    /// <summary>目录名 → 用途提示（帮助用户找到投影 / 枪械工坊这类 mod 数据目录）</summary>
    private static readonly Dictionary<string, string> DirHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["schematics"] = "投影（Litematica）原理图",
        ["xaerowaypoints"] = "Xaero 小地图路径点",
        ["xaeroworldmap"] = "Xaero 世界地图缓存",
        ["journeymap"] = "JourneyMap 地图数据",
        ["tacz"] = "永恒枪械工坊（TaCZ）枪械包",
        ["defaultconfigs"] = "新世界默认配置",
        ["shaderpacks"] = "光影包",
        ["resourcepacks"] = "资源包",
    };

    /// <summary>依赖键 → CurseForge 加载器 id 前缀</summary>
    private static readonly (string Key, string Prefix)[] LoaderPrefixes =
    [
        ("forge", "forge"),
        ("neoforge", "neoforge"),
        ("fabric-loader", "fabric"),
        ("quilt-loader", "quilt"),
    ];

    /// <summary>依赖键 → MultiMC 组件 uid</summary>
    private static readonly (string Key, string Uid)[] ComponentUids =
    [
        ("fabric-loader", "net.fabricmc.fabric-loader"),
        ("quilt-loader", "org.quiltmc.quilt-loader"),
        ("forge", "net.minecraftforge"),
        ("neoforge", "net.neoforged"),
    ];

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
            var files = EnumerateContent(gameRoot, [name], [], includeGameSettings: false, out _);
            var isStandard = StandardDirs.Contains(name, StringComparer.OrdinalIgnoreCase);
            result.Add(new ExportDirCandidate(
                name, isStandard, files.Count, files.Sum(f => f.Size), HintFor(gameRoot, name)));
        }
        return result;
    }

    /// <summary>按勾选目录与选项导出整合包</summary>
    public Task<ExportResult> ExportAsync(ExportRequest request,
        IProgress<string>? status = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            status?.Report("正在扫描导出内容…");
            var files = EnumerateContent(request.GameRoot, request.Dirs, request.ExcludeRules,
                request.IncludeGameSettings, out var skipped);

            var tmpZip = request.TargetZipPath + ".tmp";
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
            try
            {
                var result = request.Format switch
                {
                    ModPackExportFormat.CurseForge =>
                        await WriteCurseForgeAsync(request, files, tmpZip, status, ct),
                    ModPackExportFormat.MultiMc =>
                        WriteMultiMc(request, files, tmpZip, status, ct),
                    _ => await WriteModrinthAsync(request, files, tmpZip, status, ct),
                };

                File.Move(tmpZip, request.TargetZipPath, overwrite: true);
                var warning = LoaderKeys.Any(request.Dependencies.ContainsKey)
                    ? null
                    : "未识别到加载器依赖（Fabric/Forge/NeoForge），导入方需自行选择加载器";
                return result with
                {
                    SkippedFiles = result.SkippedFiles + skipped,
                    LoaderWarning = warning,
                };
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* 临时文件清理失败可忽略 */ }
            }
        }, ct);

    // ── Modrinth：SHA1 反查 → files[] + overrides ─────────────────────────

    private async Task<ExportResult> WriteModrinthAsync(
        ExportRequest request, IReadOnlyList<ContentFile> files, string tmpZip,
        IProgress<string>? status, CancellationToken ct)
    {
        status?.Report($"正在计算 SHA1（{files.Count} 个文件）…");
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
            known = await mods.GetVersionsByHashesAsync(sha1s.Values.Distinct().ToList(), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 反查失败 → 全部打进 overrides，不阻断导出 */ }

        var indexFiles = new List<ExportFile>();
        var embedded = new List<(string EntryName, string Abs)>();
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
            else if (request.UnresolvedAsOverrides)
            {
                embedded.Add(($"overrides/{file.Rel}", file.Abs));
            }
        }

        var index = new ExportIndex
        {
            FormatVersion = 1,
            GameVersion = request.GameVersion,
            Name = request.Name,
            VersionId = request.VersionId,
            Dependencies = new Dictionary<string, string>(request.Dependencies),
            Files = indexFiles,
        };

        status?.Report("正在打包…");
        using var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create);
        WriteText(zip, "modrinth.index.json", JsonSerializer.Serialize(index, JsonOpts), request.Compression);
        foreach (var (entryName, abs) in embedded) AddFile(zip, entryName, abs, request.Compression);

        return new ExportResult(indexFiles.Count, embedded.Count, 0, totalBytes, null);
    }

    // ── CurseForge：指纹反查 → manifest.json + overrides ───────────────────

    private async Task<ExportResult> WriteCurseForgeAsync(
        ExportRequest request, IReadOnlyList<ContentFile> files, string tmpZip,
        IProgress<string>? status, CancellationToken ct)
    {
        status?.Report($"正在计算文件指纹（{files.Count} 个文件）…");
        var prints = new List<(ContentFile File, int Print)>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            prints.Add((file, CurseForgeService.Fingerprint(file.Abs)));
        }

        status?.Report("正在识别 CurseForge 资源…");
        Dictionary<int, CurseForgeMatch> known;
        try
        {
            known = await curseForge.MatchFingerprintsAsync(prints.Select(p => p.Print).ToList(), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { known = []; }

        var manifestFiles = new List<ExportManifestFile>();
        var embedded = new List<(string EntryName, string Abs)>();
        long totalBytes = 0;
        foreach (var (file, print) in prints)
        {
            ct.ThrowIfCancellationRequested();
            totalBytes += file.Size;
            if (known.TryGetValue(print, out var match)
                && long.TryParse(match.ProjectId, out var projectId)
                && long.TryParse(match.FileId, out var fileId))
            {
                manifestFiles.Add(new ExportManifestFile
                {
                    ProjectId = projectId,
                    FileId = fileId,
                    Required = true,
                    FileLength = file.Size,
                });
            }
            else if (request.UnresolvedAsOverrides)
            {
                embedded.Add(($"overrides/{file.Rel}", file.Abs));
            }
        }

        var manifest = new ExportManifest
        {
            Minecraft = new ExportManifestMinecraft
            {
                Version = request.GameVersion,
                ModLoaders = LoaderId(request.Dependencies) is { } loaderId
                    ? [new ExportManifestLoader { Id = loaderId, Primary = true }]
                    : [],
            },
            Name = request.Name,
            Version = request.VersionId,
            Author = request.Author,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            Files = manifestFiles,
        };

        status?.Report("正在打包…");
        using var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create);
        WriteText(zip, "manifest.json", JsonSerializer.Serialize(manifest, JsonOpts), request.Compression);
        foreach (var (entryName, abs) in embedded) AddFile(zip, entryName, abs, request.Compression);

        return new ExportResult(manifestFiles.Count, embedded.Count, 0, totalBytes, null);
    }

    // ── MultiMC / Prism：自包含，内容原样打进 .minecraft ────────────────────

    private ExportResult WriteMultiMc(
        ExportRequest request, IReadOnlyList<ContentFile> files, string tmpZip,
        IProgress<string>? status, CancellationToken ct)
    {
        var name = PackPathGuard.SafeFileName(request.Name, "instance");
        status?.Report("正在打包…");
        using var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create);

        WriteText(zip, "instance.cfg", InstanceCfg(name), request.Compression);
        WriteText(zip, "mmc-pack.json", MmcPack(request), request.Compression);
        if (!string.IsNullOrWhiteSpace(request.Author) || !string.IsNullOrWhiteSpace(request.Description))
            WriteText(zip, "pack.json", PackMeta(request), request.Compression);

        long totalBytes = 0;
        var count = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            totalBytes += file.Size;
            AddFile(zip, $".minecraft/{file.Rel}", file.Abs, request.Compression);
            count++;
        }
        return new ExportResult(0, count, 0, totalBytes, null);
    }

    private static string InstanceCfg(string name) =>
        $"""
         [General]
         ConfigVersion=1.2
         InstanceType=OneSix
         iconKey=default
         name={SingleLine(name)}
         JoinServerOnLaunch=false
         """;

    private static string MmcPack(ExportRequest request)
    {
        var components = new List<MmcComponentOut>();
        if (!string.IsNullOrWhiteSpace(request.GameVersion))
            components.Add(new MmcComponentOut { Uid = "net.minecraft", Version = request.GameVersion, Important = true });
        foreach (var (key, uid) in ComponentUids)
            if (request.Dependencies.TryGetValue(key, out var version) && !string.IsNullOrWhiteSpace(version))
                components.Add(new MmcComponentOut { Uid = uid, Version = version });

        return JsonSerializer.Serialize(
            new MmcPackOut { FormatVersion = 1, Components = components }, JsonOpts);
    }

    private static string PackMeta(ExportRequest request) =>
        JsonSerializer.Serialize(new PackMetaOut
        {
            Name = request.Name,
            Version = request.VersionId,
            Author = request.Author,
            Description = request.Description,
        }, JsonOpts);

    private static string SingleLine(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    // ── 内容枚举与规则 ───────────────────────────────────────────────────

    /// <summary>枚举待导出文件（相对 GameRoot 的 posix 路径；噪音 / 命中排除规则者计入 skipped）</summary>
    private static List<ContentFile> EnumerateContent(
        string gameRoot, IReadOnlyList<string> dirs, IReadOnlyList<string> excludeRules,
        bool includeGameSettings, out int skipped)
    {
        var list = new List<ContentFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        void Consider(string abs)
        {
            count++;
            var rel = Path.GetRelativePath(gameRoot, abs).Replace('\\', '/');
            if (IsNoise(rel) || IsExcluded(rel, excludeRules)) return;
            if (!seen.Add(rel)) return;
            list.Add(new ContentFile(rel, abs, new FileInfo(abs).Length));
        }

        foreach (var dir in dirs)
        {
            var name = dir.Replace('\\', '/').Trim('/');
            if (name.Length == 0) continue;
            var abs = Path.Combine(gameRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(abs)) { Consider(abs); continue; }
            if (!Directory.Exists(abs)) continue;
            foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                Consider(file);
        }

        if (includeGameSettings)
            foreach (var name in GameSettingFiles)
            {
                var abs = Path.Combine(gameRoot, name);
                if (File.Exists(abs)) Consider(abs);
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

    /// <summary>排除规则：含 "/" 时对整条相对路径做通配，否则对任意一层目录名匹配</summary>
    private static bool IsExcluded(string rel, IReadOnlyList<string> rules)
    {
        foreach (var raw in rules)
        {
            var pattern = raw.Replace('\\', '/').Trim();
            if (pattern.Length == 0) continue;
            if (pattern.Contains('/'))
            {
                if (FileSystemName.MatchesSimpleExpression(pattern.Trim('/'), rel, ignoreCase: true))
                    return true;
                continue;
            }
            foreach (var segment in rel.Split('/'))
                if (FileSystemName.MatchesSimpleExpression(pattern, segment, ignoreCase: true))
                    return true;
        }
        return false;
    }

    private string? HintFor(string gameRoot, string name)
    {
        if (name.Equals("config", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(gameRoot, "config", "tacz")))
            return "含永恒枪械工坊（TaCZ）配置";
        return DirHints.GetValueOrDefault(name);
    }

    private static string? LoaderId(IReadOnlyDictionary<string, string> dependencies)
    {
        foreach (var (key, prefix) in LoaderPrefixes)
            if (dependencies.TryGetValue(key, out var version) && !string.IsNullOrWhiteSpace(version))
                return $"{prefix}-{version}";
        return null;
    }

    // ── 归档写入 ─────────────────────────────────────────────────────────

    private static void AddFile(ZipArchive zip, string entryName, string absPath, CompressionLevel level)
    {
        var entry = zip.CreateEntry(entryName, level);
        using var src = File.OpenRead(absPath);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }

    private static void WriteText(ZipArchive zip, string entryName, string content, CompressionLevel level)
    {
        var entry = zip.CreateEntry(entryName, level);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed record ContentFile(string Rel, string Abs, long Size);
}

// ── 索引文件写出 DTO ──

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

file sealed class ExportManifest
{
    [JsonPropertyName("minecraft")]
    public ExportManifestMinecraft Minecraft { get; set; } = new();

    [JsonPropertyName("manifestType")]
    public string ManifestType { get; set; } = "minecraftModpack";

    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("files")]
    public List<ExportManifestFile> Files { get; set; } = [];

    [JsonPropertyName("overrides")]
    public string Overrides { get; set; } = "overrides";
}

file sealed class ExportManifestMinecraft
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("modLoaders")]
    public List<ExportManifestLoader> ModLoaders { get; set; } = [];
}

file sealed class ExportManifestLoader
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}

file sealed class ExportManifestFile
{
    [JsonPropertyName("projectID")]
    public long ProjectId { get; set; }

    [JsonPropertyName("fileID")]
    public long FileId { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("fileLength")]
    public long FileLength { get; set; }
}

file sealed class MmcPackOut
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("components")]
    public List<MmcComponentOut> Components { get; set; } = [];
}

file sealed class MmcComponentOut
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("important")]
    public bool Important { get; set; }
}

file sealed class PackMetaOut
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}
