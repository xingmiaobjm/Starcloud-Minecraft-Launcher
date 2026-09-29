// CurseForge 资源接入：搜索 / 项目文件 / 下载直链。
//
// ── 接口的三层回退 ──────────────────────────────────────────────────────────
// 旧 Tauri 版走的是 `www.curseforge.com/api/v1/...`（PCL2 那套无 key 接口）。
// 实测（2026-09-21）该地址已被 Cloudflare 托管挑战拦下，第三方客户端拿不到 200；
// 而官方 API `api.curseforge.com/v1` 未带 key 时明确返回
// `403 Forbidden: API Key missing or invalid`。所以本实现做成**三层**：
//
//   · 官方 `https://api.curseforge.com/v1`：有密钥时最先尝试，数据结构最全，
//     且文件下载的归属统计能算到本启动器；
//   · MCIM 镜像 `https://mod.mcimirror.top/curseforge/v1`：免 key、国内 CDN 加速，
//     官方**过慢 / 连不上 / 环境不允许**时自动接管。注意它只缓存 Mod 类内容，
//     非 Mod（整合包/资源包/地图）与 >20MB 的文件可能拿不到；
//   · curse.tools `https://api.curse.tools/v1`：免 key 的社区只读代理，兜最后一道。
//
// 单层超过 6 秒无响应即判定「过慢」→ 记 10 分钟冷却并换下一层；成功的层会被记住，
// 本会话内优先（见 <see cref="Ladder"/> / <see cref="SendAsync"/>）。
// 密钥只在官方层发（镜像与代理用不到，也不该外泄）。
//
// ── 密钥从哪来 ─────────────────────────────────────────────────────────────
// 用户自备（设置里填）优先，留空则用构建时注入的内置密钥（官方分发版本共用一把，
// 不进 GitHub）——见 <see cref="CurseForgeKey"/> 与 secrets/README.md。
// 另外官方 CDN 自 2026-07-16 起下载也必须带 key，由
// <see cref="CurseForgeCdnAuthHandler"/> 统一补，见 <see cref="CurseForgeCdn"/>。
//
// ── 与 Modrinth 的关系 ─────────────────────────────────────────────────────
// 搜索结果与文件列表**直接复用 Modrinth 的 DTO**（ModrinthHit / ModrinthVersion），
// 只在 DTO 上多带一个 Source 标记。这样下载页、资源库、依赖检查、整合包安装这些
// 既有链路一行不用改，CurseForge 只是"另一个数据来源"。

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>资源来源标记（DTO 与资源库条目共用）</summary>
public static class ContentSourceIds
{
    public const string Modrinth = "modrinth";
    public const string CurseForge = "curseforge";

    public static string Display(string? id) =>
        string.Equals(id, CurseForge, StringComparison.OrdinalIgnoreCase) ? "CurseForge" : "Modrinth";
}

/// <summary>指纹反查命中：CurseForge 侧的 projectID / fileID（导出 manifest.json 的 files[] 用）</summary>
public sealed record CurseForgeMatch(string ProjectId, string FileId);

public sealed class CurseForgeService
{
    /// <summary>社区维护的官方 API 只读代理（无需 key，实测 200 且结构同官方）</summary>
    public const string ProxyApiBase = "https://api.curse.tools/v1";

    /// <summary>官方 API（需要用户自备 API Key）</summary>
    public const string OfficialApiBase = "https://api.curseforge.com/v1";

    /// <summary>Minecraft 在 CurseForge 的 gameId</summary>
    private const int MinecraftGameId = 432;

