// 插件清单（.mclplug 解压根 manifest.json）模型 + 解析 + 校验 + 官方签名验证
// 规范（docs/plugin-spec.md 1.x / docs/plugin-spec-v2.md 2.0）：缺 manifest 直接拒绝加载；
// 权限、依赖、扩展点全部走白名单校验；校验按 manifest.apiVersion 分派。

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

    /// <summary>
    /// 1.x 可选官方签名：对 manifest.json 文件字节的 SHA256 做的 ECDSA P-256 签名（base64）。
    /// 2.0 已移到 integrity.json（整包签名），此字段在 2.0 插件中不再使用。
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    // ── 2.0 新增元数据（规范 2.0 §3；1.x 插件留空即可） ──

    /// <summary>语言标签（BCP-47）→ 展示名/描述覆盖</summary>
    [JsonPropertyName("i18n")]
    public Dictionary<string, PluginLocalization>? I18n { get; init; }

    [JsonPropertyName("license")] public string? License { get; init; }
    [JsonPropertyName("homepage")] public string? Homepage { get; init; }
    [JsonPropertyName("repository")] public string? Repository { get; init; }
    [JsonPropertyName("keywords")] public string[] Keywords { get; init; } = [];

    /// <summary>兼容的启动器版本区间（取代 1.x 的 compatibility.minLauncherVersion）</summary>
    [JsonPropertyName("engines")] public PluginEngines? Engines { get; init; }

    /// <summary>入口运行时：js / wasm / py；缺省按入口文件推断</summary>
    [JsonPropertyName("runtime")] public string? Runtime { get; init; }

    /// <summary>插件间依赖：插件 id → 版本区间</summary>
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; init; } = [];

    /// <summary>插件间冲突：插件 id → 版本区间（命中即停用后加载者）</summary>
    [JsonPropertyName("conflicts")]
    public Dictionary<string, string> Conflicts { get; init; } = [];

    /// <summary>声明式 UI 与扩展点</summary>
    [JsonPropertyName("contributes")] public PluginContributes? Contributes { get; init; }

    /// <summary>插件更新清单地址（本体只查询与提示）</summary>
    [JsonPropertyName("updates")] public PluginUpdateInfo? Updates { get; init; }

    /// <summary>是否 2.0 规范（决定权限语法与校验分支）</summary>
    [JsonIgnore]
    public bool IsApi2 => string.Equals(ApiVersion, PluginEnvironment.ApiVersion2, StringComparison.Ordinal);

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
        else if (!IsValidPluginId(Id))
            errors.Add($"id 非法（需反域名格式，如 com.example.myplugin）：{Id}");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("manifest 缺少 name");
        if (string.IsNullOrWhiteSpace(Version)) errors.Add("manifest 缺少 version");
        if (string.IsNullOrWhiteSpace(Author)) errors.Add("manifest 缺少 author");

        var api2 = IsApi2;
        if (string.IsNullOrWhiteSpace(ApiVersion))
        {
            errors.Add("manifest 缺少 apiVersion");
            api2 = false;
        }
        else if (!PluginEnvironment.IsSupportedApi(ApiVersion))
        {
            errors.Add($"apiVersion 不兼容：插件 {ApiVersion}，启动器支持 " +
                       $"{string.Join(" / ", PluginEnvironment.SupportedApiVersions)}");
            api2 = false;
        }

        if (api2) ValidateApi2(launcherVersion, errors);
        else ValidateApi1(launcherVersion, errors);

        if (RequiresOfficialAi && !PluginEnvironment.OfficialChinaBuild)
            errors.Add("该模块仅官方国内发布版可用");
        return errors;
    }

    /// <summary>1.x 校验：粗粒度权限白名单 + minLauncherVersion 下限</summary>
    private void ValidateApi1(string launcherVersion, List<string> errors)
    {
        if (!VersionAtLeast(launcherVersion, Compatibility.MinLauncherVersion))
            errors.Add($"需要启动器 {Compatibility.MinLauncherVersion} 及以上（当前 {launcherVersion}）");

        foreach (var p in Permissions.Concat(OptionalPermissions))
        {
            if (PluginPermissions.IsForbidden(p))
                errors.Add($"权限被禁止开放：{p}（插件无权接管账号身份校验）");
            else if (!PluginPermissions.IsAllowed(p))
                errors.Add($"未知权限（白名单制）：{p}");
        }
    }

    /// <summary>2.0 校验：严格语义化版本 + engines 区间 + 细粒度能力 + 依赖 / 扩展点</summary>
    private void ValidateApi2(string launcherVersion, List<string> errors)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                Version, @"^\d+\.\d+\.\d+([-+][0-9A-Za-z.\-]+)?$"))
            errors.Add($"2.0 插件 version 必须是严格语义化版本（主.次.修订）：{Version}");

        // 兼容性：engines.starcloud 为区间；缺省时接受 1.x 的 minLauncherVersion 作等价下限
        var rangeText = Engines?.Starcloud;
        if (!string.IsNullOrWhiteSpace(rangeText))
        {
            if (!PluginVersionRange.TryParse(rangeText, out var range, out var rangeError))
                errors.Add($"engines.starcloud 区间非法：{rangeError}");
            else if (!range!.Matches(launcherVersion))
                errors.Add($"需要启动器版本 {rangeText}（当前 {launcherVersion}）");
        }
        else if (!VersionAtLeast(launcherVersion, Compatibility.MinLauncherVersion))
        {
            errors.Add($"需要启动器 {Compatibility.MinLauncherVersion} 及以上（当前 {launcherVersion}）");
        }

        if (!string.IsNullOrWhiteSpace(Runtime) && Runtime is not ("js" or "wasm" or "py"))
            errors.Add($"未知 runtime：{Runtime}（可选 js / wasm / py）");

        if (!PluginPermissions.TryParseAll(Permissions.Concat(OptionalPermissions),
                out _, out var permissionErrors))
            errors.AddRange(permissionErrors);

        ValidateDependencyMap(Dependencies, "dependencies", errors);
        ValidateDependencyMap(Conflicts, "conflicts", errors);
        ValidateContributes(errors);
        ValidateUrl(Updates?.ManifestUrl, "updates.manifestUrl", errors);
    }

    private static void ValidateDependencyMap(
        Dictionary<string, string> map, string field, List<string> errors)
    {
        foreach (var (id, range) in map)
        {
            if (!IsValidPluginId(id)) errors.Add($"{field} 的插件 id 非法：{id}");
            if (!PluginVersionRange.TryParse(range, out _, out var rangeError))
                errors.Add($"{field}[{id}] 版本区间非法：{rangeError}");
        }
    }

    private void ValidateContributes(List<string> errors)
    {
        var c = Contributes;
        if (c is null) return;

        foreach (var panel in c.Panels)
        {
            if (string.IsNullOrWhiteSpace(panel.Id)) errors.Add("contributes.panels 缺少 id");
            ValidateViewPath(panel.View, "contributes.panels.view", errors);
        }
        foreach (var slot in c.Slots)
        {
            if (!PluginExtensionPoints.SlotPoints.Contains(slot.Point))
                errors.Add($"未知挂载点：{slot.Point}（可用：{string.Join(" / ", PluginExtensionPoints.SlotPoints)}）");
            ValidateViewPath(slot.View, "contributes.slots.view", errors);
        }
        foreach (var section in c.VersionSections)
        {
            if (string.IsNullOrWhiteSpace(section.Id)) errors.Add("contributes.versionSections 缺少 id");
            ValidateViewPath(section.View, "contributes.versionSections.view", errors);
        }
        foreach (var card in c.HomeCards)
        {
            if (string.IsNullOrWhiteSpace(card.Id)) errors.Add("contributes.homeCards 缺少 id");
            ValidateViewPath(card.View, "contributes.homeCards.view", errors);
        }
        if (c.NavItems.Length > 1)
            errors.Add($"contributes.navItems 最多 1 条（当前 {c.NavItems.Length} 条）—— 一级导航位置每个插件只能占一个");
        foreach (var nav in c.NavItems)
        {
            if (string.IsNullOrWhiteSpace(nav.Id)) errors.Add("contributes.navItems 缺少 id");
            if (string.IsNullOrWhiteSpace(nav.Title)) errors.Add("contributes.navItems 缺少 title");
            // icon 只校验非空：白名单命中与否在装配导航栏时判定并回落默认图标（设计 §4 fail-soft）
            if (string.IsNullOrWhiteSpace(nav.Icon)) errors.Add("contributes.navItems 缺少 icon");
            ValidateViewPath(nav.View, "contributes.navItems.view", errors);
        }
        foreach (var item in c.ContextMenu)
        {
            if (!PluginExtensionPoints.MenuPoints.Contains(item.Point))
                errors.Add($"未知菜单挂载点：{item.Point}（可用：{string.Join(" / ", PluginExtensionPoints.MenuPoints)}）");
            if (string.IsNullOrWhiteSpace(item.Action)) errors.Add("contributes.contextMenu 缺少 action");
        }
        if (c.Settings is not null)
            ValidateViewPath(c.Settings.Schema, "contributes.settings.schema", errors);
    }

    private static void ValidateViewPath(string? path, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path)) { errors.Add($"{field} 不能为空"); return; }
        if (!IsSafeRelativePath(path)) errors.Add($"{field} 必须是包内相对路径：{path}");
    }

    private static void ValidateUrl(string? url, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            errors.Add($"{field} 必须是 http(s) 绝对地址：{url}");
    }

    /// <summary>插件 id：反域名格式，≤64 字符</summary>
    public static bool IsValidPluginId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 &&
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$");

    /// <summary>包内相对路径：拒绝绝对路径、盘符与 .. 穿越</summary>
    public static bool IsSafeRelativePath(string path)
    {
        var norm = path.Replace('\\', '/');
        if (Path.IsPathRooted(norm) || norm.Contains(':')) return false;
        return !norm.Split('/').Contains("..");
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

/// <summary>2.0 本地化覆盖（规范 2.0 §3）</summary>
public sealed class PluginLocalization
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

/// <summary>2.0 兼容引擎区间</summary>
public sealed class PluginEngines
{
    [JsonPropertyName("starcloud")] public string? Starcloud { get; init; }
}

/// <summary>2.0 声明式扩展点（规范 2.0 §6.2 / §7）</summary>
public sealed class PluginContributes
{
    [JsonPropertyName("panels")] public PluginViewRef[] Panels { get; init; } = [];
    [JsonPropertyName("slots")] public PluginSlotRef[] Slots { get; init; } = [];
    [JsonPropertyName("versionSections")] public PluginViewRef[] VersionSections { get; init; } = [];
    [JsonPropertyName("homeCards")] public PluginViewRef[] HomeCards { get; init; } = [];
    [JsonPropertyName("navItems")] public PluginNavRef[] NavItems { get; init; } = [];
    [JsonPropertyName("contextMenu")] public PluginMenuRef[] ContextMenu { get; init; } = [];
    [JsonPropertyName("settings")] public PluginSettingsRef? Settings { get; init; }
}

/// <summary>指向包内声明式视图文件的扩展点条目</summary>
public sealed class PluginViewRef
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = "";
}

