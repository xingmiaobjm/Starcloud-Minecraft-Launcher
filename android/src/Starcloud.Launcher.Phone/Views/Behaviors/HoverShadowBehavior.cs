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
//
// ── 性能约定 ────────────────────────────────────────────────────────────
// 悬停状态由「窗口级指针位置」统一推导（而不是各元素自己的进出事件），所以它的代价是
// 「每次鼠标移动 × 在场卡片数」。本实现守住三条线，保证这个乘积不会随时间或翻页变大：
//
//   1) **全局表只留在场元素**：容器卸载（翻页、列表回收）即从表里摘除，页面元素不再被
//      静态表强引用住。旧实现只清动画状态、不摘引用，翻页越多表越长——既拖慢每次移动，
//      也让已离开的页面无法回收。
//   2) **位置缓存**：每张卡片在窗口坐标系里的矩形只在接线、改尺寸、滚动（EffectiveViewportChanged）
//      时重算；指针事件里只做矩形算术。旧实现对每张卡片各做一次 e.GetCurrentPoint(el) 坐标变换，
//      卡片上百时每次移动就是上百次变换。
//   3) **零分配**：复用两个待处理缓冲，不再每次事件 new List。
//
// Uno 移植说明：合成 API（ElementCompositionPreview / Compositor 关键帧动画）在 Skia 渲染下可用，
// 全部动效路径原样保留；触屏上 PointerMoved 同样驱动跟手光源，行为语义与桌面一致。

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.Launcher.Phone.Services;
using Windows.Foundation;
using Windows.UI;

