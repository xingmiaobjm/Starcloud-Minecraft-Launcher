// 提示条动效：入场「从上往下掉落 + 回弹」，收起「三次方缓出」上浮淡出。
// 用合成层动画（ElementCompositionPreview），不碰 RenderTransform/布局属性；
// InfoBar.Closing 可取消 —— 先播收起动画再放行真正的关闭。

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace Starcloud.App.Services;

internal static class NoticeFx
{
    /// <summary>
    /// 已挂过的 InfoBar（引用相等）：页面是缓存页，每次重新进入 Loaded 都会调 Attach，
    /// 不加这道闸就会在同一个 InfoBar 上叠加 N 个 IsOpen 回调 ——
    /// 一次打开触发 N 次掉落动画，看起来就是「通知一直在播入场动画」。
    /// </summary>
    private static readonly HashSet<InfoBar> Attached = [];

    /// <summary>给 InfoBar 挂上掉落/收起动效（同一个实例只挂一次）。</summary>
    public static void Attach(InfoBar bar)
    {
        lock (Attached)
        {
            if (!Attached.Add(bar)) return;
        }

        // InfoBar 没有 Opening 事件：挂 IsOpen 属性回调当入场时机
        bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
        {
            if (bar.IsOpen) DropIn(bar);
        });
        var closing = false;
        bar.Closing += (_, e) =>
        {
            if (closing || !AppServices.Instance.Motion.Enabled) return;
            e.Cancel = true;
            closing = true;
            CollapseUp(bar, () =>
            {
                closing = false;
                bar.IsOpen = false;   // 放行真正的关闭（这次 Closing 会被 closing 标志放行）
            });
        };
    }

    /// <summary>入场：先摆到上方 80px，用弹簧（欠阻尼）落到位 —— 自带回弹。</summary>
    private static void DropIn(FrameworkElement bar)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            ElementCompositionPreview.SetIsTranslationEnabled(bar, true);
            var compositor = visual.Compositor;
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");

            if (!AppServices.Instance.Motion.Enabled)
            {
                visual.Opacity = 1f;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                return;
            }

            // 起点：上方 80px、全透明；透明度快速跟上（不掉落的过程干等）
            visual.Properties.InsertVector3("Translation", new Vector3(0, -80, 0));
            visual.Opacity = 0f;

            var spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = Vector3.Zero;
            spring.DampingRatio = 0.5f;                       // 欠阻尼 = 明显回弹
            spring.Period = TimeSpan.FromMilliseconds(110);
            visual.StartAnimation("Translation", spring);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(120);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
            visual.StartAnimation("Opacity", fade);
        }
        catch
        {
            // 静默：动效失败不影响提示本身
        }
    }

    /// <summary>收起：三次方缓出（cubic ease-out）上浮 28px + 淡出，完成后回调。</summary>
    private static void CollapseUp(FrameworkElement bar, Action completed)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            ElementCompositionPreview.SetIsTranslationEnabled(bar, true);
            var compositor = visual.Compositor;
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");

            // 三次方缓出：先快后慢（cubic-bezier(.215,.61,.355,1)）
            var easeOut = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f));

            // 不写起点关键帧：动画从当前值出发（快速关闭时弹簧可能还飞在半路）
            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Duration = TimeSpan.FromMilliseconds(170);
            slide.InsertKeyFrame(1f, new Vector3(0, -28, 0), easeOut);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(170);
            fade.InsertKeyFrame(0f, 1f);
            fade.InsertKeyFrame(1f, 0f, easeOut);

            // 两条动画都结束（或出错兜底）后再放行关闭
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) =>
            {
                try
                {
                    visual.StopAnimation("Translation");
                    visual.StopAnimation("Opacity");
                    visual.Opacity = 1f;
                    visual.Properties.InsertVector3("Translation", Vector3.Zero);
                }
                catch { /* 元素可能已卸载 */ }
                completed();
            };
            visual.StartAnimation("Translation", slide);
            visual.StartAnimation("Opacity", fade);
            batch.End();
        }
        catch
        {
            completed();   // 动效失败：直接放行关闭
        }
    }
}
