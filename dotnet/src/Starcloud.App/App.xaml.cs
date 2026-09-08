// WinUI 3 应用入口

using System.IO;
using Microsoft.UI.Xaml;

namespace Starcloud.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // 全局异常日志：WinUI stowed exception（0xc000027b）默认不留托管堆栈，落地到文件便于诊断
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Starcloud");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}\n{e.Exception}\n\n");
            }
            catch
            {
                // 日志写入失败时忽略，避免二次异常
            }
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();

        // 拉人即玩：starcloud://join?t=… 协议激活（未打包应用经命令行参数进入）
        var url = args.Arguments?.Trim();
        if (!string.IsNullOrEmpty(url) &&
            url.StartsWith("starcloud://join", StringComparison.OrdinalIgnoreCase) &&
            MainWindow is MainWindow win)
        {
            _ = win.HandleJoinUrlAsync(url);
        }
    }
}
