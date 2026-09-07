// 页面列表行模型（WinUI XAML 编译器要求 x:DataType 引用命名空间级类型，不能是页面嵌套类型）

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Starcloud.App.Views;

/// <summary>首页版本列表条目（已安装版本）</summary>
public sealed record VersionRow(string Id, string SizeText);

/// <summary>下载页版本列表条目</summary>
public sealed record DownloadRow(string Id, string TimeText, string Badge, Brush? BadgeBrush);

/// <summary>版本页本地版本条目</summary>
public sealed record LocalVersionRow(string Id, string KindText, string SizeText);

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
