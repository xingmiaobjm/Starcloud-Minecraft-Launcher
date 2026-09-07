// Fabric 加载器版本查询（meta.fabricmc.net）

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public sealed class FabricService
{
    private readonly DownloadService _downloader;

    public FabricService(DownloadService downloader) => _downloader = downloader;

    public static string DownloadSourceUrl => "https://meta.fabricmc.net/v2/versions/loader";

    public async Task<List<FabricLoaderEntry>> ListLoadersAsync(string mcVersion,
        CancellationToken ct = default)
    {
        var json = await _downloader.GetStringAsync($"{DownloadSourceUrl}/{mcVersion}", ct);
        return JsonSerializer.Deserialize<List<FabricLoaderEntry>>(json) ?? [];
    }
}

public sealed class FabricLoaderEntry
{
    [JsonPropertyName("loader")]
    public FabricLoaderVersion Loader { get; set; } = new();

    [JsonIgnore]
    public string Version => Loader.Version;
}

public sealed class FabricLoaderVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }
}