namespace Starcloud.Launcher.Phone.Views.Behaviors;

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

    /// <summary>所有已接线的光斑层（仅在场元素）：悬停状态由窗口级指针位置直接推导，不依赖元素自身的进出事件</summary>
    private static readonly List<State> Cards = [];
    /// <summary>当前正在发光的层（用于"回到窗口时才点亮"等判断）</summary>
    private static readonly List<State> Glowing = [];

    /// <summary>本次事件里"待退场"的层（复用缓冲，避免每帧分配）</summary>
    private static readonly List<State> Leaving = [];
    /// <summary>本次事件里发现已脱离可视树的层（复用缓冲）</summary>
    private static readonly List<State> Detached = [];

    /// <summary>已挂上看门狗的窗口根元素（多窗口：主窗口 + Nova 浮窗各一份）</summary>
    private static readonly List<FrameworkElement> HookedRoots = [];

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

    /// <summary>
    /// 光斑半径（像素；0 = 按载体对角线自动，见 ApplyLight）。
    /// <para>给"载体比元素大、但可见空间很窄"的地方用：标题栏只有 48px 高，半径一旦超过可视半高，
    /// 渐变的尾巴就够不到零、直接被窗口上沿和内容分界切一刀——看到的"方边"就是这条切线。
    /// 显式给一个能在可视区里自己淡完的半径，边界就不存在了。</para>
    /// </summary>
    public static readonly DependencyProperty GlowRadiusProperty =
        DependencyProperty.RegisterAttached(
            "GlowRadius",
            typeof(double),
            typeof(HoverShadowBehavior),
            new PropertyMetadata(0d, OnGlowRadiusChanged));

    public static double GetGlowRadius(DependencyObject element) => (double)element.GetValue(GlowRadiusProperty);

    public static void SetGlowRadius(DependencyObject element, double value) => element.SetValue(GlowRadiusProperty, value);

    /// <summary>
    /// 点亮范围相对元素四边各内收的像素（默认 0 = 整个元素都算）。
    /// <para>用来把"点亮范围"和"点击热区"分开：三连键的按钮是 46×48 的点击热区，
    /// 但只在光标进到中间那一小块时才点亮光斑，免得贴着按钮边缘划过也亮一团。</para>
    /// </summary>
    public static readonly DependencyProperty TriggerInsetProperty =
        DependencyProperty.RegisterAttached(
            "TriggerInset",
            typeof(double),
            typeof(HoverShadowBehavior),
            new PropertyMetadata(0d));

    public static double GetTriggerInset(DependencyObject element) => (double)element.GetValue(TriggerInsetProperty);

    public static void SetTriggerInset(DependencyObject element, double value) => element.SetValue(TriggerInsetProperty, value);

    /// <summary>
    /// 悬停时元素自己要不要上移（默认 true）。
    /// <para>给「位移已经由另一个行为负责」的调用点用：左侧分类导航项同时挂了
    /// <c>RowHoverBehavior</c>（它负责悬停底色与按下反馈，并做 -2px 上浮）。本行为再抬一次
    /// 就是两次位移叠加（合成为一次 -3px 的上移）。关掉之后本行为只管光斑，位移只有一个来源。</para>
    /// </summary>
    public static readonly DependencyProperty LiftEnabledProperty =
        DependencyProperty.RegisterAttached(
            "LiftEnabled",
            typeof(bool),
            typeof(HoverShadowBehavior),
            new PropertyMetadata(true));

    public static bool GetLiftEnabled(DependencyObject element) => (bool)element.GetValue(LiftEnabledProperty);

    public static void SetLiftEnabled(DependencyObject element, bool value) => element.SetValue(LiftEnabledProperty, value);

    private static void OnGlowRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 半径是摆光源时才用的：改完立刻按当前位置重画一次，不必等下一次鼠标移动
        if (d is not FrameworkElement fe) return;
        if (!States.TryGetValue(fe, out var state) || !state.Wired) return;
        ApplyLight(fe, state, state.LightX, state.LightY);
    }

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
                // 内联渐变写死的是叠黑色：深色主题下立刻换成叠白，否则那一块比背景更暗
                if (state.Brush is { } carrierBrush) RetintBrush(carrierBrush, element.ActualTheme);
                if (state.Brush is null)
                {
                    state.LayerVisual = null; // 载体上没有径向渐变：退回自建层，别留一个动不了的图层
                }
                else
                {
                    // 载体允许比元素大（标题栏三连键就是外扩一圈的），它自己改尺寸时原点缓存要作废
                    layer.SizeChanged -= OnCarrierSizeChanged;
                    layer.SizeChanged += OnCarrierSizeChanged;
                    state.LayerOriginValid = false;
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
            state.Root = RootOf(element);
            if (!Cards.Contains(state)) Cards.Add(state);
        }
        catch
        {
            state.Visual = null;
            state.LayerVisual = null;
            state.Compositor = null;
            return;
        }

        Hook(element);
        HookRootWatchdog(element);
        state.Wired = true;
    }

    /// <summary>元素自己的指针事件：负责跟手光源与快速响应（看门狗只做兜底）</summary>
    private static void Hook(FrameworkElement element)
    {
        element.PointerEntered -= OnPointerEntered;
        element.PointerEntered += OnPointerEntered;
        element.PointerMoved -= OnPointerMoved;
        element.PointerMoved += OnPointerMoved;
        element.PointerExited -= OnPointerExited;
        element.PointerExited += OnPointerExited;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        element.EffectiveViewportChanged -= OnEffectiveViewportChanged;
        element.EffectiveViewportChanged += OnEffectiveViewportChanged;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        element.Unloaded -= OnUnloaded;
        element.Unloaded += OnUnloaded;
        // 主题订阅与 Unhook 里的退订成对：模板内联光斑要在这里才能跟着切深浅
        element.ActualThemeChanged -= OnThemeChanged;
        element.ActualThemeChanged += OnThemeChanged;
    }

    private static void Unhook(FrameworkElement element)
    {
        element.PointerEntered -= OnPointerEntered;
        element.PointerMoved -= OnPointerMoved;
        element.PointerExited -= OnPointerExited;
        element.SizeChanged -= OnSizeChanged;
        element.EffectiveViewportChanged -= OnEffectiveViewportChanged;
        element.ActualThemeChanged -= OnThemeChanged;
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
    }

    private static FrameworkElement? RootOf(FrameworkElement element)
    {
        try { return element.XamlRoot?.Content as FrameworkElement; }
        catch { return null; }
    }

    /// <summary>
    /// 全局看门狗：光斑的淡出不能只依赖元素自己的 PointerExited——快速滑动、移到滚动条上、
    /// 指针直接移出窗口时这个事件不一定送达，那一层就留在原地"卡住"。
    /// 所以额外在窗口根元素上监听：指针一动就复核所有发光层，指针不在其范围内立即淡出；离开窗口全部淡出。
    /// <para>每个窗口（主窗口 / Nova 浮窗）各挂一次；事件里按 XamlRoot 归属分流，互不干扰。</para>
    /// </summary>
    private static void HookRootWatchdog(FrameworkElement element)
    {
        try
        {
            if (element.XamlRoot?.Content is not FrameworkElement root) return;
            if (HookedRoots.Contains(root)) return;
            root.PointerMoved += OnRootPointerMoved;
            root.PointerExited += OnRootPointerExited;
            // 窗口尺寸变化会让所有缓存矩形失效（布局重排），一次性作废、下次事件再按需重算
            root.SizeChanged += OnRootSizeChanged;
            root.Unloaded += OnRootUnloaded; // 窗口关闭（如 Nova 浮窗）→ 摘掉看门狗，不留下对根元素的强引用
            HookedRoots.Add(root);
        }
        catch
        {
            // 静默：拿不到根元素就退回只靠元素自身的进出事件
        }
    }

    private static void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement root) Evaluate(root, e);
    }

    private static void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 指针整个移出窗口 = 没有任何目标卡片 → 直接灭，不留尾巴（只处理本窗口的层）
        var root = sender as FrameworkElement;
        Leaving.Clear();
        foreach (var state in Glowing) Leaving.Add(state);
        foreach (var state in Leaving)
            if (root is null || ReferenceEquals(state.Root, root)) ResetState(state);
        Leaving.Clear();
    }

    private static void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var state in Cards)
            if (ReferenceEquals(state.Root, sender)) state.BoundsValid = false;
    }

    private static void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root) return;
        root.PointerMoved -= OnRootPointerMoved;
        root.PointerExited -= OnRootPointerExited;
        root.SizeChanged -= OnRootSizeChanged;
        root.Unloaded -= OnRootUnloaded;
        HookedRoots.Remove(root);
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
    /// <para>本窗口的指针位置只取一次（一次坐标变换），其余判断都是缓存矩形上的算术。</para>
    /// </summary>
    private static void Evaluate(FrameworkElement root, PointerRoutedEventArgs e)
    {
        if (Cards.Count == 0) return;

        double px, py;
        try
        {
            var p = e.GetCurrentPoint(root).Position;
            px = p.X;
            py = p.Y;
        }
        catch
        {
            return;
        }

        var anyNear = false;
        Leaving.Clear();
        Detached.Clear();

        for (var i = 0; i < Cards.Count; i++)
        {
            var state = Cards[i];
            if (state.Element is not { } el) continue;
            if (el.XamlRoot is null)
            {
                Detached.Add(state); // 已脱离可视树：本次事件后统一摘除，不再参与后续判定
                continue;
            }
            // 接线时元素还没进可视树（XamlRoot 为空）时补一次归属：否则它会永远等不到自己窗口的事件
            state.Root ??= RootOf(el);
            if (!ReferenceEquals(state.Root, root)) continue; // 别的窗口的层，由那个窗口的事件负责

            double lx, ly;          // 元素内坐标（光源跟手用）
            bool inside, near;

            // 点亮范围可以比元素小（TriggerInset）：矩形四个边各内收一点再判定。
            // 用标量算而不是构造 Rect，指针事件里依旧零分配。
            var inset = GetTriggerInset(el);

            if (BoundsOf(el, state, out var b))
            {
                lx = px - b.X;
                ly = py - b.Y;
                var x0 = b.X + inset;
                var y0 = b.Y + inset;
                var x1 = b.X + b.Width - inset;
                var y1 = b.Y + b.Height - inset;
                inside = px >= x0 && py >= y0 && px <= x1 && py <= y1;
                near = px >= x0 - NearMargin && py >= y0 - NearMargin &&
                       px <= x1 + NearMargin && py <= y1 + NearMargin;
            }
            else
            {
                // 弹层内容（ContentDialog / Flyout 挂在 PopupRoot 下，与窗口内容不是同一棵可视树）
                // 拿不到窗口坐标：退回逐元素换算——对话框底部的取消/关闭这类按钮靠这条路径工作。
                try
                {
                    var lp = e.GetCurrentPoint(el).Position;
                    lx = lp.X;
                    ly = lp.Y;
                }
                catch
                {
                    continue;
                }

                var ew = el.ActualWidth;
                var eh = el.ActualHeight;
                if (ew <= 0 || eh <= 0) continue;
                inside = lx >= inset && ly >= inset && lx <= ew - inset && ly <= eh - inset;
                near = lx >= inset - NearMargin && ly >= inset - NearMargin &&
                       lx <= ew - inset + NearMargin && ly <= eh - inset + NearMargin;
            }

            if (inside)
            {
                anyNear = true;
                ApplyLight(el, state, lx, ly);
                if (!Glowing.Contains(state))
                {
                    state.Hovered = true;
                    Glowing.Add(state);
                    Animate(state, true, HoverIn);
                }
            }
            else if (Glowing.Contains(state))
            {
                Leaving.Add(state); // 先收集，等知道"有没有别的卡片接着"再决定怎么退场
            }
            if (near) anyNear = true;
        }

        foreach (var state in Detached)
        {
            Cards.Remove(state);
            Glowing.Remove(state);
        }

        foreach (var state in Leaving)
        {
            if (anyNear) FadeOut(state);   // 卡片 ↔ 卡片（含跨过行间缝隙）：交叉淡出
            else ResetState(state);        // 离开所有卡片：立刻灭
        }
    }

    /// <summary>
    /// 元素在窗口坐标系里的矩形；失效时重算（接线 / 改尺寸 / 滚动后才重算）。
    /// 返回 false 表示这次拿不到（尺寸为 0 或不在同一棵可视树）——调用方退回避开窗口坐标的判定路径。
    /// </summary>
    private static bool BoundsOf(FrameworkElement element, State state, out Rect bounds)
    {
        if (state.BoundsValid)
        {
            bounds = state.Bounds;
            return bounds.Width > 0 && bounds.Height > 0;
        }

        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            state.Bounds = default;
            bounds = default;
            return false;
        }

        try
        {
            var transform = element.TransformToVisual(state.Root);
            state.Bounds = transform.TransformBounds(new Rect(0, 0, w, h));
            state.BoundsValid = true;
        }
        catch
        {
            // 拿不到变换（弹层内容在 PopupRoot 下）：本次退回逐元素判定，下次事件再试
            state.Bounds = default;
            state.BoundsValid = false;
        }
        bounds = state.Bounds;
        return state.BoundsValid && bounds.Width > 0 && bounds.Height > 0;
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
        Unhook(element);
        state.Wired = false;
        ResetState(state);
        Cards.Remove(state);
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
                // 位移可以按元素关掉（见 LiftEnabled）：分类导航项的位移交给 RowHoverBehavior，
                // 这里再抬一次就是两次上移叠加。元素取不到时按默认开处理，不影响既有调用点。
                var lift = state.Element is null || GetLiftEnabled(state.Element);
                if (!lift) return;

                // 抬升：与阴影同一条曲线、同一时长，两者不会一个先停一个还在走
                var liftAnim = compositor.CreateVector3KeyFrameAnimation();
                liftAnim.Duration = duration;
                liftAnim.InsertKeyFrame(1f, hovered ? new Vector3(0f, HoverLiftY, 0f) : Vector3.Zero, ease);
                visual.StartAnimation("Translation", liftAnim);
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
        if (state.Layer is { } carrier) carrier.SizeChanged -= OnCarrierSizeChanged; // 别攥住要回收的载体
        state.LayerOriginValid = false;
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
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s) || !s.Wired) return;
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
        // 不在这里判"淡出还是立刻灭"：只有统一判定知道指针有没有落到别的卡片上。
        // 移出时也不复位光源——光斑留在最后位置淡出，复位会让它"跳回中心"，看着像抽搐。
        if (sender is FrameworkElement fe && fe.XamlRoot?.Content is FrameworkElement root) Evaluate(root, e);
    }

    /// <summary>
    /// 阴影以光标为中心：径向渐变的中心与渐变原点跟着指针走，深色核心就在光标下、向外渐淡。
    /// 只改渐变刷的相对坐标（0~1），纯 XAML 属性更新，不涉及布局。
    /// </summary>
    private static void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s) || !s.Wired) return;
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
    /// <para>光斑画在<b>载体</b>上（没有载体时才是元素自己）。载体允许比元素大一圈——
    /// 标题栏三连键就是这么用的：按钮只有 46×48，光斑得铺到按钮之外才谈得上"范围"。
    /// 所以尺寸取载体的，元素内坐标先减掉载体原点再归一化。</para>
    /// </summary>
    private static void ApplyLight(FrameworkElement element, State state, double x, double y)
    {
        if (state.Brush is null && state.AutoBrush is null) return;
        state.LightX = x; // 记住这次的光源位置：改半径 / 改尺寸时按原位置重画，而不是弹回中心
        state.LightY = y;
        state.HasLight = true;

        // 只有"自带载体 + 解析出径向渐变"这条路径才是载体当画面；自建层是按元素尺寸建的，
        // 拿载体尺寸去映射会错位。
        var surface = state.LayerVisual is not null && state.Layer is { } carrier ? carrier : element;
        var w = surface.ActualWidth;
        var h = surface.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            surface = element;
            w = element.ActualWidth;
            h = element.ActualHeight;
        }
        if (w <= 0 || h <= 0) return;

        if (!ReferenceEquals(surface, element))
        {
            var origin = LayerOriginOf(element, state, surface);
            x -= origin.X;
            y -= origin.Y;
        }

        // 光斑是正圆：半径各向同性，再按像素换算成相对值（RadiusX = r/w、RadiusY = r/h）。
        // 若直接把相对半径按长宽比铺满，光斑会被拉成一颗横椭圆。
        // 半径基准取元素的"短边"（扁长行上就是行高），再 ×2.5 放大——纯短边（850×60 的行 → 60px）
        // 光斑只有一小团，太挤。取对角线或最长边则会让整行被均匀染满、上下被硬切出一条横带（方边）。
        // 倍数只决定横向铺多开：半径 = k×短边时，最近的那条边永远落在 d = 1/(2k) 的相对位置上，
        // 所以 k 变大并不会让行边更硬（k=2 与 2.5 的行边灰度几乎一致），只是横向更宽。
        // 显式半径优先——可视空间比载体窄的地方（顶栏三连键）必须自己指定。
        var manualRadius = GetGlowRadius(element);
        var radiusPx = manualRadius > 0 ? manualRadius : Math.Min(w, h) * 2.5;
        var radiusX = radiusPx / w;
        var radiusY = radiusPx / h;
        var cx = Math.Clamp(x / w, 0d, 1d);
        var cy = Math.Clamp(y / h, 0d, 1d);

        if (state.Brush is { } brush)
        {
            // 调用方自带的 XAML 径向渐变：坐标与半径都是 0~1 的相对值
            brush.RadiusX = radiusX;
            brush.RadiusY = radiusY;
            var center = new Point(cx, cy);
            // Uno 的 RadialGradientBrush 未实现 GradientOrigin（Center 即光斑原点），只设 Center
            brush.Center = center;
            return;
        }

        // 自建合成层：同一套映射模式（Relative），语义与上面完全一致
        var auto = state.AutoBrush!;
        auto.EllipseCenter = new Vector2((float)cx, (float)cy);
        auto.EllipseRadius = new Vector2((float)radiusX, (float)radiusY);
    }

    /// <summary>
    /// 载体左上角在元素坐标系里的位置（缓存）。载体通常正贴在元素上（原点 0,0），
    /// 标题栏三连键那种"外扩一圈"的载体则要偏出去——把光标坐标平移过去，光斑才正好落在光标下。
    /// 只在接线 / 元素或载体改尺寸后重算，指针事件里不再做坐标变换。
    /// </summary>
    private static Point LayerOriginOf(FrameworkElement element, State state, FrameworkElement surface)
    {
        if (state.LayerOriginValid) return state.LayerOrigin;
        try
        {
            state.LayerOrigin = surface.TransformToVisual(element).TransformPoint(new Point(0, 0));
        }
        catch
        {
            state.LayerOrigin = new Point(0, 0); // 拿不到变换（弹层等）就按同尺寸处理
        }
        state.LayerOriginValid = true;
        return state.LayerOrigin;
    }

    /// <summary>载体自己改尺寸（外扩载体随布局重排）→ 原点缓存作废，下次指针事件重算</summary>
    private static void OnCarrierSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement carrier) return;
        for (var i = 0; i < Cards.Count; i++)
        {
            var s = Cards[i];
            if (ReferenceEquals(s.Layer, carrier)) s.LayerOriginValid = false;
        }
    }

    /// <summary>
    /// 容器卸载：摘掉动画状态并<b>从全局表移除</b>。
    /// <para>翻页 / 列表虚拟化回收容器时触发。不清引用的话，静态表会一直攥着已经离开的页面，
    /// 每次鼠标移动都要为它们做一遍判定；元素重新上场时由 Loaded 重新接线。</para>
    /// </summary>
    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        ResetState(s);
        s.Wired = false;
        s.BoundsValid = false;
        Cards.Remove(s);
    }

    /// <summary>元素重新上场（容器回收复用 / 页面返回）：重新接线，并按新位置重算缓存矩形</summary>
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !GetIsEnabled(fe)) return;
        if (States.TryGetValue(fe, out var s) && s.Wired)
        {
            s.Root = RootOf(fe);
            s.BoundsValid = false;
            return;
        }
        Wire(fe);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.BoundsValid = false;
        s.LayerOriginValid = false; // 载体跟着元素摆（居中/负边距），元素改尺寸它也会挪
        try
        {
            // 自建层的矩形几何要跟着元素尺寸走，否则按钮改尺寸后光斑会停在旧大小
            if (s.AutoGeometry is { } geometry)
            {
                var size = new Vector2((float)fe.ActualWidth, (float)fe.ActualHeight);
                geometry.Size = size;
                if (s.LayerVisual is { } layerVisual) layerVisual.Size = size;
            }
            // 有光源就按原位置重画（改尺寸不该让光斑跳回中心），没有才摆中心
            if (s.HasLight) ApplyLight(fe, s, s.LightX, s.LightY);
            else ApplyLight(fe, s, fe.ActualWidth / 2, fe.ActualHeight / 2);
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>滚动 / 布局位移：缓存的窗口坐标矩形作废，下次判定时重算</summary>
    private static void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (!States.TryGetValue(sender, out var s)) return;
        s.BoundsValid = false;
    }

    private sealed class State
    {
        public Compositor? Compositor;
        public Visual? Visual;
        public Visual? LayerVisual;
        public FrameworkElement? Layer;
        public FrameworkElement? Element;
        /// <summary>元素所属窗口根（多窗口时按它分流指针事件）</summary>
        public FrameworkElement? Root;
        public RadialGradientBrush? Brush;
        // 自建光斑层（元素没有自带载体时用，例如普通按钮）：画在元素内容之上
        public CompositionRadialGradientBrush? AutoBrush;
        public CompositionRoundedRectangleGeometry? AutoGeometry;
        /// <summary>元素在窗口坐标系里的矩形（只在接线 / 改尺寸 / 滚动后重算）</summary>
        public Rect Bounds;
        public bool BoundsValid;
        /// <summary>载体左上角在元素坐标系里的位置（同 Bounds 一样缓存，见 LayerOriginOf）</summary>
        public Point LayerOrigin;
        public bool LayerOriginValid;
        /// <summary>上一次光源位置（元素内坐标）：改半径 / 改尺寸后据此重画，不跳回中心</summary>
        public double LightX;
        public double LightY;
        public bool HasLight;
        public bool Wired;
        public bool Hovered;
    }

    /// <summary>
    /// 光斑渐变的档位透明度，末档 1.0 固定全透明（见 <see cref="GlowOffsets"/>）。
    /// 与各页 XAML 里内联的 <c>&lt;RadialGradientBrush&gt;</c> 共用同一张表：0x28 → 0x05 依次衰减。
    /// </summary>
    private static readonly byte[] GlowAlphas = [0x28, 0x1F, 0x16, 0x0D, 0x05];

    /// <summary>
    /// 光斑底色：浅色主题叠黑压暗、深色主题叠白提亮——深色下沿用黑只会让那一块比背景更暗。
    /// </summary>
    private static Color GlowTint(ElementTheme theme) =>
        theme == ElementTheme.Dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);

    /// <summary>光斑渐变各档的偏移位置（与 <see cref="GlowAlphas"/> 一一对应）</summary>
    private static readonly float[] GlowOffsets = [0f, 0.18f, 0.40f, 0.62f, 0.82f, 1f];

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
        FillAutoBrush(brush, element.ActualTheme);

        var size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
        // 必须用圆角矩形：按钮是圆角的（本主题 ControlCornerRadius=12），直角矩形会把光斑
        // 填进四个圆角缺口——两个按钮之间的缝隙正好是四个缺角交汇处，看着就是"阴影渲染到了按钮外"。
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2((float)CornerRadiusOf(element));
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
        // 自建层不在可视化树里，模板那套主题重写轮不到它；主题订阅统一在 Hook 里挂，
        // 切主题时由 OnThemeChanged 把颜色重写一遍，否则旧深浅会留在屏幕上。
        return shapeVisual;
    }

    /// <summary>
    /// 往自建光斑画刷里写颜色：底色随主题（<see cref="GlowTint"/>），档位用 <see cref="GlowAlphas"/>，
    /// 末档 1.0 全透明收尾。
    /// <para>创建时与切主题时都走这里，保证两条路径的观感一致。</para>
    /// </summary>
    private static void FillAutoBrush(CompositionRadialGradientBrush brush, ElementTheme theme)
    {
        var tint = GlowTint(theme);
        for (var i = 0; i < GlowOffsets.Length; i++)
        {
            var a = i < GlowAlphas.Length ? GlowAlphas[i] : (byte)0;
            var color = Color.FromArgb(a, tint.R, tint.G, tint.B);
            if (i < brush.ColorStops.Count) brush.ColorStops[i].Color = color;
            else brush.ColorStops.Add(brush.Compositor.CreateColorGradientStop(GlowOffsets[i], color));
        }
    }

    /// <summary>
    /// 元素主题变了：把光斑底色重写一遍。自建层改合成画刷（<see cref="FillAutoBrush"/>），
    /// 模板/页面里内联的 XAML 径向渐变改颜色档（<see cref="RetintBrush"/>）。
    /// </summary>
    private static void OnThemeChanged(FrameworkElement sender, object args)
    {
        if (!States.TryGetValue(sender, out var state)) return;
        try
        {
            if (state.AutoBrush is { } auto) FillAutoBrush(auto, sender.ActualTheme);
            else if (state.Brush is { } brush) RetintBrush(brush, sender.ActualTheme);
        }
        catch { /* 静默降级 */ }
    }

    /// <summary>
    /// 把内联径向渐变统一成当前主题的底色：只改每档的 RGB，alpha 原样保留。
    /// <para>内联渐变写死了叠黑色（浅色主题正确）；深色主题下不换白，那一块会比背景更暗。
    /// 保留 alpha 是因为加强档（标题栏 0x3E）与卡片档（0x28）的差别全在透明度上。</para>
    /// </summary>
    private static void RetintBrush(RadialGradientBrush brush, ElementTheme theme)
    {
        var tint = GlowTint(theme);
        foreach (var stop in brush.GradientStops)
        {
            stop.Color = Color.FromArgb(stop.Color.A, tint.R, tint.G, tint.B);
        }
    }

    /// <summary>元素是否有可见底色：纯透明底或没设底色的（图标按钮）不算</summary>
    internal static bool HasVisibleBackground(FrameworkElement element) =>
        element switch
        {
            Control control => IsVisible(control.Background),
            Panel panel => IsVisible(panel.Background),
            _ => false,
        };

    /// <summary>
    /// 元素的圆角半径：控件、Border 与 Grid 都读自己的 CornerRadius，其余按直角处理。
    /// <para>列表行常常是"带圆角的 Grid"（不是控件），只认 Control 的话自建光斑会是直角矩形，
    /// 铺在圆角行上四个角就会溢出。</para>
    /// </summary>
    private static double CornerRadiusOf(FrameworkElement element) => element switch
    {
        Control control => control.CornerRadius.TopLeft,
        Border border => border.CornerRadius.TopLeft,
        Grid grid => grid.CornerRadius.TopLeft,
        _ => 0d,
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
}
