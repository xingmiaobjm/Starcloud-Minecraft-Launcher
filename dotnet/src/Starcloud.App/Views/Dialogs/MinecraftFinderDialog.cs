// 「自动搜索 .minecraft 目录」弹窗：打开即开始扫描，结果增量上屏，选中即切换。
//
// 扫描在后台跑（MinecraftFolderScanner），这里只负责：
//   把找到的候选逐条加进列表（IProgress 在 UI 线程创建，回调自动回到 UI 线程）、
//   显示当前进度、允许随时停止 / 重扫，最后返回选中的目录。
//
// 关窗即取消：用户不等了不该让后台还啃硬盘。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Dialogs;

internal static class MinecraftFinderDialog
{
    /// <summary>弹出搜索窗；返回选中的目录（取消返回 null）——兼容单选调用点</summary>
    public static async Task<string?> ShowAsync(XamlRoot root)
    {
        var picked = await ShowMultipleAsync(root);
        return picked.FirstOrDefault();
    }

    /// <summary>弹出搜索窗（多选）；返回全部选中的目录（取消返回空表）</summary>
    public static async Task<IReadOnlyList<string>> ShowMultipleAsync(XamlRoot root)
    {
        var status = new TextBlock
        {
            Text = Loc.T("准备扫描…"),
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Height = 260,
            ItemsSource = new List<MinecraftFolderCandidate>(),
        };

        var empty = new TextBlock
        {
            Text = Loc.T("还没有找到 .minecraft 目录，正在继续搜索…"),
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(status);
        body.Children.Add(list);
        body.Children.Add(empty);

        var stopButton = new Button { Content = Loc.T("停止搜索") };
        var rescanButton = new Button { Content = Loc.T("重新搜索") };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(stopButton);
        buttons.Children.Add(rescanButton);

        var panel = new StackPanel();
        panel.Children.Add(body);
        panel.Children.Add(buttons);

        var dialog = new ThemedDialog
        {
            Title = Loc.T("自动搜索 .minecraft 目录"),
            Content = panel,
            PrimaryButtonText = Loc.T("导入所选目录"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = root,
        };

        CancellationTokenSource? cts = null;

        void StartScan()
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var items = new List<MinecraftFolderCandidate>();
            list.ItemsSource = items;
            empty.Visibility = Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = false;
            stopButton.IsEnabled = true;
            stopButton.Visibility = Visibility.Visible;   // 重新搜索时停止键回来

            // 结果与状态回调都在后台线程触发；IProgress 在 UI 线程创建 → 自动切回 UI 线程
            var found = new Progress<MinecraftFolderCandidate>(c =>
            {
                if (token.IsCancellationRequested) return;
                items.Add(c);
                list.ItemsSource = null;
                list.ItemsSource = items;   // List<T> 无通知，重绑一次即可（量级几十条）
                empty.Visibility = Visibility.Collapsed;
            });
            var progress = new Progress<string>(text =>
            {
                if (!token.IsCancellationRequested) status.Text = text;
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await MinecraftFolderScanner.ScanAsync(
                        c => ((IProgress<MinecraftFolderCandidate>)found).Report(c),
                        t => ((IProgress<string>)progress).Report(t), token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    LauncherServiceLog("扫描 .minecraft 目录失败", ex);
                }
                finally
                {
                    _ = DispatcherQueueBridge(root, () =>
                    {
                        // 扫描已经结束：「停止搜索」不再有意义，直接收起，避免用户以为还在扫
                        stopButton.Visibility = Visibility.Collapsed;
                        dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
                        if (items.Count == 0 && !token.IsCancellationRequested)
                            status.Text = Loc.T("没有找到 .minecraft 目录。若游戏装在非常规位置，请用「浏览」手动选择。");
                        else if (!token.IsCancellationRequested)
                            status.Text = Loc.T("共找到 {0} 个目录，选中一个或多个后点「导入所选目录」。", items.Count);
                    });
                }
            });
        }

        list.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        stopButton.Click += (_, _) =>
        {
            cts?.Cancel();
            stopButton.IsEnabled = false;
            stopButton.Visibility = Visibility.Collapsed;
            status.Text = Loc.T("已停止搜索，结果保留在列表中。");
        };
        rescanButton.Click += (_, _) => StartScan();

        StartScan();
        var result = await dialog.ShowAsync();
        cts?.Cancel();
        cts?.Dispose();
        if (result != ContentDialogResult.Primary) return [];
        return list.SelectedItems
            .OfType<MinecraftFolderCandidate>()
            .Select(c => c.Path)
            .ToList();
    }

    /// <summary>后台线程回 UI 线程：借 XamlRoot 元素的 DispatcherQueue</summary>
    private static bool DispatcherQueueBridge(XamlRoot root, Action action)
    {
        var queue = root.Content?.DispatcherQueue;
        if (queue is null) return false;
        return queue.TryEnqueue(() => action());
    }

    private static void LauncherServiceLog(string message, Exception ex) =>
        LauncherLog.Write("versions", $"{message}：{ex.Message}");
}
