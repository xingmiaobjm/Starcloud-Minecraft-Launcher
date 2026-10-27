// 合成层动效助手：把"淡入 + 位移"这类动效统一做成独立动画（跑在合成线程，不触发布局）。
//
// 设计取向：短、稳、有收尾（expo-out），只做一件事——内容从"略偏移 + 透明"过渡到"到位 + 不透明"，
// 以及反向的淡出。所有调用点都先问 MotionService：动画关闭时直接落到终态，零开销。
//
// 与 XAML Storyboard 的区别：这里动画的是 Composition visual 的 Opacity / Translation，
// 不经过 UI 线程排版，列表滚动或数据回填时不会互相打架。

using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.App.Views;
using Windows.Foundation;

namespace Starcloud.App.Services;

/// <summary>合成层动效助手（淡入位移 / 淡出）。</summary>
public static class MotionFx
{
    // ── Fluent motion（微软商店/Windows 11 那套动效语言）──
    // 进场：Fast Out, Slow In —— 起手快、收尾稳；退场：Slow Out, Fast In —— 走得干脆。
    // 两者都是非线性贝塞尔，且刻意不含回弹（Fluent 明确避免弹簧式回荡）。

    /// <summary>Fluent 入场控制点（Fast Out, Slow In）</summary>
    private static readonly Vector2 EnterControl1 = new(0.1f, 0.9f);
    private static readonly Vector2 EnterControl2 = new(0.2f, 1f);

    /// <summary>Fluent 退场控制点（Slow Out, Fast In）</summary>
    private static readonly Vector2 ExitControl1 = new(0.7f, 0f);
    private static readonly Vector2 ExitControl2 = new(1f, 0.5f);

    /// <summary>
    /// 全局动效节奏系数：项目刻意比 Fluent 标准档再从容一档（时长整体放大约 1.5 倍）。
    /// 只放大时长、不改曲线形状——动效语言与缓动形状不变，只是被看见得更充分。
    /// 零散写死的时长统一走 <see cref="Scale"/>，不要再各自写魔数。
    /// </summary>
    private const double Pace = 1.5;

    /// <summary>把某个标准档时长按全局节奏放大</summary>
    public static int Scale(int ms) => (int)Math.Round(ms * Pace);

    /// <summary>默认入场时长（页面骨架）：切页要"跟手"，比列表羽化（400ms）快半拍。</summary>
    public const int EnterMs = (int)(220 * Pace);

    /// <summary>默认退场时长（退场必须比入场快，读起来才利落）</summary>
    public const int ExitMs = (int)(140 * Pace);

    /// <summary>
    /// 框架 PointerOver 的基准时长。WinUI 3 自己的 <c>VisualTransition</c>／状态切换取这一档（167ms），
    /// 拿不准指针反馈该用多久时就对齐它；同样按 <see cref="Pace"/> 放大。
    /// </summary>
    public const int PointerOverMs = (int)(167 * Pace);

    /// <summary>指针悬停淡入时长（Fluent Normal 档；比 PointerOverMs 再缓一档，避免"啪一下"亮起来）</summary>
    public const int HoverFadeInMs = (int)(200 * Pace);

    /// <summary>指针悬停淡出时长（Fluent Slow 档，刻意比淡入长，退出来才有可见的尾迹）</summary>
    public const int HoverFadeOutMs = (int)(300 * Pace);

    /// <summary>折叠区箭头旋转（右 → 下）时长</summary>
    public const int ChevronMs = (int)(220 * Pace);

    /// <summary>抽屉展开时长：比进场再从容一点，读起来"推出去"更确定</summary>
    public const int DrawerExpandMs = (int)(280 * Pace);

    /// <summary>抽屉收起时长：比展开快，收得干脆</summary>
    public const int DrawerCollapseMs = (int)(200 * Pace);

    /// <summary>
    /// 抽屉收起时的内容淡出时长。<b>必须短于 <see cref="DrawerCollapseMs"/></b>：
    /// 淡出若晚于高度归零，内容会在还留着残影时被整体移除，看上去就是"收起卡一下"。
    /// </summary>
    public const int DrawerFadeMs = (int)(160 * Pace);

    /// <summary>
    /// XAML <see cref="Storyboard"/> 用的 Fluent 进场样条（Fast Out, Slow In）。
    /// 依赖型属性（如高度）上不了合成层，只能用 <see cref="SplineDoubleKeyFrame"/> 直接写样条本体。
    /// </summary>
    public static KeySpline CreateEnterSpline() => new()
    {
        ControlPoint1 = new Point(0.1, 0.9),
        ControlPoint2 = new Point(0.2, 1),
    };

