// Modrinth API v2：资源搜索 + 项目版本文件查询（api.modrinth.com，无需鉴权）

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public sealed class ModrinthService
{
    public const string ApiBase = "https://api.modrinth.com/v2";

    private readonly DownloadService _downloader;

    public ModrinthService(DownloadService downloader) => _downloader = downloader;

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
        string? category = null, CancellationToken ct = default)
    {
        // facets 外层数组为 AND，内层数组为 OR
        var facets = new List<IReadOnlyList<string>> { new[] { $"project_type:{projectType}" } };
        if (!string.IsNullOrWhiteSpace(loader)) facets.Add([$"categories:{loader}"]);
        if (!string.IsNullOrWhiteSpace(category)) facets.Add([$"categories:{category}"]);
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add([$"versions:{gameVersion}"]);

        var url = $"{ApiBase}/search?limit={limit}&offset={offset}&index={index}" +
                  $"&query={Uri.EscapeDataString(query ?? "")}" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        var json = await _downloader.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<ModrinthSearchResult>(json) ?? new();
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
