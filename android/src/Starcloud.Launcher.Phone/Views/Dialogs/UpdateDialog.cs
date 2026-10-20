// 更新弹窗：发现新版 → 确认下载（进度）→ 就绪
// 模式照 CrashDialog 的阶段机（args.Cancel / GetDeferral / 弹窗内行内状态）：
// 同一时刻只允许一个 ContentDialog，下载进度一律在弹窗内呈现，不嵌套二级对话框。
// 安卓适配（PORTING 公约：桌面窗口专属流程 no-op 并注释）：
//   - 保留「下载 + 大小与 SHA256 校验」全流程（DownloadArtifactAsync 内部校验）；
//   - 桌面的安装形态探测（注册表 / uninstall.exe）与程序目录可写检查是 Windows 语义，跳过；
//   - 拉起更新脚本 + 自动重启环节 no-op：安卓包体是 APK，就地覆盖不适用，
//     校验完成后提示用户手动覆盖安装新版 APK。

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views.Dialogs;

/// <summary>自更新确认弹窗</summary>
internal static class UpdateDialog
{
    /// <summary>展示更新弹窗；安卓端完成下载校验后提示手动覆盖安装（不自动重启）</summary>
    public static async Task ShowAsync(XamlRoot root, UpdateManifest manifest)
    {
        var s = AppServices.Instance;
        var current = SelfUpdateService.CurrentVersion();

        // 手机版：去掉 MinWidth=440 固定下限（竖屏可用宽约 300-360 DIP 放不下），宽度随对话框自适应，上限 520 不变
        var content = new StackPanel { Spacing = 10, MaxWidth = 520 };

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
                FontWeight = FontWeights.Medium,
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

        // 桌面文案是「更新期间启动器会自动关闭，完成后自动重启」；安卓无自动安装链路，如实说明
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("将下载并校验新版更新包；安卓端暂不支持自动安装，完成后请覆盖安装新版安装包。"),
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

        // 桌面按安装形态选产物（NSIS 安装包 / 便携 zip）；安卓统一取便携 zip 产物做下载校验。
        // DetectInstallForm（Windows 注册表探测）与 IsAppDirWritable（安装目录可写性）都是
        // 桌面安装形态语义，安卓不适用，跳过（原判断允许禁用主按钮，见桌面蓝本）。
        var artifact = manifest.Portable;

        var downloading = false;
        var readyMode = false;
        var wantFinish = false;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            if (readyMode) { wantFinish = true; return; }        // 放行关闭 → 弹窗后收尾
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

                // 下载到更新缓存目录并做大小 + SHA256 校验，失败即抛（与桌面同一条校验链）
                await s.SelfUpdate.DownloadArtifactAsync(artifact, progress);

                bar.Visibility = Visibility.Collapsed;
                ShowStatus(status, Loc.T("更新包已下载并校验完成。请覆盖安装新版安装包后重启启动器。"));
                dlg.PrimaryButtonText = Loc.T("完成");
                readyMode = true;
                dlg.IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex)
            {
                bar.Visibility = Visibility.Collapsed;
                ShowStatus(status, Loc.T("更新失败：{0}", ex.Message));
                LauncherLog.Error("update", "更新下载/校验失败", ex);
                dlg.IsPrimaryButtonEnabled = true;               // 允许重试
            }
            finally
            {
                downloading = false;
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();

        if (wantFinish)
        {
            // 桌面在此拉起更新脚本（LaunchUpdaterScript）并 Application.Current.Exit() 退出进程；
            // 安卓无对应重启机制（APK 覆盖安装由用户完成），降级为记录日志即可。
            LauncherLog.Write("update", $"更新包已校验就绪（{manifest.Version}），等待用户手动覆盖安装");
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
