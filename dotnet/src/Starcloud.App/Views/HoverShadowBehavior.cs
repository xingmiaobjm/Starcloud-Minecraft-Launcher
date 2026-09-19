// 悬浮阴影附加属性：鼠标移入时元素「浮起」、背后的阴影层渐显，移出归零。
//
// 实现取向（在 ThemeShadow / CompositionProjectedShadow / DropShadow 三种合成阴影都试过、
// 本机要么不渲染要么图层不对之后定的）：**阴影本身就是 XAML 元素**。
// 调用方在卡片背后放一层「阴影层」（几个同心半透明圆角矩形，靠 RenderTransform 缩放外扩、
// 不参与布局），本行为只做一件事——把这一层的合成 Opacity 在 0/1 之间做动画。
//
// 组件层只做「已知在本项目里稳定生效」的事：合成 Opacity/Translation 动画（界面里其它
// 淡入淡出、错峰入场、交叉过渡都用它，实测生效），不碰任何需要推断承接面的阴影 API。
//
// 动效关闭时直接到位（该显示就显示、该隐藏就隐藏），任一环节失败静默降级。

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;

namespace Starcloud.App.Views;

/// <summary>悬浮抬升阴影：悬停时背后的阴影层渐显、元素轻微上移，移出归零。</summary>
public static class HoverShadowBehavior
{
    /// <summary>悬停时元素的微量上移（px）</summary>
    private const float HoverLiftY = -1f;

    /// <summary>淡入要快：跨行时新的光斑立刻接上，避免出现"空白帧"</summary>
    private static readonly TimeSpan HoverIn = TimeSpan.FromMilliseconds(110);

