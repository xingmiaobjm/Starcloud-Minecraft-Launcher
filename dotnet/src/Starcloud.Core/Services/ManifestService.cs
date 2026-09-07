// 版本清单：获取 + 本地缓存 + 安装状态标记（对应旧 Rust version_manifest_cache）

using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

public sealed class ManifestService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly DownloadService _downloader;

    public bool LastLoadFromCache { get; private set; }

    public ManifestService(DownloadService downloader) => _downloader = downloader;

    /// <summary>读取版本清单：优先缓存（快速展示），后台再刷新由调用方触发。</summary>
    public async Task<MinecraftManifest> GetManifestAsync(bool forceRefresh, CancellationToken ct = default)
    {
        LauncherPaths.EnsureDataDir();
        if (!forceRefresh && File.Exists(LauncherPaths.ManifestCacheFile))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<MinecraftManifest>(
                    File.ReadAllText(LauncherPaths.ManifestCacheFile));
                if (cached is { Versions.Count: > 0 })
                {
                    LastLoadFromCache = true;
                    return cached;
                }
            }
            catch
            {
                /* 缓存损坏则走网络 */
            }
        }

        var json = await _downloader.GetStringAsync(ManifestUrl, ct);
        var manifest = JsonSerializer.Deserialize<MinecraftManifest>(json)
                       ?? throw new InvalidOperationException("版本清单解析失败");
        File.WriteAllText(LauncherPaths.ManifestCacheFile, json);
        LastLoadFromCache = false;
        return manifest;
    }

    /// <summary>列出已安装版本（versions/&lt;id&gt;/&lt;id&gt;.json 存在即已安装）</summary>
    public IReadOnlyList<InstalledVersionInfo> ListInstalled(string dotMinecraft)
    {
        var dir = Path.Combine(dotMinecraft, "versions");
        var result = new List<InstalledVersionInfo>();
        if (!Directory.Exists(dir)) return result;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            var json = Path.Combine(sub, $"{id}.json");
            if (!File.Exists(json)) continue;
            long size = 0;
            var jar = Path.Combine(sub, $"{id}.jar");
            if (File.Exists(jar)) size = new FileInfo(jar).Length;
            result.Add(new InstalledVersionInfo(id, size));
        }
        return result;
    }
}

public sealed record InstalledVersionInfo(string Id, long SizeBytes);

/// <summary>已安装版本的删除</summary>
public static class InstalledVersionManager
{
    public static void Remove(string dotMinecraft, string versionId)
    {
        var dir = Path.Combine(dotMinecraft, "versions", versionId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>设置持久化辅助（last_version 等）</summary>
public static class SettingsJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
