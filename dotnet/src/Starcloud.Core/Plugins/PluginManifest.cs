// 插件清单（.mclplug 解压根 manifest.json）模型 + 解析 + 校验 + 官方签名验证
// 规范（docs/plugin-spec.md）：缺 manifest 直接拒绝加载；权限全部走白名单校验。

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Plugins;

public sealed class PluginManifest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("apiVersion")] public string ApiVersion { get; init; } = "";
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("permissions")] public string[] Permissions { get; init; } = [];
    [JsonPropertyName("optionalPermissions")] public string[] OptionalPermissions { get; init; } = [];

    [JsonPropertyName("compatibility")]
    public PluginCompatibility Compatibility { get; init; } = new();

    /// <summary>AI 模块标记：仅官方国内发布版可启用（规范 §6）</summary>
    [JsonPropertyName("requires_official_ai")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresOfficialAi { get; init; }

    /// <summary>可选官方签名：对 manifest.json 文件字节的 SHA256 做的 ECDSA P-256 签名（base64）</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    // ── 解析 ──

    public static PluginManifest? ParseFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ── 校验（返回错误列表；空 = 通过） ──

    public List<string> Validate(string launcherVersion)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
            errors.Add("manifest 缺少 id");
        else if (Id.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(
                     Id, @"^[a-zA-Z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$"))
            errors.Add($"id 非法（需反域名格式，如 com.example.myplugin）：{Id}");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("manifest 缺少 name");
        if (string.IsNullOrWhiteSpace(Version)) errors.Add("manifest 缺少 version");
        if (string.IsNullOrWhiteSpace(Author)) errors.Add("manifest 缺少 author");

        if (string.IsNullOrWhiteSpace(ApiVersion))
            errors.Add("manifest 缺少 apiVersion");
        else if (ApiVersion != PluginEnvironment.ApiVersion)
            errors.Add($"apiVersion 不兼容：插件 {ApiVersion}，启动器 {PluginEnvironment.ApiVersion}");

        if (!VersionAtLeast(launcherVersion, Compatibility.MinLauncherVersion))
            errors.Add($"需要启动器 {Compatibility.MinLauncherVersion} 及以上（当前 {launcherVersion}）");

        foreach (var p in Permissions.Concat(OptionalPermissions))
        {
            if (PluginPermissions.IsForbidden(p))
                errors.Add($"权限被禁止开放：{p}（插件无权接管账号身份校验）");
            else if (!PluginPermissions.IsAllowed(p))
                errors.Add($"未知权限（白名单制）：{p}");
        }
        if (Permissions.Length == 0 && OptionalPermissions.Length == 0)
        {
            // 允许零权限插件（纯面板信息类也至少要 ui_panel；零权限仅日志能力，合法）
        }

        if (RequiresOfficialAi && !PluginEnvironment.OfficialChinaBuild)
            errors.Add("该模块仅官方国内发布版可用");
        return errors;
    }

    /// <summary>语义版本段比较：actual ≥ required（数字段逐位比较，缺失段按 0）</summary>
    public static bool VersionAtLeast(string actual, string required)
    {
        if (string.IsNullOrWhiteSpace(required) || required == "0.0.0") return true;
        var a = ParseSegments(actual);
        var r = ParseSegments(required);
        for (var i = 0; i < Math.Max(a.Count, r.Count); i++)
        {
            var av = i < a.Count ? a[i] : 0;
            var rv = i < r.Count ? r[i] : 0;
            if (av != rv) return av > rv;
        }
        return true;
    }

    private static List<int> ParseSegments(string v) =>
        v.Split('-')[0].Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToList();

    /// <summary>
    /// 官方签名验证（规范 §5 可选项）：对 manifest.json 文件字节 SHA256 做 ECDSA P-256 验签。
    /// 未配置公钥或无签名字段 → false（按「第三方未验证」处理，不阻断加载）。
    /// </summary>
    public bool VerifyOfficialSignature(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(Signature)) return false;
        var publicKey = PluginEnvironment.OfficialPublicKey;
        if (string.IsNullOrWhiteSpace(publicKey)) return false;
        try
        {
            var hash = SHA256.HashData(File.ReadAllBytes(manifestPath));
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.VerifyHash(hash, Convert.FromBase64String(Signature));
        }
        catch
        {
            return false;
        }
    }
}

public sealed class PluginCompatibility
{
    [JsonPropertyName("minLauncherVersion")]
    public string MinLauncherVersion { get; init; } = "0.0.0";
}
