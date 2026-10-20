// 左侧导航单标签（手机版 Shell 专用，延续竖屏版五格设计、横屏排到屏幕左缘）：
// 图标 + 文字纵排，选中态用主题强调色。竖排在 64 宽侧栏里，每格固定 54 高触控目标。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Starcloud.Launcher.Phone.Views.Controls;

public sealed class BottomTabButton : Grid
{
    public static readonly DependencyProperty Tag2Property = DependencyProperty.Register(
        nameof(Tag2), typeof(string), typeof(BottomTabButton), null);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(BottomTabButton),
        new PropertyMetadata(default(string), (d, _) => ((BottomTabButton)d)._icon.Glyph = (string?)((BottomTabButton)d).Glyph ?? ""));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(BottomTabButton),
        // 经 Loc.T 翻译（源语言即键，zh 直出 / en 查词典）：Loc.Text 附加属性的
        // 分发 switch 不覆盖 Grid 子类，这里在属性回调内自行接住
        new PropertyMetadata(default(string),
            (d, _) => ((BottomTabButton)d)._label.Text =
                Localization.Loc.T((string?)((BottomTabButton)d).Label ?? "")));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(BottomTabButton),
        new PropertyMetadata(false, (d, _) => ((BottomTabButton)d).ApplySelection()));

    private readonly FontIcon _icon;
    private readonly TextBlock _label;
    private readonly Ellipse _runningDot;

    public BottomTabButton()
    {
        // 整格可点：Uno 里无背景的 Grid 不参与命中测试，必须铺一层画刷。
        // 用纯透明画刷（透明度 0 会连图标文字一起藏掉——真机实测踩坑）；
        // Transparent 画刷参与命中测试且不可见，基态视觉由图标/文字承担。
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // 行 0 图标（含右上运行圆点）、行 1 文字；整格可点击，交给外层 Tapped 统一处理。
        // 高 48：五格竖排（含 5dp 间距 ×2×5 + 顶栏 44）在 436dp 屏高内恰好放得下（54 会裁掉最后一格）
        Height = 48;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Margin = new Thickness(0, 5, 0, 5);

        var iconHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        _icon = new FontIcon
        {
            FontFamily = (FontFamily)Application.Current.Resources["ScIconFont"],
            FontSize = 19,
        };
        // 运行状态圆点（仅「我的」格用）：游戏运行中点亮（Shell 里控制）
        _runningDot = new Ellipse
        {
            Width = 7, Height = 7, Fill = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, -3, 0),
            Visibility = Visibility.Collapsed,
        };
        iconHost.Children.Add(_icon);
        iconHost.Children.Add(_runningDot);
        SetRow(iconHost, 0);
        Children.Add(iconHost);

        _label = new TextBlock
        {
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };
        SetRow(_label, 1);
        Children.Add(_label);

        ApplySelection();
    }

    /// <summary>路由 tag（home/versions/download/multiplayer/more）。
    /// 与 FrameworkElement.Tag 分开命名，避免与控件基类 Tag（object）混淆。</summary>
    public string? Tag2
    {
        get => (string?)GetValue(Tag2Property);
        set => SetValue(Tag2Property, value);
    }

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>运行中圆点（游戏运行中点亮；竖屏版同款语义）。</summary>
    public void SetRunningDot(bool running) =>
        _runningDot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

    private void ApplySelection()
    {
        var fg = (Brush)Application.Current.Resources[
            IsSelected ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush"];
        _icon.Foreground = fg;
        _label.Foreground = fg;
        _icon.FontWeight = IsSelected ? Microsoft.UI.Text.FontWeights.Medium
            : Microsoft.UI.Text.FontWeights.Normal;
    }
}
