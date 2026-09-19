// 合成层动效助手：把"淡入 + 位移"这类动效统一做成独立动画（跑在合成线程，不触发布局）。
//
// 设计取向：短、稳、有收尾（expo-out），只做一件事——内容从"略偏移 + 透明"过渡到"到位 + 不透明"，
// 以及反向的淡出。所有调用点都先问 MotionService：动画关闭时直接落到终态，零开销。
//
// 与 XAML Storyboard 的区别：这里动画的是 Composition visual 的 Opacity / Translation，
// 不经过 UI 线程排版，列表滚动或数据回填时不会互相打架。

using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Starcloud.App.Views;

namespace Starcloud.App.Services;

/// <summary>合成层动效助手（淡入位移 / 淡出）。</summary>
public static class MotionFx
{
    /// <summary>默认入场时长</summary>
    public const int EnterMs = 260;

    /// <summary>默认退场时长（退场应比入场快，读起来才利落）</summary>
    public const int ExitMs = 150;

    /// <summary>
    /// 淡入 + 位移到位。
    /// <para>元素必须先可见且已参与布局，否则拿不到 visual。</para>
    /// </summary>
    /// <param name="element">目标元素</param>
    /// <param name="fromY">起始纵向偏移（px，正值 = 从下方浮起）</param>
    /// <param name="durationMs">时长</param>
    /// <param name="delayMs">延迟（列表错峰入场用）</param>
    public static void FadeSlideIn(FrameworkElement element, float fromY = 16f,
        int durationMs = EnterMs, int delayMs = 0)
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

            // 透明度：短促的缓出淡入（延迟用于列表错峰）
            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);

            // 位移：用弹簧（WinUI 自然动效）——到位时轻微过冲再收住，比贝塞尔"活"
            var spring = compositor.CreateSpringVector3Animation();
            spring.InitialValue = new Vector3(0f, fromY, 0f);
            spring.FinalValue = Vector3.Zero;
            // 关键：周期只取时长的六成、阻尼接近临界。周期取满会让收敛时间远超预期，
            // 表现为"高度早就展开了、内容还在荡"的后摇。
            spring.DampingRatio = 0.92f;                       // <1 保留一点点回弹，但不晃
            spring.Period = TimeSpan.FromMilliseconds(Math.Max(40, durationMs * 0.6));
            spring.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            visual.StartAnimation("Translation", spring);
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
            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.4f, 0f), new Vector2(0.7f, 0.2f));

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

    /// <summary>列表错峰入场：第 i 项延迟 = min(i, <paramref name="maxStagger"/>) × <paramref name="stepMs"/></summary>
    public static void StaggerIn(FrameworkElement element, int index,
        int stepMs = 26, int maxStagger = 9, float fromY = 18f)
    {
        var step = Math.Clamp(index, 0, maxStagger) * stepMs;
        FadeSlideIn(element, fromY, EnterMs, step);
    }

    /// <summary>
    /// 列表回填后让可见项错峰入场。列表控件是虚拟化的，容器要等一帧才实现，
    /// 所以内部推迟到下一轮消息循环再取样；只动画「已经实现」的容器，滚动出来的新项不做动画（避免滚动时闪烁）。
    /// </summary>
    public static void StaggerListItems(ListViewBase list, int maxItems = 12, float fromY = 18f)
    {
        if (!AppServices.Instance.Motion.Enabled) return;
        list.DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                var count = Math.Min(list.Items.Count, maxItems);
                for (var i = 0; i < count; i++)
                    if (list.ContainerFromIndex(i) is FrameworkElement container)
                        StaggerIn(container, i, fromY: fromY);
            }
            catch
            {
                // 静默：入场动效失败不影响列表
            }
        });
    }
}
