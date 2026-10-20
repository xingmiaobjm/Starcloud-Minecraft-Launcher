// 游戏画面承载视图（Uno Skia 原生视图宿主）：
// Skia 渲染器下把 Android.Views.SurfaceView 直接设为 ContentControl.Content
// （官方契约：using-skia-hosting-native-controls），Surface 生命周期与触摸事件
// 在原生层捕获后转发给跨平台事件（合并 MAUI 壳的 GameSurfaceView + Handler 两层）。
// 命名空间避坑：裸写 Android.* 会解析到 Starcloud.Android.*，一律 global:: 限定。

using Microsoft.UI.Xaml.Controls;

namespace Starcloud.Launcher.Phone.Views;

/// <summary>触摸动作类型（与 Android MotionEvent 主动作一一对应）。</summary>
public enum SurfaceTouchAction
{
    Pressed,
    Moved,
    Released,
    Cancelled,
}

/// <summary>触摸事件参数（视图内坐标）。</summary>
public sealed class SurfaceTouchEventArgs : EventArgs
{
    public required SurfaceTouchAction Action { get; init; }
    public required float X { get; init; }
    public required float Y { get; init; }
}

public sealed class GameSurfaceHost : ContentControl
{
    /// <summary>原生 Surface 已创建（参数为 global::Android.Views.Surface，此后可注册给游戏窗口）。</summary>
    public event EventHandler<object?>? SurfaceCreated;

    /// <summary>原生 Surface 尺寸变化（像素）。</summary>
    public event EventHandler<(int Width, int Height)>? SurfaceChanged;

    /// <summary>原生 Surface 已销毁（需停止渲染）。</summary>
    public event EventHandler? SurfaceDestroyed;

    /// <summary>原生层触摸事件。</summary>
    public event EventHandler<SurfaceTouchEventArgs>? TouchReceived;

    public GameSurfaceHost()
    {
        // ContentPresenter 默认贴左上，拉伸对齐才能铺满外层容器
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) =>
        {
            if (Content is not null)
                return;
            // SurfaceView 经视图树 attach 到窗口，用 Application.Context 即可
            var surface = new global::Android.Views.SurfaceView(
                global::Android.App.Application.Context);
            surface.Holder!.AddCallback(new SurfaceHolderCallback(this));
            surface.SetOnTouchListener(new SurfaceTouchListener(this));
            Content = surface;
        };
    }

    /// <summary>转发 SurfaceHolder 三段生命周期回调。</summary>
    private sealed class SurfaceHolderCallback : Java.Lang.Object,
        global::Android.Views.ISurfaceHolderCallback
    {
        private readonly GameSurfaceHost _owner;

        public SurfaceHolderCallback(GameSurfaceHost owner) => _owner = owner;

        public void SurfaceCreated(global::Android.Views.ISurfaceHolder holder)
            => _owner.SurfaceCreated?.Invoke(_owner, holder.Surface);

        public void SurfaceChanged(global::Android.Views.ISurfaceHolder holder,
            global::Android.Graphics.Format format, int width, int height)
            => _owner.SurfaceChanged?.Invoke(_owner, (width, height));

        public void SurfaceDestroyed(global::Android.Views.ISurfaceHolder holder)
            => _owner.SurfaceDestroyed?.Invoke(_owner, EventArgs.Empty);
    }

    /// <summary>消费触摸事件并转发；返回 true 表示事件已被原生层处理。</summary>
    private sealed class SurfaceTouchListener : Java.Lang.Object,
        global::Android.Views.View.IOnTouchListener
    {
        private readonly GameSurfaceHost _owner;

        public SurfaceTouchListener(GameSurfaceHost owner) => _owner = owner;

        public bool OnTouch(global::Android.Views.View? v, global::Android.Views.MotionEvent? e)
        {
            if (e is null)
                return false;

            SurfaceTouchAction? action = e.ActionMasked switch
            {
                global::Android.Views.MotionEventActions.Down => SurfaceTouchAction.Pressed,
                global::Android.Views.MotionEventActions.Move => SurfaceTouchAction.Moved,
                global::Android.Views.MotionEventActions.Up => SurfaceTouchAction.Released,
                global::Android.Views.MotionEventActions.Cancel => SurfaceTouchAction.Cancelled,
                _ => null,
            };

            if (action.HasValue)
            {
                _owner.TouchReceived?.Invoke(_owner, new SurfaceTouchEventArgs
                {
                    Action = action.Value,
                    X = e.GetX(),
                    Y = e.GetY(),
                });
            }

            return true;
        }
    }
}
