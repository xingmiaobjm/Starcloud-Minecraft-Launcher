// 浅色主题下，直接落在页面背景（自定义壁纸 / 毛玻璃，没有纯色底）上的文字加一圈 1px 白色描边，
// 保证黑字在任何壁纸上都读得清。
//
// 为什么是「白字副本」而不是阴影 / 描边 API：WinUI 3 的 TextBlock 没有文字阴影或描边属性；
// ThemeShadow 只能是深色、且按元素矩形投影；把合成 DropShadow 挂到 XAML 元素视觉上在本项目里
// 也不渲染（HoverShadowBehavior 顶部记着这三种都试过的结论）。所以用最朴素的画法：
// 同一个 TextBlock 的白字副本，四正向各偏 1px、四对角各偏 (1,1)px，围出一圈 1px 描边。
//
// 与「多层偏移糊一圈柔光」的区别：这里是**单一 1px 环 + 低透明度**（正向 18%、对角 12%），
// 偏移是整像素、层数固定，所以字形边缘不会被自己的副本糊成重影——看着就是一圈很淡的白边。
//
// 生效范围：浅色主题（深色主题文字本身是浅色，不需要白边）。
// 用法（两种）：
//   · 挂在 TextBlock 上：只给这一处文字加描边；
//   · 挂在容器 Panel 上：子树里所有 TextBlock 都加（页面抬头这种整块落在背景上的用这个）。
// 前提：目标文字在 Panel 里（要把它挪进一层包装 Grid）。不在 Panel 里则静默跳过。

using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Starcloud.App.Views.Behaviors;

/// <summary>浅色主题下给页面背景上的文字加 1px 白色描边（实现见文件头注释）。</summary>
public static class TextOutlineBehavior
{
    /// <summary>
    /// 1px 描边环：四正向 18%、四对角 12%（对角距离 1.41px，压淡一点才和正向边缘接得平）。
    /// </summary>
    private static readonly (double Dx, double Dy, double Alpha)[] Ring =
    [
        (1, 0, 0.18), (-1, 0, 0.18), (0, 1, 0.18), (0, -1, 0.18),
        (1, 1, 0.12), (-1, 1, 0.12), (1, -1, 0.12), (-1, -1, 0.12),
    ];

    private static readonly ConditionalWeakTable<TextBlock, Outline> States = new();
    private static readonly Brush OutlineBrush = new SolidColorBrush(Colors.White);

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
            foreach (var (dx, dy, alpha) in Ring)
            {
                var clone = new TextBlock
                {
                    IsHitTestVisible = false,
                    IsTabStop = false,
                    Foreground = OutlineBrush,
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
                UIElement.VisibilityProperty, (_, _) => ApplyTheme(outline));
            source.ActualThemeChanged += outline.OnThemeChanged;

            outline.Wired = true;
            ApplyTheme(outline);
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

    /// <summary>描边只在浅色主题显示：深色主题文字本身是浅色，白边只会糊</summary>
    private static void ApplyTheme(Outline outline)
    {
        var light = outline.Source.ActualTheme != ElementTheme.Dark;
        var visibility = light ? outline.Source.Visibility : Visibility.Collapsed;
        foreach (var clone in outline.Clones) clone.Visibility = visibility;
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

        public void OnThemeChanged(FrameworkElement sender, object args) => ApplyTheme(this);
    }
}
