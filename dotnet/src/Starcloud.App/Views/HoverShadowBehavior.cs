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

    /// <summary>
    /// 判定"指针是否邻着某张卡片"时往外扩的像素。用来盖住行与行之间的缝隙（行 Margin 2px），
    /// 保证光标从一张卡片划到另一张卡片时不会被缝隙打断成交叉淡出。
    /// </summary>
    private const double NearMargin = 8d;

    /// <summary>
    /// 淡入 200ms：原先 110ms 配上"起步极快"的曲线，阴影几乎是"啪一下"亮起来，
    /// 跨行移动时会看成闪一下。Fluent Normal 档 + 匀称曲线，亮起有个过程但依然跟手。
    /// </summary>
    private static readonly TimeSpan HoverIn = TimeSpan.FromMilliseconds(MotionFx.HoverFadeInMs);

    /// <summary>
    /// 淡出 300ms：<b>只在光标从一张卡片移到另一张卡片时</b>用。比淡入长，旧卡片的
    /// 光斑慢慢退、和新卡片交叠成一团连续的暗晕；移到卡片之外（工具栏、空白、窗口外）
    /// 则直接灭，见 <see cref="Evaluate"/>。
    /// </summary>
    private static readonly TimeSpan HoverOut = TimeSpan.FromMilliseconds(MotionFx.HoverFadeOutMs);

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    /// <summary>所有已接线的光斑层：悬停状态由窗口级指针位置直接推导，不依赖元素自身的进出事件</summary>
    private static readonly List<State> Cards = [];
    /// <summary>当前正在发光的层（用于"回到窗口时才点亮"等判断）</summary>
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

        try
        {
            state.Visual = ElementCompositionPreview.GetElementVisual(element);
            state.Compositor = state.Visual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            if (GetCarrier(element) is { } layer)
            {
                // 调用方自带的 XAML 阴影层（列表行、卡片、瓦片）
                state.Layer = layer;
                state.LayerVisual = ElementCompositionPreview.GetElementVisual(layer);
                state.Brush = ResolveBrush(layer);
                if (state.Brush is null)
                {
                    state.LayerVisual = null; // 载体上没有径向渐变：退回自建层，别留一个动不了的图层
                }
            }
            else
            {
                // 自带载体还没解析出来（绑定晚于 IsEnabled）：挂一次性 Loaded 兜底重试
                element.Loaded -= RetryOnLoaded;
                element.Loaded += RetryOnLoaded;
            }

            // 没有自带载体的（普通按钮）：自建一层合成光斑；建不出来就放弃接这个元素
            var layerVisual = state.LayerVisual ?? CreateAutoLayer(element, state);
            if (layerVisual is null) return;

            layerVisual.Opacity = 0f;   // 常态：阴影层不可见
            ApplyLight(element, state, element.ActualWidth / 2, element.ActualHeight / 2);
            state.Element = element;
            if (!Cards.Contains(state)) Cards.Add(state);
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

    private static void OnRootPointerMoved(object sender, PointerRoutedEventArgs e) => Evaluate(e);

    private static void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 指针整个移出窗口 = 没有任何目标卡片 → 直接灭，不留尾巴
        foreach (var state in Glowing.ToList()) ResetState(state);
    }

    /// <summary>
    /// 由指针位置统一推导每一层的悬停状态（不依赖元素自己的进出事件，滚动/虚拟化下也不会漏）。
    /// <para>退场的分寸分两种，这是本行为的关键手感：</para>
    /// <list type="bullet">
    /// <item>光标落在<b>另一张卡片</b>上 → 旧的按 <see cref="HoverOut"/> 淡出：两张卡片的光斑交叠，
    /// 移动过程是连续的，不会闪断。</item>
    /// <item>光标落在<b>任何卡片之外</b>（工具栏、空白、窗口外）→ 立刻灭：没有"接力"的对象，
    /// 再拖一条淡出尾巴只会看着像阴影卡住。</item>
    /// </list>
    /// <para>判定"有没有接力对象"时用的是<b>外扩</b>范围（<see cref="NearMargin"/>）：列表行之间
    /// 有 2px 缝隙（行 Margin 造成），光标慢速划过缝隙的那一帧会短暂处于"谁都不在指针下"，
    /// 若按严格命中判定就会先灭再亮，看着像闪断。</para>
    /// </summary>
    private static void Evaluate(PointerRoutedEventArgs e)
    {
        if (Cards.Count == 0) return;
        var anyNear = false;
        var leaving = new List<State>();
        foreach (var state in Cards.ToList())
        {
            if (state.Element is not { } el) continue;
            try
            {
                if (el.XamlRoot is null)
                {
                    Cards.Remove(state);
                    Glowing.Remove(state);
                    continue;
                }
                var p = e.GetCurrentPoint(el).Position;
                var inside = p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
                var near = p.X >= -NearMargin && p.Y >= -NearMargin &&
                           p.X <= el.ActualWidth + NearMargin && p.Y <= el.ActualHeight + NearMargin;
                if (inside)
                {
                    anyNear = true;
                    ApplyLight(el, state, p.X, p.Y);
                    if (!Glowing.Contains(state))
                    {
                        state.Hovered = true;
                        Glowing.Add(state);
                        Animate(state, true, HoverIn);
                    }
                }
                else if (Glowing.Contains(state))
                {
                    leaving.Add(state); // 先收集，等知道"有没有别的卡片接着"再决定怎么退场
                }
                if (near) anyNear = true;
            }
            catch
            {
                // 静默：元素已被回收
            }
        }

        foreach (var state in leaving)
        {
            if (anyNear) FadeOut(state);   // 卡片 ↔ 卡片（含跨过行间缝隙）：交叉淡出
            else ResetState(state);        // 离开所有卡片：立刻灭
        }
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
            // 悬停反馈是"亮度 + 微位移"的交叉过渡，不是内容进出场：两个方向都用匀称的 FadeEase，
            // 只有时长分快慢（进 200ms / 出 300ms），这样淡出过程看得见，也不会有"停顿后突然消失"。
            var ease = MotionFx.FadeEase(compositor);

            if (state.LayerVisual is { } layerVisual)
            {
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = duration;
                fade.InsertKeyFrame(1f, hovered ? 1f : 0f, ease);
                layerVisual.StartAnimation("Opacity", fade);
            }

            if (state.Visual is { } visual)
            {
                // 抬升：与阴影同一条曲线、同一时长，两者不会一个先停一个还在走
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
        // 不在这里判"淡出还是立刻灭"：只有统一判定知道指针有没有落到别的卡片上。
        // 移出时也不复位光源——光斑留在最后位置淡出，复位会让它"跳回中心"，看着像抽搐。
        => Evaluate(e);

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
        if (state.Brush is null && state.AutoBrush is null) return;
        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 半径按对角线取：卡片再大也能覆盖到边缘与角落（近处深、远处浅），不会出现"离光标远的那一侧没被染到"。
        // 小元素（按钮/图标卡）用下限兜底，保持"一束光"的紧凑手感。
        var radiusPx = Math.Max(36d, 0.6 * Math.Sqrt(w * w + h * h));
        var cx = Math.Clamp(x / w, 0d, 1d);
        var cy = Math.Clamp(y / h, 0d, 1d);

        if (state.Brush is { } brush)
        {
            // 调用方自带的 XAML 径向渐变：坐标与半径都是 0~1 的相对值
            brush.RadiusX = radiusPx / w;
            brush.RadiusY = radiusPx / h;
            var center = new Windows.Foundation.Point(cx, cy);
            brush.Center = center;
            brush.GradientOrigin = center;
            return;
        }

        // 自建合成层：同一套映射模式（Relative），语义与上面完全一致
        var auto = state.AutoBrush!;
        auto.EllipseCenter = new Vector2((float)cx, (float)cy);
        auto.EllipseRadius = new Vector2((float)(radiusPx / w), (float)(radiusPx / h));
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
        // 自建光斑层（元素没有自带载体时用，例如普通按钮）：画在元素内容之上
        public CompositionRadialGradientBrush? AutoBrush;
        public CompositionRectangleGeometry? AutoGeometry;
        public bool Wired;
        public bool Hovered;
    }

    /// <summary>自建光斑的渐变档位，与各处 XAML 里那组保持一致（中心 16% 黑，向外递减）</summary>
    private static readonly (byte Alpha, float Offset)[] GlowStops =
    [
        (0x28, 0f), (0x1F, 0.18f), (0x16, 0.40f), (0x0D, 0.62f), (0x05, 0.82f), (0x00, 1f),
    ];

    /// <summary>
    /// 元素没有自带光斑层时，自建一层合成光斑（画在元素内容之上）。
    /// <para>用途：普通按钮这类拿不到模板内部结构、又要有同一套跟手内阴影的元素。
    /// 只给"有可见底色"的元素建——纯透明底的图标按钮本来就不是白底按钮，
    /// 在上面盖一层深色渐变只会显得脏。</para>
    /// </summary>
    private static Visual? CreateAutoLayer(FrameworkElement element, State state)
    {
        if (state.Compositor is not { } compositor) return null;
        if (!HasVisibleBackground(element)) return null; // 纯透明底图标按钮：不加

        var brush = compositor.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative; // 与 XAML 径向渐变同一套 0~1 语义
        foreach (var (alpha, offset) in GlowStops)
            brush.ColorStops.Add(compositor.CreateColorGradientStop(offset,
                Windows.UI.Color.FromArgb(alpha, 0, 0, 0)));

        var size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
        var geometry = compositor.CreateRectangleGeometry();
        geometry.Size = size;
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        var shapeVisual = compositor.CreateShapeVisual();
        shapeVisual.Size = size; // ShapeVisual 的尺寸为 0 时整层不渲染（这里踩过）
        shapeVisual.Shapes.Add(shape);
        shapeVisual.Opacity = 0f;
        ElementCompositionPreview.SetElementChildVisual(element, shapeVisual);

        state.AutoBrush = brush;
        state.AutoGeometry = geometry;
        state.LayerVisual = shapeVisual;
        return shapeVisual;
    }

    /// <summary>元素是否有可见底色：纯透明底或没设底色的（图标按钮）不算</summary>
    private static bool HasVisibleBackground(FrameworkElement element) =>
        element switch
        {
            Control control => IsVisible(control.Background),
            Panel panel => IsVisible(panel.Background),
            _ => false,
        };

    private static bool IsVisible(Brush? brush) => brush switch
    {
        SolidColorBrush solid => solid.Color.A > 0,
        null => false,
        _ => true, // 渐变等其它画刷按"有底色"处理
    };

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
            // 自建层的矩形几何要跟着元素尺寸走，否则按钮改尺寸后光斑会停在旧大小
            if (s.AutoGeometry is { } geometry)
            {
                var size = new Vector2((float)fe.ActualWidth, (float)fe.ActualHeight);
                geometry.Size = size;
                if (s.LayerVisual is { } layerVisual) layerVisual.Size = size;
            }
            ApplyLight(fe, s, fe.ActualWidth / 2, fe.ActualHeight / 2);
        }
        catch
        {
            // 静默
        }
    }
}
