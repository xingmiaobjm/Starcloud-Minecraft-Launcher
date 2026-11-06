// 房主侧「一键上传整合包」：解析版本 → 打包（Modrinth 轻量格式）→ 上传 → 返回 pack 元数据。
//
// 为什么是 Modrinth 格式：mod 信息只存索引（SHA1 → Modrinth API 反查出下载 URL），
// 包里只有几 MB 的 modrinth.index.json + 少数反查不到才进 overrides 的 mod ——
// 200 MB 服务端上限对轻量格式基本无感（自包含 MultiMC 打几百 MB 很容易超）。
// 玩家端导入走 ModPackDetector（识别 modrinth.index.json → MrPack 安装链），
// 反查失败时 UnresolvedAsOverrides 兜底：包退化为「索引 + 全部 mod 原样」仍可用。
//
// 缓存：同一个版本第一次要打包 + 上传，拿到 file_id 后写盘复用。
// 失效判定看「版本目录里的 mods 最后写入时间」——加了/删了 mod 就该重打，
// 只是启动游戏不会让缓存失效。
//
// 无 UI：进度走 IProgress<string>，异常直接抛给页面翻成提示。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services.Rooms;

/// <summary>打包上传后的结果（建房时直接塞进 CreateRoomRequest.Pack）。</summary>
public sealed record RoomPackCandidate(
    string FileId,
    long Size,
    string Format,
    string McVersion,
    string Loader,
    string Name,
    string VersionId,
    /// <summary>true = 命中了本地缓存（没有重新打包上传）。</summary>
    bool FromCache,
    /// <summary>对象存储的公开下载地址（服务端下发，玩家端直接下载用）。</summary>
    string? Url = null)
{
    public RoomPackInfo ToPackInfo() => new(FileId, Size, null, Format, McVersion, Loader)
    {
        Name = Name,
        Url = Url,
    };
}

/// <summary>版本 JSON 的关键信息（建房只需要这三样）。</summary>
public sealed record RoomVersionInfo(string VersionId, string McVersion, string Loader)
{
    /// <summary>一键上传整合包时给 MultiMC 包用的名字。</summary>
    public string PackName => $"{VersionId}";
}

public sealed class RoomPackFlow
{
    /// <summary>/v1/files 的单文件上限（与好友文件一致）。</summary>
    public const long MaxPackBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ModPackExportService _export;
    private readonly RoomService _rooms;
    private readonly Func<string> _dotMc;
    private readonly string _cachePath;
    private readonly object _gate = new();

    public RoomPackFlow(ModPackExportService export, RoomService rooms,
        Func<string> dotMc, string? cachePath = null)
    {
        _export = export;
        _rooms = rooms;
        _dotMc = dotMc;
        _cachePath = cachePath ?? Path.Combine(LauncherPaths.DataDir, "rooms_packs.json");
    }

