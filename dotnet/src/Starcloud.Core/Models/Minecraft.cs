// Minecraft 清单与版本 JSON 的数据模型（System.Text.Json，蛇形命名）

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Models;

public sealed class MinecraftManifest
{
    [JsonPropertyName("latest")]
    public ManifestLatest Latest { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<ManifestVersion> Versions { get; set; } = [];
}

public sealed class ManifestLatest
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = "";

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; set; } = "";
}

public sealed class ManifestVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("releaseTime")]
    public string ReleaseTime { get; set; } = "";

    [JsonIgnore]
    public bool Installed { get; set; }
}

/// <summary>版本 JSON（version json）</summary>
public sealed class MinecraftVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("mainClass")]
    public string MainClass { get; set; } = "";

    /// <summary>旧版（1.13 前）启动参数字符串</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    /// <summary>新代（1.13+）启动参数（jvm / game 两组）</summary>
    [JsonPropertyName("arguments")]
    public VersionArguments? Arguments { get; set; }

    /// <summary>
    /// Forge 1.13 之前用的类路径字段（整条 classpath 以 ";" 分隔）。
    /// <para>Forge 的安装器写的是这个字段而不是 arguments.jvm：不认它就会退化成
    /// 「启动器自己拼的 classpath」，少了 Forge 补丁 jar，报「找不到主类」。</para>
    /// </summary>
    [JsonPropertyName("legacyClassPath")]
    public string? LegacyClassPath { get; set; }

    /// <summary>
    /// logging.client.argument（1.16 起 Mojang 用来指定 log4j 配置）。
    /// <para>不接的话游戏日志走默认配置，日志文件位置与格式都不是官方那套。</para>
    /// </summary>
    [JsonPropertyName("logging")]
    public JsonElement? Logging { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionRef? JavaVersion { get; set; }

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = [];

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("releaseTime")]
    public string? ReleaseTime { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class VersionArguments
{
    [JsonPropertyName("game")]
    public List<ArgumentItem> Game { get; set; } = [];

    [JsonPropertyName("jvm")]
    public List<ArgumentItem> Jvm { get; set; } = [];
}

/// <summary>参数项：纯字符串，或带规则的对象（value 可为数组）</summary>
[JsonConverter(typeof(ArgumentItemConverter))]
public sealed class ArgumentItem
{
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    [JsonPropertyName("rules")]
    public List<OsRule>? Rules { get; set; }

    [JsonIgnore]
    public bool IsObject => Value.ValueKind is JsonValueKind.String or JsonValueKind.Array;
}

/// <summary>版本 JSON 的参数是字符串/对象混排数组，字符串项转为 {value: 字符串}</summary>
public sealed class ArgumentItemConverter : JsonConverter<ArgumentItem>
{
    public override ArgumentItem? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new ArgumentItem { Value = JsonSerializer.SerializeToElement(reader.GetString()) };

        var item = new ArgumentItem();
        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("value", out var v))
                item.Value = v.Clone();
            if (el.TryGetProperty("rules", out var r))
                item.Rules = r.Deserialize<List<OsRule>>(options);
        }
        return item;
    }

    public override void Write(Utf8JsonWriter writer, ArgumentItem value, JsonSerializerOptions options)
    {
        if (value.Rules is null)
        {
            value.Value.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        value.Value.WriteTo(writer);
        writer.WritePropertyName("rules");
        JsonSerializer.Serialize(writer, value.Rules, options);
        writer.WriteEndObject();
    }
}

public sealed class OsRule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    [JsonPropertyName("os")]
    public OsSpec? Os { get; set; }

    [JsonPropertyName("features")]
    public Dictionary<string, JsonElement>? Features { get; set; }
}

public sealed class OsSpec
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public DownloadableFile? Client { get; set; }

    [JsonPropertyName("client_mappings")]
    public DownloadableFile? ClientMappings { get; set; }
}

public sealed class DownloadableFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")]
    public string Component { get; set; } = "java-runtime-generic";

    /// <summary>所需 Java 主版本；0 = 元数据未提供（由 JavaRequirement 按版本号推断）</summary>
    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

public sealed class Library
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("rules")]
    public List<OsRule>? Rules { get; set; }

    [JsonPropertyName("extract")]
    public ExtractSpec? Extract { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    // Fabric/Quilt profile 的库条目没有 downloads，只在顶层给 sha1/size（Maven 坐标 + 仓库根）
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadableFile? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadableFile>? Classifiers { get; set; }
}

public sealed class ExtractSpec
{
    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }
}

/// <summary>资源索引文件内容：{ "objects": { "name": { "hash", "size } } }</summary>
public sealed class AssetIndexFile
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>Fabric 加载器元数据（launcher-meta profile）</summary>
public sealed class FabricProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("inheritsFrom")]
    public string InheritsFrom { get; set; } = "";

    [JsonPropertyName("releaseTime")]
    public string? ReleaseTime { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("mainClass")]
    public string MainClass { get; set; } = "";

    [JsonPropertyName("arguments")]
    public VersionArguments? Arguments { get; set; }

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = [];

    [JsonPropertyName("minecraft")]
    public string? Minecraft { get; set; }
}
