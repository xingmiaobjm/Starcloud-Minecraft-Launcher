// 日志页：读取启动器日志尾部

using Microsoft.UI.Xaml.Controls;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        var logFile = Path.Combine(LauncherPaths.LogsDir, "launcher.log");
        if (!File.Exists(logFile))
        {
            LogText.Text = "（暂无日志）";
            return;
        }
        try
        {
            var lines = File.ReadLines(logFile).TakeLast(400);
            LogText.Text = string.Join('\n', lines);
        }
        catch (Exception ex)
        {
            LogText.Text = $"读取日志失败：{ex.Message}";
        }
    }
}
