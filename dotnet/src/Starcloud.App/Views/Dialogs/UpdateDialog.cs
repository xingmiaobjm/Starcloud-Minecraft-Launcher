// 更新弹窗：发现新版 → 确认下载（进度）→ 就绪 → 重启安装。
//
// 布局与云端公告同款：全窗遮罩 + 居中卡片的置顶 Popup（无边框 + Acrylic 窗口在
// 125% 缩放下 ContentDialog 定位偏右下，2026-10-24 统一改为自绘浮层）。
// 阶段机沿用旧 ContentDialog 版：稍后（关闭）/ 立即更新（下载进度）→ 就绪 → 立即重启安装。
//
// 与旧版的行为差异：旧版下载期间靠 PrimaryButtonClick 的 args.Cancel 留在弹窗，
// 现在按钮直接驱动状态机；「稍后」在下载中不可用（防止下载到一半关掉弹窗丢文件）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Foundation;

namespace Starcloud.App.Views.Dialogs;

/// <summary>自更新确认弹窗（遮罩 + 居中卡片）</summary>
internal static class UpdateDialog
{
    /// <summary>展示更新弹窗；用户点「立即重启安装」后脚本已被拉起、应用随即退出</summary>
    public static void Show(XamlRoot root, UpdateManifest manifest)
    {
        var s = AppServices.Instance;
        var current = SelfUpdateService.CurrentVersion();

        var width = Math.Clamp(root.Size.Width * 0.6, 440, 720);
        var maxHeight = Math.Clamp(root.Size.Height * 0.8, 320, 620);

        // ── 遮罩 ──
        var mask = new Border
        {
            Width = root.Size.Width,
            Height = root.Size.Height,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0)),
        };
        var maskPopup = new Popup { Child = mask, XamlRoot = root, IsLightDismissEnabled = false };

        // ── 卡片 ──
        var title = new TextBlock
        {
            Text = Loc.T("发现新版本 {0}", manifest.Version),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var versionLine = new TextBlock
        {
            Text = Loc.T("当前版本 {0} → 新版本 {1}", current, manifest.Version),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };

        var notes = Loc.Current == "en-US" && !string.IsNullOrWhiteSpace(manifest.NotesEn)
            ? manifest.NotesEn!
            : Loc.TCore(manifest.Notes);

        var notesLabel = new TextBlock
        {
            Text = Loc.T("更新说明"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible,
        };
        var notesScroller = new ScrollViewer
        {
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = notes,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible,
            },
        };

        var hint = new TextBlock
        {
            Text = Loc.T("更新期间启动器会自动关闭，完成后自动重启。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };

        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };

        var primary = new Button
        {
            Content = Loc.T("立即更新"),
            MinWidth = 140,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var later = new Button
        {
            Content = Loc.T("稍后"),
            Margin = new Thickness(0, 0, 8, 0),
        };

        // ── 卡片网格 ──
        var cardGrid = new Grid { RowSpacing = 10 };
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 版本行
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 更新说明标签
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 更新说明滚动区
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 提示
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状态
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 进度条
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 弹性
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮行

        Grid.SetRow(versionLine, 0);
        cardGrid.Children.Add(versionLine);
        Grid.SetRow(notesLabel, 1);
        cardGrid.Children.Add(notesLabel);
        Grid.SetRow(notesScroller, 2);
        cardGrid.Children.Add(notesScroller);
        Grid.SetRow(hint, 3);
        cardGrid.Children.Add(hint);
        Grid.SetRow(status, 4);
        cardGrid.Children.Add(status);
        Grid.SetRow(bar, 5);
        cardGrid.Children.Add(bar);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttonRow.Children.Add(later);
        buttonRow.Children.Add(primary);
        Grid.SetRow(buttonRow, 7);
        cardGrid.Children.Add(buttonRow);

        var card = new Border
        {
            Width = width,
            MaxHeight = maxHeight,
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(28, 22, 28, 18),
            Child = cardGrid,
        };

        var layer = new Grid();
        layer.Children.Add(mask);
        layer.Children.Add(card);

        var layerPopup = new Popup { Child = layer, XamlRoot = root, IsLightDismissEnabled = false };

        // ── 阶段机（与旧 ContentDialog 版等价；「稍后」= 关闭） ──
        var form = SelfUpdateService.DetectInstallForm();
        var artifact = form == UpdateInstallForm.NsisInstaller ? manifest.Setup : manifest.Portable;
        var writable = form == UpdateInstallForm.NsisInstaller
            || SelfUpdateService.IsAppDirWritable(AppContext.BaseDirectory);
        var closed = false;
        var downloading = false;
        var readyMode = false;
        string artifactPath = "";

        static void ShowStatus(TextBlock target, string text)
        {
            target.Text = text;
            target.Visibility = Visibility.Visible;
        }

        void Close()
        {
            if (closed || downloading) return;   // 下载到一半不允许关掉丢文件
            closed = true;
            layerPopup.IsOpen = false;
        }

        later.Click += (_, _) => Close();
        mask.Tapped += (_, _) => { if (!downloading) Close(); };

        primary.Click += async (_, _) =>
        {
            if (readyMode)
            {
                primary.IsEnabled = false;
                try
                {
                    s.SelfUpdate.LaunchUpdaterScript(Environment.ProcessId, form, artifactPath, manifest.Version);
                }
                catch (Exception ex)
                {
                    LauncherLog.Error("update", "拉起更新脚本失败", ex);
                    ShowStatus(status, Loc.T("更新失败：{0}", ex.Message));
                    primary.IsEnabled = true;
                    return;
                }
                Application.Current.Exit();
                return;
            }
            if (downloading) return;
            if (artifact is null)
            {
                ShowStatus(status, Loc.T("更新失败：{0}", Loc.T("清单缺少对应形态的下载文件")));
                return;
            }

            downloading = true;
            primary.IsEnabled = false;
            try
            {
                ShowStatus(status, Loc.T("正在下载…"));
                bar.IsIndeterminate = true;
                bar.Visibility = Visibility.Visible;
                // Progress<T> 在 UI 线程创建，回调自动封送回 UI 线程
                var progress = new Progress<double>(pct =>
                {
                    bar.IsIndeterminate = false;
                    bar.Value = pct;
                    ShowStatus(status, Loc.T("正在下载… {0}%", (int)pct));
                });

                artifactPath = await s.SelfUpdate.DownloadArtifactAsync(artifact, progress);

                if (form == UpdateInstallForm.Portable)
                {
                    ShowStatus(status, Loc.T("正在准备安装…"));
                    await s.SelfUpdate.ExtractPortableAsync(artifactPath);
                }

                bar.Visibility = Visibility.Collapsed;
                ShowStatus(status, Loc.T("更新已就绪，点击「立即重启安装」完成升级（启动器将自动关闭并重启）。"));
                primary.Content = Loc.T("立即重启安装");
                readyMode = true;
                primary.IsEnabled = true;
            }
            catch (Exception ex)
            {
                bar.Visibility = Visibility.Collapsed;
                ShowStatus(status, Loc.T("更新失败：{0}", ex.Message));
                LauncherLog.Error("update", "更新下载/准备失败", ex);
                primary.IsEnabled = true;               // 允许重试
            }
            finally
            {
                downloading = false;
            }
        };

        if (!writable)
        {
            // 便携版放在只读目录（如 Program Files）时无法就地覆盖
            primary.IsEnabled = false;
            ShowStatus(status, Loc.T("程序目录不可写，无法就地更新：{0}",
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)));
        }

        layerPopup.IsOpen = true;
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
}
