// 应用入口：对齐桌面 App.xaml.cs（语言先行 / 全局异常日志 / OnLaunched 启动序列）。
// 桌面窗口专属（自更新后台检查 UpdateStartup、自更新重启 UI）按公约降级为 no-op，见尾部注释。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;

namespace Starcloud.Launcher.Uno;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    // 跨页启动请求：版本列表页详情页「启动」后导航回主页消费（与桌面同约定）
    public static string? PendingLaunchVersionId;

    // 跨页日志跳转：运行状态页点「打开日志」时置 "game"，日志页进入后消费一次
    public static string? PendingLogSource;

    public App()
    {
#if __ANDROID__
        // Skia 渲染后端选型：Uno 6 实验开关 UseVulkanOnSkiaAndroid 曾开 Vulkan（全屏栅格
        // 约 17ms/帧 → 驱动开销更低），但真机实测（P40 Pro，手机壳同源问题）息屏恢复 /
        // 换页后「非动态渲染区域全黑」——Vulkan 交换链不做损伤区保留，静态区域不再重画
        // 就读成未定义内容（黑块）。回退 OpenGL ES：无此问题，性能在本机可接受。
        global::Uno.UI.FeatureConfiguration.Rendering.UseVulkanOnSkiaAndroid = false;
        // GL 全屏栅格化开销大 → 开启 Skia 子树跳过优化：不变子树（顶栏/侧栏/静态卡）
        // 的渲染输出被缓存，切换页面/滚动时不重复栅格化（Uno 官方 Skia 专属优化）。
        global::Uno.UI.FeatureConfiguration.Rendering.EnableVisualSubtreeSkippingOptimization = true;
        // 阈值前移：默认阈值下缓存要等较久才介入，切页场景享受不到；显式收紧
        global::Uno.UI.FeatureConfiguration.Rendering.VisualSubtreeSkippingOptimizationCleanFramesThreshold = 8;
        global::Uno.UI.FeatureConfiguration.Rendering.VisualSubtreeSkippingOptimizationVisualCountThreshold = 10;
#endif
        // 界面语言必须在任何 XAML 解析之前确定（D3 多语言）；首次访问 AppServices 即完成服务装配
        Loc.Initialize(AppServices.Instance.Settings.UiLanguage);
        InitializeComponent();
        // 全局异常日志：XAML 崩溃默认不留托管堆栈，落地到数据目录便于诊断（对齐桌面 crash.log）
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = LauncherPaths.DataDir;
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
        // 全应用 UI 线程调度器（后台线程回调切主线程用，GameLauncherService 日志等）
        UiThread.Init(DispatcherQueue.GetForCurrentThread());

        // 主题初始化在壳内完成：ShellRootView 构造时按 AppearanceService 应用设置主题
        // （"system" 跟随系统深浅；桌面在 MainWindow 构造里做的是同一件事）
        MainWindow = new Window
        {
            Content = new ShellRootView(),
        };
        MainWindow.Activate();

        // 插件系统：后台加载所有已启用插件（Python 起子进程，不阻塞 UI）
        _ = Task.Run(() =>
        {
            try { AppServices.Instance.Plugins.LoadEnabledAtStartup(); }
            catch { /* 插件加载失败不阻塞启动 */ }
        });

        // 桌面 OnLaunched 里还有两件事，安卓端降级：
        // ① 自动更新后台检查（UpdateStartup.RunAsync）——自更新依赖桌面重启脚本与安装包流程，no-op；
        // ② starcloud://join 协议激活——安卓经 Intent 过滤进入（MainActivity 侧处理），
        //    壳就绪后可调 ((ShellRootView)MainWindow.Content).HandleJoinUrlAsync(url)。
    }
}
