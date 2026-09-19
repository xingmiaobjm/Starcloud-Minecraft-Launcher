// 页面列表行模型（WinUI XAML 编译器要求 x:DataType 引用命名空间级类型，不能是页面嵌套类型）

using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Localization;
using Starcloud.App.Services;

namespace Starcloud.App.Views;

/// <summary>下载页版本列表条目（Icon 为按版本类型选取的等距方块渲染图）</summary>
public sealed record DownloadRow(
    string Id, string TimeText, string Badge, Brush? BadgeBrush, ImageSource? Icon);

/// <summary>下载详情页加载器构建行（Selected 决定行尾"已选"徽标可见性）</summary>
public sealed record LoaderBuildRow(string Version, string Sub, bool Selected)
{
    public Visibility SelectedVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>下载页原版列表的大版本分组（抽屉折叠行）</summary>
public sealed class VersionGroupRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string Major { get; init; } = "";
    public int Count { get; init; }
    public string CountText => $"({Count})";
    public List<DownloadRow> Items { get; init; } = [];

    /// <summary>抽屉是否展开（组头点击切换，驱动内容显隐与箭头方向）</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>本地版本条目，首页版本库与版本页共用（含实例档案信息：类别 / 隔离 / 上次启动）</summary>
public sealed record LocalVersionRow(string Id, string KindText, string SizeText,
    string? IconPath, string Category, bool Isolated, string LastPlayedText, string SearchKey)
{
    public bool HasIcon => !string.IsNullOrEmpty(IconPath);
    // IconPath 由 VersionRowHelpers.DisplayIconPath 解析好（自定义 > 加载器 > 版本类型）
    public ImageSource? Icon => VersionRowHelpers.ResolveIconSource(IconPath);
    public Visibility IconImageVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IconFontVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsolatedVisibility => Isolated ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CategoryVisibility => string.IsNullOrEmpty(Category) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>存档页世界条目（含所属版本归属）</summary>
public sealed record SaveRow(string FolderName, string Name, string? IconPath,
    string LastPlayedText, string SizeText,
    string VersionLabel, string VersionId, bool Isolated, string GameRoot, string SearchKey)
{
    public bool HasIcon => IconPath is not null;
    public ImageSource? Icon => VersionRowHelpers.ResolveIconSource(IconPath);
    public Visibility IsolatedVisibility => Isolated ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>社区资源搜索结果行视图模型</summary>
public sealed class ModRow
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string Author { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string LoadersText { get; init; } = "";
    public string DownloadsText { get; init; } = "";
    public string UpdatedText { get; init; } = "";
    public ImageSource? Icon { get; init; }

    public Visibility LoadersVisibility =>
        string.IsNullOrEmpty(LoadersText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TagsVisibility =>
        Tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>账户页卡片条目（Avatar 为皮肤头像，加载失败 / 无网络时为 null，由 Initial 色块兜底）</summary>
public sealed record AccountRow(
    string Uuid, string Username, string TypeText, bool IsDefault, ImageSource? Avatar)
{
    public string Initial => string.IsNullOrWhiteSpace(Username) ? "?" : Username[..1].ToUpperInvariant();
    public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DefaultBtnVisibility => IsDefault ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>版本内容分区的单个文件行</summary>
public sealed record ContentFileRow(
    string FileName, string SizeText, string StatusText, bool Enabled, string Kind, string FullPath)
{
    public string ToggleVerb => Enabled ? Loc.T("停用") : Loc.T("启用");
}

/// <summary>导出整合包的内容目录候选行（勾选状态可变）</summary>
public sealed class ExportDirRow
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    public bool IsStandard { get; init; }
    public bool IsChecked { get; set; }
}

/// <summary>备份列表行</summary>
public sealed record BackupRow(
    string Id, string Name, string TimeText, string SizeText, string IncludesText);

/// <summary>下载页版本列表模板选择：大版本分组行用抽屉模板，其余平铺</summary>
public sealed class VersionRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate Flat { get; set; } = null!;
    public DataTemplate Grouped { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is VersionGroupRow ? Grouped : Flat;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

/// <summary>
/// 社区资源远程图标（Modrinth CDN）解析：优先本地磁盘缓存，命中直接读文件、不再发网络请求；
/// 未命中先直连远程显示，同时后台落盘，供下次复用。
/// </summary>
public static class RemoteIconHelper
{
    public static ImageSource? Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var cache = AppServices.Instance.IconCache;
        if (cache.TryGetCachedPath(url) is { } local)
            return VersionRowHelpers.ResolveIconSource(local);
        cache.BeginCacheInBackground(url);
        return VersionRowHelpers.ResolveIconSource(url);
    }
}

/// <summary>
/// 玩家皮肤头像（第三方服务 minotar.net，helm = 头部含帽子层）：微软账户用 UUID（不随改名失效），
/// 第三方账户用认证服务器的会话皮肤。复用社区图标的「缓存优先 + 直连兜底」逻辑；请求失败（无网络 /
/// 名字不存在返回 404）时 <see cref="Microsoft.UI.Xaml.Controls.Image"/> 不渲染，由底层的首字母色块自然兜底。
/// 注：曾用 mc-heads.net，实测对本机微软档案间歇性返回 403（Cloudflare 拦截），故弃用。
/// </summary>
public static class AvatarHelper
{
    /// <summary>头像边长（像素）：128 供首页 72dip 大头像放大后仍清晰，列表小头像同源复用。</summary>
    private const int Size = 128;

    /// <summary>拼头像 URL；账户标识缺失返回 null（调用方保持首字母色块）。</summary>
    public static string? UrlFor(string? type, string? uuid, string? username)
    {
        var id = type == "microsoft" ? uuid : username;
        if (string.IsNullOrWhiteSpace(id)) return null;
        return $"https://minotar.net/helm/{Uri.EscapeDataString(id)}/{Size}";
    }

    /// <summary>解析账户头像；无标识 / 无网络 / 名字不存在时返回 null。</summary>
    public static ImageSource? Resolve(string? type, string? uuid, string? username, string? externalBase = null) =>
        type == "external" ? ResolveExternal(uuid, externalBase)
                           : RemoteIconHelper.Resolve(UrlFor(type, uuid, username));

    /// <summary>external 内存缓存 (serverBase, uuid) → 皮肤 URL；null 值也缓存，避免重复请求。</summary>
    private static readonly Dictionary<(string, string), string?> ExternalUrlCache = [];

    /// <summary>
    /// 第三方皮肤：会话服务器查询是异步的，首次调用后台解析（此时显示首字母色块），
    /// 解析完成后下一次刷新列表即命中缓存显示皮肤；失败保持首字母色块。
    /// </summary>
    private static ImageSource? ResolveExternal(string? uuid, string? externalBase)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(externalBase)) return null;
        var cacheKey = (externalBase, uuid);
        if (!ExternalUrlCache.TryGetValue(cacheKey, out var url))
        {
            ExternalUrlCache[cacheKey] = null; // 占位防并发重复请求
            _ = ResolveExternalCoreAsync(externalBase, uuid);
            return null;
        }
        return url is null ? null : RemoteIconHelper.Resolve(url);
    }

    private static async Task ResolveExternalCoreAsync(string externalBase, string uuid)
    {
        string? url = null;
        try
        {
            url = await AppServices.Instance.Yggdrasil.GetSkinUrlAsync(externalBase, uuid);
        }
        catch { /* 皮肤是尽力而为 */ }
        ExternalUrlCache[(externalBase, uuid)] = string.IsNullOrWhiteSpace(url) ? null : url;
    }
}

/// <summary>
/// Mod 名称中文翻译（来源：MC 百科搜索页第一条模组条目的标题）。缓存优先：内存 → 磁盘
/// （mod_title_cache.json，英文原名 → 中文名）→ 联网解析；未命中先显示原名并后台解析，
/// 下一次列表刷新即显示中文；解析失败（无网络 / 页面结构变化）静默保留原名。
/// </summary>
public static class ModTitleHelper
{
    /// <summary>英文名 → 中文名；null = 已查过但无翻译（占位防重复请求）。</summary>
    private static readonly Dictionary<string, string?> Cache = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static bool _loaded;

    static ModTitleHelper() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Starcloud Launcher)");

    private static string CachePath => Path.Combine(
        Starcloud.Core.Services.LauncherPaths.DataDir, "mod_title_cache.json");

    /// <summary>显示标题：命中翻译显示中文，否则原名（未命中的顺手发起后台解析）。</summary>
    public static string Display(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        EnsureLoaded();
        if (Cache.TryGetValue(title, out var zh)) return zh ?? title;
        Cache[title] = null;   // 占位，避免同名重复请求
        _ = ResolveAsync(title);
        return title;
    }

    /// <summary>异步取翻译：命中直接返回；未命中解析后返回（无翻译返回 null，供详情窗标题即时更新）。</summary>
    public static async Task<string?> GetAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        EnsureLoaded();
        if (Cache.TryGetValue(title, out var zh)) return zh;
        Cache[title] = null;   // 占位，避免同名重复请求
        return await ResolveAsync(title);
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(CachePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
            if (map is null) return;
            foreach (var (k, v) in map) Cache[k] = v;
        }
        catch { /* 缓存损坏从零开始 */ }
    }

    private static async Task<string?> ResolveAsync(string title)
    {
        try
        {
            var url = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(title)}";
            var html = await Http.GetStringAsync(url);
            // 条目结构：<a ... href="https://www.mcmod.cn/class/3124.html"><em>Fabric</em><em>API</em></a>
            // 页面 URL 为 /class/<id>.html，关键词以 <em> 高亮分词包裹；相邻 <em> 之间补空格还原词距
            var m = Regex.Match(html, @"href=""[^""]*/class/\d+\.html""[^>]*>(.*?)</a>",
                RegexOptions.Singleline);
            if (!m.Success) return null;
            var zh = System.Net.WebUtility.HtmlDecode(
                Regex.Replace(Regex.Replace(m.Groups[1].Value, @"</em>\s*<em>", " "), "</?em>", "")).Trim();
            if (string.IsNullOrWhiteSpace(zh)) return null;
            await Gate.WaitAsync();
            try
            {
                Cache[title] = zh;
                File.WriteAllText(CachePath, JsonSerializer.Serialize(
                    Cache.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!)));
            }
            finally { Gate.Release(); }
            return zh;
        }
        catch { return null; /* 翻译是尽力而为 */ }
    }
}

