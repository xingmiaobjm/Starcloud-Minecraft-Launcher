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
    // ── Fluent motion（微软商店/Windows 11 那套动效语言）──
    // 进场：Fast Out, Slow In —— 起手快、收尾稳；退场：Slow Out, Fast In —— 走得干脆。
    // 两者都是非线性贝塞尔，且刻意不含回弹（Fluent 明确避免弹簧式回荡）。

    /// <summary>Fluent 入场控制点（Fast Out, Slow In）</summary>
    private static readonly Vector2 EnterControl1 = new(0.1f, 0.9f);
    private static readonly Vector2 EnterControl2 = new(0.2f, 1f);

    /// <summary>Fluent 退场控制点（Slow Out, Fast In）</summary>
    private static readonly Vector2 ExitControl1 = new(0.7f, 0f);
    private static readonly Vector2 ExitControl2 = new(1f, 0.5f);

    /// <summary>默认入场时长（Fluent：中小表面 250ms 一档）</summary>
    public const int EnterMs = 250;

    /// <summary>默认退场时长（退场必须比入场快，读起来才利落）</summary>
    public const int ExitMs = 140;

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

            // 透明度与位移都走 Fluent 入场曲线（同样是当量缓动，只是用关键帧配合错峰延迟）
            var ease = compositor.CreateCubicBezierEasingFunction(EnterControl1, EnterControl2);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);

            // 位移：非线性（Fluent 入场曲线），到位即止、不回弹也不拖尾
            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Duration = TimeSpan.FromMilliseconds(durationMs);
            slide.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            slide.InsertKeyFrame(0f, new Vector3(0f, fromY, 0f));
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
            var ease = compositor.CreateCubicBezierEasingFunction(ExitControl1, ExitControl2);

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

    /// <summary>只做淡入、不做位移：用于"高度已经在动，再叠位移就会显得多余/拖尾"的场景（如抽屉展开）。</summary>
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
            var ease = compositor.CreateCubicBezierEasingFunction(EnterControl1, EnterControl2);
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
