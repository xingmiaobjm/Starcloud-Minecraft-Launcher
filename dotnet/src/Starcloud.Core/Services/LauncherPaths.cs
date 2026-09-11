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

    /// <summary>Nova 时刻（高光卡片 + 下机战报 + 待处理候选池）</summary>
    public static string NovaMomentsFile => Path.Combine(DataDir, "nova.moments.json");

    /// <summary>Nova 拉人即玩（邀请令牌记录）</summary>
    public static string NovaInvitesFile => Path.Combine(DataDir, "nova.invites.json");

    public static string ManifestCacheFile => Path.Combine(DataDir, "version_manifest_cache.json");

    /// <summary>资源列表磁盘缓存目录（一 key 一文件）</summary>
    public static string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>启动器统一资源库目录</summary>
    public static string LibraryDir => Path.Combine(DataDir, "library");

    public static string LibraryIndexFile => Path.Combine(LibraryDir, "library.json");

    public static string JavaDir => Path.Combine(DataDir, "java");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>启动器侧保存的游戏日志（每局一个文件）</summary>
    public static string GameLogsDir => Path.Combine(LogsDir, "game");

    /// <summary>跨进程游戏会话锁文件（随 .minecraft，共享同一实例目录的多窗口互斥）</summary>
    public static string GameSessionFile(string dotMc) =>
        Path.Combine(dotMc, "starcloud", "game-session.json");

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(JavaDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(GameLogsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LibraryDir);
    }
}

