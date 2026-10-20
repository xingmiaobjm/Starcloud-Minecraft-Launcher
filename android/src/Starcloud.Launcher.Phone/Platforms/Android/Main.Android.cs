// Uno 原生 Application 入口：主题 / 图标 / 大堆；OnCreate 里先重定向
// STARCLOUD_DATA_DIR（Core 的 LauncherPaths 是静态一次求值，必须在首次访问前设置）。

using Android.Runtime;

namespace Starcloud.Launcher.Phone.Droid;

[global::Android.App.ApplicationAttribute(
    Label = "Starcloud Phone",
    Icon = "@mipmap/icon",
    LargeHeap = true,
    HardwareAccelerated = true,
    Theme = "@style/Theme.App.Starting"
)]
public class Application : Microsoft.UI.Xaml.NativeApplication
{
    public Application(IntPtr javaReference, JniHandleOwnership transfer)
        : base(() => new App(), javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        // Core 的 LauncherPaths.DataDir 是静态一次求值，默认走 Linux 分支（~/.local/share），
        // 在安卓上不可写；必须在首次访问前重定向到外部私有目录（与 GameLauncherService 同根）。
        var external = GetExternalFilesDir(null);
        if (external?.AbsolutePath is { Length: > 0 } path)
            Environment.SetEnvironmentVariable("STARCLOUD_DATA_DIR", path);
        base.OnCreate();
    }
}
