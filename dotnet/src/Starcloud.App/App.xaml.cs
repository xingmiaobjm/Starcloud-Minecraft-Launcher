// WinUI 3 应用入口

using System.IO;
using Microsoft.UI.Xaml;
using Starcloud.App.Localization;
using Starcloud.App.Services;

namespace Starcloud.App;

public partial class App : Application
{
    /// <summary>首次向导窗口的强引用（WinUI 的 Window 不持有引用就会被 GC 关掉）</summary>
    private Views.Dialogs.OnboardingWindow? _onboarding;

    /// <summary>启动参数（向导结束、主窗口延后创建时仍要处理 starcloud://join）</summary>
    private LaunchActivatedEventArgs? _launchArgs;

    public static Window? MainWindow { get; private set; }

    // 跨页启动请求：版本列表页详情页"启动"后导航回主页消费
    public static string? PendingLaunchVersionId;

    // 跨页日志跳转：运行状态页点「打开日志」时置 "game"，日志页进入后消费一次
    public static string? PendingLogSource;

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
        _launchArgs = args;
        // 首次使用：先只开使用引导，主窗口等引导关闭后再创建（用户设完才进启动器）。
        // 老用户 / 已完成后正常启动，不弹任何东西。
        var services = AppServices.Instance;
        if (!services.Settings.OnboardingDone && services.Accounts.Accounts.Count == 0)
        {
            try
            {
                _onboarding = new Views.Dialogs.OnboardingWindow(StartMainWindow);
                _onboarding.Activate();
                return;
            }
            catch (Exception ex)
            {
                // 向导起不来不能挡住启动：记一笔日志，照常进主窗口
                Starcloud.Core.Services.LauncherLog.Error("app", "使用引导打开失败，直接进入主窗口", ex);
            }
        }
        else if (!services.Settings.OnboardingDone)
        {
            // 老用户（已有账户）：静默标记完成，之后不再判断
            services.Settings.OnboardingDone = true;
            try { services.Settings.Save(); } catch { }
        }

        StartMainWindow();
    }

    /// <summary>创建并显示主窗口，挂上所有后台任务（首次向导关闭后也走这里，幂等）</summary>
    private void StartMainWindow()
    {
        if (MainWindow is not null) return;
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            Starcloud.Core.Services.LauncherLog.Error("app", "主窗口创建失败", ex);
            return;
        }

        // 插件系统：后台加载所有已启用插件（JS 同步加载 / Python 起子进程，不阻塞 UI）
        _ = Task.Run(() =>
        {
            try { AppServices.Instance.Plugins.LoadEnabledAtStartup(); }
            catch { /* 插件加载失败不阻塞启动 */ }
        });

        // 跨设备链路：已登录 Starcloud ID 且设置开启时后台启动（beacon + TCP 服务），失败静默
        _ = Task.Run(() =>
        {
            try { AppServices.Instance.DeviceLink.Start(); }
            catch { /* 链路启动失败不影响启动器主功能 */ }
        });

        // 高级传递优化：已登录且打开「高级传递优化」时后台探测公网端点并上报索引
        _ = Task.Run(() =>
        {
            try { AppServices.Instance.InternetP2P.Start(); }
            catch { /* 发现流程失败只会让 P2P 不参与，下载照常 */ }
        });

        // 自动更新：延迟几秒后台检查（游戏运行时避让），发现新版弹窗确认
        _ = UpdateStartup.RunAsync();

        // 云端公告：启动后延迟拉一次，有未读就弹（只渲染 Markdown 文本，不执行任何内容）
        _ = FetchAnnouncementsAsync();

        // 拉人即玩：starcloud://join?t=… 协议激活（未打包应用经命令行参数进入）
        var url = _launchArgs?.Arguments?.Trim();
        if (!string.IsNullOrEmpty(url) &&
            url.StartsWith("starcloud://join", StringComparison.OrdinalIgnoreCase) &&
            MainWindow is MainWindow win)
        {
            _ = win.HandleJoinUrlAsync(url);
        }
    }

    /// <summary>
    /// 拉取云端公告：有未读就弹一条。全程静默失败（公告是旁路能力，不能影响启动）；
    /// 内容只作为 Markdown 文本渲染，任何情况下都不执行代码。
    /// </summary>
    private async Task FetchAnnouncementsAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));   // 等主窗口首屏渲染完
            var service = AppServices.Instance.Announcements;
            var items = await service.FetchAsync();
            if (items.Count == 0) return;
            var unread = Starcloud.Core.Services.AnnouncementService.PickUnread(items, AppServices.Instance.Settings.SeenAnnouncementIds);
            if (unread is null) return;
            MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // 覆盖层挂在主窗口内容根上：比 ContentDialog 大得多、永远居中
                    if (MainWindow?.Content?.XamlRoot is { } announceRoot)
                        Views.Dialogs.AnnouncementOverlay.Show(announceRoot, unread);
                    else
                        Starcloud.Core.Services.LauncherLog.Write("app", "公告展示：XamlRoot 未就绪");
                }
                catch { /* 弹窗失败不影响任何功能 */ }
            });
        }
        catch
        {
            // 忽略：公告拉不到不是错误
        }
    }
}
