// 列表选中态的共用实现：选中行淡底 + 整条列表共用的一根竖条（「提示条」）。
//
// 为什么竖条要整块列表共用一根、而不是每行各画一条：换行时若两条各做淡入淡出，
// 眼睛看到的是「旧条消失、新条出现」；共用一根做真实位移才是「一根条被拉过去」。
// 位移曲线由前端 / 尾端两条不同步的曲线叠加而成（移动中拉伸），
// 落点前整条还会向前冲过头一点、再由阻尼拉回 —— 这一下过冲是「Q弹」的来源，
// 只有拉伸没有过冲的话，读起来仍然像硬停。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.App.Services;
using Windows.Foundation;

namespace Starcloud.App.Views.Shared;

public static class ListSelectionFx
{
    // ── 选中行淡底 ──

    private sealed class InsetFade
    {
        public bool Shown;
        public bool Animating;
        public Storyboard? Story;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, InsetFade> InsetFades = new();

    /// <summary>按列表当前选中项刷新所有已生成容器的选中淡底（模板里 Tag="inset" 的那层）。</summary>
    /// <param name="animate">true = 切换时淡入淡出；false = 直接给终态（初始化 / 容器复用）</param>
    public static void SyncInset(ListViewBase list, bool animate)
    {
        var selected = list.SelectedIndex;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is not ListViewItem container) continue;
            if (FindTagged(container, "inset") is not { } mark) continue;
            SetInsetState(mark, i == selected, animate);
        }
    }

    /// <summary>
    /// 只刷新一个容器的选中淡底。容器回收（ContainerContentChanging）时用这个：
    /// 每回收一格就整表遍历一次的话，存档几百条时会在布局阶段做平方级的无效扫描。
    /// </summary>
    public static void SyncInsetFor(ListViewBase list, int index, DependencyObject container, bool animate)
    {
        if (container is not ListViewItem item) return;
        if (FindTagged(item, "inset") is not { } mark) return;
        SetInsetState(mark, index == list.SelectedIndex, animate);
    }

    /// <summary>
    /// 选中淡底的显隐。
    /// <para>必须区分「正在动画」和「终态已就位」：容器被回收复用时状态会残留，
    /// 只按布尔值早退会把上一行的标记留在新行上（实测出现过）。</para>
    /// </summary>
    private static void SetInsetState(FrameworkElement mark, bool show, bool animate)
    {
        var state = InsetFades.GetOrCreateValue(mark);

        void Snap()
        {
            state.Story?.Stop();
            state.Story = null;
            state.Animating = false;
            state.Shown = show;
            mark.Opacity = show ? 1 : 0;
            mark.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!animate || !AppServices.Instance.Motion.Enabled)
        {
            Snap();
            return;
        }

        if (state.Shown == show)
        {
            if (state.Animating) return;
            Snap();
            return;
        }

        state.Shown = show;
        state.Story?.Stop();
        state.Animating = true;
        if (show) mark.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation
        {
            From = mark.Opacity,
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(show ? 260 : 220),
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
        };
        Storyboard.SetTarget(fade, mark);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var story = new Storyboard();
        story.Children.Add(fade);
        story.Completed += (_, _) =>
        {
            state.Animating = false;
            if (state.Story != story) return;
            mark.Opacity = show ? 1 : 0;
            mark.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        };
        state.Story = story;
        story.Begin();
    }

    // ── 选中竖条 ──

    /// <summary>
    /// 把竖条移到当前选中行：从上一行的位置滑过去，移动中纵向拉伸，
    /// 落点前整体越过目标再弹回（过冲量随移动距离增大），落定后收回原长。
    /// <para>barY / barShown 由调用页持有：列表重建时把 barY 置 NaN、barShown 置 false，
    /// 下一次调用就会直接落位而不播位移动画。</para>
    /// </summary>
    public static void UpdateBar(Canvas layer, Border bar, ListViewBase list,
        ref double barY, ref bool barShown, bool animate)
    {
        var index = list.SelectedIndex;
        if (index < 0 || list.ContainerFromIndex(index) is not ListViewItem container)
        {
            bar.Opacity = 0;
            layer.Visibility = Visibility.Collapsed;
            barShown = false;
            barY = double.NaN;
            return;
        }

        if (bar.RenderTransform is not CompositeTransform transform) return;

        var origin = container.TransformToVisual(layer).TransformPoint(new Point(0, 0));
        var height = Math.Max(12, container.ActualHeight - 20);
        var y = origin.Y + 10;

        // 贴着行左缘画（不再内缩 4），两页的竖条都更靠卡片左边
        Canvas.SetLeft(bar, Math.Max(0, origin.X));
        bar.Height = height;
        layer.Visibility = Visibility.Visible;

        var motion = AppServices.Instance.Motion.Enabled;
        if (!animate || !motion || !barShown || double.IsNaN(barY))
        {
            // 首次出现 / 初始化 / 滚动同步：直接落位，不播位移动画
            transform.TranslateY = y;
            transform.ScaleY = 1;
            bar.Opacity = 1;
            barY = y;
            barShown = true;
            return;
        }

        var from = barY;
        var movingDown = y > from;
        barY = y;

        // 上端固定为变换原点：条的上沿 = TranslateY，下沿 = TranslateY + ScaleY × 高度。
        bar.RenderTransformOrigin = new Point(0.5, 0);

        const int steps = 96;
        const int durationMs = 320;

        var topKeys = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(durationMs) };
        var scaleKeys = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(durationMs) };

        var fromTop = from;
        var fromBottom = from + height;
        var toTop = y;
        var toBottom = y + height;

        // 过冲量随移动距离增长（4~14 DIP）：跨几行时冲得明显，邻行切换时也不至于撞到相邻行
        var overshoot = Math.Clamp(Math.Abs(toTop - fromTop) * 0.28, 4, 14);
        var direction = movingDown ? 1 : -1;

        for (var i = 0; i <= steps; i++)
        {
            var x = i / (double)steps;

            // 两端曲线：前端二次方、尾端三次方。x³ ≤ x² 恒成立，
            // 所以尾端永远落后于前端（条被拉长），两者同时到达终点收拢。
            var leading = x * x;
            var gap = x * x - x * x * x;
            var wobble = 0.60 + 0.55 * Math.Sin(Math.PI * 2 * Math.Pow(1 - x, 1.5));
            var trailing = x * x * x + gap * Math.Clamp(wobble, 0, 0.97);

            double top, bottom;
            if (movingDown)
            {
                bottom = fromBottom + (toBottom - fromBottom) * leading;   // 前端=下沿
                top = fromTop + (toTop - fromTop) * trailing;              // 尾端=上沿
            }
            else
            {
                top = fromTop + (toTop - fromTop) * leading;               // 前端=上沿
                bottom = fromBottom + (toBottom - fromBottom) * trailing;  // 尾端=下沿
            }

            // 落点前的整体过冲：62% 之后向前顶出最多 overshoot，再弹回 0。
            // 两端同时位移，条的厚度不受影响 —— 这一下才是「冲过头再弹回来」。
            if (x > 0.62)
            {
                var t = (x - 0.62) / 0.38;
                var push = direction * overshoot * Math.Sin(Math.PI * t);
                top += push;
                bottom += push;
            }

            var scale = (bottom - top) / height;
            // 落位前的"压一下再弹回"：末段先压到 ~0.76、再回到 1
            if (x > 0.72)
            {
                var t = (x - 0.72) / 0.28;
                scale *= 1 - 0.24 * Math.Sin(Math.PI * t);
            }
            scale = Math.Clamp(scale, 0.5, 2.10);

            var keyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs * x));
            topKeys.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = top });
            scaleKeys.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = scale });
        }

        Storyboard.SetTarget(topKeys, bar);
        Storyboard.SetTargetProperty(topKeys, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        Storyboard.SetTarget(scaleKeys, bar);
        Storyboard.SetTargetProperty(scaleKeys, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");

        var story = new Storyboard();
        story.Children.Add(topKeys);
        story.Children.Add(scaleKeys);
        story.Begin();
    }

    // ── 可视树小工具 ──

    /// <summary>在可视树里找带指定 Tag 的模板元素（模板内的名字在页面 Namescope 外）</summary>
    public static FrameworkElement? FindTagged(DependencyObject root, string tag)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && Equals(fe.Tag, tag)) return fe;
            if (FindTagged(child, tag) is { } found) return found;
        }
        return null;
    }

    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
