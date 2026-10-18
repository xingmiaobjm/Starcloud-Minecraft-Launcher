// SC 实验室 · 投影社区：多源聚合投影资源（卡片展示 + 一键下载到投影目录）。
//
// 数据源策略（多源 + 可配置 + 容错）：
//   · GitHub —— 主源。搜索 topic:litematica 等主题下仓库，再从其 Release 取投影文件；
//     **API 基址可配置**：默认为「gh-proxy.com 镜像优先、直连回退」（本机实测直连不可达），
//     用户可在界面填写自定义镜像/代理基址。
//   · 自定义源 —— 用户在界面填的任意 JSON 接口（约定：返回数组，字段见 GenericJsonSource）。
//
// 说明：Reden（redenmc.com）没有公开的投影目录 API（其 OpenAPI 只有账号类端点，
// 投影数据走 Nuxt SSR），故不作为"源"接入。

using System.Text.Json;

namespace Starcloud.Core.Services.Lab;

/// <summary>社区里的一个投影资源（卡片模型）</summary>
public sealed record CommunityProjection(
    string Source,
    string Name,
    string Author,
    string Description,
    int Stars,
    DateTime UpdatedAt,
    string FullName,
    string HtmlUrl,
    string ImageUrl);

/// <summary>投影资源里的一个可下载文件</summary>
public sealed record CommunityFile(string Name, long SizeBytes, string DownloadUrl)
{
    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1048576.0:F1} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";
}

/// <summary>一个投影源</summary>
public interface IProjectionSource
{
    string Name { get; }
    Task<List<CommunityProjection>> SearchAsync(string query, CancellationToken ct);
    Task<List<CommunityFile>> ListFilesAsync(CommunityProjection item, CancellationToken ct);
}

/// <summary>
/// GitHub 源：搜索仓库 + 从 Release 取投影文件 + 仓库社交图作卡片封面。
/// 基址候选：设置里的自定义基址（如非空）→ gh-proxy 镜像 → 直连；成功的基址被记住。
/// </summary>
public sealed class GitHubProjectionSource : IProjectionSource
{
    private const string DirectApi = "https://api.github.com";
    private const string MirrorPrefix = "https://gh-proxy.com/";

    /// <summary>投影文件扩展名（Release 资产过滤用）</summary>
    private static readonly string[] ProjectionExtensions =
        [".litematic", ".nbt", ".schematic", ".schem"];

    private readonly DownloadService _download;
    private readonly Func<string?> _customBase;

    /// <summary>
    /// 一条通道 = API 基址 + 代理前缀（成对管理）。
    /// <para><b>为什么必须成对</b>：代理前缀只能用来拼原始 GitHub 域名
    /// （<c>https://gh-proxy.com/</c> + <c>https://opengraph.githubassets.com/…</c>）；
    /// 曾经把"API 基址"直接拿去拼图片，拼出了
    /// <c>gh-proxy.com/https://api.github.com/https://opengraph…</c> 这种嵌套错误 URL，
    /// 表现就是卡片封面全部加载失败。</para>
    /// </summary>
    private sealed record Channel(string Api, string? ProxyPrefix, string Label);

    /// <summary>上一次成功的通道（进程内记忆）；null = 尚未探到（默认镜像优先）</summary>
    private Channel? _preferred;

    private static readonly Channel MirrorChannel = new(MirrorPrefix + DirectApi, MirrorPrefix, "镜像（gh-proxy）");
    private static readonly Channel DirectChannel = new(DirectApi, null, "直连");

    private readonly Func<string?> _token;

    public GitHubProjectionSource(DownloadService download, Func<string?>? customBase = null,
        Func<string?>? token = null)
    {
        _download = download;
        _customBase = customBase ?? (() => null);
        _token = token ?? (() => null);
    }

    public string Name => "GitHub";

    /// <summary>当前通道展示名（界面标注"经镜像/直连"）</summary>
    public string CurrentBaseLabel => _preferred?.Label ?? "自动";

