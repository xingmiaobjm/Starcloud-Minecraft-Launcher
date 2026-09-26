// Starcloud 品牌标记：渲染品牌手册的主标记 SVG（Views/Controls/BrandMark.xaml）。
//
// 两个文件是同一套几何，只差描边色，所以「换主题」就是换源：
//   浅底 starcloud-mark-light.svg（描边 #111418，用于白 / 浅灰 / 浅彩底）
//   深底 starcloud-mark-dark.svg（描边 #E6EAF2，用于深色界面与深色任务栏）
// 黄面与星两个文件一致 —— 星落在黄面上，任何底色都成立。
//
// Small="True" 换成小尺寸专用版 starcloud-mark-small.svg（16–24px）：
// 该尺寸下主标记的三格描边不足一像素会糊，小尺寸版只留抬起的黄块与星。
// 小尺寸版两个主题同一份文件（它没有描边色，星永远落在黄面上），所以不跟主题换源。
// Nova 助手的图标用的就是这个变体（见 MainWindow / NovaPage / Menus）。
//
// SVG 随输出目录发布（见 csproj 的 Assets\Brand 条目），按 AppContext.BaseDirectory 解析：
// 相对路径会跟随快捷方式的「起始位置」，从开始菜单启动时可能落空。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Controls;

public sealed partial class BrandMark : UserControl
{
    private static readonly Uri LightUri = AssetUri("starcloud-mark-light.svg");
    private static readonly Uri DarkUri = AssetUri("starcloud-mark-dark.svg");
    private static readonly Uri SmallUri = AssetUri("starcloud-mark-small.svg");

    private string _current = "starcloud-mark-light.svg";

    /// <summary>小尺寸专用版（16–24px，Nova 图标）：不跟主题换源。</summary>
    public static readonly DependencyProperty SmallProperty = DependencyProperty.Register(
        nameof(Small), typeof(bool), typeof(BrandMark), new PropertyMetadata(false, (d, _) => ((BrandMark)d).Apply()));

    public bool Small
    {
        get => (bool)GetValue(SmallProperty);
        set => SetValue(SmallProperty, value);
    }

    public BrandMark()
    {
        InitializeComponent();
        // 构造时 ActualTheme 还是 Default（跟随系统），真正生效的主题要等挂进可视树
        Loaded += (_, _) => Apply();
        ActualThemeChanged += (_, _) => Apply();
        // 标记是 SVG：解析成败各留一行日志，免得「标记没显示」只能靠猜
        MarkImage.ImageOpened += (_, _) => LauncherLog.Write("app", $"品牌标记已渲染：{_current}");
        MarkImage.ImageFailed += (_, e) => LauncherLog.Write("app", $"品牌标记渲染失败：{e.ErrorMessage}");
        Apply();
    }

    private static Uri AssetUri(string fileName) =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", fileName));

    private void Apply()
    {
        if (Small)
        {
            _current = "starcloud-mark-small.svg";
            MarkImage.Source = new SvgImageSource(SmallUri);
            return;
        }
        var dark = ActualTheme == ElementTheme.Dark;
        _current = dark ? "starcloud-mark-dark.svg" : "starcloud-mark-light.svg";
        MarkImage.Source = new SvgImageSource(dark ? DarkUri : LightUri);
    }
}
