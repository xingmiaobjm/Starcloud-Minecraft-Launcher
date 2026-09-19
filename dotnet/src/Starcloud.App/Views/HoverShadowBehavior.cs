// 跟随光标的"内阴影"附加属性。两种用法：
//
// ① 单体（按钮 / 瓦片）：元素自己跟踪指针，自己的阴影层画一个以光标为圆心的径向光斑。
// ② 分组（版本列表这类由很多卡片拼成的区域）：由祖先元素声明 IsGroupHost="True" 统一跟踪指针，
//    每个卡片用同一条"全局光标位置"算出自己该画的那一段——于是光斑在卡片之间看起来是连续的，
//    却只落在卡片内部，不会沾到卡片之间的背景与缝隙。
//
// 阴影层始终是 XAML 元素（径向渐变刷），行为只改两件事：图层的合成不透明度、渐变刷的中心与半径。
// 半径按像素换算成相对值（RadiusX = r/w、RadiusY = r/h），因此任何宽高比下都是正圆。
// 动效关闭时直接到位；任一环节失败静默降级。

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

/// <summary>跟随光标的卡片内阴影。</summary>
public static class HoverShadowBehavior
{
    /// <summary>从属卡片的光斑半径（px）：小一点才有"一束光"的手感</summary>
    private const double CardRadiusPx = 90d;

    /// <summary>单体元素的光斑半径下限（px）</summary>
    private const double MinRadiusPx = 36d;

    private static readonly TimeSpan HoverIn = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan HoverOut = TimeSpan.FromMilliseconds(260);

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();
    private static readonly ConditionalWeakTable<FrameworkElement, GroupHost> Hosts = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(HoverShadowBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>阴影层元素：径向渐变刷的那一层</summary>
    public static readonly DependencyProperty CarrierProperty =
        DependencyProperty.RegisterAttached(
            "Carrier", typeof(FrameworkElement), typeof(HoverShadowBehavior),
            new PropertyMetadata(null, OnCarrierChanged));

    public static FrameworkElement? GetCarrier(DependencyObject element) => (FrameworkElement?)element.GetValue(CarrierProperty);
    public static void SetCarrier(DependencyObject element, FrameworkElement? value) => element.SetValue(CarrierProperty, value);

    /// <summary>把该元素标成"分组宿主"：由它统一跟踪指针，并驱动所有从属卡片的光斑</summary>
    public static readonly DependencyProperty IsGroupHostProperty =
        DependencyProperty.RegisterAttached(
            "IsGroupHost", typeof(bool), typeof(HoverShadowBehavior),
            new PropertyMetadata(false, OnCarrierChanged));

    public static bool GetIsGroupHost(DependencyObject element) => (bool)element.GetValue(IsGroupHostProperty);
    public static void SetIsGroupHost(DependencyObject element, bool value) => element.SetValue(IsGroupHostProperty, value);

    /// <summary>光斑半径（px）。0 = 自动（短边 ×1.35）</summary>
    public static readonly DependencyProperty RadiusPxProperty =
        DependencyProperty.RegisterAttached(
            "RadiusPx", typeof(double), typeof(HoverShadowBehavior), new PropertyMetadata(0d));

    public static double GetRadiusPx(DependencyObject element) => (double)element.GetValue(RadiusPxProperty);
    public static void SetRadiusPx(DependencyObject element, double value) => element.SetValue(RadiusPxProperty, value);

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
        if (!AppServices.Instance.Motion.Enabled) return;

        // 分组宿主：自己不需要阴影层，只负责跟踪指针
        if (GetIsGroupHost(element))
        {
            var host = Hosts.GetOrCreateValue(element);
            host.Element = element;
            element.PointerEntered += (s, e) => UpdateHostPointer(host, e);
            element.PointerMoved += (s, e) => UpdateHostPointer(host, e);
            element.PointerExited += (_, _) => FadeGroup(host, false);
            element.Unloaded += (_, _) => FadeGroup(host, false);
            state.Wired = true;
            state.IsHost = true;
            return;
        }

        if (GetCarrier(element) is not { } layer)
        {
            element.Loaded -= RetryOnLoaded;
            element.Loaded += RetryOnLoaded;
            return;
        }

        try
        {
            state.Visual = ElementCompositionPreview.GetElementVisual(element);
            state.LayerVisual = ElementCompositionPreview.GetElementVisual(layer);
            state.Compositor = state.Visual.Compositor;
            state.Layer = layer;
            state.LayerVisual.Opacity = 0f;
            state.RadiusPx = GetRadiusPx(element);
            state.Radial = ResolveBrush(layer);
            state.Host = FindHost(element);

            if (state.Host is null && state.Radial is not null)
                ApplyLight(element, state, element.ActualWidth / 2, element.ActualHeight / 2);
            else if (state.Host is not null)
            {
                var group = Hosts.GetOrCreateValue(state.Host);
                group.Element = state.Host;
                if (!group.Cards.Contains(state)) group.Cards.Add(state);
                // 组正处在悬停态（例如刚展开分组、行刚被创建出来）：立刻补上光斑，不必等下一次鼠标移动
                if (group.Active) RefreshGroup(group);
            }
        }
        catch
        {
            state.Visual = null;
            state.LayerVisual = null;
            state.Compositor = null;
            return;
        }

        // 指针事件一律挂在卡片自己身上（列表行的悬停事件是可靠的）；
        // 找到宿主时由宿主位置驱动整组，找不到就退化成单体，不影响这一张卡片亮起来
        element.PointerEntered += OnPointerEntered;
        element.PointerMoved += OnPointerMoved;
        if (state.Host is null) element.PointerExited += OnPointerExited;
        element.Unloaded += OnUnloaded;
        state.Wired = true;
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
        element.Unloaded -= OnUnloaded;
        if (state.Host is { } host && Hosts.TryGetValue(host, out var group)) group.Cards.Remove(state);
        state.Wired = false;
        ResetState(state);
    }

