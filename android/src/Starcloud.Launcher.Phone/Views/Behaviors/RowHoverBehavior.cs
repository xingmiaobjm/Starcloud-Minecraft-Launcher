// 列表行悬浮反馈（版本列表）：悬停上浮 + 高亮渐显、按下下压

using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views.Behaviors;

/// <summary>
/// 行悬浮反馈：悬停时行轻微上浮并渐显一层高亮覆盖层，按下时轻微下压。
/// <para>两者都是<b>独立动画</b>（independent animation），跑在合成线程上、不触发布局：
/// 位移走 <c>RenderTransform</c> 的 <c>TranslateTransform.Y</c>（不用 <c>Visual.Offset</c>，
/// 那是布局属性会被下一次 Layout 重置；也不用 <c>Scale</c>，行放大会溢出卡片圆角边界），
/// 高亮走覆盖层的合成 <c>Opacity</c>。</para>
/// <para>高亮层由 <see cref="OverlayProperty"/> 指定：一层铺满行、颜色取自主题资源的元素，
/// 常态 <c>Opacity=0</c>，因此深/浅色主题自动跟随，行本身的底色不受影响。</para>
/// <para>界面动效关闭时完全不接线（零开销）；任一环节失败静默放弃该元素。</para>
/// </summary>
public static class RowHoverBehavior
{
    private const double HoverLift = -2d;  // 悬停：上浮 2px
    private const double PressedDrop = 1d; // 按下：下压 1px

    private static readonly TimeSpan HoverIn = TimeSpan.FromMilliseconds(MotionFx.HoverFadeInMs);
    private static readonly TimeSpan HoverOut = TimeSpan.FromMilliseconds(MotionFx.HoverFadeOutMs);
    private static readonly TimeSpan PressIn = TimeSpan.FromMilliseconds(100);

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(RowHoverBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>悬停高亮层（铺满行的元素），悬停时 Opacity 0 → 1</summary>
    public static readonly DependencyProperty OverlayProperty =
        DependencyProperty.RegisterAttached(
            "Overlay",
            typeof(FrameworkElement),
            typeof(RowHoverBehavior),
            new PropertyMetadata(null));

    public static FrameworkElement? GetOverlay(DependencyObject element) =>
        (FrameworkElement?)element.GetValue(OverlayProperty);

    public static void SetOverlay(DependencyObject element, FrameworkElement? value) =>
        element.SetValue(OverlayProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (e.NewValue is true) Wire(fe);
        else Unwire(fe);
    }

    private static void Wire(FrameworkElement element)
    {
        var state = States.GetOrCreateValue(element);
        if (state.Wired) return;
        if (!AppServices.Instance.Motion.Enabled) return; // 动效关闭：零开销

        try
        {
            state.Compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        }
        catch
        {
            state.Compositor = null;
            return;
        }

        element.PointerEntered += OnPointerEntered;
        element.PointerExited += OnPointerExited;
        element.PointerPressed += OnPointerPressed;
        element.PointerReleased += OnPointerReleased;
        element.PointerCaptureLost += OnPointerCaptureLost;
        element.Unloaded += OnUnloaded;
        state.Wired = true;
    }

    private static void Unwire(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state) || !state.Wired) return;