    /// <summary>候选通道：自定义（若有）→ 镜像 → 直连；已探到成功的排最前</summary>
    private List<Channel> Channels()
    {
        var list = new List<Channel>();
        var custom = _customBase()?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(custom))
        {
            if (custom!.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                // 完整 API 基址：代理前缀 = 去掉 "/api.github.com" 的部分（没有就当直连用原域）
                var idx = custom.IndexOf("/https://api.github.com", StringComparison.OrdinalIgnoreCase);
                var prefix = idx > 0 ? custom[..idx] + "/" : null;
                list.Add(new Channel(custom, prefix, "自定义"));
            }
            else
            {
                // 代理前缀：API 走 前缀 + api.github.com，图片/下载走 前缀 + 原域
                list.Add(new Channel(custom + "/" + DirectApi, custom + "/", "自定义代理"));
            }
        }
        list.Add(MirrorChannel);
        list.Add(DirectChannel);
        if (_preferred is { } p)
        {
            list.RemoveAll(c => string.Equals(c.Api, p.Api, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, p);
        }
        return list.DistinctBy(c => c.Api, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<CommunityProjection>> SearchAsync(string query, CancellationToken ct)
    {
        // 检索词策略：topic:litematica 下几乎全是"围绕投影的工具/模组"（viewer、printer、库），
        // 投影文件本体（机器蓝图）多在不打 topic 的发布仓里，所以改用关键词检索并叠加
        // litematic（仓库名/描述/README 命中），覆盖面明显更广；
        // "是否真的含投影文件"交给上层的 Release 探测过滤（BuildSearchQuery 可单测）。
        var json = await GetApiAsync(
            $"/search/repositories?q={Uri.EscapeDataString(BuildSearchQuery(query))}" +
            "&sort=stars&order=desc&per_page=30", ct);
        return ParseSearchJson(json, ImageUrlFor);
    }

    /// <summary>拼 GitHub 仓库检索词（纯函数，单测覆盖）：空查询 → litematic；有词 → "词 litematic"（已含则不叠）</summary>
    internal static string BuildSearchQuery(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return "litematic";
        return q.Contains("litematic", StringComparison.OrdinalIgnoreCase) ? q : $"{q} litematic";
    }

    public async Task<List<CommunityFile>> ListFilesAsync(CommunityProjection item, CancellationToken ct)
    {
        var json = await GetApiAsync($"/repos/{item.FullName}/releases?per_page=3", ct);
        return ParseReleasesJson(json, ToGitHubUrl);
    }

    /// <summary>卡片封面：GitHub 仓库社交图（同样经当前基址代理）</summary>
    private string ImageUrlFor(string fullName) =>
        ToGitHubUrl($"https://opengraph.githubassets.com/1/{fullName}");

    /// <summary>把一个 github 域名的 URL（下载直链 / 封面图）按当前通道映射：代理前缀拼接或原样直连</summary>
    private string ToGitHubUrl(string githubUrl)
    {
        var prefix = _preferred?.ProxyPrefix ?? MirrorPrefix; // 未探到时默认镜像前缀
        return prefix is null ? githubUrl : prefix + githubUrl;
    }

    /// <summary>带通道回退的 API GET（成功者被记住，下次优先）</summary>
    private async Task<string> GetApiAsync(string path, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var channel in Channels())
        {
            try
            {
                // 可选 Token：匿名每小时只有 60 次，探测一轮就可能耗尽；填了 Token 是 5000/h
                IReadOnlyDictionary<string, string>? headers = _token() is { Length: > 0 } token
                    ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
                    : null;
                var json = await _download.GetStringAsync(channel.Api + path, headers, ct, trackSourceHealth: false);
                _preferred = channel;
                return json;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new IOException("GitHub API 不可达（尝试过的通道都失败）", last);
    }

    /// <summary>解析仓库搜索 JSON（纯函数，单测直接喂样本）；imageUrlFor 注入了卡片封面映射</summary>
    internal static List<CommunityProjection> ParseSearchJson(string json, Func<string, string> imageUrlFor)
    {
        var result = new List<CommunityProjection>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return result;
        foreach (var item in items.EnumerateArray())
        {
            var full = item.TryGetProperty("full_name", out var f) ? f.GetString() ?? "" : "";
            result.Add(new CommunityProjection(
                Source: "GitHub",
                Name: item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Author: item.TryGetProperty("owner", out var o) && o.TryGetProperty("login", out var l)
                    ? l.GetString() ?? "" : "",
                Description: item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                Stars: item.TryGetProperty("stargazers_count", out var s) ? s.GetInt32() : 0,
                UpdatedAt: item.TryGetProperty("pushed_at", out var u) && DateTime.TryParse(u.GetString(), out var dt)
                    ? dt.ToLocalTime() : DateTime.MinValue,
                FullName: full,
                HtmlUrl: item.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                ImageUrl: full.Length > 0 ? imageUrlFor(full) : ""));
        }
        return result;
    }

    /// <summary>解析 Release JSON：取最新"含投影文件"的 release 里的资产</summary>
    internal static List<CommunityFile> ParseReleasesJson(string json, Func<string, string> mapUrl)
    {
        var files = new List<CommunityFile>();
        using var doc = JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets)) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (!ProjectionExtensions.Contains(ext)) continue;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (url.Length == 0) continue;
                files.Add(new CommunityFile(name,
                    asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                    mapUrl(url)));
            }
            if (files.Count > 0) break;
        }
        return files;
    }
}

