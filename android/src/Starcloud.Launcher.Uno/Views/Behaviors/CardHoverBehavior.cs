// 卡片悬浮 / 按压反馈附加属性（D 组 ②：界面打磨与动效，设计 §6）

using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Starcloud.Launcher.Uno.Services;

namespace Starcloud.Launcher.Uno.Views.Behaviors;

/// <summary>
/// 卡片悬浮反馈：悬浮轻微放大、按下轻微缩小。
/// <para>用 Composition 显式动画驱动 <c>Scale</c>（刻意不动 <c>Offset</c>，避免覆盖 XAML 布局结果导致列表滚动错位）。</para>
/// <para>界面动效关闭时完全不接线（零开销）；任一环节失败静默放弃该元素。</para>
/// </summary>
public static class CardHoverBehavior
{
    private const float HoverScale = 1.012f;
    private const float PressedScale = 0.985f;
    /// <summary>
    /// 120ms 配"起步极快"的曲线会变成一次跳变，看着生硬。
    /// 拉长到 Fluent Normal 档（200ms）并换成匀称曲线，放大才是个能被看见的过程。
    /// </summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(MotionFx.HoverFadeInMs);

    /// <summary>按压反馈要比悬停更快：按下去必须立刻有反应，等 200ms 就成了"没反应"</summary>
    private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(100);

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(CardHoverBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

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
            state.Visual = ElementCompositionPreview.GetElementVisual(element);
            state.Compositor = state.Visual.Compositor;
        }
        catch
        {
            state.Visual = null;
            state.Compositor = null;
            return;
        }

        element.PointerEntered += OnPointerEntered;
        element.PointerExited += OnPointerExited;
        element.PointerPressed += OnPointerPressed;
        element.PointerReleased += OnPointerReleased;
        element.PointerCaptureLost += OnPointerCaptureLost;
        element.SizeChanged += OnSizeChanged;
        element.Loaded += OnLoaded;
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
        element.SizeChanged -= OnSizeChanged;
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        state.Wired = false;
        ResetState(element, state);
    }

    /// <summary>复位到常态（容器回收 / 卸载时调用，避免残留缩放）。</summary>
    private static void ResetState(FrameworkElement element, State state)
    {
        state.Hovered = false;
        state.Pressed = false;
        try
        {
            if (state.Visual is not { } visual) return;
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            UpdateCenter(element, visual);
        }
        catch
        {
            // 静默：无动效不影响功能
        }
    }

    private static void Animate(FrameworkElement element, State state, float scale)
        => Animate(element, state, scale, Duration);

    private static void Animate(FrameworkElement element, State state, float scale, TimeSpan duration)
    {
        try
        {
            if (state.Visual is not { } visual || state.Compositor is not { } compositor) return;
            UpdateCenter(element, visual);
            var easing = MotionFx.FadeEase(compositor);

            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.Duration = duration;
            scaleAnim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), easing);
            visual.StartAnimation("Scale", scaleAnim);
        }
        catch
        {
            // 静默：无动效不影响功能
        }
    }

    private static void UpdateCenter(FrameworkElement element, Visual visual)
    {
        var w = (float)element.ActualWidth;
        var h = (float)element.ActualHeight;
        if (w <= 0 || h <= 0) return;
        visual.CenterPoint = new Vector3(w / 2f, h / 2f, 0f);
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = true;
        if (!s.Pressed) Animate(fe, s, HoverScale);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = false;
        s.Pressed = false;
        Animate(fe, s, 1f);
    }

    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Pressed = true;
        Animate(fe, s, PressedScale, PressDuration);
    }

    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Pressed = false;
        Animate(fe, s, s.Hovered ? HoverScale : 1f, PressDuration);
    }

    private static void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Pressed = false;
        Animate(fe, s, s.Hovered ? HoverScale : 1f, PressDuration);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        if (s.Visual is { } visual) UpdateCenter(fe, visual);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        try
        {
            if (s.Visual is null)
            {
                s.Visual = ElementCompositionPreview.GetElementVisual(fe);
                s.Compositor = s.Visual.Compositor;
            }
            UpdateCenter(fe, s.Visual);
        }
        catch
        {
            // 静默
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        ResetState(fe, s);
    }

    private sealed class State
    {
        public Visual? Visual;
        public Compositor? Compositor;
        public bool Wired;
        public bool Hovered;
        public bool Pressed;
    }
}
