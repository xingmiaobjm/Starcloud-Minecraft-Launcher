// 页面列表行模型（WinUI XAML 编译器要求 x:DataType 引用命名空间级类型，不能是页面嵌套类型）

using System.ComponentModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Localization;
using Starcloud.App.Services;

namespace Starcloud.App.Views;

/// <summary>首页版本列表条目（已安装版本）</summary>
public sealed record VersionRow(string Id, string SizeText);

/// <summary>下载页版本列表条目（Icon 为按版本类型选取的等距方块渲染图）</summary>
public sealed record DownloadRow(
    string Id, string TimeText, string Badge, Brush? BadgeBrush, ImageSource? Icon);

/// <summary>下载页「最新版本」区块条目（最新正式版 / 最新预览版）</summary>
public sealed record LatestVersionRow(string Id, string SubText, ImageSource? Icon);

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandGlyph)));
        }
    }

    /// <summary>组头箭头字形（Segoe Fluent Icons）：收起为下箭头，展开为上箭头</summary>
    public string ExpandGlyph => _isExpanded ? "\uE70E" : "\uE70D";

    public event PropertyChangedEventHandler? PropertyChanged;
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
    public string ButtonText { get; init; } = Loc.T("安装");
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

/// <summary>资源管理列表行</summary>
public sealed record LibraryRow(
    string Id, string Kind, string Title, string FileName, string SubText,
    string SizeText, string TimeText, ImageSource? Icon, string KindLabel);

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
