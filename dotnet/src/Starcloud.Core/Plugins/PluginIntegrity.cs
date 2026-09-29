// 插件 2.0 整包完整性：逐文件 sha256 + package 摘要 + ECDSA 签名三态（规范 2.0 §8）
// 与 1.x 只签 manifest.json 字节不同，这里签名覆盖整包摘要：
// 任何一个文件被替换都会改变 package 摘要，签名随即失效。

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Plugins;

/// <summary>插件包的信任状态（规范 2.0 §8.2）</summary>
public enum PluginTrustState
{
    /// <summary>未签名 / 未知密钥：按「第三方未验证」处理</summary>
    Unsigned,

    /// <summary>官方签名校验通过</summary>
    Verified,

    /// <summary>签名与内容不匹配：拒绝加载</summary>
    Invalid,
}

/// <summary>整包签名（integrity.json 的 signature 字段）</summary>
public sealed class PluginSignature
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = "ecdsa-p256-sha256";
    [JsonPropertyName("keyId")] public string KeyId { get; init; } = "";
    [JsonPropertyName("value")] public string Value { get; init; } = "";
}

/// <summary>integrity.json：包内逐文件摘要 + 整包摘要 + 可选签名</summary>
public sealed class PluginIntegrity
{
    /// <summary>包内固定文件名</summary>
    public const string FileName = "integrity.json";

    /// <summary>2.0 摘要文件 schema</summary>
    public const string SchemaId = "starcloud-integrity/2";

    /// <summary>唯一支持的摘要算法</summary>
    public const string AlgorithmSha256 = "sha256";

    [JsonPropertyName("schema")] public string Schema { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = AlgorithmSha256;

    /// <summary>包内相对路径（'/' 分隔）→ 小写十六进制 sha256</summary>
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);

    /// <summary>把 files 按路径升序拼接后再次摘要，供签名与快速比对</summary>
    [JsonPropertyName("package")] public string Package { get; init; } = "";

    [JsonPropertyName("signature")] public PluginSignature? Signature { get; init; }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginIntegrity? ParseFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PluginIntegrity>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>计算目录的逐文件摘要与整包摘要（不含 integrity.json 自身与运行时数据目录 data/）</summary>
    public static PluginIntegrity Compute(string dir, string id, string version)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
                if (IsExcluded(relative)) continue;
                files[relative] = HashFile(path);
            }
        }
        return new PluginIntegrity
        {
            Schema = SchemaId,
            Id = id,
            Version = version,
            Algorithm = AlgorithmSha256,
            Files = files,
            Package = ComputePackage(files),
        };
    }

    /// <summary>逐文件比对：返回差异列表（空 = 一致）</summary>
    public List<string> VerifyFiles(string dir)
    {
        var errors = new List<string>();
        if (!string.Equals(Schema, SchemaId, StringComparison.Ordinal))
            errors.Add($"integrity schema 不支持：{Schema}（应为 {SchemaId}）");
        if (!string.Equals(Algorithm, AlgorithmSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add($"integrity 摘要算法不支持：{Algorithm}");

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
                if (IsExcluded(relative)) continue;
                actual[relative] = HashFile(path);
            }
        }

        foreach (var (relative, expected) in Files)
        {
            if (!actual.TryGetValue(relative, out var got)) errors.Add($"包内缺少文件：{relative}");
            else if (!string.Equals(got, expected, StringComparison.OrdinalIgnoreCase))
                errors.Add($"文件摘要不匹配：{relative}");
        }
        foreach (var relative in actual.Keys)
            if (!Files.ContainsKey(relative)) errors.Add($"包内多出未登记文件：{relative}");

        if (!string.Equals(Package, ComputePackage(Files), StringComparison.OrdinalIgnoreCase))
            errors.Add("package 摘要与 files 不一致");
        return errors;
    }

    /// <summary>整包摘要：files 按路径升序拼接 "path\0hex\n" 后再 sha256（小写十六进制）</summary>
    public static string ComputePackage(IReadOnlyDictionary<string, string> files)
    {
        var joined = new StringBuilder();
        foreach (var key in files.Keys.OrderBy(k => k, StringComparer.Ordinal))
            joined.Append(key).Append('\0').Append(files[key].ToLowerInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString())))
            .ToLowerInvariant();
    }

    /// <summary>签名载荷：规范化 JSON（键升序、无空白）</summary>
    public static string BuildSignaturePayload(string id, string version, string package)
    {
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["package"] = package,
            ["version"] = version,
        };
        return JsonSerializer.Serialize(canonical);
    }

    /// <summary>签名载荷字节（UTF-8）</summary>
    public byte[] SignaturePayloadBytes() =>
        Encoding.UTF8.GetBytes(BuildSignaturePayload(Id, Version, Package));

    /// <summary>
    /// 校验签名三态。publicKeys 为 null 时用本体内置公钥表（keyId → SPKI base64）。
    /// 未知 keyId 按「未签名」处理并提示升级启动器，不误判为无效。
    /// </summary>
    public PluginTrustState Verify(out string detail,
        IReadOnlyDictionary<string, string>? publicKeys = null)
    {
        detail = "未签名";
        if (Signature is null || string.IsNullOrWhiteSpace(Signature.Value)) return PluginTrustState.Unsigned;

        var keys = publicKeys ?? PluginEnvironment.OfficialPublicKeys;
        if (string.IsNullOrWhiteSpace(Signature.KeyId) || !keys.TryGetValue(Signature.KeyId, out var key))
        {
            detail = $"未知签名密钥 {Signature.KeyId}（请升级启动器）";
            return PluginTrustState.Unsigned;
        }
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            var ok = ecdsa.VerifyData(SignaturePayloadBytes(),
                Convert.FromBase64String(Signature.Value), HashAlgorithmName.SHA256);
            detail = ok ? $"官方签名（{Signature.KeyId}）" : "签名与内容不匹配";
            return ok ? PluginTrustState.Verified : PluginTrustState.Invalid;
        }
        catch (Exception ex)
        {
            detail = $"签名校验失败：{ex.Message}";
            return PluginTrustState.Invalid;
        }
    }

    // ── 内部 ──

    /// <summary>integrity.json 自身不入摘要；data/ 为运行时私有数据，不属于包内容</summary>
    private static bool IsExcluded(string relative) =>
        relative.Equals(FileName, StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