/// <summary>
/// 自定义源：任意返回 JSON 数组的接口（用户自建 / 镜像站）。字段大小写不敏感，常见别名都认：
///   name/title、author/owner、description/summary、url/download_url、image/image_url/thumbnail、stars、updated_at
/// </summary>
public sealed class GenericJsonSource : IProjectionSource
{
    private readonly string _url;
    private readonly DownloadService _download;

    public GenericJsonSource(string name, string url, DownloadService download)
    {
        Name = name;
        _url = url;
        _download = download;
    }

    public string Name { get; }

    public async Task<List<CommunityProjection>> SearchAsync(string query, CancellationToken ct)
    {
        var json = await _download.GetStringAsync(_url, null, ct, trackSourceHealth: false);
        return Parse(json, Name);
    }

    /// <summary>自定义源的一条记录 = 一个可直接下载的文件（名称取 URL 文件名）</summary>
    public Task<List<CommunityFile>> ListFilesAsync(CommunityProjection item, CancellationToken ct)
    {
        var fileName = Path.GetFileName(new Uri(item.FullName).LocalPath);
        return Task.FromResult<List<CommunityFile>>([new CommunityFile(fileName, 0, item.FullName)]);
    }

    internal static List<CommunityProjection> Parse(string json, string sourceName)
    {
        var result = new List<CommunityProjection>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var url = Pick(item, "url", "download_url", "file", "href");
            if (url.Length == 0) continue;
            var name = Pick(item, "name", "title");
            if (name.Length == 0)
            {
                try { name = Path.GetFileName(new Uri(url).LocalPath); } catch { name = url; }
            }
            result.Add(new CommunityProjection(
                Source: sourceName,
                Name: name,
                Author: Pick(item, "author", "owner", "uploader"),
                Description: Pick(item, "description", "summary", "desc"),
                Stars: int.TryParse(Pick(item, "stars", "stargazers_count"), out var st) ? st : 0,
                UpdatedAt: DateTime.TryParse(Pick(item, "updated_at", "pushed_at", "date"), out var dt)
                    ? dt.ToLocalTime() : DateTime.MinValue,
                FullName: url,
                HtmlUrl: Pick(item, "html_url", "page", "homepage") is { Length: > 0 } page ? page : url,
                ImageUrl: Pick(item, "image", "image_url", "thumbnail", "cover")));
        }
        return result;
    }

    private static string Pick(JsonElement item, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var prop in item.EnumerateObject())
            {
                if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString()?.Trim() ?? "";
                if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetRawText();
            }
        }
        return "";
    }
}

/// <summary>投影社区服务：多源聚合 + 短缓存 + 下载编排（源每次按设置重建，改配置即时生效）</summary>
public sealed class ProjectionCommunityService(Func<LauncherSettings> settings, DownloadService download)
{
    private readonly Dictionary<string, (DateTime At, List<CommunityProjection> Items)> _cache = new(StringComparer.Ordinal);

    /// <summary>缓存有效期：列表数据变化慢，10 分钟内不重复打 API（匿名 API 有速率限制）</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>GitHub 源实例（保留以便界面显示当前基址）</summary>
    public GitHubProjectionSource GitHub { get; } =
        new(download, () => settings().ProjectionGithubApi, () => settings().ProjectionGithubToken);

