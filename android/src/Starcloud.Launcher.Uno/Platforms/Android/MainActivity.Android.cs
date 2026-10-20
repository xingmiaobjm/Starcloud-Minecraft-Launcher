// 主入口 Activity：全应用锁定横屏（SensorLandscape，正反横向随重力）+ 沉浸式全屏（对标 FCL）；
// 游戏页与管理页共用本 Activity（对齐 MAUI 壳 MainActivity）。
// 键盘事件统一经输入枢纽投递给游戏；游戏未运行时放行系统处理。

using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Starcloud.Launcher.Uno.Platforms.Android;

namespace Starcloud.Launcher.Uno.Droid;

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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);

        base.OnCreate(savedInstanceState);
        ApplyImmersiveMode();
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
