// 启动延迟后台检查更新：不打扰首屏，游戏运行时避让，发现新版经 DispatcherQueue 弹窗

using Microsoft.UI.Xaml;
using Starcloud.App.Views;
using Starcloud.App.Views.Dialogs;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

internal static class UpdateStartup
{
    private static bool _started;

    internal static async Task RunAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));           // 让首屏先稳

            var s = AppServices.Instance;
            if (!s.Settings.AutoUpdateEnabled) return;
            if (GameSessionGuard.IsAnyGameAlive(s.Settings.DotMinecraft)) return; // 有游戏在跑（含跨进程）就避让

            SelfUpdateService.CleanupLeftovers();                // 清上次更新的下载缓存/staging/脚本

            var m = await s.SelfUpdate.FetchManifestAsync();
            if (!SelfUpdateService.IsNewerVersion(m.Version, SelfUpdateService.CurrentVersion()))
            {
                LauncherLog.Write("update", $"已是最新版本 {SelfUpdateService.CurrentVersion()}");
                return;
            }

            LauncherLog.Write("update", $"发现新版本 {m.Version}（当前 {SelfUpdateService.CurrentVersion()}）");
            App.MainWindow?.DispatcherQueue?.TryEnqueue(() =>
                UpdateDialog.Show((App.MainWindow!.Content as FrameworkElement)!.XamlRoot, m));
        }
        catch (Exception ex)
        {
            // 启动检查失败静默：不影响正常使用，可到设置页手动检查看具体原因
            LauncherLog.Error("update", "启动时检查更新失败", ex);
        }
    }
}
