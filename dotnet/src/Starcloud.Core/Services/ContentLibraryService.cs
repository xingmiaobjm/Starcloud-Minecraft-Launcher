// 启动器统一资源库：入库去重（SHA1）、索引持久化、装载到版本/存档、身份反查与更新/依赖检查

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>资源库条目</summary>
public sealed class LibraryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>mod | resourcepack | shader | datapack | modpack</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("version_id")]
    public string? ModrinthVersionId { get; set; }

    [JsonPropertyName("version_number")]
    public string? VersionNumber { get; set; }

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("downloaded_at")]
    public DateTime DownloadedAt { get; set; } = DateTime.Now;
}

/// <summary>资源库索引（library.json）</summary>
public sealed class LibraryIndex
{
    [JsonPropertyName("entries")]
    public List<LibraryEntry> Entries { get; set; } = [];
}

/// <summary>入库元信息（来自 Modrinth 或本地文件）</summary>
public sealed record LibraryAdoptInfo(
    string Title, string? IconUrl, string? Author, string? ProjectId,
    string? VersionId, string? VersionNumber, List<string> GameVersions, List<string> Loaders,
    string? SourceUrl);

/// <summary>Modrinth 身份（哈希反查结果）</summary>
public sealed record ModrinthIdentity(string ProjectId, string VersionId, string VersionNumber,
    List<string> GameVersions, List<string> Loaders);

/// <summary>更新检查结果</summary>
public sealed record UpdateCheckResult(string FileName, string CurrentVersion, string LatestVersion,
    string LatestFileName, string LatestUrl, string? LatestSha1);

/// <summary>依赖检查结果</summary>
public sealed record DependencyReport(List<MissingDependency> Required, List<ModrinthDependency> Optional,
    List<string> IncompatiblePresent);

/// <summary>缺失的必需依赖</summary>
public sealed record MissingDependency(string ProjectId, string? VersionId, string Title, string? FileName);

