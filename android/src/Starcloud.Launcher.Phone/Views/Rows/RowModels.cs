// 页面列表行模型（XAML 编译器要求 x:DataType 引用命名空间级类型，不能是页面嵌套类型）
// 移植自桌面 Starcloud.App.Views.Rows.RowModels.cs（仅改命名空间）

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;

namespace Starcloud.Launcher.Phone.Views.Rows;

/// <summary>下载页版本列表条目（Icon 为按版本类型选取的等距方块渲染图）</summary>
public sealed record DownloadRow(
    string Id, string TimeText, string Badge, Brush? BadgeBrush, ImageSource? Icon);

/// <summary>
/// 下载详情页加载器构建行（Selected 决定行尾"已选"徽标可见性）。
/// <para>做成可变 + 通知：选中某个构建时只改这一行的状态，列表不重建——重建会让整个抽屉
/// 在点击的一瞬间闪一下、滚动位置也回顶，读起来就是"不丝滑"。</para>
/// </summary>
public sealed class LoaderBuildRow : INotifyPropertyChanged
{
    private bool _selected;

    public LoaderBuildRow(string version, string sub, bool selected)
    {
        Version = version;
        Sub = sub;
        _selected = selected;
    }

    public string Version { get; }
    public string Sub { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVisibility)));
        }
    }

    public Visibility SelectedVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;

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

/// <summary>
/// 社区资源搜索结果行视图模型。
/// <para>只有 <see cref="Description"/> 可变：英文简介先显示，MCIM 的中文译文异步到达后就地替换
/// （模板用 OneWay 绑定，故整行实现 INotifyPropertyChanged）。</para>
/// </summary>
public sealed class ModRow : INotifyPropertyChanged
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string Author { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string LoadersText { get; init; } = "";
    public string DownloadsText { get; init; } = "";
    public string UpdatedText { get; init; } = "";
    public ImageSource? Icon { get; init; }

    private string _description = "";

    /// <summary>简介（可能被中文译文替换）</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value) return;
            _description = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Visibility LoadersVisibility =>
        string.IsNullOrEmpty(LoadersText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TagsVisibility =>
        Tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>账户页列表行（Avatar 为皮肤头像，加载失败 / 无网络时为 null，由首字母色块兜底）</summary>
public sealed record AccountRow(
    string Uuid, string Username, string TypeText, bool IsDefault, ImageSource? Avatar,
    string SkinText = "", string DefaultActionText = "")
{
    public string Initial => string.IsNullOrWhiteSpace(Username) ? "?" : Username[..1].ToUpperInvariant();
    public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DefaultBtnVisibility => IsDefault ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>账户正在使用的皮肤名（没指定时为空，界面据此隐藏行尾那一列）</summary>
    public Visibility SkinVisibility =>
        string.IsNullOrWhiteSpace(SkinText) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// 账户详情页「我的皮肤」的卡片行。<c>IsAdd</c> 的那一张是首位的「添加皮肤 / 拖放」占位卡，
/// 与皮肤卡共用一个模板（靠可见性切换），这样它能跟着卡片网格自动排在第一格。
/// <para><see cref="IsSelected"/> 可变：选中态是叠在卡片上的强调色描边。之所以不走
/// GridView 自身的选中态，是因为那套视觉与「使用中」徽标叠在一起会出现两个指示器，
/// 而选中（左侧预览对准它）与使用（这个账户真正在用）本来就是两件事。</para>
/// </summary>
public sealed class SkinCardRow : INotifyPropertyChanged
{
    private bool _selected;

    public SkinCardRow(string id, string name, string detail, ImageSource? preview,
        bool inUse, bool isAdd = false)
    {
        Id = id;
        Name = name;
        Detail = detail;
        Preview = preview;
        InUse = inUse;
        IsAdd = isAdd;
    }

    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public ImageSource? Preview { get; }
    public bool InUse { get; }
    public bool IsAdd { get; }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionVisibility)));
        }
    }

    public Visibility SelectionVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InUseVisibility => InUse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddVisibility => IsAdd ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardVisibility => IsAdd ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 账户详情页「默认皮肤」的卡片行（点一下 = 加入皮肤库并应用到当前账户）。
/// <para>两张状态徽标是"点完之后用户能看到什么"的关键：同一张预置皮肤只会入库一次，
/// 所以点一张<em>已经在库里</em>的卡片时，皮肤库那边不会多出新卡 —— 过去页面上只有
/// 一个悬停才看得见的 ToolTip，用户点了没任何变化，就会以为"选了默认皮肤却没进皮肤库"。
/// 现在卡片自己写明「使用中 / 已在库中」，代码再把库里那张卡滚进视野。</para>
/// </summary>
public sealed record SkinPresetRow(
    string Id, string Name, ImageSource? Preview, bool InLibrary, bool InUse = false)
{
    public string ActionText => InUse ? Loc.T("使用中") : InLibrary ? Loc.T("已在库中") : Loc.T("点击使用");
    public Visibility InUseVisibility => InUse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InLibraryVisibility => !InUse && InLibrary ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 账户详情页「皮肤站衣柜」的卡片行（第三方账号：点一张 = 把它设为该账号当前的皮肤）。
/// 缩略图是站上那张纹理渲染出来的小立绘（见 SkinThumbnails），取不到图就给 null，卡片只显示名字。
/// </summary>
public sealed record StationSkinRow(StationSkin Skin, string ModelText, ImageSource? Preview)
{
    public string Id => Skin.Id;
    public string Name => Skin.Name;
}

/// <summary>版本内容分区的单个文件行</summary>
public sealed record ContentFileRow(
    string FileName, string SizeText, string StatusText, bool Enabled, string Kind, string FullPath)
{
    public string ToggleVerb => Enabled ? Loc.T("停用") : Loc.T("启用");
}

/// <summary>备份列表行</summary>
public sealed record BackupRow(
    string Id, string Name, string TimeText, string SizeText, string IncludesText);