/// <summary>一级导航项（规范 2.0 §6.4）：icon 是宿主内置图标名，不是路径或字形</summary>
public sealed class PluginNavRef
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("icon")] public string Icon { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = "";
}

/// <summary>页面挂载区条目</summary>
public sealed class PluginSlotRef
{
    [JsonPropertyName("point")] public string Point { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = "";
    [JsonPropertyName("height")] public int Height { get; init; }
}

/// <summary>右键菜单条目（action → 插件导出的处理器名）</summary>
public sealed class PluginMenuRef
{
    [JsonPropertyName("point")] public string Point { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("action")] public string Action { get; init; } = "";
}

/// <summary>插件设置区（由 schema 生成表单，值进 ctx.storage 的 setting.* 前缀）</summary>
public sealed class PluginSettingsRef
{
    [JsonPropertyName("schema")] public string Schema { get; init; } = "";
}

/// <summary>2.0 更新清单地址</summary>
public sealed class PluginUpdateInfo
{
    [JsonPropertyName("manifestUrl")] public string? ManifestUrl { get; init; }
}

/// <summary>
/// 2.0 扩展点目录（规范 2.0 §6.2）：宿主与插件共用的挂载点白名单，
/// 声明式视图渲染与清单校验都以此为准。
/// </summary>
public static class PluginExtensionPoints
{
    /// <summary>页面挂载区挂载点</summary>
    public static readonly string[] SlotPoints =
        ["download.top", "home.top", "versions.top", "version.detail.top"];