    /// <summary>读版本 JSON：mc 版本（inheritsFrom 优先）+ 加载器 + 依赖（导出缺一不可）。</summary>
    public RoomVersionInfo ReadVersion(string versionId)
    {
        var dotMc = _dotMc();
        var jsonPath = Path.Combine(dotMc, "versions", versionId, versionId + ".json");
        var mcVersion = versionId;
        var loader = "vanilla";
        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("inheritsFrom", out var parent) &&
                parent.ValueKind == JsonValueKind.String &&
                parent.GetString() is { Length: > 0 } parentId)
            {
                mcVersion = parentId;
            }
            if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    if (!lib.TryGetProperty("name", out var nameEl) ||
                        nameEl.GetString() is not { Length: > 0 } name) continue;
                    // name 形如 net.fabricmc:fabric-loader:0.15.11
                    var parts = name.Split(':');
                    if (parts.Length < 3) continue;
                    var key = parts[0].Contains("fabric", StringComparison.OrdinalIgnoreCase) ? "fabric-loader"
                        : parts[0].Contains("quilt", StringComparison.OrdinalIgnoreCase) ? "quilt-loader"
                        : parts[0].Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "neoforge"
                        : parts[0].Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ? "forge"
                        : null;
                    if (key is null) continue;
                    deps[key] = parts[2];
                    loader = key switch
                    {
                        "fabric-loader" => "fabric",
                        "quilt-loader" => "quilt",
                        "neoforge" => "neoform",   // 服务端白名单是 neoform
                        _ => "forge",
                    };
                }
            }
        }
        catch
        {
            // 版本 JSON 读不到（损坏/自定义实例）：按 id 兜底，加载器当原版
        }
        return new RoomVersionInfo(versionId, mcVersion, loader);
    }

    /// <summary>缓存里有没有这个版本的包（UI 用来提示「已上传过，秒进」）。</summary>
    public RoomPackCandidate? TryGetCached(string versionId)
    {
        var entry = ReadCache().Versions.GetValueOrDefault(versionId);
        if (entry is null) return null;
        var info = ReadVersion(versionId);
        return new RoomPackCandidate(entry.FileId, entry.Size, entry.Format ?? "modrinth",
            entry.McVersion ?? info.McVersion, entry.Loader ?? info.Loader,
            entry.Name ?? info.PackName, versionId, FromCache: true, Url: entry.Url);
    }

    /// <summary>清掉某个版本的缓存（建房报 invalid_pack 时重传用）。</summary>
    public void InvalidateCache(string versionId)
    {
        lock (_gate)
        {
            var cache = ReadCache();
            if (cache.Versions.Remove(versionId)) WriteCache(cache);
        }
    }

    /// <summary>
    /// 确保这个版本已经打包并上传：缓存命中直接用；否则打包 → 上传 → 写缓存。
    /// <para>返回 null 表示这个版本没有可分发的内容（原版 + 空目录），建房时不带 pack。</para>
    /// </summary>
    public async Task<RoomPackCandidate?> EnsurePackAsync(string versionId, bool isolated,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var info = ReadVersion(versionId);
        var gameRoot = InstanceLayout.GameRoot(_dotMc(), versionId, isolated);

        // 缓存命中且实例没变过
        var cached = TryGetCached(versionId);
        if (cached is not null)
        {
            var entry = ReadCache().Versions.GetValueOrDefault(versionId);
            if (entry is not null &&
                string.Equals(entry.GameRoot, gameRoot, StringComparison.OrdinalIgnoreCase) &&
                entry.ModsStamp == ModsStamp(gameRoot))
            {
                status?.Report("已上传过这个版本，直接复用");
                return cached;
            }
        }

        var candidates = _export.DiscoverDirs(gameRoot)
            .Where(d => d.IsStandard && d.FileCount > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            // 纯原版实例：没有可打包的内容，玩家端按 mc_version/loader 自己装
            status?.Report("这个版本没有额外内容，跳过打包");
            return null;
        }
        var dirs = candidates.Select(d => d.Name).ToList();

        var tempPack = Path.Combine(Path.GetTempPath(), $"starcloud-room-{Guid.NewGuid():N}.mrpack");
        try
        {
            status?.Report("正在打包整合包…");
            var deps = ReadDependencies(versionId);
            var result = await _export.ExportAsync(new ExportRequest(
                gameRoot, tempPack, info.PackName, info.McVersion, deps, dirs)
            {
                // 轻量格式：mod 信息只存索引（SHA1 反查 → 下载 URL），包体通常几 MB；
                // 反查不到的 mod 才进 overrides —— Modrinth API 不可达时整包退化为
                // 「索引 + 全部 mod」，体积变大但玩家端照样能装
                Format = ModPackExportFormat.Modrinth,
                IncludeGameSettings = true,
                UnresolvedAsOverrides = true,
                // jar 本身已是压缩格式，Optimal 再压一遍纯烧 CPU：用 Fastest 拉满打包速度
                Compression = System.IO.Compression.CompressionLevel.Fastest,
            }, status, ct);

            var size = new FileInfo(tempPack).Length;
            if (size > MaxPackBytes)
            {
                // 轻量格式还超 200 MB = 大量 mod 反查不到（被塞进了 overrides）
                throw new RoomApiException(
                    $"整合包 {size / 1024.0 / 1024:F0} MB 超过服务端 {MaxPackBytes / 1024 / 1024} MB 上限" +
                    "（大量 mod 未能通过 Modrinth 识别，已原样塞入包内），请精简 mods 后再上传",
                    "pack_too_large", 413);
            }

            status?.Report($"正在上传整合包（{size / 1024.0 / 1024:F1} MB，首次较慢）…");
            var uploadProgress = new Progress<long>(sent =>
                status?.Report($"正在上传整合包… {sent * 100 / Math.Max(size, 1)}%"));
            var uploaded = await _rooms.UploadPackAsync(tempPack, info.PackName + ".mrpack", uploadProgress, ct);

            var candidate = new RoomPackCandidate(uploaded.FileId, uploaded.Size, "modrinth",
                info.McVersion, info.Loader, info.PackName, versionId, FromCache: false,
                Url: uploaded.Url);

            lock (_gate)
            {
                var cache = ReadCache();
                cache.Versions[versionId] = new PackCacheEntry
                {
                    FileId = uploaded.FileId,
                    Size = uploaded.Size,
                    Url = uploaded.Url,
                    Sha256 = uploaded.Sha256,
                    Format = candidate.Format,
                    McVersion = info.McVersion,
                    Loader = info.Loader,
                    Name = info.PackName,
                    GameRoot = gameRoot,
                    ModsStamp = ModsStamp(gameRoot),
                };
                WriteCache(cache);
            }
            status?.Report("整合包已上传");
            return candidate;
        }
        finally
        {
            try { File.Delete(tempPack); } catch { /* 临时包删不掉可忽略 */ }
        }
    }

    // ── 缓存文件 ──

    private sealed class PackCache
    {
        [JsonPropertyName("versions")]
        public Dictionary<string, PackCacheEntry> Versions { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PackCacheEntry
    {
        [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("mc_version")] public string? McVersion { get; set; }
        [JsonPropertyName("loader")] public string? Loader { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("game_root")] public string? GameRoot { get; set; }
        [JsonPropertyName("mods_stamp")] public long ModsStamp { get; set; }
    }

    private PackCache ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return new PackCache();
            var text = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<PackCache>(text, JsonOpts) ?? new PackCache();
        }
        catch
        {
            return new PackCache();   // 缓存损坏当作空（大不了重传一次）
        }
    }

    private void WriteCache(PackCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache, JsonOpts));
        }
        catch
        {
            // 缓存写不进去不影响本次建房，只是下次还会重传
        }
    }

    /// <summary>mods 目录的最后写入时间（作为「实例内容变了」的廉价指纹）。</summary>
    private static long ModsStamp(string gameRoot)
    {
        try
        {
            var mods = Path.Combine(gameRoot, "mods");
            return Directory.Exists(mods) ? Directory.GetLastWriteTimeUtc(mods).Ticks : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>导出用的依赖表（格式：fabric-loader / forge / neoforge → 版本号）。</summary>
    private Dictionary<string, string> ReadDependencies(string versionId)
    {
        var info = ReadVersion(versionId);
        // ReadVersion 已经把依赖解析出来了，但记录上没带；这里再读一次 JSON 的 libraries。
        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (info.Loader == "vanilla") return deps;
        try
        {
            var dotMc = _dotMc();
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(dotMc, "versions", versionId, versionId + ".json")));
            if (!doc.RootElement.TryGetProperty("libraries", out var libs) ||
                libs.ValueKind != JsonValueKind.Array) return deps;
            foreach (var lib in libs.EnumerateArray())
            {
                if (!lib.TryGetProperty("name", out var nameEl) ||
                    nameEl.GetString() is not { Length: > 0 } name) continue;
                var parts = name.Split(':');
                if (parts.Length < 3) continue;
                var key = parts[0].Contains("fabric", StringComparison.OrdinalIgnoreCase) ? "fabric-loader"
                    : parts[0].Contains("quilt", StringComparison.OrdinalIgnoreCase) ? "quilt-loader"
                    : parts[0].Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "neoforge"
                    : parts[0].Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ? "forge"
                    : null;
                if (key is not null) deps[key] = parts[2];
            }
        }
        catch { /* 读不到就不带依赖，导出会给出 LoaderWarning */ }
        return deps;
    }
}