/// <summary>启动器统一资源库：下载缓存、入库去重、装载到版本/存档</summary>
public sealed class ContentLibraryService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, string> KindDirs = new()
    {
        ["mod"] = "mods",
        ["resourcepack"] = "resourcepacks",
        ["shader"] = "shaderpacks",
        ["datapack"] = "datapacks",
        ["modpack"] = "modpacks",
    };

    private readonly DownloadService _downloader;
    private readonly ModrinthService _mods;
    private readonly ManifestService _manifest;
    private readonly Func<LauncherSettings> _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModrinthIdentity?> _identityCache = new(StringComparer.OrdinalIgnoreCase);
    private LibraryIndex _index;

    public ContentLibraryService(
        DownloadService downloader, ModrinthService mods, ManifestService manifest,
        Func<LauncherSettings> settings)
    {
        _downloader = downloader;
        _mods = mods;
        _manifest = manifest;
        _settings = settings;
        _index = LoadIndex();
    }

    // ── 索引读写 ──

    /// <summary>读取索引；损坏或缺失时静默重置为空索引（不阻塞使用）</summary>
    private static LibraryIndex LoadIndex()
    {
        try
        {
            var file = LauncherPaths.LibraryIndexFile;
            if (!File.Exists(file)) return new LibraryIndex();
            return JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(file), Options) ?? new LibraryIndex();
        }
        catch { return new LibraryIndex(); }
    }

    /// <summary>原子写入索引</summary>
    private void SaveIndex()
    {
        Directory.CreateDirectory(LauncherPaths.LibraryDir);
        var temp = LauncherPaths.LibraryIndexFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_index, Options));
        File.Move(temp, LauncherPaths.LibraryIndexFile, overwrite: true);
    }

    // ── 查询与目录 ──

    /// <summary>资源类别 → 库内目录绝对路径（library/&lt;folder&gt;）</summary>
    public string DirFor(string kind)
    {
        var folder = KindDirs.TryGetValue(kind, out var f) ? f : "mods";
        var dir = Path.Combine(LauncherPaths.LibraryDir, folder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>列出库内条目（按入库时间倒序）</summary>
    public Task<IReadOnlyList<LibraryEntry>> ListAsync(string? kind = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _index.Entries
                .Where(e => kind is null || kind.Length == 0 || e.Kind == kind)
                .OrderByDescending(e => e.DownloadedAt)
                .ToList();
            return Task.FromResult<IReadOnlyList<LibraryEntry>>(list);
        }
    }

    /// <summary>按 SHA1 查条目</summary>
    public LibraryEntry? FindBySha1(string sha1)
    {
        if (string.IsNullOrEmpty(sha1)) return null;
        lock (_gate)
            return _index.Entries.FirstOrDefault(e =>
                e.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按类别 + 文件名查条目</summary>
    public LibraryEntry? FindByFileName(string kind, string fileName)
    {
        lock (_gate)
            return _index.Entries.FirstOrDefault(e =>
                e.Kind == kind && e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从资源库删除条目（只删库文件与索引，不影响已装载副本）</summary>
    public void Remove(string entryId)
    {
        lock (_gate)
        {
            var entry = _index.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return;
            var path = Path.Combine(DirFor(entry.Kind), entry.FileName);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 占用中则保留文件，仅摘索引 */ }
            _index.Entries.Remove(entry);
            SaveIndex();
        }
    }

    // ── 入库 / 重扫 ──

    /// <summary>把已下载的文件收进资源库（同 SHA1 返回既有条目；同名不同 SHA1 加 " (n)"）</summary>
    public async Task<LibraryEntry> AdoptAsync(
        string kind, string sourcePath, LibraryAdoptInfo info, CancellationToken ct = default)
    {
        var sha1 = await Task.Run(() =>
            Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(sourcePath))), ct);

        if (FindBySha1(sha1) is { } existing)
        {
            try { File.Delete(sourcePath); } catch { }
            return existing;
        }

        var dir = DirFor(kind);
        var target = Path.Combine(dir, Path.GetFileName(sourcePath));
        if (File.Exists(target))
            target = InstanceLayout.NextAvailablePath(target);

        await Task.Run(() => File.Copy(sourcePath, target, overwrite: false), ct);

        var entry = new LibraryEntry
        {
            Kind = kind,
            FileName = Path.GetFileName(target),
            Sha1 = sha1,
            SizeBytes = new FileInfo(target).Length,
            Title = info.Title,
            IconUrl = info.IconUrl,
            Author = info.Author,
            ProjectId = info.ProjectId,
            ModrinthVersionId = info.VersionId,
            VersionNumber = info.VersionNumber,
            GameVersions = info.GameVersions,
            Loaders = info.Loaders,
            SourceUrl = info.SourceUrl,
            DownloadedAt = DateTime.Now,
        };

        lock (_gate)
        {
            _index.Entries.Add(entry);
            SaveIndex();
        }

        try { File.Delete(sourcePath); } catch { }
        return entry;
    }

    /// <summary>下载到库内临时目录并入库，返回库内条目</summary>
    public async Task<LibraryEntry> DownloadAndAdoptAsync(
        string kind, string url, string fileName, string? sha1, LibraryAdoptInfo info,
        CancellationToken ct = default)
    {
        var tmpDir = Path.Combine(LauncherPaths.LibraryDir, ".tmp");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, fileName);
        await _downloader.DownloadFileAsync(url, tmp, sha1, ct: ct);
        return await AdoptAsync(kind, tmp, info, ct);
    }

    /// <summary>列出已安装版本（供装载选择弹窗使用）</summary>
    public IReadOnlyList<InstalledVersionInfo> ListInstalledVersions()
        => _manifest.ListInstalled(_settings().DotMinecraft);

    /// <summary>重扫库目录，修复索引与实际文件的不一致</summary>
    public async Task<IReadOnlyList<LibraryEntry>> RescanAsync(CancellationToken ct = default)
    {
        foreach (var (kind, folder) in KindDirs)
        {
            var dir = Path.Combine(LauncherPaths.LibraryDir, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                lock (_gate)
                {
                    if (_index.Entries.Any(e => e.Kind == kind &&
                            e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                var sha1 = await Task.Run(() =>
                    Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file))), ct);
                lock (_gate)
                {
                    _index.Entries.Add(new LibraryEntry
                    {
                        Kind = kind,
                        FileName = name,
                        Sha1 = sha1,
                        SizeBytes = new FileInfo(file).Length,
                        Title = Path.GetFileNameWithoutExtension(name),
                        DownloadedAt = File.GetCreationTime(file),
                    });
                }
            }
        }

        lock (_gate)
        {
            _index.Entries.RemoveAll(e => !File.Exists(Path.Combine(LauncherPaths.LibraryDir,
                KindDirs.TryGetValue(e.Kind, out var f) ? f : "mods", e.FileName)));
            SaveIndex();
            return _index.Entries.ToList();
        }
    }

    // ── 装载 ──

    /// <summary>把库内条目装载到指定版本的对应内容目录，返回落点绝对路径</summary>
    public async Task<string> InstallToVersionAsync(
        LibraryEntry entry, string versionId, CancellationToken ct = default)
    {
        var dotMc = _settings().DotMinecraft;
        var store = new InstanceSettingsStore(dotMc);
        var profile = store.Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings());
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var dir = Path.Combine(gameRoot, InstanceLayout.ContentDirFor(entry.Kind));
        Directory.CreateDirectory(dir);

        var dest = Path.Combine(dir, entry.FileName);
        var src = Path.Combine(DirFor(entry.Kind), entry.FileName);
        await CopyAsync(src, dest, ct);
        return dest;
    }

    /// <summary>把库内数据包装载到指定版本的某个存档，返回落点绝对路径</summary>
    public async Task<string> InstallToSaveAsync(
        LibraryEntry entry, string versionId, string saveFolderName, CancellationToken ct = default)
    {
        var dotMc = _settings().DotMinecraft;
        var store = new InstanceSettingsStore(dotMc);
        var profile = store.Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings());
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var dir = Path.Combine(gameRoot, "saves", saveFolderName, "datapacks");
        Directory.CreateDirectory(dir);

        var dest = Path.Combine(dir, entry.FileName);
        var src = Path.Combine(DirFor(entry.Kind), entry.FileName);
        await CopyAsync(src, dest, ct);
        return dest;
    }

    /// <summary>复制文件（目标同名先删）</summary>
    private static Task CopyAsync(string src, string dest, CancellationToken ct) => Task.Run(() =>
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Copy(src, dest, overwrite: true);
    }, ct);

    // ── 身份反查 / 更新 / 依赖 ──

    /// <summary>批量反查 SHA1 → Modrinth 身份（进程内缓存，含负缓存）</summary>
    public async Task<IReadOnlyDictionary<string, ModrinthIdentity>> ResolveIdentitiesAsync(
        IEnumerable<string> sha1s, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ModrinthIdentity>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();
        foreach (var sha1 in sha1s.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(sha1)) continue;
            ModrinthIdentity? cached;
            lock (_gate)
            {
                if (!_identityCache.TryGetValue(sha1, out cached)) { pending.Add(sha1); continue; }
            }
            if (cached is not null) result[sha1] = cached;
        }
        if (pending.Count == 0) return result;

        var found = await _mods.GetVersionsByHashesAsync(pending, ct);
        lock (_gate)
        {
            foreach (var sha1 in pending)
            {
                if (found.TryGetValue(sha1, out var version))
                {
                    var identity = new ModrinthIdentity(version.ProjectId, version.Id,
                        version.VersionNumber, version.GameVersions, version.Loaders);
                    _identityCache[sha1] = identity;
                    result[sha1] = identity;
                }
                else _identityCache[sha1] = null;
            }
        }
        return result;
    }

    /// <summary>检查单个文件是否有更新（按 SHA1 反查项目 → 取最新兼容版本）</summary>
    public async Task<UpdateCheckResult?> CheckUpdateAsync(
        string filePath, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return null;
        var sha1 = await Task.Run(() =>
            Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(filePath))), ct);

        var identities = await ResolveIdentitiesAsync([sha1], ct);
        if (!identities.TryGetValue(sha1, out var identity)) return null;

        var versions = await _mods.ListVersionsAsync(identity.ProjectId, loader, gameVersion, ct);
        if (versions.Count == 0) return null;

        var latest = versions.FirstOrDefault(v =>
            !string.Equals(v.VersionType, "alpha", StringComparison.OrdinalIgnoreCase)) ?? versions[0];
        if (latest.PrimaryFile is not { } file) return null;
        if (string.Equals(latest.VersionNumber, identity.VersionNumber, StringComparison.OrdinalIgnoreCase))
            return null;

        return new UpdateCheckResult(
            Path.GetFileName(filePath), identity.VersionNumber, latest.VersionNumber,
            file.Filename, file.Url, file.Sha1);
    }

    /// <summary>检查某个 Modrinth 版本在当前版本实例中的依赖满足情况</summary>
    public async Task<DependencyReport> CheckDependenciesAsync(
        ModrinthVersion version, string versionId, CancellationToken ct = default)
    {
        var required = new List<MissingDependency>();
        var optional = new List<ModrinthDependency>();
        var incompatible = new List<string>();

        var dotMc = _settings().DotMinecraft;
        var store = new InstanceSettingsStore(dotMc);
        var profile = store.Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings());
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var modsDir = Path.Combine(gameRoot, "mods");

        // 已装文件（含 .disabled）→ SHA1 → 身份集合
        var projectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(modsDir))
        {
            var files = Directory.EnumerateFiles(modsDir)
                .Where(f => f.Contains(".jar", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var hashes = await Task.Run(() => files
                .Select(f => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(f))))
                .ToList(), ct);
            var identities = await ResolveIdentitiesAsync(hashes, ct);
            foreach (var identity in identities.Values)
                projectIds.Add(identity.ProjectId);
        }

        foreach (var dep in version.Dependencies)
        {
            ct.ThrowIfCancellationRequested();
            switch (dep.DependencyType)
            {
                case "required":
                    if (dep.ProjectId is null || !projectIds.Contains(dep.ProjectId))
                    {
                        var title = dep.ProjectId is null
                            ? (dep.FileName ?? "未知依赖")
                            : (await _mods.GetProjectTitleAsync(dep.ProjectId, ct) ?? dep.ProjectId);
                        required.Add(new MissingDependency(dep.ProjectId ?? "", dep.VersionId, title, dep.FileName));
                    }
                    break;
                case "optional":
                    optional.Add(dep);
                    break;
                case "incompatible":
                    if (dep.ProjectId is not null && projectIds.Contains(dep.ProjectId))
                    {
                        var title = await _mods.GetProjectTitleAsync(dep.ProjectId, ct) ?? dep.ProjectId;
                        incompatible.Add(title);
                    }
                    break;
            }
        }

        return new DependencyReport(required, optional, incompatible);
    }

    // ── 版本 id 推断 ──

    /// <summary>从版本 id 推断加载器：fabric / forge / neoforge / vanilla</summary>
    public static string ResolveLoader(string versionId, out string? loaderVersion)
    {
        loaderVersion = null;
        if (versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = versionId.Split('-');
            if (parts.Length >= 3) loaderVersion = parts[2];
            return "fabric";
        }
        if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)) return "forge";
        if (versionId.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        return "vanilla";
    }

    /// <summary>从版本 id 推断 MC 版本</summary>
    public static string ResolveMc(string versionId)
    {
        if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
            versionId.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            var parts = versionId.Split('-');
            return parts.Length >= 2 ? parts[1] : versionId;
        }
        return versionId;
    }
}