    /// <summary>XAML <see cref="Storyboard"/> 用的 Fluent 退场样条（Slow Out, Fast In）。</summary>
    public static KeySpline CreateExitSpline() => new()
    {
        ControlPoint1 = new Point(0.7, 0),
        ControlPoint2 = new Point(1, 0.5),
    };

    /// <summary>XAML <see cref="Storyboard"/> 用的匀称淡入淡出样条，与 <see cref="FadeEase"/> 同形。</summary>
    public static KeySpline CreateFadeSpline() => new()
    {
        ControlPoint1 = new Point(0.33, 0),
        ControlPoint2 = new Point(0.67, 1),
    };

    /// <summary>
    /// 三次贝塞尔缓动的纵向解：给定横轴进度求曲线上的值（二分求解，够精确也不会跑飞）。
    /// 高度 / 宽度这类上不了合成层的属性只能逐帧补间，用它取到与样条同形的进度，
    /// 补间那条腿才和合成层动画（淡入淡出、刻度旋转）是同一条曲线。
    /// </summary>
    public static double EaseProgress(KeySpline spline, double x)
    {
        if (x <= 0d) return 0d;
        if (x >= 1d) return 1d;

        double x1 = spline.ControlPoint1.X, y1 = spline.ControlPoint1.Y;
        double x2 = spline.ControlPoint2.X, y2 = spline.ControlPoint2.Y;

        double lo = 0d, hi = 1d, t = x;
        for (int i = 0; i < 20; i++)
        {
            double u = 1d - t;
            double xt = 3d * u * u * t * x1 + 3d * u * t * t * x2 + t * t * t;
            if (Math.Abs(xt - x) < 1e-4) break;
            if (xt < x) lo = t; else hi = t;
            t = (lo + hi) / 2d;
        }

        double v = 1d - t;
        return 3d * v * v * t * y1 + 3d * v * t * t * y2 + t * t * t;
    }

    /// <summary>
    /// 把一颗刻度旋到指定角度。走合成层旋转，中心钉在字形正中，描边不会绕着角甩出去。
    /// <para>方向约定：左栏版本卡那颗展开态朝上、收起态朝下（它的内容是往上收）；右栏账户卡那颗反向。</para>
    /// </summary>
    public static void RotateChevron(FrameworkElement chevron, float target, bool motion)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(chevron);
            visual.CenterPoint = new Vector3(
                (float)(chevron.ActualWidth > 0 ? chevron.ActualWidth / 2 : 6),
                (float)(chevron.ActualHeight > 0 ? chevron.ActualHeight / 2 : 3),
                0f);

            if (!motion)
            {
                visual.StopAnimation("RotationAngleInDegrees");
                visual.RotationAngleInDegrees = target;
                return;
            }