    /// <summary>向上找分组宿主（版本列表这类"很多卡片拼成一片"的容器）</summary>
    private static FrameworkElement? FindHost(FrameworkElement element)
    {
        try
        {
            var current = VisualTreeHelper.GetParent(element) as FrameworkElement;
            for (var depth = 0; current is not null && depth < 12; depth++)
            {
                if (GetIsGroupHost(current)) return current;
                current = VisualTreeHelper.GetParent(current) as FrameworkElement;
            }
        }
        catch
        {
            // 静默
        }
        return null;
    }

    /// <summary>宿主收到指针移动：记录全局位置并让所有从属卡片各自画自己那一段</summary>
    private static void UpdateHostPointer(GroupHost host, PointerRoutedEventArgs e)
    {
        if (host.Element is not { } hostElement) return;
        try
        {
            host.Point = e.GetCurrentPoint(hostElement).Position;
            host.Active = true;
        }
        catch
        {
            return;
        }
        RefreshGroup(host);
    }

    /// <summary>卡片上的指针事件：把位置换算到宿主坐标系后驱动整组（没宿主就只管自己）</summary>
    private static void UpdateFromCard(FrameworkElement card, State state, PointerRoutedEventArgs e)
    {
        var host = EnsureHost(card, state);
        if (host?.Element is not { } hostElement)
        {
            ApplyLocalLight(card, state, e);
            Animate(state, true, HoverIn);
            return;
        }

        try
        {
            host.Point = e.GetCurrentPoint(hostElement).Position;
            host.Active = true;
        }
        catch
        {
            return;
        }
        RefreshGroup(host);
    }

    /// <summary>按宿主的全局位置刷新所有从属卡片：各自算自己那一段光斑并淡入</summary>
    private static void RefreshGroup(GroupHost host)
    {
        if (host.Element is not { } hostElement) return;
        foreach (var card in host.Cards.ToList())
        {
            if (!card.Wired || card.Layer is null) continue;
            try
            {
                var local = hostElement.TransformToVisual(card.Layer).TransformPoint(host.Point);
                ApplyLight(card.Layer, card, local.X, local.Y);
            }
            catch
            {
                // 静默：虚拟化回收中
            }
            Animate(card, true, HoverIn);
        }
    }