/// <summary>版本行共用辅助（图标解析 / 时间格式化 / 类型推断）：版本页与详情页共用。</summary>
public static class VersionRowHelpers
{
    /// <summary>版本类型 → 内置图标文件名（正式版草方块 / 快照紫水晶 / 远古版圆石与泥土）</summary>
    private static readonly Dictionary<string, string> VersionIconFiles = new()
    {
        ["release"] = "natural_grass_block.png",
        ["snapshot"] = "block_of_amethyst.png",
        ["old_beta"] = "masonry_cobblestone.png",
        ["old_alpha"] = "natural_dirt.png",
    };

    /// <summary>图标解码缓存（同一文件多行复用，避免重复解码）</summary>
    private static readonly Dictionary<string, ImageSource?> VersionIconCache = [];

    /// <summary>
    /// 版本类型 → 内置等距方块图标（随输出目录发布于 Assets/MC-Icons）。
    /// 文件缺失时返回 null，由模板中的 FontIcon 兜底层负责显示。
    /// </summary>
    public static ImageSource? VersionIcon(string? type)
    {
        var file = VersionIconFiles.GetValueOrDefault(type ?? "", VersionIconFiles["release"]);
        if (VersionIconCache.TryGetValue(file, out var cached)) return cached;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "MC-Icons", file);
        var source = File.Exists(path) ? ResolveIconSource(path) : null;
        VersionIconCache[file] = source;
        return source;
    }

    /// <summary>档案图标：相对路径 → starcloud/icons/；不存在或为空 → null 走 FontIcon 回退。</summary>
    public static string? ResolveIconPath(string dotMc, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        var path = Path.IsPathRooted(iconPath) ? iconPath
            : Path.Combine(dotMc, "starcloud", "icons", iconPath);
        return File.Exists(path) ? path : null;
    }

    // 顺序敏感：neoforge 必须先于 forge 匹配（id 中 "neoforge" 包含 "forge" 子串）
    private static readonly (string Key, string File)[] LoaderIconFiles =
    {
        ("neoforge", "neoforge.png"),
        ("forge", "forge.png"),
        ("fabric", "fabric.png"),
        ("quilt", "quilt.png"),
    };

    /// <summary>
    /// 加载器图标文件名；原版返回 null。
    /// <para>判定顺序与 <see cref="DetectKind"/> 一致：先看版本 JSON 里的加载器痕迹（这是"标签写 NeoForge"
    /// 的依据），再看版本 id 里的关键字兜底。只看 id 是不行的——整合包/自建实例的名字往往和加载器无关
    /// （例如「Create Aero: New Horizons」），会一路落到原版草方块，出现"标签与图标对不上"。</para>
    /// </summary>
    private static string? LoaderIconFile(string dotMc, string versionId)
    {
        string json;
        try
        {
            json = File.ReadAllText(Path.Combine(dotMc, "versions", versionId, versionId + ".json"));
        }
        catch
        {
            json = ""; // 读不到就只按 id 兜底
        }

        foreach (var (key, file) in LoaderIconFiles)
        {
            if (json.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                versionId.Contains(key, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    /// <summary>版本 JSON 的 type 字段（release / snapshot / old_beta / old_alpha）；缺失或损坏按正式版。</summary>
    public static string DetectVersionType(string dotMc, string versionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dotMc, "versions", versionId, versionId + ".json")));
            if (doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                return type.GetString() ?? "release";
        }
        catch
        {
            // 读不到按正式版
        }
        return "release";
    }

    /// <summary>
    /// 行图标最终取用的文件路径：自定义图标 &gt; 加载器图标 &gt; 版本类型图标（正式版草方块 / 快照紫水晶 /
    /// 远古版圆石与泥土）。资源文件缺失返回 null，由模板里的 FontIcon 兜底。
    /// </summary>
    public static string? DisplayIconPath(string dotMc, string versionId, string? customIconPath)
    {
        if (ResolveIconPath(dotMc, customIconPath) is { } custom) return custom;
        var file = LoaderIconFile(dotMc, versionId)
            ?? VersionIconFiles.GetValueOrDefault(DetectVersionType(dotMc, versionId), VersionIconFiles["release"]);
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "MC-Icons", file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 本地图片路径 → ImageSource。x:Bind 绑定 Image.Source 时，本地绝对路径无法被隐式转换
    /// （XamlBindingHelper.ConvertValue 会抛 ArgumentException 导致整页崩溃），故显式构造。
    /// </summary>
    public static ImageSource? ResolveIconSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return new BitmapImage(new Uri(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>上次启动时间：「今天 14:32 / 昨天 20:01 / 3 天前 / 从未启动」。</summary>
    public static string FormatLastPlayed(DateTime? time)
    {
        if (time is not { } t) return Loc.T("从未启动");
        var days = (DateTime.Now.Date - t.Date).Days;
        return days switch
        {
            <= 0 => Loc.T("今天 {0:HH:mm}", t),
            1 => Loc.T("昨天 {0:HH:mm}", t),
            < 30 => Loc.T("{0} 天前", days),
            < 365 => Loc.T("{0} 个月前", days / 30),
            _ => $"{t:yyyy-MM-dd}",
        };
    }

    /// <summary>从版本 JSON 推断类型（NeoForge / Fabric / Forge / 原版类型）</summary>
    public static string DetectKind(string dotMc, string id)
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(dotMc, "versions", id, id + ".json"));
            if (json.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
            if (json.Contains("fabricloader", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)) return "Fabric";
            if (json.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
            if (json.Contains("\"old_beta\"", StringComparison.Ordinal)) return Loc.T("Beta 版");
            if (json.Contains("\"old_alpha\"", StringComparison.Ordinal)) return Loc.T("Alpha 版");
            if (json.Contains("\"snapshot\"", StringComparison.Ordinal)) return Loc.T("快照版");
            return Loc.T("正式版");
        }
        catch
        {
            return Loc.T("未知");
        }
    }
}
