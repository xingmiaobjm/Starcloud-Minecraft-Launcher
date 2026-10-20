// 组件清单（manifest.json）模型与解析：运行时组件（JRE / LWJGL / 图形转译 / 音频）的唯一登记处。
// 清单随应用内置（M2 提供），所有字段在解析期完成校验，避免到下载环节才发现 URL 或哈希缺失。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Android.Core.Components;

/// <summary>组件压缩包格式；None 表示无包体（保留扩展位）</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchiveKind
{
    None,
    Zip,
    TarXz,
}

public sealed class ComponentEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("archive")] public ArchiveKind Archive { get; set; } = ArchiveKind.None;
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("license")] public string License { get; set; } = "";
    [JsonPropertyName("license_url")] public string? LicenseUrl { get; set; }
    [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
}

public sealed class ComponentManifest
{
    [JsonPropertyName("manifest_version")] public int ManifestVersion { get; set; }
    [JsonPropertyName("components")] public List<ComponentEntry> Components { get; set; } = [];

    /// <summary>解析并校验清单：字段完整性 / URL 协议 / 哈希格式 / 同名重复，任何问题直接抛中文异常</summary>
    public static ComponentManifest Parse(string json)
    {
        ComponentManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ComponentManifest>(json,
                new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"组件清单解析失败：{ex.Message}", ex);
        }
        if (manifest is null)
            throw new InvalidOperationException("组件清单解析失败：内容为空");
        if (manifest.Components.Count == 0)
            throw new InvalidOperationException("组件清单为空：至少需要登记一个组件");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in manifest.Components)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                throw new InvalidOperationException("组件清单存在缺少名称的条目");
            if (string.IsNullOrWhiteSpace(c.Version))
                throw new InvalidOperationException($"组件 {c.Name} 缺少版本号");
            // asset: 为应用内置资产伪协议（APK 内组件包，宿主经 IComponentArchiveSource 提供流）
            if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "asset"))
                throw new InvalidOperationException($"组件 {c.Name} 的下载地址无效（仅支持 http/https/asset）：{c.Url}");
            if (c.Sha256.Length != 64 || !c.Sha256.All(char.IsAsciiHexDigit))
                throw new InvalidOperationException($"组件 {c.Name} 的 SHA256 必须是 64 位十六进制字符串");
            if (string.IsNullOrWhiteSpace(c.License))
                throw new InvalidOperationException($"组件 {c.Name} 缺少许可证信息：清单必须披露每个组件的许可");
            if (!seen.Add(c.Name))
                throw new InvalidOperationException($"组件清单存在重复条目：{c.Name}");
        }
        return manifest;
    }

    /// <summary>按名称查找（忽略大小写，取首个匹配）；缺失返回 null</summary>
    public ComponentEntry? Find(string name) =>
        Components.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>按名称取必需组件；缺失即抛（清单不完整属于发布错误，不能拖到运行时）</summary>
    public ComponentEntry Require(string name) =>
        Find(name) ?? throw new InvalidOperationException($"组件清单缺少 {name}：请更新应用内置清单");
}
