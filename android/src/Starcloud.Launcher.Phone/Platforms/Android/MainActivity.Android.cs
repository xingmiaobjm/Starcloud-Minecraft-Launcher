// 主入口 Activity：手机版锁定横屏（SensorLandscape，与平板版一致——左侧栏卡片贴边布局）+
// 沉浸式全屏（对标 FCL）+ 挖孔屏（P40 Pro 前摄）安全边距捕获（ShortEdges + DisplayCutout）。
// 游戏页与管理页共用本 Activity（对齐平板版 MainActivity）。
// 键盘事件统一经输入枢纽投递给游戏；游戏未运行时放行系统处理。

using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Starcloud.Launcher.Phone.Platforms.Android;

namespace Starcloud.Launcher.Phone.Droid;

[Activity(
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    // 游戏运行中弹出软键盘/旋转/外接键盘等变更不能重建 Activity（会丢 Surface）
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden,
    Exported = true
)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    // ── 显示挖孔（摄像头）安全边距 ──
    // 手机版锁定横屏 + 沉浸式全屏，P40 Pro 这类挖孔屏在横屏时前摄落在屏幕左或右缘
    // （随重力正反横换边）。壳需要把侧栏/内容避开挖孔：这里在内容视图上挂
    // OnApplyWindowInsetsListener 捕获 DisplayCutout 的安全内边距（px），经静态属性
    // + 事件供 ShellRootView 换算成 DIP 应用到根网格 Padding。SupportedOSPlatformVersion
    // 是 28，DisplayCutout API 无需版本分支；minSdk 28 时 ShortEdges 恒可用。

    /// <summary>挖孔安全内边距（px，相对当前窗口方向；未布局时为 0）。</summary>
    public static (int Left, int Top, int Right, int Bottom) CutoutPaddingPx { get; private set; }

    /// <summary>挖孔内边距变化（旋转 / 首次布局）；在主线程触发。</summary>
    public static event Action? CutoutChanged;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);

        base.OnCreate(savedInstanceState);

        // 窗口延伸进挖孔区（沉浸式全屏下系统默认 Avoidance 会把内容推下来/留黑条，
        // ShortEdges 让窗口贴边，由壳自己按 CutoutPaddingPx 留白——侧栏正好让出摄像头）
        var attrs = Window?.Attributes;
        if (attrs is not null)
            attrs.LayoutInDisplayCutoutMode = global::Android.Views.LayoutInDisplayCutoutMode.ShortEdges;

        // 捕获挖孔内边距：监听内容视图的 insets 分发（旋转后方向相关的 px 值会刷新）
        var content = (global::Android.Views.ViewGroup?)Window?.DecorView
            .FindViewById(global::Android.Resource.Id.Content);
        content?.SetOnApplyWindowInsetsListener(new CutoutInsetsListener());

        ApplyImmersiveMode();
    }

    private sealed class CutoutInsetsListener : Java.Lang.Object,
        global::Android.Views.View.IOnApplyWindowInsetsListener
    {
        public global::Android.Views.WindowInsets OnApplyWindowInsets(
            global::Android.Views.View? v, global::Android.Views.WindowInsets? insets)
        {
            if (insets is not null)
            {
                var cutout = insets.DisplayCutout;
                var (l, t, r, b) = cutout is null
                    ? (0, 0, 0, 0)
                    : (cutout.SafeInsetLeft, cutout.SafeInsetTop,
                       cutout.SafeInsetRight, cutout.SafeInsetBottom);
                if (CutoutPaddingPx != (l, t, r, b))
                {
                    CutoutPaddingPx = (l, t, r, b);
                    CutoutChanged?.Invoke();
                }
            }
            // 不消费：让 Uno 自身的 insets 处理照常进行（原样返回 = 分发继续；
            // WindowInsets.Consumed 是 API 30+，minSdk 28 下不可用）
            return insets!;
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        // 对话框/下拉通知栏收起、焦点回到应用时重新进入沉浸（官方推荐补挂点）
        if (hasFocus)
            ApplyImmersiveMode();
    }

    /// <summary>沉浸式全屏：隐藏状态栏与导航栏，手势边缘滑动临时呼出</summary>
    private void ApplyImmersiveMode()
    {
        var window = Window;
        if (window is null)
            return;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // SetDecorFitsSystemWindows 在 API 35 标记过时，但 30-34 仍是官方路径（API 35+ 由系统默认收敛）
#pragma warning disable CA1422
            window.SetDecorFitsSystemWindows(false);
#pragma warning restore CA1422
            var controller = window.InsetsController;
            if (controller is not null)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                controller.SystemBarsBehavior =
                    (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
        else
        {
            // API 28/29：旧版 SystemUiFlags 沉浸式（sticky 模式手势滑出后自动回落）
            var decor = window.DecorView;
            if (decor is not null)
                decor.SystemUiFlags = SystemUiFlags.LayoutStable
                    | SystemUiFlags.HideNavigation
                    | SystemUiFlags.Fullscreen
                    | SystemUiFlags.ImmersiveSticky;
        }
    }

    /// <summary>键盘事件统一经输入枢纽投递给游戏；游戏未运行时放行系统处理</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (GameInputHub.HandleKey(e))
            return true;
        return base.DispatchKeyEvent(e);
    }
}
