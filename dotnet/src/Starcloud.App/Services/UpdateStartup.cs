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
            // 云配置 + 活跃上报（各自失败即静默；S3 加速镜像的参与由配置决定）
            await s.Cloud.RefreshConfigAsync();
            s.Download.Sources.StarcloudMirrorEnabled = s.Cloud.Config.S3Mirror;
            _ = s.Cloud.ReportActiveAsync(SelfUpdateService.CurrentVersion());   // fire-and-forget：永不阻塞启动

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

            // 云控静默更新：服务器允许且用户开了自动更新 → 不弹窗，后台下载完直接静默升级
            if (s.Cloud.Config.SilentUpdate)
            {
                LauncherLog.Write("update", "云控静默更新已启用：后台下载并静默升级");
                _ = SilentApplyAsync(s, m);
                return;
            }

            App.MainWindow?.DispatcherQueue?.TryEnqueue(() =>
                UpdateDialog.Show((App.MainWindow!.Content as FrameworkElement)!.XamlRoot, m));
        }
        catch (Exception ex)
        {
            // 启动检查失败静默：不影响正常使用，可到设置页手动检查看具体原因
            LauncherLog.Error("update", "启动时检查更新失败", ex);
        }
    }

    /// <summary>
    /// 云控静默更新：下载新版本 → 直接走静默升级脚本（进程退出、升级、自动重启）。
    /// 全程无 UI；失败落日志，下次启动重试。
    /// </summary>
    private static async Task SilentApplyAsync(AppServices s, UpdateManifest m)
    {
        try
        {
            // 清单是具名字段（setup = NSIS 安装器 / portable = 便携 zip），按能力挑
            var (artifact, form) = (m.Setup, UpdateInstallForm.NsisInstaller);
            if (artifact is null)
            {
                artifact = m.Portable;
                form = UpdateInstallForm.Portable;
            }
            if (artifact is null)
            {
                LauncherLog.Write("update", "静默更新中止：清单里没有可用产物");
                return;
            }
            var path = await s.SelfUpdate.DownloadArtifactAsync(artifact, null);
            LauncherLog.Write("update", $"静默更新下载完成：{path}，即将退出并升级");
            await Task.Delay(TimeSpan.FromSeconds(2));   // 给日志一点落盘时间
            s.SelfUpdate.LaunchUpdaterScript(Environment.ProcessId, form, path, m.Version);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("update", "静默更新失败（下次启动重试）", ex);
        }
    }
}