    private List<IProjectionSource> BuildSources()
    {
        List<IProjectionSource> sources = [GitHub];
        var custom = settings().ProjectionCustomSources ?? "";
        foreach (var line in custom.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var uri = new Uri(line);
                sources.Add(new GenericJsonSource(uri.Host, line, download));
            }
            catch
            {
                // URL 非法就跳过这一行（界面允许用户边改边存）
            }
        }
        return sources;
    }

    public IReadOnlyList<string> SourceNames => BuildSources().Select(s => s.Name).ToList();

    /// <summary>聚合搜索：逐源抓取，单源失败不影响其余；去重后按星数排序</summary>
    public async Task<(List<CommunityProjection> Items, List<string> FailedSources)> SearchAsync(
        string query, bool forceRefresh = false, CancellationToken ct = default)
    {
        var key = query.Trim().ToLowerInvariant();
        if (!forceRefresh && _cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
            return (hit.Items, []);

        var items = new List<CommunityProjection>();
        var failed = new List<string>();
        foreach (var source in BuildSources())
        {
            try
            {
                items.AddRange(await source.SearchAsync(query, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failed.Add(source.Name);
            }
        }
        items = items
            .GroupBy(i => i.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(i => i.Stars)
            .ToList();
        _cache[key] = (DateTime.UtcNow, items);
        return (items, failed);
    }

    public async Task<List<CommunityFile>> ListFilesAsync(CommunityProjection item, CancellationToken ct = default)
    {
        var source = BuildSources().FirstOrDefault(s => s.Name == item.Source)
                     ?? throw new InvalidOperationException($"未知来源 {item.Source}");
        return await source.ListFilesAsync(item, ct);
    }

    /// <summary>Release 探测结果的缓存（FullName → 文件表；含"空结果"——工具仓库不重复探测）</summary>
    private readonly Dictionary<string, (DateTime At, List<CommunityFile> Files)> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _fileCacheLoaded;

    /// <summary>探测结果缓存有效期：匿名 API 每小时只有 60 次额度，这层缓存必须比列表缓存更持久</summary>
    private static readonly TimeSpan FileCacheTtl = TimeSpan.FromHours(6);

    private static string FileCachePath => Path.Combine(LauncherPaths.DataDir, "community_probe_cache.json");

    private sealed record CachedProbe(DateTime At, List<CommunityFile> Files);

    /// <summary>
    /// 并发探测一批仓库的 Release 里有没有投影文件（"只看含投影文件的仓库"过滤用）。
    /// <paramref name="progress"/> 回调 (已完成, 总数)；单个失败按"无文件"处理且不缓存（下次再试）。
    /// </summary>
    public async Task<(Dictionary<string, List<CommunityFile>> Files, HashSet<string> Failed)> ProbeFilesAsync(
        IReadOnlyList<CommunityProjection> items, int maxProbe = 12, int concurrency = 4,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        LoadFileCache();
        var result = new Dictionary<string, List<CommunityFile>>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<CommunityProjection>();

        foreach (var item in items)
        {
            if (item.Source != "GitHub") { pending.Add(item); continue; } // 自定义源的 URL 本身就是文件，不需探测
            if (_fileCache.TryGetValue(item.FullName, out var hit) && DateTime.UtcNow - hit.At < FileCacheTtl)
                result[item.FullName] = hit.Files;
            else if (pending.Count < maxProbe)
                pending.Add(item);
            // 超出本轮上限的：不探测也不判失败——界面按"未探测"保留显示，绝不当成"无文件"过滤掉
        }

        var done = 0;
        var total = pending.Count;
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = pending.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                List<CommunityFile> files;
                if (item.Source != "GitHub")
                {
                    files = [new CommunityFile(Path.GetFileName(new Uri(item.FullName).LocalPath), 0, item.FullName)];
                }
                else
                {
                    try
                    {
                        files = await ListFilesAsync(item, ct);
                        _fileCache[item.FullName] = (DateTime.UtcNow, files); // 含空结果：确认过没文件就不重复探测
                    }
                    catch
                    {
                        // 探测失败（额度耗尽 / 网络）：**必须与"确认无文件"区分**——
                        // 否则额度一满，列表会被整片误过滤成空（实测踩过）
                        files = [];
                        lock (failed) failed.Add(item.FullName);
                    }
                }
                lock (result) result[item.FullName] = files;
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress?.Invoke(n, total);
            }
        });
        await Task.WhenAll(tasks);
        SaveFileCache();
        return (result, failed);
    }

    /// <summary>探测缓存的磁盘读写（跨会话保留：额度耗尽时旧结果仍可用）</summary>
    private void LoadFileCache()
    {
        if (_fileCacheLoaded) return;
        _fileCacheLoaded = true;
        try
        {
            if (!File.Exists(FileCachePath)) return;
            var cached = JsonSerializer.Deserialize<Dictionary<string, CachedProbe>>(File.ReadAllText(FileCachePath));
            if (cached is null) return;
            foreach (var (key, value) in cached)
                if (DateTime.UtcNow - value.At < FileCacheTtl)
                    _fileCache[key] = (value.At, value.Files);
        }
        catch
        {
            // 缓存坏了就当没有
        }
    }

    private void SaveFileCache()
    {
        try
        {
            Directory.CreateDirectory(LauncherPaths.DataDir);
            var snapshot = _fileCache.ToDictionary(kv => kv.Key, kv => new CachedProbe(kv.Value.At, kv.Value.Files));
            File.WriteAllText(FileCachePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // 缓存写不进去不影响功能
        }
    }

    /// <summary>下载到投影目录（重名自动加序号）；返回落点</summary>
    public async Task<string> DownloadAsync(CommunityFile file, string projectionDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(projectionDir);
        var dest = UniquePath(projectionDir, file.Name);
        await download.DownloadFileAsync(file.DownloadUrl, dest, sha1: null, maxRetries: 2, ct);
        return dest;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(dir, fileName);
        for (var i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name}-{i}{ext}");
        return candidate;
    }
}