    /// <summary>懒解析宿主：行是先建好元素、后进可视树的，所以每次指针事件都确认一次</summary>
    private static GroupHost? EnsureHost(FrameworkElement card, State state)
    {
        if (state.Host is null) state.Host = FindHost(card);
        if (state.Host is null) return null;
        var host = Hosts.GetOrCreateValue(state.Host);
        host.Element = state.Host;
        if (!host.Cards.Contains(state)) host.Cards.Add(state);
        return host;
    }

    private static void ApplyLocalLight(FrameworkElement element, State state, PointerRoutedEventArgs e)
    {
        try
        {
            var p = e.GetCurrentPoint(element).Position;
            ApplyLight(element, state, p.X, p.Y);
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>整组淡入 / 淡出，并按全局位置刷新每张卡片的局部光斑</summary>
    private static void FadeGroup(GroupHost host, bool visible)
    {
        host.Active = visible;
        foreach (var card in host.Cards.ToList())
        {
            if (!card.Wired) continue;
            Animate(card, visible, visible ? HoverIn : HoverOut);
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = true;
        UpdateFromCard(fe, s, e);
    }

    private static void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        UpdateFromCard(fe, s, e);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = false;
        Animate(s, false, HoverOut);   // 不复位光源：留在最后位置淡出，避免"跳回中心"的抽搐
    }

    /// <summary>把光斑放到 (x, y)（该元素坐标系内的像素值），半径按像素换算成相对值</summary>
    private static void ApplyLight(FrameworkElement element, State state, double x, double y)
    {
        if (state.Radial is not { } brush) return;
        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var radiusPx = state.Host is not null
            ? (state.RadiusPx > 0 ? state.RadiusPx : CardRadiusPx)
            : state.RadiusPx > 0 ? state.RadiusPx : Math.Max(MinRadiusPx, Math.Min(w, h) * 1.35);

        brush.RadiusX = radiusPx / w;
        brush.RadiusY = radiusPx / h;
        var center = new Windows.Foundation.Point(
            Math.Clamp(x / w, 0d, 1d), Math.Clamp(y / h, 0d, 1d));
        brush.Center = center;
        brush.GradientOrigin = center;
    }

    private static void Animate(State state, bool visible, TimeSpan duration)
    {
        try
        {
            if (state.LayerVisual is not { } layerVisual || state.Compositor is not { } compositor) return;
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = duration;
            fade.InsertKeyFrame(1f, visible ? 1f : 0f,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f)));
            layerVisual.StartAnimation("Opacity", fade);
        }
        catch
        {
            // 静默
        }
    }

    private static void ResetState(State state)
    {
        state.Hovered = false;
        try
        {
            if (state.LayerVisual is { } layerVisual)
            {
                layerVisual.StopAnimation("Opacity");
                layerVisual.Opacity = 0f;
            }
        }
        catch
        {
            // 静默
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        ResetState(s);
    }

    /// <summary>取阴影层上的径向渐变刷（卡片用 Border.Background，圆形头像用 Shape.Fill）</summary>
    private static RadialGradientBrush? ResolveBrush(FrameworkElement layer)
    {
        var brush = layer switch
        {
            Border border => border.Background,
            Microsoft.UI.Xaml.Shapes.Shape shape => shape.Fill,
            Panel panel => panel.Background,
            _ => null,
        };
        return brush as RadialGradientBrush;
    }

    private sealed class State
    {
        public Compositor? Compositor;
        public Visual? Visual;
        public Visual? LayerVisual;
        public FrameworkElement? Layer;
        public FrameworkElement? Host;
        public RadialGradientBrush? Radial;
        public double RadiusPx;
        public bool Wired;
        public bool Hovered;
        public bool IsHost;
    }

    /// <summary>分组宿主：跟踪指针位置，持有从属卡片列表</summary>
    private sealed class GroupHost
    {
        public FrameworkElement? Element;
        public Windows.Foundation.Point Point;
        public bool Active;
        public List<State> Cards { get; } = [];
    }
}