/// <summary>Nova 第三方 LLM 模型档案（OpenAI 兼容，支持多模型随时切换）</summary>
public sealed class NovaLlmProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";
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
    // 旧版单模型三字段仅保留用于迁移读取；新设置用 nova_llm_profiles 多模型档案。
    [JsonPropertyName("nova_llm_base_url")]
    public string? NovaLlmBaseUrl { get; set; } = "";

    [JsonPropertyName("nova_llm_api_key")]
    public string? NovaLlmApiKey { get; set; } = "";

    [JsonPropertyName("nova_llm_model")]
    public string? NovaLlmModel { get; set; } = "";

    // ── Nova 多模型档案（第三方 LLM 添加；ActiveId 为空时取第一个） ──
    [JsonPropertyName("nova_llm_profiles")]
    public List<NovaLlmProfile> NovaLlmProfiles { get; set; } = [];

    [JsonPropertyName("nova_llm_active_id")]
    public string? NovaLlmActiveId { get; set; }

    /// <summary>当前生效的自建模型档案（未选中 → 第一个；空 → null，静默降级到云端）</summary>
    public NovaLlmProfile? ActiveLlmProfile() =>
        NovaLlmProfiles.FirstOrDefault(p => p.Id == NovaLlmActiveId)
        ?? NovaLlmProfiles.FirstOrDefault();

    // ── Nova 游戏内浮窗（设计：nova-overlay-design.md §3.3；默认关 = 开启即显式授权） ──
    [JsonPropertyName("nova_overlay_enabled")]
    public bool NovaOverlayEnabled { get; set; } = false;

    [JsonPropertyName("nova_overlay_x")]
    public int? NovaOverlayX { get; set; }

    [JsonPropertyName("nova_overlay_y")]
    public int? NovaOverlayY { get; set; }

    // ── 资源列表缓存开关（设计：resource-cache-multi-java-global-settings-design.md §3.1） ──
    [JsonPropertyName("resource_cache_enabled")]
    public bool ResourceCacheEnabled { get; set; } = true;

    // ── 全局默认窗口（版本设置「跟随全局设置」档位取此处） ──
    [JsonPropertyName("default_window_width")]
    public int? DefaultWindowWidth { get; set; } = 854;

    [JsonPropertyName("default_window_height")]
    public int? DefaultWindowHeight { get; set; } = 480;

    [JsonPropertyName("default_fullscreen")]
    public bool DefaultFullscreen { get; set; } = false;

    // ── 全局环境变量（版本级 EnvVars 之外的公共环境变量） ──
    [JsonPropertyName("global_env_vars")]
    public Dictionary<string, string> GlobalEnvVars { get; set; } = new();

    // ── 启动可靠性（设计：launch-reliability-design.md §9） ──
    /// <summary>启动即崩时自动重试一次（默认开）</summary>
    [JsonPropertyName("launch_auto_retry")]
    public bool LaunchAutoRetry { get; set; } = true;

    /// <summary>启动覆盖层默认展开实时日志（默认关，克制）</summary>
    [JsonPropertyName("show_game_log_on_launch")]
    public bool ShowGameLogOnLaunch { get; set; } = false;

    // ── 自定义背景与外观（设计：custom-background-theme-design.md §4.1） ──
    /// <summary>背景来源：none（仅毛玻璃）/ builtin（内置壁纸）/ file（本地图片）</summary>
    [JsonPropertyName("background_mode")]
    public string BackgroundMode { get; set; } = "none";

    /// <summary>内置壁纸 id（background_mode = builtin 时生效）</summary>
    [JsonPropertyName("background_builtin_id")]
    public string? BackgroundBuiltinId { get; set; }

    /// <summary>本地背景图绝对路径（background_mode = file 时生效）</summary>
    [JsonPropertyName("background_image_path")]
    public string? BackgroundImagePath { get; set; }

    /// <summary>背景图不透明度（0.2–1.0）</summary>
    [JsonPropertyName("background_image_opacity")]
    public double BackgroundImageOpacity { get; set; } = 0.7;

    /// <summary>背景图模糊半径（0–64 px）</summary>
    [JsonPropertyName("background_blur_radius")]
    public double BackgroundBlurRadius { get; set; } = 24;

    /// <summary>毛玻璃遮罩不透明度（0–1）；null 或 follow_theme = true 时跟随主题默认值</summary>
    [JsonPropertyName("background_mask_opacity")]
    public double? BackgroundMaskOpacity { get; set; }

    /// <summary>毛玻璃遮罩强度是否跟随主题（深 0.45 / 浅 0.25）</summary>
    [JsonPropertyName("background_follow_theme")]
    public bool BackgroundFollowTheme { get; set; } = true;

    // ── 界面动效（设计：ui-polish-motion-design.md §4） ──
    /// <summary>界面动效：system（跟随系统）/ on（始终开启）/ off（关闭）</summary>
    [JsonPropertyName("ui_animations")]
    public string UiAnimations { get; set; } = "system";

    // ── 启动器界面语言（设计：localization-design.md §3.5） ──
    // 注意与上方 Language（游戏语言，透传 --lang）无关；非法值由 Loc.Initialize 回落 zh-CN。
    /// <summary>启动器界面语言：zh-CN（默认）/ en-US</summary>
    [JsonPropertyName("ui_language")]
    public string UiLanguage { get; set; } = "zh-CN";

    /// <summary>背景字段范围钳制（每次加载/保存前调用，避免脏数据导致 UI 越界）</summary>
    public void ClampBackgroundValues()
    {
        if (BackgroundMode is not ("none" or "builtin" or "file")) BackgroundMode = "none";
        BackgroundImageOpacity = Math.Clamp(BackgroundImageOpacity, 0.2, 1.0);
        BackgroundBlurRadius = Math.Clamp(BackgroundBlurRadius, 0, 64);
        if (BackgroundMaskOpacity is { } m) BackgroundMaskOpacity = Math.Clamp(m, 0, 1);
        if (UiAnimations is not ("system" or "on" or "off")) UiAnimations = "system";
    }

    /// <summary>本地背景图实际生效路径；未配置 / 文件不存在 → null（builtin 由 App 侧解析）。</summary>
    public string? ResolvedBackgroundPath()
    {
        if (BackgroundMode != "file") return null;
        var p = BackgroundImagePath;
        return !string.IsNullOrWhiteSpace(p) && File.Exists(p) ? p : null;
    }

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
            var s = JsonSerializer.Deserialize<LauncherSettings>(
                File.ReadAllText(LauncherPaths.SettingsFile), Options) ?? new LauncherSettings();
            s.MigrateLegacyLlm();
            s.ClampBackgroundValues();
            return s;
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    /// <summary>旧版单模型三字段 → 多模型档案（幂等：仅档案列表为空且旧字段齐全时迁移）</summary>
    private void MigrateLegacyLlm()
    {
        if (NovaLlmProfiles.Count > 0) return;
        var baseUrl = NovaLlmBaseUrl?.Trim();
        var apiKey = NovaLlmApiKey?.Trim();
        var model = NovaLlmModel?.Trim();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model)) return;
        var p = new NovaLlmProfile { Name = "默认模型", BaseUrl = baseUrl, ApiKey = apiKey, Model = model };
        NovaLlmProfiles.Add(p);
        NovaLlmActiveId = p.Id;
    }

    public void Save()
    {
        LauncherPaths.EnsureDataDir();
        ClampBackgroundValues();
        File.WriteAllText(LauncherPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
    }
}
