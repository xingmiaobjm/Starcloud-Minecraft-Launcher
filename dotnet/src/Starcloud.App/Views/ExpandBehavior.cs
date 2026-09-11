// 抽屉展开动效附加属性（大版本抽屉自绘实现：高度瞬时切换 + 合成线程友好的淡入位移）

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.App.Services;

namespace Starcloud.App.Views;

/// <summary>
/// 抽屉内容展开动效。
/// <para>
/// 与内置 <c>Expander</c> 的关键差别：高度由 <c>Visibility</c> <b>瞬时</b>切换，不做逐帧 Height 动画
/// —— Height 属依赖型动画（dependent animation），会触发 Layout 重排且无法跑在合成线程，故收尾容易掉帧。
/// 这里只对内容做 <c>Opacity</c> 淡入 + 轻微上移，两者都是可运行在合成线程上的<b>独立动画</b>（independent animation）。
/// </para>
/// <para>动效关闭（设置项或系统动画偏好）时直接瞬时显隐；任一环节失败静默降级，不影响功能。</para>
/// </summary>
public static class ExpandBehavior
{
    /// <summary>动画时长：足够短，展开感利落</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    /// <summary>展开时内容的起始上移量（像素）</summary>
    private const double SlideOffset = 10d;

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.RegisterAttached(
            "IsExpanded",
            typeof(bool),
            typeof(ExpandBehavior),
            new PropertyMetadata(false, OnIsExpandedChanged));

    public static bool GetIsExpanded(DependencyObject element) => (bool)element.GetValue(IsExpandedProperty);

    public static void SetIsExpanded(DependencyObject element, bool value) => element.SetValue(IsExpandedProperty, value);

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true) Expand(element);
        else Collapse(element);
    }

    /// <summary>展开：先瞬时显示（一次布局，不做 Height 动画），再对内容做淡入 + 上移到位。</summary>
    private static void Expand(FrameworkElement element)
    {
        element.Visibility = Visibility.Visible;

        var state = States.GetOrCreateValue(element);
        Stop(state);
        try
        {
            var transform = element.RenderTransform as TranslateTransform;

            // 动效关闭：直接到位
            if (!AppServices.Instance.Motion.Enabled)
            {
                element.Opacity = 1;
                if (transform is not null) transform.Y = 0;
                return;
            }

            element.Opacity = 0;
            if (transform is not null) transform.Y = -SlideOffset;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var storyboard = new Storyboard();

            var fade = new DoubleAnimation { To = 1, Duration = Duration, EasingFunction = ease };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            if (transform is not null)
            {
                var slide = new DoubleAnimation { To = 0, Duration = Duration, EasingFunction = ease };
                Storyboard.SetTarget(slide, element);
                Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
                storyboard.Children.Add(slide);
            }

            state.Storyboard = storyboard;
            storyboard.Begin();
        }
        catch
        {
            // 静默：即使动画失败也要保证内容可见
            element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform t) t.Y = 0;
        }
    }

    /// <summary>收起：瞬时折叠（不播放退出动画，避免收起瞬间的布局抖动）。</summary>
    private static void Collapse(FrameworkElement element)
    {
        if (States.TryGetValue(element, out var state)) Stop(state);
        element.Opacity = 1;
        if (element.RenderTransform is TranslateTransform transform) transform.Y = 0;
        element.Visibility = Visibility.Collapsed;
    }

    private static void Stop(State state)
    {
        try
        {
            state.Storyboard?.Stop();
        }
        catch
        {
            // 静默
        }
        state.Storyboard = null;
    }

    private sealed class State
    {
        public Storyboard? Storyboard;
    }
}