        element.PointerEntered -= OnPointerEntered;
        element.PointerExited -= OnPointerExited;
        element.PointerPressed -= OnPointerPressed;
        element.PointerReleased -= OnPointerReleased;
        element.PointerCaptureLost -= OnPointerCaptureLost;
        element.Unloaded -= OnUnloaded;
        state.Wired = false;
        ResetState(element, state);
    }

    /// <summary>复位到常态（容器回收 / 卸载时调用，避免残留位移与高亮）。</summary>
    private static void ResetState(FrameworkElement element, State state)
    {
        state.Hovered = false;
        state.Pressed = false;
        try
        {
            state.Lift?.Stop();
            state.Lift = null;
            if (state.LiftTransform is { } transform) transform.Y = 0;

            if (state.OverlayVisual is { } overlay)
            {
                overlay.StopAnimation("Opacity");
                overlay.Opacity = 0f;
            }
        }
        catch
        {
            // 静默：无动效不影响功能
        }
    }

    /// <summary>上浮 / 回位 / 下压：动画 <c>TranslateTransform.Y</c>（独立动画，不触发布局）</summary>
    private static void AnimateLift(State state, FrameworkElement element, double lift, TimeSpan duration)
    {
        try
        {
            if (state.Compositor is null || EnsureTransform(element, state) is not { } transform) return;

            var from = transform.Y; // 从当前视觉位置接着走，快速来回移动时不会跳
            state.Lift?.Stop();
            transform.Y = 0; // 基准值保持 0：视觉位置完全由动画的 From/To 决定

            // 与高亮层、阴影层同一条曲线、同一时长：行悬停是"亮度 + 微位移"的交叉过渡，
            // 用匀称的 FadeEase；进出场样条只留给内容进出场。
            var slide = new DoubleAnimationUsingKeyFrames { Duration = duration };
            slide.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value = from,
            });
            slide.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(duration),
                Value = lift,
                KeySpline = MotionFx.CreateFadeSpline(),
            });
            var storyboard = new Storyboard();
            Storyboard.SetTarget(slide, element);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
            storyboard.Children.Add(slide);

            state.Lift = storyboard;
            storyboard.Begin();
        }
        catch
        {
            // 静默：无动效不影响功能
        }
    }

    /// <summary>取得（必要时创建）承载位移的 <c>TranslateTransform</c>；已有其他变换时放弃，不覆盖。</summary>
    private static TranslateTransform? EnsureTransform(FrameworkElement element, State state)
    {
        if (state.LiftTransform is { } cached) return cached;
        if (element.RenderTransform is TranslateTransform existing)
        {
            state.LiftTransform = existing;
            return existing;
        }
        if (element.RenderTransform is not null) return null;

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        state.LiftTransform = transform;
        return transform;
    }

    /// <summary>高亮层渐显 / 渐隐：动画覆盖层的合成 <c>Opacity</c></summary>
    private static void AnimateOverlay(FrameworkElement element, State state, float opacity, TimeSpan duration)
    {
        try
        {
            var overlay = GetOverlay(element);
            if (overlay is null) return;
            if (!ReferenceEquals(state.Overlay, overlay))
            {
                state.Overlay = overlay;
                state.OverlayVisual = ElementCompositionPreview.GetElementVisual(overlay);
            }
            if (state.OverlayVisual is not { } overlayVisual || state.Compositor is not { } compositor) return;

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = duration;
            anim.InsertKeyFrame(1f, opacity, Ease(compositor));
            overlayVisual.StartAnimation("Opacity", anim);
        }
        catch
        {
            // 静默：无动效不影响功能
        }
    }

    /// <summary>高亮层淡入淡出：与阴影、位移同一条匀称曲线</summary>
    private static CompositionEasingFunction Ease(Compositor compositor) => MotionFx.FadeEase(compositor);

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = true;
        if (s.Pressed) return;
        AnimateLift(s, fe, HoverLift, HoverIn);
        AnimateOverlay(fe, s, 1f, HoverIn);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = false;
        s.Pressed = false;
        AnimateLift(s, fe, 0d, HoverOut);
        AnimateOverlay(fe, s, 0f, HoverOut);
    }

    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Pressed = true;
        AnimateLift(s, fe, PressedDrop, PressIn);
        AnimateOverlay(fe, s, 1f, PressIn);
    }

    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Pressed = false;
        ApplyHover(s, fe);
    }

    private static void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Pressed = false;
        ApplyHover(s, fe);
    }

    private static void ApplyHover(State state, FrameworkElement element)
    {
        AnimateLift(state, element, state.Hovered ? HoverLift : 0d, HoverOut);
        AnimateOverlay(element, state, state.Hovered ? 1f : 0f, HoverOut);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        ResetState(fe, s);
    }

    private sealed class State
    {
        public Compositor? Compositor;
        public TranslateTransform? LiftTransform;
        public Storyboard? Lift;
        public FrameworkElement? Overlay;
        public Visual? OverlayVisual;
        public bool Wired;
        public bool Hovered;
        public bool Pressed;
    }
}
