// 直接落在页面背景（自定义壁纸 / 毛玻璃，没有纯色底）上的文字，统一是**白字 + 一层灰色落影**，
// 保证它在任何壁纸上都读得清，也和卡片里那些靠底色衬出来的正文区分得开。
//
// 为什么是「灰字副本」而不是阴影 / 描边 API：WinUI 3 的 TextBlock 没有文字阴影或描边属性；
// ThemeShadow 只能是深色、且按元素矩形投影；把合成 DropShadow 挂到 XAML 元素视觉上在本项目里
// 也不渲染（HoverShadowBehavior 顶部记着这三种都试过的结论）。所以用最朴素的画法：
// 同一个 TextBlock 的灰字副本垫在下面，正下方各偏 1px / 2px，近的一层实、远的一层淡，
// 合起来是一片柔和的落影。
//
// 与「四正向各偏 1px 围一圈描边」的区别：偏移是单向的（只往下），所以不会把字形糊成重影，
// 看着就是文字底下压着的一片灰影；层数也只有两层，再多只是把同一片影子叠深，不会更柔。
//
// 生效范围：**正下方直接落在背景上的文字**——页面抬头的标题，以及紧跟它那行说明。
// 芯片 / 徽章里的文字由它们自己的底负责对比度（accent 面上的黑字更是不能动），因此不参与，
// 也就解释了为什么挂一个 Panel 却只有一部分 TextBlock 变了样：
//   · 挂在 TextBlock 上：只给这一处文字加；
//   · 挂在容器 Panel 上：子树里凡是「父级也是 Panel」的 TextBlock 都加（页面抬头这种整块落在背景上的用这个）。
// 前提：目标文字在 Panel 里（要把它挪进一层包装 Grid 放副本）。不在 Panel 里则整块跳过。
//
// 类名沿用 TextOutlineBehavior：它现在的职责是「落在壁纸上的文字可读性」，不再是一圈描边。
// 页面抬头已经不再写字（位置由左侧导航指示），所以现在只剩联机页底部那行居中的说明还在用它。

using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Starcloud.Launcher.Phone.Views.Behaviors;

/// <summary>落在页面背景上的文字：白字 + 一层灰色落影（实现见文件头注释）。</summary>
public static class TextOutlineBehavior
{
    /// <summary>
    /// 灰色落影：正下方 1px（近、较实）+ 2px（远、很淡）。单向偏移，不会形成描边那样的重影。
    /// </summary>
    private static readonly (double Dx, double Dy, double Alpha)[] Shadow =
    [
        (0, 1, 0.50),
        (0, 2, 0.22),
    ];

    private static readonly ConditionalWeakTable<TextBlock, Outline> States = new();

    /// <summary>本体文字色：壁纸不可控，白字 + 落影是唯一在各色壁纸上都立得住的组合。</summary>
    private static readonly Brush TextBrush = new SolidColorBrush(Colors.White);

