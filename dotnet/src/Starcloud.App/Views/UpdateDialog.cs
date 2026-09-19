// 更新弹窗：发现新版 → 确认下载（进度）→ 就绪 → 重启安装
// 模式照 CrashDialog 的阶段机（args.Cancel / GetDeferral / 弹窗内行内状态）：
// WinUI 同一时刻只允许一个 ContentDialog，下载进度一律在弹窗内呈现，不嵌套二级对话框。

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

/// <summary>自更新确认弹窗</summary>
internal static class UpdateDialog
{
    /// <summary>展示更新弹窗；用户点「立即重启安装」后脚本已被拉起、应用随即退出</summary>
    public static async Task ShowAsync(XamlRoot root, UpdateManifest manifest)
    {
        var s = AppServices.Instance;
        var current = SelfUpdateService.CurrentVersion();

        var content = new StackPanel { Spacing = 10, MinWidth = 440, MaxWidth = 520 };

        content.Children.Add(new TextBlock
        {
            Text = Loc.T("当前版本 {0} → 新版本 {1}", current, manifest.Version),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });

        var notes = Loc.Current == "en-US" && !string.IsNullOrWhiteSpace(manifest.NotesEn)
            ? manifest.NotesEn!
            : Loc.TCore(manifest.Notes);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            content.Children.Add(new TextBlock
            {
                Text = Loc.T("更新说明"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
            });
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = notes,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = Loc.T("更新期间启动器会自动关闭，完成后自动重启。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        });

        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        content.Children.Add(status);
        content.Children.Add(bar);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("发现新版本 {0}", manifest.Version),
            Content = new ScrollViewer { Content = content, MaxHeight = 460 },
            PrimaryButtonText = Loc.T("立即更新"),
            CloseButtonText = Loc.T("稍后"),
            XamlRoot = root,
        };

        var form = SelfUpdateService.DetectInstallForm();
        var artifact = form == UpdateInstallForm.NsisInstaller ? manifest.Setup : manifest.Portable;
        var writable = form == UpdateInstallForm.NsisInstaller
            || SelfUpdateService.IsAppDirWritable(AppContext.BaseDirectory);
        if (!writable)
        {
            // 便携版放在只读目录（如 Program Files）时无法就地覆盖
            dlg.IsPrimaryButtonEnabled = false;
            ShowStatus(status, Loc.T("程序目录不可写，无法就地更新：{0}",
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)));
        }

        var downloading = false;
        var readyMode = false;
        var wantRestart = false;
        string artifactPath = "";

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            if (readyMode) { wantRestart = true; return; }      // 放行关闭 → 弹窗后拉脚本退出
            if (downloading) { args.Cancel = true; return; }
            if (artifact is null)
            {
                args.Cancel = true;
                ShowStatus(status, Loc.T("更新失败：{0}", Loc.T("清单缺少对应形态的下载文件")));
                return;
            }

            downloading = true;
            args.Cancel = true;                                  // 下载期间留在弹窗
            dlg.IsPrimaryButtonEnabled = false;
            var deferral = args.GetDeferral();
            try
            {
                ShowStatus(status, Loc.T("正在下载…"));
                bar.IsIndeterminate = true;
                bar.Visibility = Visibility.Visible;
                var progress = new Progress<double>(pct =>
                {
                    dlg.DispatcherQueue.TryEnqueue(() =>
                    {
                        bar.IsIndeterminate = false;
                        bar.Value = pct;
                        status.Text = Loc.T("正在下载… {0}%", (int)pct);
                    });
                });

                artifactPath = await s.SelfUpdate.DownloadArtifactAsync(artifact, progress);

                if (form == UpdateInstallForm.Portable)
                {
                    ShowStatus(status, Loc.T("正在准备安装…"));
                    await s.SelfUpdate.ExtractPortableAsync(artifactPath);
                }

                bar.Visibility = Visibility.Collapsed;
                ShowStatus(status, Loc.T("更新已就绪，点击「立即重启安装」完成升级（启动器将自动关闭并重启）。"));
                dlg.PrimaryButtonText = Loc.T("立即重启安装");
                readyMode = true;
                dlg.IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex)
            {
                bar.Visibility = Visibility.Collapsed;
                ShowStatus(status, Loc.T("更新失败：{0}", ex.Message));
                LauncherLog.Error("update", "更新下载/准备失败", ex);
                dlg.IsPrimaryButtonEnabled = true;               // 允许重试
            }
            finally
            {
                downloading = false;
                deferral.Complete();
            }
        };

        var result = await dlg.ShowAsync();

        if (result == ContentDialogResult.Primary && wantRestart)
        {
            try
            {
                s.SelfUpdate.LaunchUpdaterScript(Environment.ProcessId, form, artifactPath, manifest.Version);
            }
            catch (Exception ex)
            {
                LauncherLog.Error("update", "拉起更新脚本失败", ex);
                return;
            }
            Application.Current.Exit();
        }
    }

    private static void ShowStatus(TextBlock status, string text)
    {
        status.Text = text;
        status.Visibility = Visibility.Visible;
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
}
