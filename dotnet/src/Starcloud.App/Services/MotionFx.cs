// 合成层动效助手：把"淡入 + 位移"这类动效统一做成独立动画（跑在合成线程，不触发布局）。
//
// 设计取向：短、稳、有收尾（expo-out），只做一件事——内容从"略偏移 + 透明"过渡到"到位 + 不透明"，
// 以及反向的淡出。所有调用点都先问 MotionService：动画关闭时直接落到终态，零开销。
//
// 与 XAML Storyboard 的区别：这里动画的是 Composition visual 的 Opacity / Translation，
// 不经过 UI 线程排版，列表滚动或数据回填时不会互相打架。

using System.Numerics;
using Microsoft.UI.Xaml;
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

            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);

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
}
