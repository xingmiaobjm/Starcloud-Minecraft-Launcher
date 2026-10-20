// 抽屉展开/收起动效附加属性（大版本抽屉、加载器面板等）。
//
// 展开：高度 0 → 自然高度（依赖型动画，需 EnableDependentAnimation）+ 内容淡入上移；
// 收起：高度 → 0 后隐藏。高度动画是唯一需要排版参与的一环，因此时长压得短、
// 并且只在「一次测量 + 一条动画」里完成，避免逐帧反复测量。
// 内容淡入/位移走合成层独立动画（MotionFx），滚动或回填时不与排版打架。
//
// 动效关闭（设置项或系统动画偏好）时直接瞬时显隐；任一环节失败静默降级。

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views.Behaviors;

/// <summary>抽屉内容展开/收起：高度平滑过渡 + 内容淡入位移。</summary>
public static class ExpandBehavior
{
    /// <summary>展开时长：比进场再从容一点（时长由 MotionFx 全局节奏统一放大）</summary>
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(MotionFx.DrawerExpandMs);

    /// <summary>收起时长：比展开快，收得干脆</summary>
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(MotionFx.DrawerCollapseMs);

    /// <summary>展开时内容起始上移量（px）</summary>
    private const float SlideOffset = 12f;


    /// <summary>展开时内容淡入 + 上移时长（ms）：与高度动画同长，高度到位时内容正好收住</summary>
    private const int ContentSlideMs = MotionFx.DrawerExpandMs;

    /// <summary>
    /// 收起时内容淡出时长（ms）：<b>必须短于收起时长</b>。
    /// 淡出若晚于高度归零，元素会在还留着残影的那一帧被设成 Collapsed，看上去就是"收起卡一下"。
    /// </summary>
    private const int ContentFadeMs = MotionFx.DrawerFadeMs;

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.RegisterAttached(
            "IsExpanded",
            typeof(bool),
            typeof(ExpandBehavior),
            new PropertyMetadata(false, OnIsExpandedChanged));

    public static bool GetIsExpanded(DependencyObject element) => (bool)element.GetValue(IsExpandedProperty);

    public static void SetIsExpanded(DependencyObject element, bool value) => element.SetValue(IsExpandedProperty, value);

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true) Expand(element);
        else Collapse(element);
    }

    private static void Expand(FrameworkElement element)
    {
        var state = States.GetOrCreateValue(element);
        Stop(state);

        // 先按自然高度量一次（此时元素需可见），拿到目标高度后再从 0 动画过去
        element.Visibility = Visibility.Visible;
        element.Height = double.NaN;
        element.Opacity = 1;
        if (!AppServices.Instance.Motion.Enabled)
        {
            MotionFx.Reset(element);
            return;
        }

        double target;
        try
        {
            element.UpdateLayout();
            target = element.ActualHeight;
        }
        catch
        {
            MotionFx.Reset(element);
            return;
        }
        if (target <= 0)
        {
            // 拿不到高度（尚未布局）：直接展开，不做高度动画
            MotionFx.FadeSlideIn(element, SlideOffset, ContentSlideMs);
            return;
        }

        AnimateHeight(element, state, 0, target, ExpandDuration);
        // 内容淡入 + 轻微上移：非线性（Fluent 入场曲线），到位即止
        MotionFx.FadeSlideIn(element, SlideOffset, ContentSlideMs);
    }

    private static void Collapse(FrameworkElement element)
    {
        var state = States.GetOrCreateValue(element);
        Stop(state);

        if (!AppServices.Instance.Motion.Enabled || element.Visibility != Visibility.Visible)
        {
            FinishCollapse(element);
            return;
        }

        var current = element.ActualHeight;
        if (current <= 0)
        {
            FinishCollapse(element);
            return;
        }

        MotionFx.FadeOut(element, ContentFadeMs);
        AnimateHeight(element, state, current, 0, CollapseDuration, onDone: () => FinishCollapse(element));
    }

    private static void FinishCollapse(FrameworkElement element)
    {
        element.Height = double.NaN;
        element.Visibility = Visibility.Collapsed;
        MotionFx.Reset(element);
    }

    /// <summary>高度动画：依赖型动画（会走排版），因此显式开 EnableDependentAnimation</summary>
    private static void AnimateHeight(FrameworkElement element, State state,
        double from, double to, TimeSpan duration, Action? onDone = null)
    {
        try
        {
            element.Height = from;

            // 高度是依赖型属性，只能用 XAML Storyboard（合成层拿不到），所以开 EnableDependentAnimation。
            // 曲线用 SplineDoubleKeyFrame 直接写 Fluent 关键样条本体，不再用内置缓动类近似。
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = duration,
                EnableDependentAnimation = true, // 不开这个，依赖型属性上的动画会被直接忽略
            };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value = from,
            });
            animation.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(duration),
                Value = to,
                KeySpline = to >= from ? MotionFx.CreateEnterSpline() : MotionFx.CreateExitSpline(),
            });
            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Height");

            if (onDone is not null)
            {
                storyboard.Completed += (_, _) =>
                {
                    state.Storyboard = null;
                    onDone();
                };
            }
            else
            {
                // 展开完成：交回自然高度，后续内容变化（例如筛选、重新搜索）不会被钉死
                storyboard.Completed += (_, _) =>
                {
                    state.Storyboard = null;
                    element.Height = double.NaN;
                };
            }

            storyboard.Children.Add(animation);
            state.Storyboard = storyboard;
            storyboard.Begin();
        }
        catch
        {
            element.Height = double.NaN;
            onDone?.Invoke();
        }
    }

    private static void Stop(State state)
    {
        try
        {
            state.Storyboard?.Stop();
        }
        catch
        {
            // 静默
        }
        state.Storyboard = null;
    }

    private sealed class State
    {
        public Storyboard? Storyboard;
    }
}
