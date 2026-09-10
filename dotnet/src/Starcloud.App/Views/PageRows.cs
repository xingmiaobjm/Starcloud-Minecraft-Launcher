// 页面列表行模型（WinUI XAML 编译器要求 x:DataType 引用命名空间级类型，不能是页面嵌套类型）

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Starcloud.App.Views;

/// <summary>首页版本列表条目（已安装版本）</summary>
public sealed record VersionRow(string Id, string SizeText);

/// <summary>下载页版本列表条目</summary>
public sealed record DownloadRow(string Id, string TimeText, string Badge, Brush? BadgeBrush);

/// <summary>下载详情页加载器构建行（Selected 决定行尾"已选"徽标可见性）</summary>
public sealed record LoaderBuildRow(string Version, string Sub, bool Selected)
{
    public Visibility SelectedVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>下载页原版列表的大版本分组（抽屉折叠行）</summary>
public sealed class VersionGroupRow
{
    public string Major { get; init; } = "";
    public int Count { get; init; }
    public string CountText => $"({Count})";
    public List<DownloadRow> Items { get; init; } = [];
}

/// <summary>版本页本地版本条目（含实例档案信息：类别 / 隔离 / 上次启动）</summary>
public sealed record LocalVersionRow(string Id, string KindText, string SizeText,
    string? IconPath, string Category, bool Isolated, string LastPlayedText, string SearchKey)
{
    public bool HasIcon => !string.IsNullOrEmpty(IconPath);
    public ImageSource? Icon => VersionRowHelpers.ResolveIconSource(IconPath);
    public Visibility HasIconIconVisibility => HasIcon ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconFallbackVisibility => HasIcon ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IsolatedVisibility => Isolated ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CategoryVisibility => string.IsNullOrEmpty(Category) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>存档页世界条目</summary>
public sealed record SaveRow(string FolderName, string Name, string? IconPath,
    string LastPlayedText, string SizeText)
{
    public bool HasIcon => IconPath is not null;
    public ImageSource? Icon => VersionRowHelpers.ResolveIconSource(IconPath);
}

/// <summary>社区资源搜索结果行视图模型</summary>
public sealed class ModRow
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string LoadersText { get; init; } = "";
    public string DownloadsText { get; init; } = "";
    public string UpdatedText { get; init; } = "";
    public string ButtonText { get; init; } = "安装";
    public ImageSource? Icon { get; init; }

    public Visibility LoadersVisibility =>
        string.IsNullOrEmpty(LoadersText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TagsVisibility =>
        Tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>账户页卡片条目</summary>
public sealed record AccountRow(string Uuid, string Username, string TypeText, bool IsDefault)
{
    public string Initial => string.IsNullOrWhiteSpace(Username) ? "?" : Username[..1].ToUpperInvariant();
    public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DefaultBtnVisibility => IsDefault ? Visibility.Collapsed : Visibility.Visible;
}

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

/// <summary>版本行共用辅助（图标解析 / 时间格式化 / 类型推断）：版本页与详情页共用。</summary>
public static class VersionRowHelpers
{
    /// <summary>档案图标：相对路径 → starcloud/icons/；不存在或为空 → null 走 FontIcon 回退。</summary>
    public static string? ResolveIconPath(string dotMc, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        var path = Path.IsPathRooted(iconPath) ? iconPath
            : Path.Combine(dotMc, "starcloud", "icons", iconPath);
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
        if (time is not { } t) return "从未启动";
        var days = (DateTime.Now.Date - t.Date).Days;
        return days switch
        {
            <= 0 => $"今天 {t:HH:mm}",
            1 => $"昨天 {t:HH:mm}",
            < 30 => $"{days} 天前",
            < 365 => $"{days / 30} 个月前",
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
            if (json.Contains("\"old_beta\"", StringComparison.Ordinal)) return "Beta 版";
            if (json.Contains("\"old_alpha\"", StringComparison.Ordinal)) return "Alpha 版";
            if (json.Contains("\"snapshot\"", StringComparison.Ordinal)) return "快照版";
            return "正式版";
        }
        catch
        {
            return "未知";
        }
    }
}
