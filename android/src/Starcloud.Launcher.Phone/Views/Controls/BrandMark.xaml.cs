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
// Uno 移植说明：SVG 的 ms-appx 加载在 Uno 安卓上不触发（SvgImageSource 连 ImageFailed
// 都不冒，装了 Uno.WinUI.Svg 也一样），故三张标记在构建期栅格化为 512px 透明 PNG
// （Assets/Images/brand_mark_{light,dark,small}.png，UnoImage 通道，实测可渲染），
// 代码逻辑不变：按主题换 light/dark，Small 换 small 专用版，统一按 ms-appx:/// 解析；
// 日志同步打 Android.Util.Log（tag=StarcloudFlow），Debug 输出未必进 logcat。

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Starcloud.Launcher.Phone.Views.Controls;

public sealed partial class BrandMark : UserControl
{
    private static readonly Uri LightUri = AssetUri("brand_mark_light.png");
    private static readonly Uri DarkUri = AssetUri("brand_mark_dark.png");
    private static readonly Uri SmallUri = AssetUri("brand_mark_small.png");

    private string _current = "brand_mark_light.png";

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
        // 标记是 SVG：解析成败各留一行日志，免得「标记没显示」只能靠猜。
        // Debug.WriteLine 在安卓上未必进 logcat，同步打 Android.Util.Log（tag=StarcloudFlow）保底可诊断。
        MarkImage.ImageOpened += (_, _) =>
        {
            Debug.WriteLine($"[app] 品牌标记已渲染：{_current}");
            global::Android.Util.Log.Info("StarcloudFlow", $"品牌标记已渲染：{_current}");
        };
        MarkImage.ImageFailed += (_, e) =>
        {
            Debug.WriteLine($"[app] 品牌标记渲染失败：{e.ErrorMessage}");
            global::Android.Util.Log.Warn("StarcloudFlow", $"品牌标记渲染失败：{_current}：{e.ErrorMessage}");
        };
        Apply();
    }

    private static Uri AssetUri(string fileName) =>
        new($"ms-appx:///Assets/Images/{fileName}");

    private void Apply()
    {
        if (Small)
        {
            _current = "brand_mark_small.png";
            MarkImage.Source = new BitmapImage(SmallUri);
            return;
        }
        var dark = ActualTheme == ElementTheme.Dark;
        _current = dark ? "brand_mark_dark.png" : "brand_mark_light.png";
        MarkImage.Source = new BitmapImage(dark ? DarkUri : LightUri);
    }
}
