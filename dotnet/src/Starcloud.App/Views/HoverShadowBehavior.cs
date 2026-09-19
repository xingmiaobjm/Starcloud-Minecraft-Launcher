// 悬浮抬升阴影附加属性：鼠标移入时元素「浮起」并投出阴影，移出落回。
//
// 用框架 ThemeShadow —— 阴影形状跟随元素自身的实际内容（圆角卡片、透明底按钮都能正确投），
// 且由框架负责图层关系（不会盖住元素内容，这是合成 DropShadow 做不到的）。抬升高度走
// 合成层 Translation（独立动画，不触发布局、不覆盖 RenderTransform）。
//
// ThemeShadow 需要指定「承接阴影的元素」（否则不渲染）：优先用 Receiver 附加属性显式指定，
// 未指定时向上找最近的若干祖先元素作为承接面（列表行落在所在卡片上）。
//
// 只给「需要显得可点、可抬起」的元素用（顶栏透明图标按钮、版本列表行边界等）；
// 动效关闭时完全不接线（零开销），任一环节失败静默放弃该元素。

using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;

namespace Starcloud.App.Views;

/// <summary>
/// 悬浮抬升阴影：悬停时沿 Z 轴抬起并在下方投出阴影（同时轻微上移 1px，强化"浮起"手感），
/// 移出归零。只改 Z 与微量 Y，不动 X，也不会造成列表滚动错位。
/// </summary>
public static class HoverShadowBehavior
{
    /// <summary>悬停抬升高度（px）：越大阴影越散越明显</summary>
    private const float HoverLiftZ = 32f;

    /// <summary>悬停时的微量上移（px）：配合阴影一起读作"浮起"</summary>
    private const float HoverLiftY = -1f;

    private static readonly TimeSpan HoverIn = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan HoverOut = TimeSpan.FromMilliseconds(140);

    /// <summary>未显式指定承接面时，向上查找的最大层数</summary>
    private const int MaxReceiverSearchDepth = 8;

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(HoverShadowBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>阴影承接元素：阴影投在它上面（不指定时向上找祖先）</summary>
    public static readonly DependencyProperty ReceiverProperty =
        DependencyProperty.RegisterAttached(
            "Receiver",
            typeof(FrameworkElement),
            typeof(HoverShadowBehavior),
            new PropertyMetadata(null));

    public static FrameworkElement? GetReceiver(DependencyObject element) =>
        (FrameworkElement?)element.GetValue(ReceiverProperty);

    public static void SetReceiver(DependencyObject element, FrameworkElement? value) =>
        element.SetValue(ReceiverProperty, value);

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
            var visual = ElementCompositionPreview.GetElementVisual(element);
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            state.Visual = visual;
            state.Compositor = visual.Compositor;

            var shadow = new ThemeShadow();
            if (GetReceiver(element) is { } explicitReceiver) shadow.Receivers.Add(explicitReceiver);
            else foreach (var ancestor in FindReceivers(element)) shadow.Receivers.Add(ancestor);
            element.Shadow = shadow;
        }
        catch
        {
            state.Visual = null;
            state.Compositor = null;
            return;
        }

        element.PointerEntered += OnPointerEntered;
        element.PointerExited += OnPointerExited;
        element.Unloaded += OnUnloaded;
        state.Wired = true;
    }

    private static void Unwire(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state) || !state.Wired) return;
        element.PointerEntered -= OnPointerEntered;
        element.PointerExited -= OnPointerExited;
        element.Unloaded -= OnUnloaded;
        state.Wired = false;
        element.Shadow = null;
        ResetState(state);
    }

    /// <summary>向上找承接阴影的祖先（列表行落在所在卡片/面板上）；失败时返回空集合，交由框架推断</summary>
    private static List<FrameworkElement> FindReceivers(FrameworkElement element)
    {
        var found = new List<FrameworkElement>();
        try
        {
            var current = VisualTreeHelper.GetParent(element) as FrameworkElement;
            for (var depth = 0; current is not null && depth < MaxReceiverSearchDepth; depth++)
            {
                found.Add(current);
                current = VisualTreeHelper.GetParent(current) as FrameworkElement;
            }
        }
        catch
        {
            // 静默：拿不到祖先就不指定承接面
        }
        return found;
    }

    /// <summary>复位到常态（容器回收 / 卸载时调用，避免残留抬升）</summary>
    private static void ResetState(State state)
    {
        state.Hovered = false;
        try
        {
            if (state.Visual is not { } visual) return;
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch
        {
            // 静默：无动效不影响功能
        }
    }

    private static void AnimateLift(State state, bool hovered, TimeSpan duration)
    {
        try
        {
            if (state.Visual is not { } visual || state.Compositor is not { } compositor) return;

            var target = hovered ? new Vector3(0f, HoverLiftY, HoverLiftZ) : Vector3.Zero;
            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.Duration = duration;
            anim.InsertKeyFrame(1f, target,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f)));
            visual.StartAnimation("Translation", anim);
        }
        catch
        {
            // 静默：无动效不影响功能
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = true;
        AnimateLift(s, true, HoverIn);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = false;
        AnimateLift(s, false, HoverOut);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        ResetState(s);
    }

    private sealed class State
    {
        public Visual? Visual;
        public Compositor? Compositor;
        public bool Wired;
        public bool Hovered;
    }
}
