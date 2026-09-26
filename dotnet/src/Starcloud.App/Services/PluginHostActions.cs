// 插件宿主桥（App 侧）：把「只有 UI 层才有」的能力交给插件 SDK
// （确认框 / 提示框 / 切页 / 打开链接与目录 / 剪贴板）。
//
// 线程约定：插件钩子可能在任意线程执行（启动前钩子在启动线程、事件投递在计时器线程），
// 这些能力又全是 UI 线程亲和的，所以：
//   · 每次调用都经 DispatcherQueue 切到 UI 线程；
//   · 对话框用 TCS 把结果带回给等待中的插件线程，UI 线程不阻塞（否则弹窗永远显示不出来）。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Localization;
using Starcloud.App.Views.Dialogs;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

internal sealed class PluginHostActions : IPluginHostBridge
{
    private static XamlRoot? Root =>
        (App.MainWindow as MainWindow)?.Content?.XamlRoot;

    public bool? Confirm(string title, string message) =>
        ShowOnUiAsync(() => DialogAsync(title, message, info: false));

    public bool? ShowInfo(string title, string message)
    {
        ShowOnUiAsync(() => DialogAsync(title, message, info: true));
        return true;
    }

    public void OpenPage(string page)
    {
        var tag = (page ?? "").Trim().ToLowerInvariant() switch
        {
            "nova" => "ai",
            var other => other,
        };
        RunOnUi(() =>
        {
            (App.MainWindow as MainWindow)?.NavigateTo(tag);
            return true;
        });
    }

    public string OpenUrl(string url)
    {
        RunOnUi(() =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        });
        LauncherLog.Write("plugin", $"插件请求打开链接：{url}");
        return $"已用默认浏览器打开 {url}";
    }

    public string OpenPath(string path)
    {
        RunOnUi(() =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return true;
        });
        return $"已在资源管理器中打开 {path}";
    }

    public string? ClipboardText() =>
        RunOnUi(() =>
        {
            try
            {
                // GetContent 需要在 UI 线程；空剪贴板会抛异常，当作 null
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                return content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
                    ? content.GetTextAsync().AsTask().GetAwaiter().GetResult()
                    : null;
            }
            catch
            {
                return null;
            }
        });

    public void SetClipboardText(string text) => RunOnUi(() =>
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text ?? "");
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        return true;
    });

    /// <summary>弹一个主题一致的对话框；info=true 只有「知道了」，否则是确认 / 取消</summary>
    private static async Task<bool?> DialogAsync(string title, string message, bool info)
    {
        var root = Root;
        if (root is null) return null;
        var dlg = new ThemedDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? Loc.T("插件提示") : title,
            Content = new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
            },
            XamlRoot = root,
        };
        if (info)
        {
            dlg.CloseButtonText = Loc.T("知道了");
            await dlg.ShowAsync();
            return true;
        }
        dlg.PrimaryButtonText = Loc.T("确认");
        dlg.CloseButtonText = Loc.T("取消");
        dlg.DefaultButton = ContentDialogButton.Primary;
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>把一段「要显示对话框」的 UI 工作转成同步等待（调用方是插件线程，不是 UI 线程）</summary>
    private static bool? ShowOnUiAsync(Func<Task<bool?>> work)
    {
        var dq = App.MainWindow?.DispatcherQueue;
        if (dq is null) return null; // 无窗口（无头 / 已退出）→ 宿主没有界面能力
        if (dq.HasThreadAccess)
        {
            // 已经在 UI 线程：不能阻塞等待自己（那会死锁），直接按「无界面能力」返回。
            // 插件钩子里的 onLoad 有可能就在 UI 线程上跑（用户在插件页点启用），
            // 所以这里留一条日志，避免插件作者对着 false 猜原因。
            LauncherLog.Write("plugin", "插件在 UI 线程上请求对话框，已按「未确认」返回");
            return null;
        }
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dq.TryEnqueue(async void () =>
            {
                try
                {
                    tcs.TrySetResult(await work());
                }
                catch (Exception ex)
                {
                    LauncherLog.Write("plugin", $"插件对话框失败：{ex.Message}");
                    tcs.TrySetResult(null);
                }
            }))
            return null;
        // 用户在弹窗上停留的时间不可预期：给 5 分钟上限，超时按「取消」处理，避免插件线程永不返回
        return tcs.Task.Wait(TimeSpan.FromMinutes(5)) ? tcs.Task.Result : null;
    }

    private static T RunOnUi<T>(Func<T> work)
    {
        var dq = App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        if (dq is null || dq.HasThreadAccess) return work();
        T result = default!;
        using var done = new ManualResetEventSlim(false);
        dq.TryEnqueue(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                LauncherLog.Write("plugin", $"插件宿主能力失败：{ex.Message}");
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(30));
        return result;
    }
}
