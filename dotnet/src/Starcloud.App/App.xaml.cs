// WinUI 3 应用入口

using System.IO;
using Microsoft.UI.Xaml;
using Starcloud.App.Localization;
using Starcloud.App.Services;

namespace Starcloud.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    // 跨页启动请求：版本列表页详情页"启动"后导航回主页消费
    public static string? PendingLaunchVersionId;

    // 当前运行中的版本 id（游戏进程存活期间非空）
    public static string? RunningVersionId;

    public App()
    {
        // 界面语言必须在任何 XAML 解析之前确定（D3 多语言）
        Loc.Initialize(AppServices.Instance.Settings.UiLanguage);
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

        // 插件系统：后台加载所有已启用插件（JS 同步加载 / Python 起子进程，不阻塞 UI）
        _ = Task.Run(() =>
        {
            try { AppServices.Instance.Plugins.LoadEnabledAtStartup(); }
            catch { /* 插件加载失败不阻塞启动 */ }
        });

        // 自动更新：延迟几秒后台检查（游戏运行时避让），发现新版弹窗确认
        _ = UpdateStartup.RunAsync();

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
