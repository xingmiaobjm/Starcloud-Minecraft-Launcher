// 路径与设置持久化（%APPDATA%/starcloud-launcher，与历史版本共用数据根）

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public static class LauncherPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "starcloud-launcher");

    public static string SettingsFile => Path.Combine(DataDir, "launcher.settings.json");

    public static string AccountsFile => Path.Combine(DataDir, "accounts.json");

    /// <summary>Starcloud ID 会话（含云 AI 助手 API Key）</summary>
    public static string StarcloudIdFile => Path.Combine(DataDir, "starcloud-id.json");

    /// <summary>Nova 动手许可层三档开关</summary>
    public static string NovaPermissionsFile => Path.Combine(DataDir, "nova.permissions.json");

    /// <summary>Nova 动手可审计流水</summary>
    public static string NovaActionsFile => Path.Combine(DataDir, "nova.actions.json");

    public static string ManifestCacheFile => Path.Combine(DataDir, "version_manifest_cache.json");

    public static string JavaDir => Path.Combine(DataDir, "java");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(JavaDir);
        Directory.CreateDirectory(LogsDir);
    }
}

public sealed class LauncherSettings
{
    [JsonPropertyName("dot_minecraft")]
    public string DotMinecraft { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh_CN";

    [JsonPropertyName("default_min_memory_mb")]
    public int DefaultMinMemoryMb { get; set; } = 512;

    [JsonPropertyName("default_max_memory_mb")]
    public int DefaultMaxMemoryMb { get; set; } = 4096;

    [JsonPropertyName("java_executable")]
    public string? JavaExecutable { get; set; }

    [JsonPropertyName("extra_jvm_args")]
    public List<string> ExtraJvmArgs { get; set; } = [];

    [JsonPropertyName("use_builtin_jvm_args")]
    public bool UseBuiltinJvmArgs { get; set; } = true;

    [JsonPropertyName("download_concurrency")]
    public int DownloadConcurrency { get; set; } = 16;

    [JsonPropertyName("download_max_retries")]
    public int DownloadMaxRetries { get; set; } = 3;

    [JsonPropertyName("version_isolated")]
    public bool VersionIsolated { get; set; } = true;

    [JsonPropertyName("last_username")]
    public string? LastUsername { get; set; }

    [JsonPropertyName("last_version")]
    public string? LastVersion { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    // ── Nova LLM 降级链第 1 级：用户自填 Key（OpenAI 兼容协议，零成本） ──
    [JsonPropertyName("nova_llm_base_url")]
    public string? NovaLlmBaseUrl { get; set; } = "";

    [JsonPropertyName("nova_llm_api_key")]
    public string? NovaLlmApiKey { get; set; } = "";

    [JsonPropertyName("nova_llm_model")]
    public string? NovaLlmModel { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static LauncherSettings Load()
    {
        LauncherPaths.EnsureDataDir();
        if (!File.Exists(LauncherPaths.SettingsFile)) return new LauncherSettings();
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(
                File.ReadAllText(LauncherPaths.SettingsFile), Options) ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save()
    {
        LauncherPaths.EnsureDataDir();
        File.WriteAllText(LauncherPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
    }
}
