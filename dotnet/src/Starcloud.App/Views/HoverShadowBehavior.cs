// 悬浮抬升阴影附加属性：鼠标移入时元素「浮起」并在下方承接面上投出阴影，移出落回。
//
// 用合成层的 CompositionProjectedShadow（投影阴影）：阴影画在**承接面**（Receiver）上，
// 而不是画在元素自己身上——这是相对于 ThemeShadow / SetElementChildVisual 的关键差别，
// 后者在当前 WinUI 3 版本下要么不渲染（承接面推断不成立），要么会把阴影糊在元素内容之上。
//
// 承接面取法：优先用 Receiver 附加属性显式指定（顶栏按钮 → 标题栏），
// 未指定时向上找最近一个"有底色"的祖先（版本行 → 所在卡片）。
//
// 抬升高度走合成层 Translation（独立动画，不触发布局）；阴影不透明度同属合成动画。
// 动效关闭时完全不接线（零开销），任一环节失败静默放弃该元素。

using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;

namespace Starcloud.App.Views;

/// <summary>悬浮抬升阴影：悬停时沿 Z 轴抬起并在承接面上投出阴影，同时轻微上移 1px，移出归零。</summary>
public static class HoverShadowBehavior
{
    /// <summary>悬停抬升高度（px）：越大阴影偏移越明显</summary>
    private const float HoverLiftZ = 44f;

    /// <summary>悬停时的微量上移（px）：配合阴影一起读作"浮起"</summary>
    private const float HoverLiftY = -1f;

    /// <summary>模糊半径倍率（0~1）：越大越散</summary>
    private const float ShadowBlurMultiplier = 0.72f;

    private static readonly TimeSpan HoverIn = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan HoverOut = TimeSpan.FromMilliseconds(140);

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

    /// <summary>阴影承接元素（阴影画在它上面）；不指定时向上找最近的有底色祖先</summary>
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
            var casterVisual = ElementCompositionPreview.GetElementVisual(element);
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            state.Visual = casterVisual;
            state.Compositor = casterVisual.Compositor;

            var receiverElement = ResolveReceiver(element);
            if (receiverElement is null) return;              // 没有承接面：宁可不投影，也不糊在元素上
            var receiverVisual = ElementCompositionPreview.GetElementVisual(receiverElement);

            var shadow = state.Compositor!.CreateProjectedShadow();
            shadow.BlurRadiusMultiplier = ShadowBlurMultiplier;
            // 阴影方向由光源决定：给一个从左上打来的远景光，阴影自然落在右下
            var light = state.Compositor.CreateDistantLight();
            light.Direction = new Vector3(0.35f, 0.55f, -1f);
            light.CoordinateSpace = receiverVisual;
            shadow.LightSource = light;

            var caster = state.Compositor.CreateProjectedShadowCaster();
            caster.CastingVisual = casterVisual;
            var receiver = state.Compositor.CreateProjectedShadowReceiver();
            receiver.ReceivingVisual = receiverVisual;
            shadow.Casters.InsertAtTop(caster);
            shadow.Receivers.Add(receiver);

            state.Shadow = shadow;
            state.Light = light;
            state.Caster = caster;
            state.Receiver = receiver;
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
        ResetState(state);
        state.Shadow = null;
        state.Light = null;
        state.Caster = null;
        state.Receiver = null;
    }

    /// <summary>
    /// 承接面：显式 Receiver → 最近的有可见底色的祖先 → 一路向上一路记，最后取窗口内容根。
    /// 取根元素是因为内容区（下载页等）的容器多半没有自己的底色，页底其实是窗口合成背景；
    /// 把阴影投在根元素的合成面上，视觉上正好落在元素「身后」。
    /// </summary>
    private static FrameworkElement? ResolveReceiver(FrameworkElement element)
    {
        if (GetReceiver(element) is { } explicitReceiver) return explicitReceiver;
        try
        {
            FrameworkElement? lastAncestor = null;
            var current = VisualTreeHelper.GetParent(element) as FrameworkElement;
            for (var depth = 0; current is not null && depth < MaxReceiverSearchDepth; depth++)
            {
                if (HasPaintedBackground(current)) return current;    // 有可见底色 = 能接住阴影
                lastAncestor = current;
                current = VisualTreeHelper.GetParent(current) as FrameworkElement;
            }
            return lastAncestor;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>该元素是否涂了「可见」底色（透明底会被投影裁掉，不能当承接面）</summary>
    private static bool HasPaintedBackground(FrameworkElement element)
    {
        var brush = element switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            Control control => control.Background,
            _ => null,
        };
        if (brush is null || brush.Opacity <= 0) return false;
        if (brush is SolidColorBrush solid) return solid.Color.A > 0;
        return true;   // 亚克力 / 渐变 / 主题刷：按可见处理
    }

    /// <summary>复位到常态（容器回收 / 卸载时调用，避免残留抬升与阴影）</summary>
    private static void ResetState(State state)
    {
        state.Hovered = false;
        try
        {
            if (state.Visual is { } visual)
            {
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
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
            anim.InsertKeyFrame(1f, target, Ease(compositor));
            visual.StartAnimation("Translation", anim);
        }
        catch
        {
            // 静默
        }
    }

    private static CompositionEasingFunction Ease(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

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
        public Compositor? Compositor;
        public Visual? Visual;
        public CompositionProjectedShadow? Shadow;
        public DistantLight? Light;
        public CompositionProjectedShadowCaster? Caster;
        public CompositionProjectedShadowReceiver? Receiver;
        public bool Wired;
        public bool Hovered;
    }
}
