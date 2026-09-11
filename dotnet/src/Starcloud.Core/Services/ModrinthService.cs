// Modrinth API v2：资源搜索 + 项目版本文件查询（api.modrinth.com，无需鉴权）

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public sealed class ModrinthService
{
    public const string ApiBase = "https://api.modrinth.com/v2";

    private readonly DownloadService _downloader;
    private readonly ResourceCacheService? _cache;

    public ModrinthService(DownloadService downloader, ResourceCacheService? cache = null)
    {
        _downloader = downloader;
        _cache = cache;
    }

    /// <summary>上次搜索是否命中磁盘缓存（供下载页显示「缓存」角标）</summary>
    public bool LastSearchFromCache { get; private set; }

    private List<ModrinthCategory>? _categoriesCache;

    /// <summary>全部分类标签（首次调用后缓存；project_type 字段区分资源类型）</summary>
    public async Task<IReadOnlyList<ModrinthCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        if (_categoriesCache is not null) return _categoriesCache;
        var json = await _downloader.GetStringAsync($"{ApiBase}/tag/category", ct);
        _categoriesCache = JsonSerializer.Deserialize<List<ModrinthCategory>>(json) ?? [];
        return _categoriesCache;
    }

    /// <summary>
    /// 搜索资源。projectType: mod/modpack/datapack/resourcepack/shader；
    /// loader/category/gameVersion 为 null 时不筛选；
    /// index: relevance/downloads/follows/newest/updated
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query, string projectType, string? loader, string? gameVersion,
        int offset = 0, int limit = 20, string index = "relevance",
        string? category = null, CancellationToken ct = default, bool forceRefresh = false)
    {
        // 仅缓存默认列表（首页 / 无关键词 / 无分类 / 按相关度 / offset=0）
        var cacheable = _cache is not null && offset == 0 && string.IsNullOrEmpty(query)
                        && category is null && index == "relevance";
        var cacheKey = cacheable
            ? $"modrinth.search.{projectType}.{loader ?? "any"}.{gameVersion ?? "any"}"
            : null;

        if (!forceRefresh && cacheKey is not null
            && _cache!.TryRead<ModrinthSearchResult>(cacheKey, out var cached)
            && cached is not null)
        {
            LastSearchFromCache = true;
            return cached;
        }

        // facets 外层数组为 AND，内层数组为 OR
        var facets = new List<IReadOnlyList<string>> { new[] { $"project_type:{projectType}" } };
        if (!string.IsNullOrWhiteSpace(loader)) facets.Add([$"categories:{loader}"]);
        if (!string.IsNullOrWhiteSpace(category)) facets.Add([$"categories:{category}"]);
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add([$"versions:{gameVersion}"]);

        var url = $"{ApiBase}/search?limit={limit}&offset={offset}&index={index}" +
                  $"&query={Uri.EscapeDataString(query ?? "")}" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        var json = await _downloader.GetStringAsync(url, ct);
        var result = JsonSerializer.Deserialize<ModrinthSearchResult>(json) ?? new();
        LastSearchFromCache = false;
        if (cacheKey is not null) _cache!.Write(cacheKey, result);
        return result;
    }

    /// <summary>查询项目兼容指定加载器/游戏版本的文件（按发布时间倒序）</summary>
    public async Task<List<ModrinthVersion>> ListVersionsAsync(
        string projectId, string? loader, string? gameVersion,
        CancellationToken ct = default)
    {
        var url = $"{ApiBase}/project/{projectId}/version";
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(loader))
            filters.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}");
        if (!string.IsNullOrWhiteSpace(gameVersion))
            filters.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        if (filters.Count > 0) url += "?" + string.Join("&", filters);
        var json = await _downloader.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<List<ModrinthVersion>>(json) ?? [];
    }

    /// <summary>按 SHA1 批量反查版本（分片 100/批；未命中的哈希不出现在结果字典）</summary>
    public async Task<Dictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(
        IReadOnlyCollection<string> sha1s, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        if (sha1s.Count == 0) return result;

        foreach (var chunk in sha1s.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            var body = JsonSerializer.Serialize(new { hashes = chunk, algorithm = "sha1" });
            var json = await _downloader.PostJsonAsync($"{ApiBase}/version_files", body, ct);
            var map = JsonSerializer.Deserialize<Dictionary<string, ModrinthVersion>>(json);
            if (map is null) continue;
            foreach (var (hash, version) in map) result[hash] = version;
        }
        return result;
    }

    /// <summary>取项目标题（依赖提示用；失败返回 null）</summary>
    public async Task<string?> GetProjectTitleAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        try
        {
            var json = await _downloader.GetStringAsync($"{ApiBase}/project/{projectId}", ct);
            return JsonSerializer.Deserialize<ModrinthProject>(json)?.Title;
        }
        catch { return null; }
    }
}

public sealed class ModrinthSearchResult
{
    [JsonPropertyName("total_hits")]
    public int TotalHits { get; set; }

    [JsonPropertyName("hits")]
    public List<ModrinthHit> Hits { get; set; } = [];
}

public sealed class ModrinthHit
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("date_modified")]
    public DateTime DateModified { get; set; }

    [JsonPropertyName("display_categories")]
    public List<string> DisplayCategories { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];
}

/// <summary>分类标签（/v2/tag/category）</summary>
public sealed class ModrinthCategory
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = "";
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    /// <summary>release / beta / alpha</summary>
    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = "";

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<ModrinthDependency> Dependencies { get; set; } = [];

    [JsonIgnore]
    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }
}

/// <summary>Modrinth 版本依赖项</summary>
public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    /// <summary>required | optional | incompatible | embedded</summary>
    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; set; } = "";
}

/// <summary>Modrinth 项目（仅取标题等展示字段）</summary>
public sealed class ModrinthProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}
