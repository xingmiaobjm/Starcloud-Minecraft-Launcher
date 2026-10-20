// 版本行共用辅助（图标解析 / 类型推断 / 时间格式化）：版本页、首页与详情页共用。
// 移植自桌面 Starcloud.App.Views.Rows.VersionRowHelpers.cs（仅改命名空间与图标解析适配）：
// 桌面内置方块图标从磁盘 Assets/MC-Icons 读取，安卓改为打包进 APK 的
// Assets/Images/*.png（csproj UnoImage 项），经 ms-appx:/// URI 由 Skia 加载器解析；
// 自定义实例图标仍是真实文件路径，按 file:// URI 加载（同 Services/Ui.FileImage）。
//
// 性能取向：类型的三个问题时（"推断类型标签 / 选加载器图标 / 选版本类型图标"）都落在同一个
// 版本 JSON 上。这里把"读文件 + 解析"收敛成一次 Meta 调用并缓存结果，
// 于是版本列表的每一行从「3 次读文件 + 1 次 JSON 解析」降到「至多 1 次，之后纯内存」；
// 缓存按文件写入时间失效（安装 / 改名 / 换加载器后自动重读）。

using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.Launcher.Phone.Localization;

namespace Starcloud.Launcher.Phone.Views.Rows;

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
    /// 版本类型 → 内置等距方块图标（随包发布于 Assets/Images，csproj UnoImage 项保证打包）。
    /// 由模板中的 FontIcon 兜底层负责显示加载失败的场合。
    /// </summary>
    public static ImageSource? VersionIcon(string? type)
    {
        var file = VersionIconFiles.GetValueOrDefault(type ?? "", VersionIconFiles["release"]);
        if (VersionIconCache.TryGetValue(file, out var cached)) return cached;
        var source = ResolveIconSource(BuiltInIconPath(file));
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
    [
        ("neoforge", "neoforge.png"),
        ("forge", "forge.png"),
        ("fabric", "fabric.png"),
        ("quilt", "quilt.png"),
    ];

    /// <summary>
    /// 版本 JSON 派生信息：类型（release / snapshot / old_beta / old_alpha）与命中的加载器。
    /// <para>两者都来自同一份 JSON，且<b>只读一次</b>——列表每行原先要读三次文件、解析一次 JSON，
    /// 版本多了会拖慢整页；这里合并成一次并缓存。</para>
    /// </summary>
    private readonly record struct Meta(string Type, string? LoaderKey);

    private static readonly Dictionary<string, (DateTime Stamp, Meta Value)> MetaCache = [];
    private static readonly object MetaGate = new();

    private static string JsonPath(string dotMc, string versionId) =>
        Path.Combine(dotMc, "versions", versionId, versionId + ".json");

    private static Meta ReadMeta(string dotMc, string versionId)
    {
        var path = JsonPath(dotMc, versionId);
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch { stamp = DateTime.MinValue; }

        lock (MetaGate)
        {
            if (MetaCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Value;
        }

        string json;
        try { json = File.ReadAllText(path); }
        catch { json = ""; } // 读不到就只按 id 兜底

        var loader = LoaderKeyOf(json, versionId);
        var meta = new Meta(TypeOf(json), loader);
        lock (MetaGate) { MetaCache[path] = (stamp, meta); }
        return meta;
    }

    /// <summary>
    /// 加载器判定：先看版本 JSON 里的加载器痕迹，再看版本 id 关键字兜底。
    /// 只看 id 是不行的——整合包/自建实例的名字往往和加载器无关（例如「Create Aero: New Horizons」），
    /// 会一路落到原版草方块，出现"标签与图标对不上"。
    /// </summary>
    private static string? LoaderKeyOf(string json, string versionId)
    {
        foreach (var (key, _) in LoaderIconFiles)
        {
            if (json.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                versionId.Contains(key, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    /// <summary>版本 JSON 的 type 字段（release / snapshot / old_beta / old_alpha）；缺失或损坏按正式版。</summary>
    private static string TypeOf(string json)
    {
        if (json.Length == 0) return "release";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                return type.GetString() ?? "release";
        }
        catch
        {
            // 读不到按正式版
        }
        return "release";
    }

    /// <summary>版本 JSON 的 type 字段（release / snapshot / old_beta / old_alpha）；缺失或损坏按正式版。</summary>
    public static string DetectVersionType(string dotMc, string versionId) =>
        ReadMeta(dotMc, versionId).Type;

    /// <summary>内置图标 → 包内资源 URI（Assets/Images 由 csproj UnoImage 全量打包，无需存在性检查）</summary>
    private static string? BuiltInIconPath(string file) =>
        $"ms-appx:///Assets/Images/{file}";

    /// <summary>
    /// 行图标最终取用的文件路径：自定义图标 &gt; 加载器图标 &gt; 版本类型图标（正式版草方块 / 快照紫水晶 /
    /// 远古版圆石与泥土）。资源文件缺失时由模板里的 FontIcon 兜底。
    /// </summary>
    public static string? DisplayIconPath(string dotMc, string versionId, string? customIconPath)
    {
        if (ResolveIconPath(dotMc, customIconPath) is { } custom) return custom;
        var meta = ReadMeta(dotMc, versionId);
        var file = meta.LoaderKey is { } loader
            ? LoaderIconFiles.First(f => f.Key == loader).File
            : VersionIconFiles.GetValueOrDefault(meta.Type, VersionIconFiles["release"]);
        return BuiltInIconPath(file);
    }

    /// <summary>
    /// 本地图片路径 → ImageSource。包内资源（ms-appx:///）原样解析；
    /// 磁盘绝对路径必须补 file:// 前缀（安卓不允许省略 scheme 的绝对路径 URI），
    /// 否则 x:Bind 绑定 Image.Source 时会抛异常导致整页崩溃，故显式构造。
    /// </summary>
    public static ImageSource? ResolveIconSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var uri = path.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(path)
                : new Uri("file://" + path);
            return new BitmapImage(uri);
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

    /// <summary>
    /// 从版本 JSON 推断类型标签（NeoForge / Fabric / Forge / Quilt / 原版类型）。
    /// 与 <see cref="DisplayIconPath"/> 同源：标签与图标都读同一份缓存结果，不会再出现"标签写 Fabric、
    /// 图标却是原版草方块"这种两套判定打架的情况。
    /// </summary>
    public static string DetectKind(string dotMc, string id)
    {
        var meta = ReadMeta(dotMc, id);
        if (meta.LoaderKey is { } loaderKey) return LoaderLabel(loaderKey);
        return meta.Type switch
        {
            "old_beta" => Loc.T("Beta 版"),
            "old_alpha" => Loc.T("Alpha 版"),
            "snapshot" => Loc.T("快照版"),
            _ => Loc.T("正式版"),
        };
    }

    private static string LoaderLabel(string loaderKey) => loaderKey switch
    {
        "neoforge" => "NeoForge",
        "forge" => "Forge",
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        _ => Loc.T("正式版"),
    };
}
