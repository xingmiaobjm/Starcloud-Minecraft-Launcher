// 页面列表行模型（WinUI XAML 编译器要求 x:DataType 引用命名空间级类型，不能是页面嵌套类型）

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

/// <summary>版本页本地版本条目</summary>
public sealed record LocalVersionRow(string Id, string KindText, string SizeText);

/// <summary>存档页世界条目</summary>
public sealed record SaveRow(string FolderName, string Name, string? IconPath,
    string LastPlayedText, string SizeText)
{
    public bool HasIcon => IconPath is not null;
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
