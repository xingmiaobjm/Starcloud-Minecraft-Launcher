// 路径与设置持久化（数据根/starcloud-launcher；Windows 与历史版本共用 %APPDATA% 数据根）

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public static class LauncherPaths
{
    /// <summary>
    /// 数据根目录；设置 STARCLOUD_DATA_DIR 可重定向（测试隔离用）。
    /// 默认 Windows %APPDATA%、macOS ~/Library/Application Support、Linux XDG_DATA_HOME 或 ~/.local/share。
    /// </summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("STARCLOUD_DATA_DIR") is { Length: > 0 } redirected
            ? redirected
            : Path.Combine(PlatformInfo.DefaultAppDataRoot, "starcloud-launcher");

    public static string SettingsFile => Path.Combine(DataDir, "launcher.settings.json");

    public static string AccountsFile => Path.Combine(DataDir, "accounts.json");

    /// <summary>Starcloud ID 会话（含云 AI 助手 API Key）</summary>
    public static string StarcloudIdFile => Path.Combine(DataDir, "starcloud-id.json");

    /// <summary>正版登录的 refresh_token（DPAPI 加密、30 天有效；UI 不展示）</summary>
    public static string MicrosoftTokensFile => Path.Combine(DataDir, "microsoft.tokens.json");

    /// <summary>第三方（Yggdrasil / 皮肤站）登录的 access_token（DPAPI 加密；UI 不展示）</summary>
    public static string ExternalTokensFile => Path.Combine(DataDir, "external.tokens.json");

    /// <summary>Nova 动手许可层三档开关</summary>
    public static string NovaPermissionsFile => Path.Combine(DataDir, "nova.permissions.json");

    /// <summary>Nova 可审计的操作日志</summary>
    public static string NovaActionsFile => Path.Combine(DataDir, "nova.actions.json");

    /// <summary>Nova 时刻（高光卡片 + 下机战报 + 待处理候选池）</summary>
    public static string NovaMomentsFile => Path.Combine(DataDir, "nova.moments.json");

    /// <summary>Nova 拉人即玩（邀请令牌记录）</summary>
    public static string NovaInvitesFile => Path.Combine(DataDir, "nova.invites.json");

    public static string ManifestCacheFile => Path.Combine(DataDir, "version_manifest_cache.json");

    /// <summary>资源列表磁盘缓存目录（一 key 一文件）</summary>
    public static string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>社区资源图标磁盘缓存目录（一 URL 一图片文件）</summary>
    public static string IconCacheDir => Path.Combine(CacheDir, "icons");

    /// <summary>启动器统一资源库目录</summary>
    public static string LibraryDir => Path.Combine(DataDir, "library");

    public static string LibraryIndexFile => Path.Combine(LibraryDir, "library.json");

    public static string JavaDir => Path.Combine(DataDir, "java");

    // ── 皮肤库（多皮肤管理，skin-library） ──
    /// <summary>皮肤库目录：一皮肤一 PNG + library.json 索引</summary>
    public static string SkinsDir => Path.Combine(DataDir, "skins");

    public static string SkinLibraryFile => Path.Combine(SkinsDir, "library.json");

    // ── 插件系统（plugin-spec.md） ──
    /// <summary>插件根目录：packs 安装包 / unpacked 解压根 / logs 运行日志 / _runtime 宿主 SDK</summary>
    public static string PluginsDir => Path.Combine(DataDir, "plugins");

    public static string PluginPacksDir => Path.Combine(PluginsDir, "packs");

    public static string PluginUnpackedDir => Path.Combine(PluginsDir, "unpacked");

    public static string PluginLogsDir => Path.Combine(PluginsDir, "logs");

    /// <summary>Python 插件宿主 SDK（starcloud_host.py）所在目录</summary>
    public static string PluginRuntimeDir => Path.Combine(PluginsDir, "_runtime");

    /// <summary>插件启用状态与免责确认持久化</summary>
    public static string PluginStateFile => Path.Combine(PluginsDir, "plugin-state.json");

    // ── 联机（multiplayer） ──
    /// <summary>联机根目录：核心 / 运行目录 / 第三方 SDK 清单 / 下载缓存</summary>
    public static string MultiplayerDir => Path.Combine(DataDir, "multiplayer");

    /// <summary>第三方联机 SDK 清单目录（<c>*.json</c>，字段见 multiplayer-sdk-design.md）</summary>
    public static string MultiplayerSdkDir => Path.Combine(MultiplayerDir, "providers");

    /// <summary>某个提供方的核心安装目录（一提供方一子目录，互不覆盖）</summary>
    public static string MultiplayerCoreDir(string providerId) =>
        Path.Combine(MultiplayerDir, "core", providerId);

    /// <summary>某个提供方的运行目录（端口文件等会话期产物）</summary>
    public static string MultiplayerRunDir(string providerId) =>
        Path.Combine(MultiplayerDir, "run", providerId);

    /// <summary>联机核心平台包下载缓存目录（安装完即清理）</summary>
    public static string MultiplayerDownloadDir => Path.Combine(MultiplayerDir, "download");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>自更新工作目录（下载缓存 / staging / 更新脚本），启动时清理</summary>
    public static string UpdateDir => Path.Combine(DataDir, "update");

    /// <summary>自更新下载目录（setup.exe / portable.zip 成品缓存）</summary>
    public static string UpdateDownloadDir => Path.Combine(UpdateDir, "downloads");

    /// <summary>便携版更新 staging 目录（解压新版文件，等应用退出后 robocopy 覆盖）</summary>
    public static string UpdateStagingDir => Path.Combine(UpdateDir, "staging");

    /// <summary>启动器侧保存的游戏日志（每局一个文件）</summary>
    public static string GameLogsDir => Path.Combine(LogsDir, "game");

    /// <summary>.minecraft 下启动器侧状态目录（游戏实例注册 + 本局启动上下文）</summary>
    public static string GameStateDir(string dotMc) => Path.Combine(dotMc, "starcloud");

    /// <summary>单个游戏实例的注册文件（一实例一文件，pid 即实例键；跨进程共享同一实例目录也能互相看见）</summary>
    public static string GameSessionFile(string dotMc, int pid) =>
        Path.Combine(GameStateDir(dotMc), $"game-session-{pid}.json");

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(JavaDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(GameLogsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(IconCacheDir);
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(SkinsDir);
        Directory.CreateDirectory(MultiplayerDir);
        Directory.CreateDirectory(MultiplayerSdkDir);
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
    public string DotMinecraft { get; set; } = PlatformInfo.DefaultDotMinecraft;

    /// <summary>最近用过的 .minecraft 目录（版本页顶部下拉切换用；<see cref="DotMinecraft"/> 是当前生效的那个）</summary>
    [JsonPropertyName("known_dot_minecraft_dirs")]
    public List<string> KnownDotMinecraftDirs { get; set; } = [];

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

    /// <summary>下载源：auto（多源分片竞速）/ official / bmclapi / fastmirror / rainyun / custom</summary>
    [JsonPropertyName("download_source")]
    public string DownloadSource { get; set; } = "auto";

    /// <summary>自定义镜像根 URL（空 = 不启用；自动模式下配置即纳入竞速池）</summary>
    [JsonPropertyName("download_custom_mirror_url")]
    public string? DownloadCustomMirrorUrl { get; set; } = "";

    [JsonPropertyName("version_isolated")]
    public bool VersionIsolated { get; set; } = true;

    [JsonPropertyName("last_username")]
    public string? LastUsername { get; set; }

    [JsonPropertyName("last_version")]
    public string? LastVersion { get; set; }

    /// <summary>
    /// 局域网 P2P 加速是否接受「其他 Starcloud ID 用户」的设备（默认开）。
    /// 跨账号连接只开放按 SHA1 取游戏文件的通道，存档同步仍限同账号。
    /// </summary>
    [JsonPropertyName("device_p2p_cross_account")]
    public bool DeviceP2PCrossAccount { get; set; } = true;

    /// <summary>
    /// 正版登录令牌加密落盘（默认开）：开启后 access_token 与 refresh_token 一起用 DPAPI
    /// 加密保存，重启可直接复用；关闭时只保 refresh_token（仍加密）。
    /// </summary>
    [JsonPropertyName("microsoft_token_persist")]
    public bool MicrosoftTokenPersist { get; set; } = true;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    /// <summary>账户页右侧玩家形象是否播放走路动画（关闭 = 站立静止）。</summary>
    [JsonPropertyName("account_walk_animation")]
    public bool AccountWalkAnimation { get; set; } = true;

    // ── 主页两栏卡片的收起 / 展开状态 ──
    // 主页每次重新进入都是新实例（NavigationCacheMode=Disabled），状态必须落在设置里才不会还原；
    // 顺带也就跨重启保留了。false 即默认的展开态。
    /// <summary>主页左栏版本卡是否收起（收起后只剩一条窄条）</summary>
    [JsonPropertyName("home_versions_collapsed")]
    public bool HomeVersionsCollapsed { get; set; }

    /// <summary>主页右栏账户 / 启动卡是否收起</summary>
    [JsonPropertyName("home_account_collapsed")]
    public bool HomeAccountCollapsed { get; set; }

    /// <summary>
    /// 微软（正版）登录是否用内置登录窗口（默认开）。
    /// 关闭后回到「显示一次性验证码 + 打开系统浏览器」的方式。
    /// </summary>
    [JsonPropertyName("microsoft_login_embedded")]
    public bool MicrosoftLoginEmbedded { get; set; } = true;

    // ── 联机（multiplayer） ──
    /// <summary>当前联机提供方 id（terracotta = 内置陶瓦联机；其它来自第三方 SDK 清单）</summary>
    [JsonPropertyName("multiplayer_provider")]
    public string MultiplayerProvider { get; set; } = "terracotta";

    /// <summary>联机玩家名（空 = 跟随当前账户名）</summary>
    [JsonPropertyName("multiplayer_nickname")]
    public string MultiplayerNickname { get; set; } = "";

    /// <summary>公共节点地区偏好：auto / cn / global（同房间玩家建议接同一地区节点）</summary>
    [JsonPropertyName("multiplayer_node_region")]
    public string MultiplayerNodeRegion { get; set; } = "auto";

    /// <summary>
    /// CurseForge 官方 API Key（可空）。留空走社区只读代理（无需 key）；
    /// 填了就直连官方 api.curseforge.com，走官方授权路径。
    /// </summary>
    [JsonPropertyName("curseforge_api_key")]
    public string CurseForgeApiKey { get; set; } = "";

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

    /// <summary>
    /// 自接模型是否配好：地址 + 模型名齐全就算。
    /// Key 允许留空——本地推理服务（LM Studio / Ollama / llama.cpp）本来就不要 Key，
    /// 以前把「没 Key」等同于「没接入」，于是自接的本地模型被跳过、请求落到云端中转，
    /// 用户看到的却是云端的报错（400 / 401），完全对不上号。
    /// Nova 的通道判定与「云端额度要不要算」共用这一条，避免两处规则漂移。
    /// </summary>
    public NovaLlmProfile? ReadyLlmProfile()
    {
        var profile = ActiveLlmProfile();
        return profile is not null
            && !string.IsNullOrWhiteSpace(profile.BaseUrl)
            && !string.IsNullOrWhiteSpace(profile.Model)
            ? profile
            : null;
    }

    // ── Nova 云端中转（Starcloud ID 登录时后端下发的 API Key；每用户每周 100 万 token） ──
    // 默认开：登录后 Nova 开箱可用。自填模型 Key 仍然优先，见 NovaService 的降级链。
    [JsonPropertyName("nova_gateway_enabled")]
    public bool NovaGatewayEnabled { get; set; } = true;

    // 额度缓存：只用于「刚打开设置页时先显示上次的数值」，真值一律以服务端返回为准
    [JsonPropertyName("nova_quota_limit")]
    public long? NovaQuotaLimit { get; set; }

    [JsonPropertyName("nova_quota_used")]
    public long? NovaQuotaUsed { get; set; }

    [JsonPropertyName("nova_quota_reset_at")]
    public DateTimeOffset? NovaQuotaResetAt { get; set; }

    [JsonPropertyName("nova_quota_checked_at")]
    public DateTimeOffset? NovaQuotaCheckedAt { get; set; }

    /// <summary>写回服务端额度（下次开设置页先显示缓存，再异步刷新）。</summary>
    public void CacheQuota(NovaQuota q)
    {
        NovaQuotaLimit = q.Limit;
        NovaQuotaUsed = q.Used;
        NovaQuotaResetAt = q.ResetAt;
        NovaQuotaCheckedAt = DateTimeOffset.Now;
    }

    /// <summary>缓存里的额度快照；从没查过 → null。</summary>
    public NovaQuota? CachedQuota() =>
        NovaQuotaLimit is { } limit && NovaQuotaUsed is { } used
            ? new NovaQuota(limit, used, Math.Max(0, limit - used), NovaQuotaResetAt)
            : null;

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

    /// <summary>
    /// 安装 Mod 时自动下载缺失的必需前置（默认开）。
    /// 关闭后仍会在详情页展示前置资源，只是不自动装 —— 与 PCL 的「自动安装前置」同义。
    /// </summary>
    [JsonPropertyName("auto_install_mod_dependencies")]
    public bool AutoInstallModDependencies { get; set; } = true;

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
    /// <summary>
    /// 界面动效四档：minimal（极少，只有页面切换）/ lite（较少，+跟手光斑与毛玻璃）/
    /// balanced（均衡，+全局鼠标光照与引力形变）/ full（全部，+弹簧与环境光斑）。
    /// <para>旧值迁移：为空或非法时按旧字段 <see cref="UiAnimations"/> 推导（off → 极少，其余 → 均衡）。</para>
    /// </summary>
    [JsonPropertyName("ui_motion_level")]
    public string UiMotionLevel { get; set; } = "";

    // ── 跨设备同步与 P2P 加速（设计：2026-10-14-cross-device-sync-design.md） ──
    /// <summary>设备链路总开关（UDP 发现 + TCP 服务）：需登录 Starcloud ID 才会真正启动</summary>
    [JsonPropertyName("device_link_enabled")]
    public bool DeviceLinkEnabled { get; set; } = false;

    /// <summary>本机在对端展示的设备名（空 = 用机器名）</summary>
    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";

    /// <summary>已读过的云端公告 id（最多保留 50 条，避免设置文件无限增长）</summary>
    [JsonPropertyName("seen_announcement_ids")]
    public List<string> SeenAnnouncementIds { get; set; } = [];

    /// <summary>首次启动向导是否已完成（跳过也算完成，避免每次都弹）</summary>
    [JsonPropertyName("onboarding_done")]
    public bool OnboardingDone { get; set; } = false;

    /// <summary>下载 P2P 加速：优先从局域网同账号设备取已下载文件（只认 SHA1，校验不过回退公网）</summary>
    [JsonPropertyName("p2p_download_enabled")]
    public bool P2pDownloadEnabled { get; set; } = true;

    /// <summary>
    /// 高级传递优化（跨网络 P2P）：默认关。开启后会向发现服务上报公网端点与文件 SHA1 索引，
    /// 并可能向其他用户提供少量上行；服务器只做发现、不转发文件（设计：2026-10-22）。
    /// </summary>
    [JsonPropertyName("p2p_internet_enabled")]
    public bool P2pInternetEnabled { get; set; } = false;

    /// <summary>高级传递优化供文件限速（MB/s，1–32）</summary>
    [JsonPropertyName("p2p_internet_upload_mbps")]
    public int P2pInternetUploadMbps { get; set; } = 4;

    // ── SC 实验室 · 投影社区（2026-10-14） ──
    /// <summary>投影社区 GitHub API 基址（空 = 默认 gh-proxy 镜像优先、直连回退；可填自定义代理前缀）</summary>
    [JsonPropertyName("projection_github_api")]
    public string? ProjectionGithubApi { get; set; } = "";

    /// <summary>投影社区 GitHub Token（可选；匿名 API 每小时仅 60 次，填了 Token 是 5000/h，Release 探测更稳）</summary>
    [JsonPropertyName("projection_github_token")]
    public string? ProjectionGithubToken { get; set; } = "";

    /// <summary>投影社区自定义源（每行一个 JSON 接口 URL；返回数组，字段见 GenericJsonSource）</summary>
    [JsonPropertyName("projection_custom_sources")]
    public string? ProjectionCustomSources { get; set; } = "";

    /// <summary>
    /// 旧版界面动效开关（system / on / off），保留给插件 API（白名单键 ui_animations）兼容；
    /// 与 <see cref="UiMotionLevel"/> 同义同步：极少 = off，其余 = on。
    /// </summary>
    [JsonPropertyName("ui_animations")]
    public string UiAnimations { get; set; } = "system";

    // ── 启动器界面语言（设计：localization-design.md §3.5） ──
    // 注意与上方 Language（游戏语言，透传 --lang）无关；非法值由 Loc.Initialize 回落 zh-CN。
    /// <summary>启动器界面语言：zh-CN（默认）/ en-US</summary>
    [JsonPropertyName("ui_language")]
    public string UiLanguage { get; set; } = "zh-CN";

    // ── 自动更新（设计：auto-update-feature.md） ──
    /// <summary>启动几秒后后台检查新版本，发现新版弹窗确认后更新；游戏运行时避让</summary>
    [JsonPropertyName("auto_update_enabled")]
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>自定义更新清单地址（空 = 用 SelfUpdateService.DefaultManifestUrl 内置地址，便于测试指向本地服务器）</summary>
    [JsonPropertyName("update_manifest_url")]
    public string? UpdateManifestUrl { get; set; } = "";

    /// <summary>背景字段范围钳制（每次加载/保存前调用，避免脏数据导致 UI 越界）</summary>
    public void ClampBackgroundValues()
    {
        if (BackgroundMode is not ("none" or "builtin" or "file")) BackgroundMode = "none";
        BackgroundImageOpacity = Math.Clamp(BackgroundImageOpacity, 0.2, 1.0);
        BackgroundBlurRadius = Math.Clamp(BackgroundBlurRadius, 0, 64);
        if (BackgroundMaskOpacity is { } m) BackgroundMaskOpacity = Math.Clamp(m, 0, 1);
        // 动效四档：为空（老配置首次升级）或非法时按旧开关推导；随后把旧开关同步成同义值，
        // 插件读写的 ui_animations 与等级永远一致。
        if (UiMotionLevel is not ("minimal" or "lite" or "balanced" or "full"))
            UiMotionLevel = UiAnimations == "off" ? "minimal" : "balanced";
        UiAnimations = UiMotionLevel == "minimal" ? "off" : "on";
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