    /// <summary>淡出要慢：旧行的光斑慢慢退，和新行交叠成一团连续的暗晕</summary>
    private static readonly TimeSpan HoverOut = TimeSpan.FromMilliseconds(260);

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    /// <summary>当前正在发光的光斑层：由窗口根元素的指针事件兜底清理，避免"卡住"</summary>
    private static readonly List<State> Glowing = [];
    private static bool _rootHooked;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(HoverShadowBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>阴影层元素（卡片背后那层）：悬停时它的 Opacity 由 0 渐显到 1</summary>
    public static readonly DependencyProperty CarrierProperty =
        DependencyProperty.RegisterAttached(
            "Carrier",
            typeof(FrameworkElement),
            typeof(HoverShadowBehavior),
            new PropertyMetadata(null, OnCarrierChanged));

    public static FrameworkElement? GetCarrier(DependencyObject element) =>
        (FrameworkElement?)element.GetValue(CarrierProperty);

    public static void SetCarrier(DependencyObject element, FrameworkElement? value) =>
        element.SetValue(CarrierProperty, value);

    /// <summary>载体是绑定来的，可能在 IsEnabled 之后才解析出值——解析到就再试一次接线</summary>
    private static void OnCarrierChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not FrameworkElement) return;
        if (!GetIsEnabled(fe)) return;
        Unwire(fe);
        Wire(fe);
    }

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
        if (GetCarrier(element) is not { } layer)
        {
            // 阴影层还没解析出来（绑定晚于 IsEnabled）：挂一次性 Loaded 兜底重试
            element.Loaded -= RetryOnLoaded;
            element.Loaded += RetryOnLoaded;
            return;
        }

        try
        {
            state.Visual = ElementCompositionPreview.GetElementVisual(element);
            state.LayerVisual = ElementCompositionPreview.GetElementVisual(layer);
            state.Compositor = state.Visual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            state.Layer = layer;
            state.LayerVisual.Opacity = 0f;   // 常态：阴影层不可见
            state.Brush = ResolveBrush(layer);
            ApplyLight(element, state, element.ActualWidth / 2, element.ActualHeight / 2);
            state.Element = element;
        }
        catch
        {
            state.Visual = null;
            state.LayerVisual = null;
            state.Compositor = null;
            return;
        }

        element.PointerEntered += OnPointerEntered;
        element.PointerMoved += OnPointerMoved;
        element.PointerExited += OnPointerExited;
        element.SizeChanged += OnSizeChanged;
        element.Unloaded += OnUnloaded;
        HookRootWatchdog(element);
        state.Wired = true;
    }

    /// <summary>
    /// 全局看门狗：光斑的淡出不能只依赖元素自己的 PointerExited——快速滑动、移到滚动条上、
    /// 指针直接移出窗口时这个事件不一定送达，那一层就留在原地"卡住"。
    /// 所以额外在窗口根元素上监听：指针一动就复核所有发光层，指针不在其范围内立即淡出；离开窗口全部淡出。
    /// </summary>
    private static void HookRootWatchdog(FrameworkElement element)
    {
        if (_rootHooked) return;
        try
        {
            if (element.XamlRoot?.Content is not FrameworkElement root) return;
            root.PointerMoved += OnRootPointerMoved;
            root.PointerExited += OnRootPointerExited;
            _rootHooked = true;
        }
        catch
        {
            // 静默：拿不到根元素就退回只靠元素自身的进出事件
        }
    }

    private static void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Glowing.Count == 0) return;
        foreach (var state in Glowing.ToList())
        {
            if (state.Element is not { } el) continue;
            try
            {
                var p = e.GetCurrentPoint(el).Position;
                var inside = p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
                if (!inside) FadeOut(state);
            }
            catch
            {
                // 静默：元素已被回收
            }
        }
    }

    private static void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        foreach (var state in Glowing.ToList()) FadeOut(state);
    }

    /// <summary>立刻让某一层淡出并摘出"发光中"列表</summary>
    private static void FadeOut(State state)
    {
        state.Hovered = false;
        Glowing.Remove(state);
        Animate(state, false, HoverOut);
    }

    private static void RetryOnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        fe.Loaded -= RetryOnLoaded;
        if (!GetIsEnabled(fe)) return;
        if (States.TryGetValue(fe, out var s) && s.Wired) return;
        Wire(fe);
    }

    private static void Unwire(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state) || !state.Wired) return;
        element.PointerEntered -= OnPointerEntered;
        element.PointerMoved -= OnPointerMoved;
        element.PointerExited -= OnPointerExited;
        element.SizeChanged -= OnSizeChanged;
        element.Unloaded -= OnUnloaded;
        state.Wired = false;
        ResetState(state);
    }

    /// <summary>阴影层不透明度 + 元素上移：都是合成动画，不触发布局</summary>
    private static void Animate(State state, bool hovered, TimeSpan duration)
    {
        try
        {
            if (state.Compositor is not { } compositor) return;
            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

            if (state.LayerVisual is { } layerVisual)
            {
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = duration;
                fade.InsertKeyFrame(1f, hovered ? 1f : 0f, ease);
                layerVisual.StartAnimation("Opacity", fade);
            }

            if (state.Visual is { } visual)
            {
                var lift = compositor.CreateVector3KeyFrameAnimation();
                lift.Duration = duration;
                lift.InsertKeyFrame(1f, hovered ? new Vector3(0f, HoverLiftY, 0f) : Vector3.Zero, ease);
                visual.StartAnimation("Translation", lift);
            }
        }
        catch
        {
            // 静默：动效失败不影响功能
        }
    }

    /// <summary>复位到常态（容器回收 / 卸载时调用）</summary>
    private static void ResetState(State state)
    {
        state.Hovered = false;
        Glowing.Remove(state);
        try
        {
            if (state.LayerVisual is { } layerVisual)
            {
                layerVisual.StopAnimation("Opacity");
                layerVisual.Opacity = 0f;
            }
            if (state.Visual is { } visual)
            {
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
        }
        catch
        {
            // 静默
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = true;
        if (!Glowing.Contains(s)) Glowing.Add(s);
        // 进入就按当前指针位置点亮：不沿用上一次的旧位置，否则会先闪一下再跳
        try
        {
            var p = e.GetCurrentPoint(fe).Position;
            ApplyLight(fe, s, p.X, p.Y);
        }
        catch
        {
            // 静默
        }
        Animate(s, true, HoverIn);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = false;
        Glowing.Remove(s);
        // 移出不复位光源：光斑留在最后位置淡出；复位会让它"跳回中心"，看着像抽搐
        Animate(s, false, HoverOut);
    }

    /// <summary>
    /// 阴影以光标为中心：径向渐变的中心与渐变原点跟着指针走，深色核心就在光标下、向外渐淡。
    /// 只改渐变刷的相对坐标（0~1），纯 XAML 属性更新，不涉及布局。
    /// </summary>
    private static void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        // 被看门狗提前淡出后又移回本元素（没有新的 Enter 事件）：补一次淡入
        if (!Glowing.Contains(s))
        {
            s.Hovered = true;
            Glowing.Add(s);
            Animate(s, true, HoverIn);
        }
        try
        {
            var p = e.GetCurrentPoint(fe).Position;
            ApplyLight(fe, s, p.X, p.Y);
        }
        catch
        {
            // 静默：光源跟随失败不影响内阴影本身
        }
    }

    /// <summary>
    /// 把光斑放到元素内的 (x, y)（像素坐标）。
    /// 关键：半径要按像素换算成相对值（RadiusX = r/w、RadiusY = r/h），
    /// 否则在又宽又扁的列表行上，0.9/0.9 的相对半径会拉成一个横向椭圆。
    /// </summary>
    private static void ApplyLight(FrameworkElement element, State state, double x, double y)
    {
        if (state.Brush is not { } brush) return;
        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 以短边为基准 → 像素上是正圆；系数放大到 1.15 让衰减铺得更开（羽化更柔）
        // 系数 1.35 + 下限 36px：衰减铺得更开，羽化更柔
        var radiusPx = Math.Max(36d, Math.Min(w, h) * 1.35);
        brush.RadiusX = radiusPx / w;
        brush.RadiusY = radiusPx / h;

        var center = new Windows.Foundation.Point(
            Math.Clamp(x / w, 0d, 1d), Math.Clamp(y / h, 0d, 1d));
        brush.Center = center;
        brush.GradientOrigin = center;
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
        public Visual? LayerVisual;
        public FrameworkElement? Layer;
        public FrameworkElement? Element;
        public RadialGradientBrush? Brush;
        public bool Wired;
        public bool Hovered;
    }

    /// <summary>取阴影层上那个径向渐变刷（卡片用 Border.Background，圆形头像用 Shape.Fill）</summary>
    private static RadialGradientBrush? ResolveBrush(FrameworkElement layer) => layer switch
    {
        Border border => border.Background as RadialGradientBrush,
        Microsoft.UI.Xaml.Shapes.Shape shape => shape.Fill as RadialGradientBrush,
        Panel panel => panel.Background as RadialGradientBrush,
        _ => null,
    };

    /// <summary>元素尺寸变化后按新尺寸重算半径（否则宽高比一变又会被拉成椭圆）</summary>
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        try
        {
            ApplyLight(fe, s, fe.ActualWidth / 2, fe.ActualHeight / 2);
        }
        catch
        {
            // 静默
        }
    }
}
