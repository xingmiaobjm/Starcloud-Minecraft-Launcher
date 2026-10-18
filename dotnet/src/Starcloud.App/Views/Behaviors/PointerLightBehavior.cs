// 全局实时鼠标光照（「均衡」档起效，「全部」档增强）。
//
// 把整扇窗口当成一间屋子：指针是一盏移动的灯，接了线的按钮 / 滑块是玻璃小件——
//   1) 边缘光（液态玻璃）：朝向光标那一侧的 1px 内缘被照亮，越近越亮（模板里的 EdgeLight 层）；
//   2) 阴影跟随鼠标：按钮底下那层软阴影朝「背离光标」的方向平移——光标在左，影子往右跑；
//   3) 引力形变：指针悬停时按钮朝光标方向微微隆起（非均匀缩放 + 微平移，Win10 磁贴式的"被吸过去"感），
//      移出时弹性归位。
//
// 性能取向与 HoverShadowBehavior 一致：
//   · 悬停/邻近距离由「窗口级指针位置」统一推导（每窗口一次坐标换算，其余全是缓存矩形上的算术）；
//   · 只对光标邻域（约 200px）内的目标做 XAML 写入，远处目标仅在越界后衰减一次到位；
//   · 容器卸载即从全局表摘除，翻页/列表回收不会让表越滚越长；全程零分配。
//
// 档位联动（MotionService）：
//   minimal → 模板里的光照层保持全灭，本行为完全不接线；
//   lite    → 只把边缘光摆成「顶部常亮的静态玻璃高光」，不进全局表、零逐帧开销；
//   balanced→ 全部动态效果；
//   full    → 形变幅度更大、释放带弹性动画。
// 等级切换对已加载页面不追溯（与悬浮反馈同一约定：下次进页生效）。

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
using Windows.Foundation;

namespace Starcloud.App.Views.Behaviors;

/// <summary>指针光照模式：无 / 按钮（边缘光+阴影+形变）/ 滑轨（滑块：仅形变，光由柔光层负责）。</summary>
public enum PointerLightMode
{
    /// <summary>不参与（默认）</summary>
    None,
    /// <summary>按钮：边缘光 + 阴影跟随 + 引力形变</summary>
    Button,
    /// <summary>滑轨：仅引力形变（跟手柔光由 HoverShadowBehavior.SoftGlow 负责）</summary>
    Tube,
}

/// <summary>全局鼠标光照：边缘光 / 阴影跟随鼠标 / 引力形变（均衡档起效，全部档增强）。</summary>
public static class PointerLightBehavior
{
    /// <summary>模板里边缘光层的部件名（与 GlowButton 的认领同源，普通按钮由可视树查找兜底）</summary>
    internal const string EdgePartName = "EdgeLight";
    /// <summary>模板里玻璃高光面的部件名</summary>
    internal const string GlassPartName = "GlassSurface";
    /// <summary>模板里阴影层的部件名</summary>
    internal const string ShadowPartName = "ShadowHost";

    // ── 档位参数 ────────────────────────────────────────────────
    /// <summary>边缘光常亮底值：液态玻璃的 1px 玻璃缘平时隐约可见（对齐商店的克制观感）</summary>
    private const double EdgeBaseOpacity = 0.20;
    /// <summary>边缘光最亮值：表面晕移除后由边缘光独担悬停反馈，到六成亮、仍不压过原生悬停底色</summary>
    private const double EdgeMaxOpacity = 0.62;
    /// <summary>光标多近开始把边缘光推到最亮（px）</summary>
    private const double EdgeNearDist = 150d;
    /// <summary>超过这个距离就不再逐帧更新（px）</summary>
    private const double ActiveDist = 220d;
    /// <summary>阴影只在这个距离内跟随（px），超出回落到静置位</summary>
    private const double ShadowActiveDist = 420d;

