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
using Starcloud.App.Services;

namespace Starcloud.App.Views;

/// <summary>抽屉内容展开/收起：高度平滑过渡 + 内容淡入位移。</summary>
public static class ExpandBehavior
{
    /// <summary>展开时长：略长于收起，读起来"推出去"更从容（高度是依赖型动画，能短则短更不容易掉帧）</summary>
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(210);

    /// <summary>收起时长：比展开快，收得干脆</summary>
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>展开时内容起始上移量（px）</summary>
    private const float SlideOffset = 10f;

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
            MotionFx.FadeSlideIn(element, SlideOffset);
            return;
        }

        AnimateHeight(element, state, 0, target, ExpandDuration);
        MotionFx.FadeSlideIn(element, SlideOffset);
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

        MotionFx.FadeOut(element, 120);
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

            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = duration,
                EnableDependentAnimation = true, // 高度是依赖型属性，不开这个动画会被忽略
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
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