    /// <summary>落影色：中灰（不是纯黑）——纯黑在深色壁纸上会把白字的边缘咬出硬边。</summary>
    private static readonly Brush ShadowBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 90, 90));

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(TextOutlineBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        try
        {
            if (d is not FrameworkElement fe || e.NewValue is not true) return;
            switch (fe)
            {
                case TextBlock tb:
                    Wire(tb);
                    break;
                case Panel panel:
                    // 容器：Loaded 后再走子树（此时可视树已成型）
                    panel.Loaded -= OnPanelLoaded;
                    panel.Loaded += OnPanelLoaded;
                    if (panel.IsLoaded) WirePanel(panel);
                    break;
            }
        }
        catch
        {
            // 附加属性变更里出问题只会是「没加上描边」，不能把异常抛回 XAML 解析 / 布局
        }
    }

    private static void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded 里抛异常会直接冒到 Dispatcher，宁可少一圈描边也不能让页面挂掉
        try { if (sender is Panel panel) WirePanel(panel); }
        catch { /* 静默降级 */ }
    }

    private static void WirePanel(Panel panel)
    {
        foreach (var text in Descendants(panel)) Wire(text);
    }

    /// <summary>子树里的全部 TextBlock（含嵌套容器）</summary>
    private static IEnumerable<TextBlock> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) yield return text;
            else foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Wire(TextBlock source)
    {
        var outline = States.GetOrCreateValue(source);
        if (outline.Wired) return;
        if (source.Parent is not Panel parent) return; // 没有 Panel 可插入包装层：跳过
        var index = parent.Children.IndexOf(source);
        if (index < 0) return;

        try
        {
            var host = new Grid
            {
                Margin = source.Margin,
                HorizontalAlignment = source.HorizontalAlignment,
                VerticalAlignment = source.VerticalAlignment,
            };
            if (!double.IsNaN(source.Width)) host.Width = source.Width;
            if (!double.IsNaN(source.Height)) host.Height = source.Height;
            if (!double.IsNaN(source.MaxWidth)) host.MaxWidth = source.MaxWidth;
            // 外层定位随包装层一起搬（Grid 行/列、Canvas 坐标），本体留在包装层里 (0,0)
            Grid.SetRow(host, Grid.GetRow(source));
            Grid.SetColumn(host, Grid.GetColumn(source));
            Grid.SetRowSpan(host, Grid.GetRowSpan(source));
            Grid.SetColumnSpan(host, Grid.GetColumnSpan(source));
            if (!double.IsNaN(Canvas.GetLeft(source))) Canvas.SetLeft(host, Canvas.GetLeft(source));
            if (!double.IsNaN(Canvas.GetTop(source))) Canvas.SetTop(host, Canvas.GetTop(source));

            parent.Children.RemoveAt(index);
            source.Margin = new Thickness(0);
            Grid.SetRow(source, 0);
            Grid.SetColumn(source, 0);
            Grid.SetRowSpan(source, 1);
            Grid.SetColumnSpan(source, 1);
            Canvas.SetLeft(source, double.NaN);
            Canvas.SetTop(source, double.NaN);

            host.Children.Add(source);
            parent.Children.Insert(index, host);

            outline.Source = source;
            // 白字是本行为的主诉求：原先「保持主题色 + 白描边」在壁纸上偏暗，且和卡片正文分不开
            source.Foreground = TextBrush;
            foreach (var (dx, dy, alpha) in Shadow)
            {
                var clone = new TextBlock
                {
                    IsHitTestVisible = false,
                    IsTabStop = false,
                    Foreground = ShadowBrush,
                    Opacity = alpha,
                    RenderTransform = new TranslateTransform { X = dx, Y = dy },
                };
                Sync(outline, clone);
                host.Children.Insert(0, clone); // 副本一律垫在本体下面
                outline.Clones.Add(clone);
            }

            outline.TextToken = source.RegisterPropertyChangedCallback(
                TextBlock.TextProperty, (_, _) => SyncAll(outline));
            outline.FontToken = source.RegisterPropertyChangedCallback(
                TextBlock.FontSizeProperty, (_, _) => SyncAll(outline));
            outline.WeightToken = source.RegisterPropertyChangedCallback(
                TextBlock.FontWeightProperty, (_, _) => SyncAll(outline));
            outline.AlignToken = source.RegisterPropertyChangedCallback(
                FrameworkElement.HorizontalAlignmentProperty, (_, _) => SyncAll(outline));
            outline.VisibleToken = source.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, (_, _) => SyncVisibility(outline));

            outline.Wired = true;
            SyncVisibility(outline);
        }
        catch
        {
            // 布局树异常（例如元素正在被回收）：静默放弃，不影响页面本身
            outline.Wired = false;
        }
    }

    private static void SyncAll(Outline outline)
    {
        foreach (var clone in outline.Clones) Sync(outline, clone);
    }

    private static void Sync(Outline outline, TextBlock clone)
    {
        var s = outline.Source;
        clone.Text = s.Text;
        clone.FontFamily = s.FontFamily;
        clone.FontSize = s.FontSize;
        clone.FontStretch = s.FontStretch;
        clone.FontStyle = s.FontStyle;
        clone.FontWeight = s.FontWeight;
        clone.CharacterSpacing = s.CharacterSpacing;
        clone.LineHeight = s.LineHeight;
        clone.LineStackingStrategy = s.LineStackingStrategy;
        clone.MaxLines = s.MaxLines;
        clone.TextAlignment = s.TextAlignment;
        clone.TextTrimming = s.TextTrimming;
        clone.TextWrapping = s.TextWrapping;
        clone.HorizontalAlignment = s.HorizontalAlignment;
        clone.VerticalAlignment = s.VerticalAlignment;
        clone.Width = s.Width;
        clone.Height = s.Height;
        clone.MaxWidth = s.MaxWidth;
    }

    /// <summary>落影跟着本体一起显隐：本体收起来时，副本不能留在原地。</summary>
    private static void SyncVisibility(Outline outline)
    {
        foreach (var clone in outline.Clones) clone.Visibility = outline.Source.Visibility;
    }

    private sealed class Outline
    {
        public TextBlock Source = null!;
        public readonly List<TextBlock> Clones = [];
        public bool Wired;
        public long TextToken;
        public long FontToken;
        public long WeightToken;
        public long AlignToken;
        public long VisibleToken;
    }
}
