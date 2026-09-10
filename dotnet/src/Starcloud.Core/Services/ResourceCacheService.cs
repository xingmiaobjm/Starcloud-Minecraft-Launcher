// 资源列表磁盘缓存：一 key 一文件，文件名 = key 的 SHA1（小写十六进制）

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>
/// 资源列表拉取缓存（Modrinth 搜索默认列表 / 版本清单）。
/// 开关实时读 <see cref="LauncherSettings.ResourceCacheEnabled"/>：关闭时完全不读不写。
/// </summary>
public sealed class ResourceCacheService(Func<LauncherSettings> settings)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Envelope(DateTime SavedAt, JsonElement Payload);

    /// <summary>缓存是否启用（镜像 LauncherSettings.ResourceCacheEnabled）。</summary>
    public bool Enabled => settings().ResourceCacheEnabled;

    private static string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(LauncherPaths.CacheDir, hash + ".json");
    }

    /// <summary>尝试读取缓存；未启用 / 未命中 / 损坏均返回 false。</summary>
    public bool TryRead<T>(string key, out T? value)
    {
        value = default;
        if (!Enabled) return false;
        var path = PathFor(key);
        if (!File.Exists(path)) return false;
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Options);
            if (env is null) return false;
            value = env.Payload.Deserialize<T>(Options);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入缓存（先写临时文件再原子替换）；未启用时直接返回。</summary>
    public void Write<T>(string key, T value)
    {
        if (!Enabled) return;
        Directory.CreateDirectory(LauncherPaths.CacheDir);
        var env = new Envelope(DateTime.Now, JsonSerializer.SerializeToElement(value, Options));
        var json = JsonSerializer.Serialize(env, Options);
        var path = PathFor(key);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>清空全部缓存文件。</summary>
    public void ClearAll()
    {
        if (!Directory.Exists(LauncherPaths.CacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(LauncherPaths.CacheDir, "*.json"))
        {
            try { File.Delete(f); }
            catch { /* 忽略占用 */ }
        }
    }

    /// <summary>旧 version_manifest_cache.json → 新 manifest.vanilla 缓存，迁移后删除旧文件。</summary>
    public void MigrateLegacyManifest()
    {
        var legacy = LauncherPaths.ManifestCacheFile;
        if (!Enabled || !File.Exists(legacy)) return;
        try
        {
            var json = File.ReadAllText(legacy);
            using var doc = JsonDocument.Parse(json);
            Write("manifest.vanilla", doc.RootElement.Clone());
            File.Delete(legacy);
        }
        catch
        {
            /* 迁移失败保留旧文件，下次再试 */
        }
    }
}