            var compositor = visual.Compositor;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(ChevronMs);
            anim.InsertKeyFrame(1f, target, ControlEase(compositor));
            visual.StartAnimation("RotationAngleInDegrees", anim);
        }
        catch
        {
            // 静默：动效失败不影响收起 / 展开
        }
    }

    /// <summary>
    /// 缓动函数缓存：<c>CreateCubicBezierEasingFunction</c> 每次调用都会新建一个合成对象，
    /// 而缓动本身是不可变的、可以跨动画复用。悬停、列表入场这些路径每帧都可能取一次，
    /// 原先每次都新建，纯属给合成线程添垃圾；这里按 Compositor 各存一份。
    /// </summary>
    private static readonly ConditionalWeakTable<Compositor, Eases> EaseCache = new();

    private sealed class Eases
    {
        public CompositionEasingFunction? Enter;
        public CompositionEasingFunction? Exit;
        public CompositionEasingFunction? Control;
        public CompositionEasingFunction? Fade;
    }

    /// <summary>合成层 Fluent 进场缓动（Fast Out, Slow In）。</summary>
    public static CompositionEasingFunction EnterEase(Compositor compositor)
    {
        var eases = EaseCache.GetOrCreateValue(compositor);
        return eases.Enter ??= compositor.CreateCubicBezierEasingFunction(EnterControl1, EnterControl2);
    }

    /// <summary>合成层 Fluent 退场缓动（Slow Out, Fast In）。</summary>
    public static CompositionEasingFunction ExitEase(Compositor compositor)
    {
        var eases = EaseCache.GetOrCreateValue(compositor);
        return eases.Exit ??= compositor.CreateCubicBezierEasingFunction(ExitControl1, ExitControl2);
    }

    /// <summary>
    /// 合成层 Fluent 控件缓动（ControlFastOutSlowIn，<c>(0.16,1)/(0.3,1)</c>）：
    /// 用于小控件、指示器这类"要比表面更快收尾"的位移。
    /// </summary>
    public static CompositionEasingFunction ControlEase(Compositor compositor)
    {
        var eases = EaseCache.GetOrCreateValue(compositor);
        return eases.Control ??= compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
    }

    /// <summary>
    /// 纯明暗交叉淡入淡出用的缓动：匀称的缓入缓出，中途斜率最大。
    /// <para>Fluent 的进场/退场样条都是"起步极快"型（30% 时间就走完 87%），用在阴影、高亮这类
    /// <b>纯明暗渐变</b>上会退化成"啪一下没了"——这正是悬停阴影显得生硬的原因；
    /// 匀称淡出才能让整个淡出过程被看见。内容进出场仍用 EnterEase/ExitEase。</para>
    /// </summary>
    public static CompositionEasingFunction FadeEase(Compositor compositor)
    {
        var eases = EaseCache.GetOrCreateValue(compositor);
        return eases.Fade ??= compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.33f, 0f), new Vector2(0.67f, 1f));
    }

    /// <summary>
    /// 淡入 + 位移到位。
    /// <para>元素必须先可见且已参与布局，否则拿不到 visual。</para>
    /// </summary>
    /// <param name="element">目标元素</param>
    /// <param name="fromY">起始纵向偏移（px，正值 = 从下方浮起）</param>
    /// <param name="durationMs">时长</param>
    /// <param name="delayMs">延迟（列表错峰入场用）</param>
    /// <param name="fromX">起始横向偏移（px，负值 = 从左侧滑入，即「从左往右移出」）</param>
    public static void FadeSlideIn(FrameworkElement element, float fromY = 16f,
        int durationMs = EnterMs, int delayMs = 0, float fromX = 0f)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            if (!AppServices.Instance.Motion.Enabled)
            {
                visual.StopAnimation("Opacity");
                visual.StopAnimation("Translation");
                visual.Opacity = 1f;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                return;
            }

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");

            // 透明度与位移都走 Fluent 入场曲线（关键帧 + 错峰延迟）
            var ease = EnterEase(compositor);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);

            // 位移：非线性（Fluent 入场曲线），到位即止
            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Duration = TimeSpan.FromMilliseconds(durationMs);
            slide.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            slide.InsertKeyFrame(0f, new Vector3(fromX, fromY, 0f));
            slide.InsertKeyFrame(1f, Vector3.Zero, ease);
            visual.StartAnimation("Translation", slide);
        }
        catch
        {
            // 静默：动效失败不影响功能
        }
    }

    /// <summary>淡出（元素保持可见，由调用方在结束后自行切换显隐）</summary>
    public static void FadeOut(FrameworkElement element, int durationMs = ExitMs)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);

            if (!AppServices.Instance.Motion.Enabled)
            {
                visual.StopAnimation("Opacity");
                visual.Opacity = 1f;
                return;
            }

            var compositor = visual.Compositor;
            var ease = ExitEase(compositor);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.InsertKeyFrame(1f, 0f, ease);
            visual.StartAnimation("Opacity", fade);
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>只做淡入、不做位移：用于"高度已经在动，再叠位移就会显得多余/拖尾"的场景。</summary>
    /// <summary>淡到指定不透明度（常驻元素收起时用：不是回到 0，只是退回静息态）</summary>
    public static void FadeTo(FrameworkElement element, double to, int durationMs = EnterMs)
    {
        if (element is null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (!AppServices.Instance.Motion.Enabled)
            {
                visual.StopAnimation("Opacity");
                visual.Opacity = (float)to;
                element.Opacity = to;
                return;
            }
            var compositor = visual.Compositor;
            var ease = ExitEase(compositor);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.InsertKeyFrame(1f, (float)to, ease);
            visual.StartAnimation("Opacity", fade);
        }
        catch
        {
            // 静默
        }
    }

    public static void FadeIn(FrameworkElement element, int durationMs = EnterMs, int delayMs = 0)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (!AppServices.Instance.Motion.Enabled)
            {
                visual.StopAnimation("Opacity");
                visual.Opacity = 1f;
                return;
            }

            var compositor = visual.Compositor;
            visual.StopAnimation("Opacity");
            var ease = EnterEase(compositor);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);
        }
        catch
        {
            // 静默：动效失败不影响功能
        }
    }

    /// <summary>把元素瞬间复位到"可见且不透明"（切换/复用前调用，避免残留状态）</summary>
    public static void Reset(FrameworkElement element)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1f;
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>
    /// 列表错峰入场：第 i 项延迟 = min(i, <paramref name="maxStagger"/>) × <paramref name="stepMs"/>。
    /// 默认横向（从左往右羽化移出）——内容页的列表条目按这个方向出场，与页面骨架的纵向浮起区分开。
    /// </summary>
    public static void StaggerIn(FrameworkElement element, int index,
        int stepMs = (int)(26 * Pace), int maxStagger = 9, float fromY = 0f, float fromX = -30f)
    {
        var step = Math.Clamp(index, 0, maxStagger) * stepMs;
        FadeSlideIn(element, fromY, EnterMs, step, fromX);
    }

    /// <summary>
    /// 列表项「从左往右羽化」：TranslateTransform 横向滑入 + 淡入。
    /// 用 XAML 的 TranslateTransform（最稳的 2D 变换）：元素创建时就挂上，
    /// Storyboard 动画它的 X；基础延迟留给页面转场，避免动画在切页过程中播完看不见。
    /// </summary>
    public static void FeatherIn(FrameworkElement element, int index,
        int stepMs = 38, int maxStagger = 10, float fromX = -90f)
    {
        try
        {
            if (!AppServices.Instance.Motion.Enabled) return;

            if (element.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }
            transform.X = fromX;
            element.Opacity = 0;

            var delay = TimeSpan.FromMilliseconds(
                150 + Math.Clamp(index, 0, maxStagger) * stepMs);
            var duration = TimeSpan.FromMilliseconds(400);
            var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

            var sb = new Storyboard();

            var slide = new DoubleAnimation
            {
                From = fromX,
                To = 0,
                Duration = duration,
                BeginTime = delay,
                EasingFunction = fadeEase,
            };
            Storyboard.SetTarget(slide, element);
            Storyboard.SetTargetProperty(slide,
                "(UIElement.RenderTransform).(TranslateTransform.X)");
            sb.Children.Add(slide);

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration,
                BeginTime = delay,
                EasingFunction = fadeEase,
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            sb.Begin();
            sb.Completed += (_, _) =>
            {
                try
                {
                    transform.X = 0;
                    element.Opacity = 1;
                }
                catch { /* 元素可能已回收 */ }
            };
        }
        catch
        {
            // 静默：入场动效失败不影响列表
        }
    }

    /// <summary>动画作用在模板根元素上，而不是 ListViewItem 容器：
    /// 容器的 RenderTransform 归控件模板与拖拽逻辑所有，覆盖它会打架。</summary>
    private static FrameworkElement AnimationTargetOf(FrameworkElement container) =>
        (container as ContentControl)?.ContentTemplateRoot as FrameworkElement ?? container;

    /// <summary>
    /// 列表回填后让可见项错峰入场。坑：<c>ItemsSource</c> 刚换完时容器往往还没实现
    /// （布局没跑），直接取样 <c>ContainerFromIndex</c> 会全部扑空——条目瞬间落定、
    /// 入场动画整体没跑。所以这里先强制布局一轮，仍拿不到容器就再让几轮重试。
    /// <para>只动画首批可见容器；滚动出来的新项不做动画（避免滚动时闪烁）。</para>
    /// </summary>
    public static void StaggerListItems(ListViewBase list, int maxItems = 8, float fromY = 0f,
        float fromX = -90f)
    {
        if (!AppServices.Instance.Motion.Enabled) return;
        var attempts = 0;
        void Sample()
        {
            try
            {
                // 必须先强制布局：虚拟化列表的 ListViewItem 是布局阶段生成的，
                // 刚设置 ItemsSource 时视觉树里一个容器都没有（日志里的「没有容器可播」）。
                list.UpdateLayout();
                var containers = CollectContainers(list).Take(maxItems).ToList();
                if (containers.Count == 0 && ++attempts <= 8)
                {
                    list.DispatcherQueue?.TryEnqueue(Sample);
                    return;
                }

                for (var i = 0; i < containers.Count; i++)
                    FeatherIn(AnimationTargetOf(containers[i]), i, fromX: fromX);

                Starcloud.Core.Services.LauncherLog.Write("motion", containers.Count == 0
                    ? $"列表入场动画没有容器可播：{list.Name} items={list.Items.Count}"
                    : $"入场动画已播：{list.Name} animated={containers.Count}/{list.Items.Count}");
            }
            catch
            {
                // 静默：入场动效失败不影响列表
            }
        }
        list.DispatcherQueue?.TryEnqueue(Sample);
    }

    /// <summary>按视觉树收集已实现的列表项容器（虚拟化下列表只会实现可见的那几项）。</summary>
    private static IEnumerable<FrameworkElement> CollectContainers(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListViewItem item)
                yield return item;
            else
                foreach (var nested in CollectContainers(child))
                    yield return nested;
        }
    }
}