    /// <summary>右键菜单挂载点</summary>
    public static readonly string[] MenuPoints = ["version.row", "save.row", "content.row"];

    /// <summary>插件导航项的 tag 前缀：plugin:&lt;插件 id&gt;:&lt;导航 id&gt;（与内置 tag 不可能撞）</summary>
    public const string NavTagPrefix = "plugin:";

    /// <summary>拼一个插件导航项的 tag</summary>
    public static string PluginNavTag(string pluginId, string navId) =>
        $"{NavTagPrefix}{pluginId}:{navId}";

    /// <summary>解一个 tag；不是插件 tag 或两段为空都返回 false</summary>
    public static bool TryParseNavTag(string? tag, out string pluginId, out string navId)
    {
        pluginId = ""; navId = "";
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith(NavTagPrefix, StringComparison.Ordinal)) return false;
        var rest = tag[NavTagPrefix.Length..];
        var at = rest.IndexOf(':');
        if (at <= 0 || at == rest.Length - 1) return false;
        pluginId = rest[..at];
        navId = rest[(at + 1)..];
        return pluginId.Length > 0 && navId.Length > 0;
    }

    /// <summary>插件导航项的图标白名单：名字 → 字形。字形复用导航栏里已在用的那批 HarmonyOS 图标。</summary>
    public static readonly IReadOnlyDictionary<string, string> NavIcons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["home"] = "\uEC77",       // 首页 / 概览
            ["instance"] = "\uEA94",   // 版本 / 实例
            ["download"] = "\uE90C",   // 下载 / 资源
            ["saves"] = "\uEA90",      // 存档 / 世界
            ["server"] = "\uEB91",     // 联机 / 服务器
            ["account"] = "\uEB8F",    // 账户 / 身份
            ["extension"] = "\uEABC",  // 扩展 / 插件（默认）
            ["log"] = "\uEC64",        // 日志 / 记录
        };

    /// <summary>默认图标名（未声明或未命中白名单时用它）</summary>
    public const string DefaultNavIcon = "extension";

    /// <summary>解析图标名；未命中白名单返回 false 并给出默认字形（调用方据此记一行日志）</summary>
    public static bool TryResolveNavIcon(string? name, out string glyph)
    {
        if (name is not null && NavIcons.TryGetValue(name, out var hit)) { glyph = hit; return true; }
        glyph = NavIcons[DefaultNavIcon];
        return false;
    }
}