    /// <summary>阴影最大位移（px）：均衡档克制，全部档略宽（立体感刻意压低，只读作"光在动"）</summary>
    private static double ShadowMaxShift(bool full) => full ? 3.0 : 2.2;
    /// <summary>阴影静置位（px）：光不在时影子永远略向下坠（有重力）</summary>
    private const double ShadowRestY = 0.8;
    /// <summary>阴影位移随距离的增益（每 px 距离位移多少 px）</summary>
    private const double ShadowGain = 0.045;

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    /// <summary>所有已接线的动态目标（仅在场元素，均衡档及以上）</summary>
    private static readonly List<State> Targets = [];

    /// <summary>已挂上看门狗的窗口根元素（主窗口 / Nova 浮窗各一份）</summary>
    private static readonly List<FrameworkElement> HookedRoots = [];

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode",
            typeof(PointerLightMode),
            typeof(PointerLightBehavior),
            new PropertyMetadata(PointerLightMode.None, OnModeChanged));

    public static PointerLightMode GetMode(DependencyObject element) =>
        (PointerLightMode)element.GetValue(ModeProperty);

    public static void SetMode(DependencyObject element, PointerLightMode value) =>
        element.SetValue(ModeProperty, value);

    /// <summary>模板里的边缘光层（1px 内缘的径向渐变 Border）：由本行为驱动渐变中心与不透明度</summary>
    public static readonly DependencyProperty EdgePartProperty =
        DependencyProperty.RegisterAttached(
            "EdgePart",
            typeof(Border),
            typeof(PointerLightBehavior),
            new PropertyMetadata(null, OnPartChanged));

    public static Border? GetEdgePart(DependencyObject element) =>
        (Border?)element.GetValue(EdgePartProperty);

    public static void SetEdgePart(DependencyObject element, Border? value) =>
        element.SetValue(EdgePartProperty, value);

    /// <summary>模板里的玻璃高光面（液态玻璃材质的上缘反光，较少档起静态呈现）</summary>
    public static readonly DependencyProperty GlassPartProperty =
        DependencyProperty.RegisterAttached(
            "GlassPart",
            typeof(Border),
            typeof(PointerLightBehavior),
            new PropertyMetadata(null, OnPartChanged));

    public static Border? GetGlassPart(DependencyObject element) =>
        (Border?)element.GetValue(GlassPartProperty);

    public static void SetGlassPart(DependencyObject element, Border? value) =>
        element.SetValue(GlassPartProperty, value);

    /// <summary>模板里的阴影层（Grid，RenderTransform 是 TranslateTransform）：由本行为驱动平移</summary>
    public static readonly DependencyProperty ShadowPartProperty =
        DependencyProperty.RegisterAttached(
            "ShadowPart",
            typeof(FrameworkElement),
            typeof(PointerLightBehavior),
            new PropertyMetadata(null, OnPartChanged));

    public static FrameworkElement? GetShadowPart(DependencyObject element) =>
        (FrameworkElement?)element.GetValue(ShadowPartProperty);

    public static void SetShadowPart(DependencyObject element, FrameworkElement? value) =>
        element.SetValue(ShadowPartProperty, value);

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (e.NewValue is PointerLightMode mode && mode != PointerLightMode.None) Wire(fe);
        else Unwire(fe);
    }

    /// <summary>模板部件可能晚于 Mode 设进来（样式先解析、模板后应用）：到位就再接线一次</summary>
    private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (GetMode(fe) == PointerLightMode.None) return;
        if (States.TryGetValue(fe, out var state) && state.Wired)
        {
            // 已接线：只补充部件引用并按档位重摆静态层，其余照旧
            ResolveParts(fe, state);
            ApplyStaticLayers(state);
            return;
        }
        Wire(fe);
    }

    private static void Wire(FrameworkElement element)
    {
        var mode = GetMode(element);
        if (mode == PointerLightMode.None) return;

        var state = States.GetOrCreateValue(element);
        state.Element = element;
        state.Mode = mode;
        ResolveParts(element, state);

        try
        {
            state.Visual = ElementCompositionPreview.GetElementVisual(element);
        }
        catch
        {
            state.Visual = null;
        }

        var motion = AppServices.Instance.Motion;
        state.Level = motion.Level;
        if (motion.Level == UiMotionLevel.Minimal)
        {
            // 极少：玻璃层全灭（模板默认 Opacity=0，这里兜住动态设过的情况），不进全局表
            SetEdge(state, 0, 0.5, 0.5);
            SetGlass(state, 0);
            SetElementOpacity(state.Shadow, 0);
            state.Wired = false;
            return;
        }

        // 较少起：静态液态玻璃质感——玻璃面高光 + 常亮玻璃缘 + 常驻落影。
        // 三层都是摆好就不动的静态层，零逐帧开销；动态的边缘光跟随与阴影位移从均衡档才开始。
        ApplyStaticLayers(state);
        if (motion.Level == UiMotionLevel.Lite)
        {
            state.Wired = false;
            return;
        }

        // 均衡及以上：进入全局表，指针一动就跟着走
        state.Wired = true;
        if (!Targets.Contains(state)) Targets.Add(state);

        element.PointerEntered -= OnPointerEntered;
        element.PointerEntered += OnPointerEntered;
        element.PointerMoved -= OnPointerMoved;
        element.PointerMoved += OnPointerMoved;
        element.PointerExited -= OnPointerExited;
        element.PointerExited += OnPointerExited;
        element.PointerPressed -= OnPointerPressed;
        element.PointerPressed += OnPointerPressed;
        element.PointerReleased -= OnPointerReleased;
        element.PointerReleased += OnPointerReleased;
        element.PointerCaptureLost -= OnPointerCaptureLost;
        element.PointerCaptureLost += OnPointerCaptureLost;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        element.EffectiveViewportChanged -= OnEffectiveViewportChanged;
        element.EffectiveViewportChanged += OnEffectiveViewportChanged;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        element.Unloaded -= OnUnloaded;
        element.Unloaded += OnUnloaded;

        HookRootWatchdog(element);
    }

    /// <summary>按当前档位把三块玻璃层摆到该有的形态（幂等；接线、补部件、档位刷新都走这里）</summary>
    private static void ApplyStaticLayers(State state)
    {
        var motion = AppServices.Instance.Motion;
        state.Level = motion.Level;
        if (motion.Level == UiMotionLevel.Minimal)
        {
            SetEdge(state, 0, 0.5, 0.5);
            SetGlass(state, 0);
            SetElementOpacity(state.Shadow, 0);
            return;
        }
        SetEdge(state, EdgeBaseOpacity, 0.5, -0.25);
        SetGlass(state, 1);
        // 阴影常驻：按钮悬浮在页面之上，影子是液态玻璃的落影
        SetElementOpacity(state.Shadow, 1);
        SetShadow(state, 0, ShadowRestY);
    }

    /// <summary>
    /// 动效档位变化后的全量重接线：遍历各窗口在场元素，把已接线的按新档位重摆、
    /// 未接线的补接——切到极少要<b>当场</b>摘掉所有光斑与玻璃层，切回升档当场恢复，
    /// 不等下一次进页。
    /// </summary>
    internal static void RefreshAll()
    {
        var motion = AppServices.Instance.Motion;
        foreach (var root in CollectRoots())
            Walk(root);

        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement fe && GetMode(fe) != PointerLightMode.None)
            {
                var wired = States.TryGetValue(fe, out var s) && s.Wired;
                if (motion.Level == UiMotionLevel.Minimal)
                {
                    // 极少：动态接线的整体摘除；较少档只摆过静态层的（Wired=false）直接复位灭灯
                    if (wired) Unwire(fe);
                    else if (s is not null) ResetState(s);
                }
                else if (wired)
                {
                    if (s!.Level != motion.Level)
                    {
                        Unwire(fe);
                        Wire(fe);
                    }
                }
                else
                {
                    Wire(fe);
                }
            }
            var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < n; i++)
                Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>RefreshAll 的遍历根：主窗口内容树 + 已挂看门狗的窗口根（如 Nova 浮窗）。
    /// 极少档启动时没有任何元素接线成功、看门狗一个都没挂，只靠 HookedRoots 会漏掉整棵主树，
    /// 所以主窗口根必须无条件参与。</summary>
    private static List<FrameworkElement> CollectRoots()
    {
        List<FrameworkElement> roots = [];
        if (App.MainWindow?.Content is FrameworkElement main) roots.Add(main);
        foreach (var r in HookedRoots)
            if (!roots.Contains(r)) roots.Add(r);
        return roots;
    }

    /// <summary>
    /// 解析三块玻璃层：优先用调用点显式认领的部件（GlowButton 在 OnApplyTemplate 里交进来），
    /// 没有就按部件名在可视树里找——普通按钮（隐式样式换用玻璃模板）没有代码认领环节，
    /// 且首次接线可能早于模板应用，所以 Loaded 时还会再解析一次兜底。
    /// </summary>
    private static void ResolveParts(FrameworkElement element, State state)
    {
        state.Edge = GetEdgePart(element) ?? FindPart(element, EdgePartName) as Border;
        state.Glass = GetGlassPart(element) ?? FindPart(element, GlassPartName) as Border;
        state.Shadow = GetShadowPart(element) ?? FindPart(element, ShadowPartName);
        state.EdgeBrush = state.Edge?.BorderBrush as RadialGradientBrush;
        state.ShadowShift = ResolveShadowShift(state.Shadow);
    }

    /// <summary>按 Name 在子树里找模板部件（模板生成的元素 Name 保留，可视树遍历可命中）</summary>
    private static FrameworkElement? FindPart(FrameworkElement root, string name)
    {
        if (root.Name == name) return root;
        var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i) is not FrameworkElement fe) continue;
            var hit = FindPart(fe, name);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static void Unwire(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state)) return;
        element.PointerEntered -= OnPointerEntered;
        element.PointerMoved -= OnPointerMoved;
        element.PointerExited -= OnPointerExited;
        element.PointerPressed -= OnPointerPressed;
        element.PointerReleased -= OnPointerReleased;
        element.PointerCaptureLost -= OnPointerCaptureLost;
        element.SizeChanged -= OnSizeChanged;
        element.EffectiveViewportChanged -= OnEffectiveViewportChanged;
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        state.Wired = false;
        state.Hovered = false;
        state.Pressed = false;
        Targets.Remove(state);
        ResetState(state);
    }

    private static TranslateTransform? ResolveShadowShift(FrameworkElement? shadow)
    {
        if (shadow is null) return null;
        if (shadow.RenderTransform is TranslateTransform t) return t;
        var created = new TranslateTransform();
        shadow.RenderTransform = created;
        return created;
    }

    /// <summary>边缘光就位：不透明度 + 渐变中心（元素内相对坐标，可以越界摆到元素外侧）</summary>
    private static void SetEdge(State state, double opacity, double cx, double cy)
    {
        if (state.Edge is not { } edge) return;
        SetElementOpacity(edge, opacity);
        if (state.EdgeBrush is not { } brush) return;
        var center = new Point(cx, cy);
        brush.Center = center;
        brush.GradientOrigin = center;
    }

    private static void SetShadow(State state, double x, double y)
    {
        if (state.ShadowShift is null) return;
        state.ShadowShift.X = x;
        state.ShadowShift.Y = y;
    }

    /// <summary>玻璃高光面：较少档起常显（液态玻璃材质），极少档全灭</summary>
    private static void SetGlass(State state, double opacity)
    {
        if (state.Glass is not null) state.Glass.Opacity = opacity;
    }

    /// <summary>
    /// 全局判定：由窗口根元素的指针事件驱动，对光标邻域内的目标更新边缘光与阴影。
    /// <para>形变只在元素自身的悬停事件里做（PointerEntered/Moved/Exited），这里不重复算。</para>
    /// </summary>
    private static void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Targets.Count == 0) return;
        if (sender is not FrameworkElement root) return;

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

        var full = AppServices.Instance.Motion.FullFx;
        var maxShift = ShadowMaxShift(full);
        List<State>? detached = null;

        for (var i = 0; i < Targets.Count; i++)
        {
            var state = Targets[i];
            if (state.Element is not { } el) continue;
            if (el.XamlRoot is null)
            {
                detached ??= [];
                detached.Add(state); // 已脱离可视树：本次事件后统一摘除
                continue;
            }
            state.Root ??= RootOf(el);
            if (!ReferenceEquals(state.Root, root)) continue;

            if (!BoundsOf(el, state, out var b)) continue;

            var cx = b.X + b.Width / 2;
            var cy = b.Y + b.Height / 2;
            var dx = cx - px;          // 光标 → 中心：阴影朝这个方向躲（光标是灯）
            var dy = cy - py;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist > ShadowActiveDist)
            {
                // 远处：只在还留着旧位移时衰减一次，之后不再写
                if (state.ShadowShift is { } s && (s.X != 0 || s.Y != ShadowRestY))
                    SetShadow(state, 0, ShadowRestY);
                if (!state.Hovered && state.Edge is { } edge && edge.Opacity > EdgeBaseOpacity)
                    SetEdge(state, EdgeBaseOpacity, 0.5, -0.25);
                continue;
            }

            // ── 边缘光：越近越亮，渐变中心摆到朝向光标的那条边 ──
            if (state.Edge is not null)
            {
                var nx = Clamp01((px - b.X) / b.Width);
                var ny = Clamp01((py - b.Y) / b.Height);
                // 中心往光标方向再推出去一点：亮核压在边缘上，而不是元素中心
                var ecx = 0.5 + (nx - 0.5) * 1.1;
                var ecy = 0.5 + (ny - 0.5) * 1.1;
                var t = dist >= EdgeNearDist
                    ? 0
                    : 1 - dist / EdgeNearDist;
                var opacity = EdgeBaseOpacity + (EdgeMaxOpacity - EdgeBaseOpacity) * t;
                if (state.Hovered) opacity = EdgeMaxOpacity;
                SetEdge(state, opacity, ecx, ecy);
            }

            // ── 阴影跟随：位移 = 背离光标的单位方向 × min(距离×增益, 上限)，再叠一点向下坠 ──
            if (state.ShadowShift is not null)
            {
                var scale = dist > 0.001 ? 1 / dist : 0;
                var shift = Math.Min(dist * ShadowGain, maxShift);
                SetShadow(state, dx * scale * shift, dy * scale * shift + ShadowRestY);
            }
        }

        if (detached is not null)
            foreach (var state in detached)
            {
                Targets.Remove(state);
                state.Wired = false;
            }
    }

    /// <summary>指针离开窗口：全部回落到静置位（边缘光回到常亮底值、阴影坠回正下方）</summary>
    private static void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement root) return;
        for (var i = 0; i < Targets.Count; i++)
        {
            var state = Targets[i];
            if (!ReferenceEquals(state.Root, root)) continue;
            SetEdge(state, EdgeBaseOpacity, 0.5, -0.25);
            SetShadow(state, 0, ShadowRestY);
        }
    }

    private static void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var state in Targets)
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

    private static void HookRootWatchdog(FrameworkElement element)
    {
        try
        {
            if (element.XamlRoot?.Content is not FrameworkElement root) return;
            if (HookedRoots.Contains(root)) return;
            root.PointerMoved += OnRootPointerMoved;
            root.PointerExited += OnRootPointerExited;
            root.SizeChanged += OnRootSizeChanged;
            root.Unloaded += OnRootUnloaded;
            HookedRoots.Add(root);
        }
        catch
        {
            // 静默：拿不到根元素就退回只靠元素自身事件（形变仍可用）
        }
    }

    // ── 按压反馈（元素自身指针事件驱动） ──────────────────────────

    /// <summary>按压时的下沉幅度（相对 1 的缩放；液态玻璃的"按下去"手感，松手回弹）</summary>
    private const float PressedScale = 0.982f;
    /// <summary>按下/回弹时长：按下要跟手（短），回弹给一点过冲尾巴（弹性）</summary>
    private const int PressDownMs = 100;
    private const int PressUpMs = 210;

    /// <summary>
    /// 按下去：走合成层 <see cref="Visual.Scale"/> 关键帧（不是直接写值——直接写会"啪"地跳一下，
    /// 这就是之前不够丝滑的原因），阴影同步用合成透明度动画收拢（影子随按压"被压进桌面"），
    /// 边缘光提亮（直写即可，它本来就逐帧跟随）。
    /// </summary>
    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s) || !s.Wired) return;
        s.Pressed = true;
        AnimateScale(fe, s, PressedScale, PressDownMs, overshoot: false);
        AnimateElementOpacity(s.Shadow, 0.25f, 120);
        SetEdge(s, EdgeMaxOpacity, 0.5, 0.5);
    }

    /// <summary>松手：缩放回弹（轻过冲 1.6% 再归位，读作"弹回来"），阴影回常驻、边缘光按档归位。</summary>
    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s) || !s.Pressed) return;
        s.Pressed = false;
        AnimateScale(fe, s, 1f, PressUpMs, overshoot: true);
        AnimateElementOpacity(s.Shadow, 1f, PressUpMs);
        ApplyStaticLayers(s);
    }

    private static void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        OnPointerReleased(sender, e);

    /// <summary>合成层缩放动画：CenterPoint 钉中心；overshoot=true 时走"过冲 → 归位"两段关键帧。</summary>
    private static void AnimateScale(FrameworkElement fe, State s, float target, int ms, bool overshoot)
    {
        if (s.Visual is not { } v) return;
        try
        {
            var w = (float)fe.ActualWidth;
            var h = (float)fe.ActualHeight;
            if (w <= 0 || h <= 0) return;
            v.CenterPoint = new Vector3(w / 2f, h / 2f, 0f);

            var compositor = v.Compositor;
            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(ms);
            if (overshoot)
            {
                anim.InsertKeyFrame(0.55f, new Vector3(1.006f, 1.006f, 1f), MotionFx.ControlEase(compositor));
                anim.InsertKeyFrame(1f, Vector3.One, MotionFx.FadeEase(compositor));
            }
            else
            {
                anim.InsertKeyFrame(1f, new Vector3(target, target, 1f), MotionFx.ControlEase(compositor));
            }
            v.StartAnimation("Scale", anim);
        }
        catch
        {
            // 静默：按压反馈失败不影响点击
        }
    }

    /// <summary>元素合成层透明度动画（阴影收拢 / 回归）</summary>
    private static void AnimateElementOpacity(FrameworkElement? element, float target, int ms)
    {
        if (element is null) return;
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(element);
            var anim = v.Compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(ms);
            anim.InsertKeyFrame(1f, target, MotionFx.FadeEase(v.Compositor));
            v.StartAnimation("Opacity", anim);
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>停掉透明度动画并写死值（动画会把直写值压住，复位路径必须走这里）</summary>
    private static void SetElementOpacity(FrameworkElement? element, double value)
    {
        if (element is null) return;
        try
        {
            ElementCompositionPreview.GetElementVisual(element).StopAnimation("Opacity");
        }
        catch
        {
            // 静默
        }
        element.Opacity = value;
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s) || !s.Wired) return;
        s.Hovered = true; // 悬停本身不改变形状：光只走边缘，形状只回应"按下"
    }

    private static void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s) || !s.Wired) return;
        if (!s.Hovered) s.Hovered = true; // 看门狗先淡出后移回、没有新 Enter 事件的兜底
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s) || !s.Wired) return;
        s.Hovered = false;
        s.Pressed = false;
    }

    // ── 缓存与复位 ──────────────────────────────────────────────

    private static FrameworkElement? RootOf(FrameworkElement element)
    {
        try { return element.XamlRoot?.Content as FrameworkElement; }
        catch { return null; }
    }

    /// <summary>元素在窗口坐标系里的矩形；失效时重算（接线 / 改尺寸 / 滚动后）。拿不到返回 false。</summary>
    private static bool BoundsOf(FrameworkElement element, State state, out Rect bounds)
    {
        if (state.BoundsValid)
        {
            bounds = state.Bounds;
            return bounds.Width > 0 && bounds.Height > 0;
        }

        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w <= 0 || h <= 0 || state.Root is null)
        {
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
            state.Bounds = default;
            state.BoundsValid = false;
        }
        bounds = state.Bounds;
        return state.BoundsValid && bounds.Width > 0 && bounds.Height > 0;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.BoundsValid = false;
    }

    private static void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (!States.TryGetValue(sender, out var s)) return;
        s.BoundsValid = false; // 滚动 / 布局位移：缓存矩形作废
    }

    /// <summary>
    /// 容器重新上场（容器复用 / 页面返回）：重新接线——
    /// <para>首次接线发生在模板应用时（元素还没进可视树），那时拿不到 XamlRoot，看门狗挂不上；
    /// 边缘光与阴影跟随全靠根元素的指针事件驱动，必须在这里补挂一次。Loaded 时模板必已应用，
    /// 普通按钮（部件靠可视树查找）也在这里补一次部件解析。</para>
    /// </summary>
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        HookRootWatchdog(fe);
        if (!States.TryGetValue(fe, out var s)) return;
        if (!s.Wired)
        {
            Wire(fe);
            return;
        }
        ResolveParts(fe, s);
        ApplyStaticLayers(s);
        s.Root = RootOf(fe);
        s.BoundsValid = false;
    }

    /// <summary>容器卸载（翻页 / 列表回收）：摘出全局表并复位模板层，静态表不再攥着离开的页面</summary>
    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !States.TryGetValue(fe, out var s)) return;
        s.Hovered = false;
        s.Wired = false;
        s.BoundsValid = false;
        Targets.Remove(s);
        ResetState(s);
    }

    /// <summary>复位到常态：形变归位、玻璃层全灭（等级切走 / 容器回收时）</summary>
    private static void ResetState(State state)
    {
        try
        {
            if (state.Visual is { } visual)
            {
                visual.StopAnimation("TransformMatrix");
                visual.TransformMatrix = Matrix4x4.Identity;
            }
        }
        catch { /* 静默 */ }
        SetEdge(state, 0, 0.5, 0.5);
        SetGlass(state, 0);
        SetShadow(state, 0, 0);
        SetElementOpacity(state.Shadow, 0);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private sealed class State
    {
        public Visual? Visual;
        public FrameworkElement? Element;
        /// <summary>元素所属窗口根（多窗口时按它分流指针事件）</summary>
        public FrameworkElement? Root;
        public PointerLightMode Mode;
        public Border? Edge;
        public RadialGradientBrush? EdgeBrush;
        public Border? Glass;
        public FrameworkElement? Shadow;
        public TranslateTransform? ShadowShift;
        /// <summary>接线时的动效档位（RefreshAll 据此判断要不要重摆）</summary>
        public UiMotionLevel Level;
        /// <summary>元素在窗口坐标系里的矩形（只在接线 / 改尺寸 / 滚动后重算）</summary>
        public Rect Bounds;
        public bool BoundsValid;
        public bool Wired;
        public bool Hovered;
        public bool Pressed;
    }
}
