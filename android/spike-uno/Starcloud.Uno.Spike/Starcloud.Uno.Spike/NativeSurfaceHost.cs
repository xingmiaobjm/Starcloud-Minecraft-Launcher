// 原生 SurfaceView 嵌入验证件（Uno spike）。
// Skia 渲染器下的原生视图嵌入契约：把 Android.Views.View 直接设为 ContentControl.Content，
// 布局/透明度/可见性由宿主接管（官方文档：using-skia-hosting-native-controls）。
// Surface 生命周期回调向画布填充纯色——证明游戏画面后续 EGL 渲染链可挂载。

using Android.Graphics;
using Android.Views;
using Microsoft.UI.Xaml.Controls;

namespace Starcloud.Uno.Spike;

public sealed class NativeSurfaceHost : ContentControl
{
    public NativeSurfaceHost()
    {
        // ContentPresenter 默认贴左上，拉伸对齐才能铺满外层 Border
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) =>
        {
            if (Content is not null)
            {
                return;
            }
            // 本项目命名空间为 Starcloud.Uno.Spike，裸写 Uno./Android. 会被解析到
            // Starcloud.Uno./Starcloud.Android.（遮蔽），必须 global:: 限定。
            // SurfaceView 经视图树 attach 到窗口，用 Application.Context 即可
            var surface = new SurfaceView(global::Android.App.Application.Context);
            surface.Holder.AddCallback(new FillCallback());
            Content = surface;
        };
    }

    /// <summary>Surface 就绪后填充纯色，验证可画（游戏链路对应 EGL 初始化点）</summary>
    private sealed class FillCallback : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder)
        {
            var canvas = holder.LockCanvas();
            if (canvas is not null)
            {
                canvas.DrawColor(global::Android.Graphics.Color.DodgerBlue);
                holder.UnlockCanvasAndPost(canvas);
            }
        }

        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
        {
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
        }
    }
}
