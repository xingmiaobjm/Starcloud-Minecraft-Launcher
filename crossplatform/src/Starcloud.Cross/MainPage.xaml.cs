using System.Runtime.InteropServices;
using Starcloud.Core.Services;
using Starcloud.Cross.Services;

namespace Starcloud.Cross;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();

        var settings = AppServices.Instance.Settings;

        // 运行环境信息（CI 截图可核对三平台差异）
        PlatformText.Text =
            $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})" +
            $" · natives: {string.Join(" > ", PlatformInfo.NativesClassifierCandidates)}";

        // Core 库引用验证：net10.0 应用消费 net8.0 Core
        CorePathText.Text = $"Core 数据目录: {LauncherPaths.DataDir}";

        // 真实服务接线验证：设置由磁盘加载（首次运行为平台默认值）
        SettingsText.Text =
            $"游戏目录（设置）: {settings.DotMinecraft}\n" +
            $"下载源: {settings.DownloadSource} · 自更新能力: {SelfUpdateService.Capability}";
    }

    // Java 扫描走 Phase 1 平台抽象层（PATH/JAVA_HOME + 平台惯例目录），验证 JavaService 跨平台可用
    private async void OnJavaScan(object sender, RoutedEventArgs e)
    {
        JavaScanButton.IsEnabled = false;
        JavaResultText.Text = "扫描中…";
        try
        {
            var runtimes = await AppServices.Instance.Java.ListRuntimesAsync();
            JavaResultText.Text = runtimes.Count == 0
                ? "未找到 Java（正常场景：干净 CI 机器；下载页可自动安装）"
                : $"找到 {runtimes.Count} 个运行时：\n" +
                  string.Join("\n", runtimes.Take(5)
                      .Select(r => $"  {r.MajorVersion,2} · {r.VersionString} · {r.Source} · {r.Executable}"));
        }
        catch (Exception ex)
        {
            JavaResultText.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            JavaScanButton.IsEnabled = true;
        }
    }
}
