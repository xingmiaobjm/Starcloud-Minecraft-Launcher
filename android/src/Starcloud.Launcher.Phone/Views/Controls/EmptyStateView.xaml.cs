// 统一空状态组件（D 组 ②：界面打磨与动效，设计 §7）
// 图标 + 标题 + 副标题 + 可选操作按钮；文案为空则对应元素自动隐藏。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Starcloud.Launcher.Phone.Views.Controls;

public sealed partial class EmptyStateView : UserControl
{
    /// <summary>点击「操作」按钮时触发（无按钮文案时不触发）。</summary>
    public event RoutedEventHandler? ActionClick;

    public EmptyStateView() => InitializeComponent();

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyStateView), new PropertyMetadata(null, OnGlyphChanged));

    public static readonly DependencyProperty IconImageProperty = DependencyProperty.Register(
        nameof(IconImage), typeof(ImageSource), typeof(EmptyStateView), new PropertyMetadata(null, OnIconImageChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStateView), new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(EmptyStateView), new PropertyMetadata(null, OnSubtitleChanged));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyStateView), new PropertyMetadata(null, OnActionTextChanged));

    /// <summary>图标字形（空则不显示图标）</summary>
    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>图标图片（品牌标记等多色图标用；设了它就不再画字形）</summary>
    public ImageSource? IconImage
    {
        get => (ImageSource?)GetValue(IconImageProperty);
        set => SetValue(IconImageProperty, value);
    }

    /// <summary>主文案</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>副文案（空则不显示）</summary>
    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>操作按钮文案（空则不显示按钮）</summary>
    public string? ActionText
    {
        get => (string?)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (EmptyStateView)d;
        var text = e.NewValue as string;
        v.Icon.Glyph = text ?? "";
        v.SyncIconVisibility();
    }

    private static void OnIconImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (EmptyStateView)d;
        v.IconPicture.Source = e.NewValue as ImageSource;
        v.SyncIconVisibility();
    }

    /// <summary>字形与图片二选一：给了图片就显示图片，否则按字形是否为空决定。</summary>
    private void SyncIconVisibility()
    {
        var hasImage = IconPicture.Source is not null;
        IconPicture.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        Icon.Visibility = hasImage || string.IsNullOrEmpty(Icon.Glyph)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((EmptyStateView)d).TitleText.Text = e.NewValue as string ?? "";

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (EmptyStateView)d;
        var text = e.NewValue as string;
        v.SubtitleText.Text = text ?? "";
        v.SubtitleText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnActionTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (EmptyStateView)d;
        var text = e.NewValue as string;
        v.ActionButton.Content = text ?? "";
        v.ActionButton.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, e);
}