    /// <summary>资源分类 → classId（mod / modpack / resourcepack / shader / datapack）</summary>
    private static readonly Dictionary<string, int> ClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mod"] = 6,
        ["modpack"] = 4471,
        ["resourcepack"] = 6946,
        ["shader"] = 6552,
        ["datapack"] = 6756,
    };

    /// <summary>加载器 → modLoaderType（官方枚举：1 Forge / 4 Fabric / 5 Quilt / 6 NeoForge）</summary>
    private static readonly Dictionary<string, int> LoaderTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forge"] = 1,
        ["fabric"] = 4,
        ["quilt"] = 5,
        ["neoforge"] = 6,
    };

    private static readonly Dictionary<int, string> LoaderNames = new()
    {
        [1] = "forge",
        [4] = "fabric",
        [5] = "quilt",
        [6] = "neoforge",
    };

    /// <summary>Modrinth 排序键 → CurseForge sortField（1 推荐 / 2 热度 / 3 更新 / 6 总下载 / 11 发布日期）</summary>
    private static readonly Dictionary<string, int> SortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relevance"] = 1,
        ["downloads"] = 6,
        ["follows"] = 2,
        ["newest"] = 11,
        ["updated"] = 3,
    };

    private readonly DownloadService _downloader;
    private readonly Func<LauncherSettings> _settings;
    private readonly Dictionary<string, ModrinthVersion> _fileCache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public CurseForgeService(DownloadService downloader, Func<LauncherSettings> settings)
    {
        _downloader = downloader;
        _settings = settings;
    }

    /// <summary>MCIM 镜像（免 key；国内 CDN 加速，官方不可达/过慢时自动接管）</summary>
    public const string McimApiBase = "https://mod.mcimirror.top/curseforge/v1";

    /// <summary>单层「过慢」判定：超过它没拿到响应就换下一层（镜像在国内通常几十毫秒）</summary>
    private const int SlowSeconds = 6;

    /// <summary>某层失败后的冷却时长，到期自动重新纳入候选</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    /// <summary>各层的冷却截止时刻（键为接口根）</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooling = new(StringComparer.Ordinal);

    /// <summary>上次成功的接口根（本会话内优先，volatile：读写都是引用，无需加锁）</summary>
    private volatile string? _preferred;

    /// <summary>当前最先尝试的接口根（设置页 / 日志展示用）</summary>
    public string ApiBase => Ladder()[0];

    /// <summary>当前是否直连官方（否则走镜像 / 社区代理）</summary>
    public bool UsingOfficialApi => string.Equals(ApiBase, OfficialApiBase, StringComparison.Ordinal);

    /// <summary>
    /// 候选层：官方（有密钥才可用）→ MCIM 镜像 → curse.tools 代理；
    /// <paramref name="preferred"/> 提到最前。公开为 internal 便于单测。
    /// </summary>
    internal static List<string> Ladder(bool hasKey, string? preferred)
    {
        var list = new List<string>();
        if (hasKey) list.Add(OfficialApiBase);
        list.Add(McimApiBase);
        list.Add(ProxyApiBase);
        if (!string.IsNullOrEmpty(preferred))
        {
            list.Remove(preferred!);
            list.Insert(0, preferred!);
        }
        return list;
    }

    private List<string> Ladder() =>
        Ladder(CurseForgeKey.Effective(_settings()) is not null, _preferred);

    /// <summary>密钥只发给官方层（镜像与社区代理用不到，也不该外泄）</summary>
    private IReadOnlyDictionary<string, string>? HeadersFor(string apiBase)
    {
        if (!string.Equals(apiBase, OfficialApiBase, StringComparison.Ordinal)) return null;
        return CurseForgeKey.Effective(_settings()) is { } key
            ? new Dictionary<string, string> { ["x-api-key"] = key }
            : null;
    }

    private Task<string> GetAsync(string path, CancellationToken ct) => SendAsync(path, null, ct);

    /// <summary>POST（指纹反查）：与 GET 同一套逐层回退</summary>
    private Task<string> PostAsync(string path, string jsonBody, CancellationToken ct) =>
        SendAsync(path, jsonBody, ct);

    /// <summary>
    /// 逐层请求：官方 → MCIM → curse.tools。
    /// <para>单层超过 <see cref="SlowSeconds"/> 秒无响应（或连接失败 / 非 2xx）即判该层不可用，
    /// 记 <see cref="Cooldown"/> 冷却并换下一层；成功的层被记住并在本会话内优先。
    /// 全部冷却时仍会按顺序试一次——不能让冷却状态把功能锁死。</para>
    /// </summary>
    private async Task<string> SendAsync(string path, string? jsonBody, CancellationToken ct)
    {
        var ladder = Ladder();
        var now = DateTimeOffset.UtcNow;
        Exception? last = null;
        foreach (var apiBase in ladder)
        {
            ct.ThrowIfCancellationRequested();
            if (Cooling(apiBase, now) && ladder.Any(b => !Cooling(b, now))) continue;

            try
            {
                using var slow = CancellationTokenSource.CreateLinkedTokenSource(ct);
                slow.CancelAfter(TimeSpan.FromSeconds(SlowSeconds));
                // trackSourceHealth: false —— 这是 CurseForge 的接口，与 Mojang 镜像健康度无关，
                // 别让一次 CF 超时把原版下载的源排序也带偏
                var text = jsonBody is null
                    ? await _downloader.GetStringAsync(apiBase + path, HeadersFor(apiBase), slow.Token,
                        trackSourceHealth: false)
                    : await _downloader.PostJsonAsync(apiBase + path, jsonBody, HeadersFor(apiBase), slow.Token);
                MarkGood(apiBase);
                return text;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex is OperationCanceledException
                    ? new TimeoutException($"{SlowSeconds} 秒无响应")
                    : ex;
                MarkBad(apiBase, last);
            }
        }
        throw new IOException($"CurseForge 接口不可用（{ladder.Count} 层都没成功）：{path}", last);
    }

    private bool Cooling(string apiBase, DateTimeOffset now) =>
        _cooling.TryGetValue(apiBase, out var until) && until > now;

    private void MarkGood(string apiBase)
    {
        if (!string.Equals(_preferred, apiBase, StringComparison.Ordinal))
            LauncherLog.Write("curseforge", $"接口使用 {EndpointName(apiBase)}");
        _preferred = apiBase;
        _cooling.TryRemove(apiBase, out _);
    }

    private void MarkBad(string apiBase, Exception ex)
    {
        _cooling[apiBase] = DateTimeOffset.UtcNow + Cooldown;
        LauncherLog.Write("curseforge",
            $"{EndpointName(apiBase)} 不可用（{ex.Message}）→ {Cooldown.TotalMinutes:F0} 分钟内改用下一层");
    }

    /// <summary>接口层展示名（日志 / 设置页）</summary>
    public static string EndpointName(string apiBase) => apiBase switch
    {
        OfficialApiBase => "CurseForge 官方 API",
        McimApiBase => "MCIM 镜像",
        _ => "curse.tools 代理",
    };

    // ── 搜索 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 搜索资源。参数与 <see cref="ModrinthService.SearchAsync"/> 同形，返回同样的结果类型，
    /// 于是界面侧可以无差别地切换来源。
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query, string kind, string? loader, string? gameVersion,
        int offset = 0, int limit = 20, string index = "relevance", CancellationToken ct = default)
    {
        var classId = ClassIds.GetValueOrDefault(kind, 6);
        var pageSize = Math.Clamp(limit, 1, 50);
        var url = $"/mods/search?gameId={MinecraftGameId}&classId={classId}" +
                  $"&index={Math.Max(0, offset)}&pageSize={pageSize}" +
                  $"&searchFilter={Uri.EscapeDataString(query ?? "")}";
        // 版本筛选只认官方版本 id：实例名会被官方 API 判成无效参数（400）
        if (GameVersionFilter.OrNull(gameVersion) is { } searchVersion)
            url += $"&gameVersion={Uri.EscapeDataString(searchVersion)}";
        if (!string.IsNullOrWhiteSpace(loader) &&
            LoaderTypes.TryGetValue(loader, out var loaderType))
            url += $"&modLoaderType={loaderType}";
        url += $"&sortField={SortFields.GetValueOrDefault(index, 1)}&sortOrder=desc";

        return MapSearchResponse(await GetAsync(url, ct));
    }

    /// <summary>把 `/mods/search` 响应映射成统一资源模型（公开以便单独测试与插件复用）</summary>
    public static ModrinthSearchResult MapSearchResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new ModrinthSearchResult();
        if (root.TryGetProperty("pagination", out var pagination) &&
            pagination.TryGetProperty("totalCount", out var total) && total.TryGetInt32(out var count))
            result.TotalHits = count;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Str(item, "id");
            if (string.IsNullOrEmpty(id)) continue;
            result.Hits.Add(new ModrinthHit
            {
                ProjectId = id,
                Slug = Str(item, "slug") ?? "",
                Title = Str(item, "name") ?? "",
                Description = Str(item, "summary") ?? "",
                Author = Authors(item),
                Downloads = Num(item, "downloadCount"),
                IconUrl = Nested(item, "logo", "thumbnailUrl") ?? Nested(item, "logo", "url"),
                DateModified = Date(item, "dateModified"),
                DisplayCategories = Categories(item),
                Loaders = IndexLoaders(item),
                Source = ContentSourceIds.CurseForge,
                WebUrl = Nested(item, "links", "websiteUrl"),
            });
        }
        return result;
    }

    // ── 项目文件 ─────────────────────────────────────────────────────────

    /// <summary>列出项目的文件（可按加载器/游戏版本筛）；返回 Modrinth 同形 DTO</summary>
    public async Task<List<ModrinthVersion>> ListVersionsAsync(
        string projectId, string? loader, string? gameVersion, CancellationToken ct = default)
    {
        if (!int.TryParse(projectId, out _))
            throw new InvalidOperationException($"无效的 CurseForge 项目 ID：{projectId}");
        var url = $"/mods/{projectId}/files?pageSize=50";
        if (GameVersionFilter.OrNull(gameVersion) is { } listVersion)
            url += $"&gameVersion={Uri.EscapeDataString(listVersion)}";
        if (!string.IsNullOrWhiteSpace(loader) && LoaderTypes.TryGetValue(loader, out var type))
            url += $"&modLoaderType={type}";

        return MapFilesResponse(projectId, await GetAsync(url, ct));
    }

    /// <summary>把 `/mods/{id}/files` 响应映射成统一文件模型</summary>
    public static List<ModrinthVersion> MapFilesResponse(string projectId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var versions = new List<ModrinthVersion>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return versions;
        foreach (var file in data.EnumerateArray())
        {
            var mapped = MapFile(projectId, file);
            if (mapped is not null) versions.Add(mapped);
        }
        return versions;
    }

    /// <summary>单个文件（整合包 manifest 里给的就是 projectID + fileID）</summary>
    public async Task<ModrinthVersion?> GetFileAsync(
        string projectId, string fileId, CancellationToken ct = default)
    {
        var key = $"{projectId}:{fileId}";
        lock (_gate) if (_fileCache.TryGetValue(key, out var cached)) return cached;

        using var doc = JsonDocument.Parse(await GetAsync($"/mods/{projectId}/files/{fileId}", ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        var mapped = MapFile(projectId, data);
        if (mapped is not null) lock (_gate) _fileCache[key] = mapped;
        return mapped;
    }

    /// <summary>项目标题（依赖提示用；失败返回 null）</summary>
    public async Task<string?> GetProjectTitleAsync(string projectId, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await GetAsync($"/mods/{projectId}", ct));
            return doc.RootElement.TryGetProperty("data", out var data)
                ? Str(data, "name")
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>批量取项目标题（依赖列表面板用）</summary>
    public async Task<Dictionary<string, string>> GetProjectTitlesAsync(
        IReadOnlyCollection<string> projectIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in projectIds.Distinct().Take(20))
        {
            var title = await GetProjectTitleAsync(id, ct);
            if (!string.IsNullOrWhiteSpace(title)) result[id] = title!;
        }
        return result;
    }

    // ── 直链 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// CurseForge CDN 直链（官方响应没给 downloadUrl 时的兜底）；
    /// 分片规则：`files/{fileId/1000}/{fileId%1000}/{fileName}`。
    /// </summary>
    public static string CdnUrl(string fileId, string fileName)
    {
        if (!long.TryParse(fileId, out var id)) return "";
        return $"https://mediafilez.forgecdn.net/files/{id / 1000}/{id % 1000}/{Uri.EscapeDataString(fileName)}";
    }

    // ── 指纹（导出整合包时反查 projectID / fileID）─────────────────────────

    /// <summary>
    /// CurseForge 文件指纹：剔除全部 ASCII 空白字节（0x09 / 0x0A / 0x0D / 0x20）后
    /// 做 murmur2（seed = 1），最后抹掉最高位成为 31 位正整数。
    /// 标准 murmur2 的初值是 <c>seed ^ length</c>（length 为剔除空白后的字节数），
    /// 所以先数一遍长度再回卷计算；两遍都是流式，不把文件整块读进内存。
    /// </summary>
    public static int Fingerprint(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Fingerprint(stream);
    }

    /// <inheritdoc cref="Fingerprint(string)"/>
    public static int Fingerprint(Stream stream)
    {
        const uint seed = 1;
        const uint m = 0x5bd1e995;
        const int r = 24;

        if (!stream.CanSeek)
        {
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            return Fingerprint(buffered);
        }

        var start = stream.Position;
        var length = CountContentBytes(stream);
        stream.Position = start;

        uint h = seed ^ (uint)length;
        var block = new byte[4];
        var filled = 0;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b is 0x09 or 0x0A or 0x0D or 0x20) continue;
                block[filled++] = b;
                if (filled < 4) continue;

                var k = (uint)(block[0] | (block[1] << 8) | (block[2] << 16) | (block[3] << 24));
                k *= m;
                k ^= k >> r;
                k *= m;
                h *= m;
                h ^= k;
                filled = 0;
            }
        }

        switch (filled)
        {
            case 3:
                h ^= (uint)(block[2] << 16);
                h ^= (uint)(block[1] << 8);
                h ^= block[0];
                h *= m;
                break;
            case 2:
                h ^= (uint)(block[1] << 8);
                h ^= block[0];
                h *= m;
                break;
            case 1:
                h ^= block[0];
                h *= m;
                break;
        }

        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return (int)(h & 0x7FFFFFFF);
    }

    /// <summary>数出剔除 ASCII 空白后剩余的字节数（用于 murmur2 的 <c>seed ^ length</c> 初值）</summary>
    private static long CountContentBytes(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        long count = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++)
                if (buffer[i] is not (0x09 or 0x0A or 0x0D or 0x20))
                    count++;
        return count;
    }

    /// <summary>
    /// 批量反查指纹 → (projectId, fileId)。官方接口单次上限 1000 个，按块提交。
    /// 任一环节失败即返回已拿到的部分（调用方对未命中文件回退 overrides 打包），不抛异常。
    /// </summary>
    public async Task<Dictionary<int, CurseForgeMatch>> MatchFingerprintsAsync(
        IReadOnlyList<int> fingerprints, CancellationToken ct = default)
    {
        var result = new Dictionary<int, CurseForgeMatch>();
        foreach (var chunk in fingerprints.Distinct().Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            var body = JsonSerializer.Serialize(new { fingerprints = chunk });
            try
            {
                var text = await PostAsync("/mods/fingerprints", body, ct);
                ParseFingerprintMatches(text, result);
            }
            catch (Exception ex)
            {
                // 反查失败不该让导出失败：未命中的文件会走 overrides，包依然完整可用
                LauncherLog.Write("curseforge", $"指纹反查失败，剩余文件将打包进 overrides：{ex.Message}");
                break;
            }
        }
        return result;
    }

    /// <summary>解析 /mods/fingerprints 响应（兼容 exactMatches 元素本身是 File 或嵌一层 file 对象）</summary>
    internal static void ParseFingerprintMatches(string json, IDictionary<int, CurseForgeMatch> sink)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("exactMatches", out var matches) ||
            matches.ValueKind != JsonValueKind.Array)
            return;

        foreach (var match in matches.EnumerateArray())
        {
            var file = match.TryGetProperty("file", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner
                : match;
            var fileId = Str(file, "id");
            var projectId = Str(file, "modId");
            if (fileId is null || projectId is null) continue;
            if (!file.TryGetProperty("fileFingerprint", out var fp)) continue;

            var value = fp.ValueKind switch
            {
                JsonValueKind.Number when fp.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(fp.GetString(), out var s) => s,
                _ => (long?)null,
            };
            if (value is null) continue;
            sink[(int)(value.Value & 0x7FFFFFFF)] = new CurseForgeMatch(projectId, fileId);
        }
    }

    // ── 映射 ─────────────────────────────────────────────────────────────

    private static ModrinthVersion? MapFile(string projectId, JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object) return null;
        var id = Str(file, "id");
        var fileName = Str(file, "fileName") ?? "";
        if (string.IsNullOrEmpty(id) || fileName.Length == 0) return null;
        var url = Str(file, "downloadUrl");
        if (string.IsNullOrWhiteSpace(url)) url = CdnUrl(id, fileName);

        var loaders = new List<string>();
        if (file.TryGetProperty("modLoaderType", out var loaderType) && loaderType.TryGetInt32(out var lt) &&
            LoaderNames.TryGetValue(lt, out var name))
            loaders.Add(name);
        // 官方 API 在部分响应里不给 modLoaderType：退而从 gameVersions 里捞加载器名
        if (loaders.Count == 0 && file.TryGetProperty("gameVersions", out var gv) &&
            gv.ValueKind == JsonValueKind.Array)
            foreach (var v in gv.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String &&
                    LoaderTypes.ContainsKey(v.GetString() ?? ""))
                    loaders.Add(v.GetString()!.ToLowerInvariant());

        return new ModrinthVersion
        {
            Id = id,
            ProjectId = projectId,
            VersionNumber = Str(file, "displayName") ?? fileName,
            VersionType = (int)Num(file, "releaseType") switch
            {
                2 => "beta",
                3 => "alpha",
                _ => "release",
            },
            GameVersions = GameVersions(file),
            Loaders = loaders.Distinct().ToList(),
            Files =
            [
                new ModrinthFile
                {
                    Url = url!,
                    Filename = fileName,
                    Primary = true,
                    Size = Num(file, "fileLength"),
                    Sha1 = Sha1(file),
                },
            ],
            Dependencies = Dependencies(file),
            Source = ContentSourceIds.CurseForge,
        };
    }

    /// <summary>GameVersions 里混着加载器名与 "Client"/"Server"，只留形如 1.21.1 / 26.3 的版本号</summary>
    private static List<string> GameVersions(JsonElement file)
    {
        var list = new List<string>();
        if (!file.TryGetProperty("gameVersions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String) continue;
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s) || !s.All(c => char.IsDigit(c) || c == '.')) continue;
            if (!list.Contains(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>SHA1（官方 hashes[].algo == 1 即 SHA1）</summary>
    private static string? Sha1(JsonElement file)
    {
        if (!file.TryGetProperty("hashes", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var h in arr.EnumerateArray())
            if (h.TryGetProperty("algo", out var algo) && algo.TryGetInt32(out var a) && a == 1)
                return Str(h, "value")?.ToLowerInvariant();
        return null;
    }

    /// <summary>依赖关系（relationType：1 内嵌 / 2 可选 / 3 必需 / 4 工具 / 5 冲突 / 6 包含）</summary>
    private static List<ModrinthDependency> Dependencies(JsonElement file)
    {
        var list = new List<ModrinthDependency>();
        if (!file.TryGetProperty("dependencies", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var d in arr.EnumerateArray())
        {
            var modId = Str(d, "modId");
            if (string.IsNullOrEmpty(modId)) continue;
            var type = (int)Num(d, "relationType") switch
            {
                2 => "optional",
                3 => "required",
                5 => "incompatible",
                _ => "embedded",
            };
            list.Add(new ModrinthDependency { ProjectId = modId, DependencyType = type });
        }
        return list;
    }

    private static string Authors(JsonElement item)
    {
        if (!item.TryGetProperty("authors", out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        return string.Join(", ", arr.EnumerateArray()
            .Select(a => Str(a, "name"))
            .Where(n => !string.IsNullOrWhiteSpace(n)));
    }

    private static List<string> Categories(JsonElement item)
    {
        var list = new List<string>();
        if (!item.TryGetProperty("categories", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var c in arr.EnumerateArray())
        {
            var name = Str(c, "name");
            if (!string.IsNullOrWhiteSpace(name)) list.Add(name!);
        }
        return list;
    }

    /// <summary>项目支持的加载器（从 latestFilesIndexes 的 modLoader 归纳）</summary>
    private static List<string> IndexLoaders(JsonElement item)
    {
        var list = new List<string>();
        if (!item.TryGetProperty("latestFilesIndexes", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
            if (e.TryGetProperty("modLoader", out var loader) && loader.TryGetInt32(out var lt) &&
                LoaderNames.TryGetValue(lt, out var name) && !list.Contains(name))
                list.Add(name);
        return list;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var n) &&
              n.ValueKind == JsonValueKind.Number
                ? n.ToString()
                : null;

    private static long Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : 0;

    private static string? Nested(JsonElement obj, string outer, string inner) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(outer, out var o) &&
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(inner, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTime Date(JsonElement obj, string name) =>
        DateTime.TryParse(Str(obj, name), out var t) ? t.ToUniversalTime() : DateTime.MinValue;
}
