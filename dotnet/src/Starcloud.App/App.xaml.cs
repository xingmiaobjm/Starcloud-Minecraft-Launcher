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

    /// <summary>主页布局编辑工具箱窗口（独立顶层窗口，同样需要强引用否则会被 GC 关掉）</summary>
    public static Views.Dialogs.ToolboxWindow? Toolbox { get; set; }

    /// <summary>启动参数（向导结束、主窗口延后创建时仍要处理 starcloud://join）</summary>
    private LaunchActivatedEventArgs? _launchArgs;

    public static Window? MainWindow { get; private set; }

    // 跨页启动请求：版本列表页详情页"启动"后导航回主页消费
    public static string? PendingLaunchVersionId;

    // 跨页启动附带的「自动连接服务器」：联机房间的「开始游戏」在启动时带房主地址
    // （走 --quickPlayMultiplayer），消费一次即清空
    public static string? PendingLaunchServer;

    // 本次启动来自联机房间：游戏窗口出现后自动跳回好友页联机标签（消费一次即清）
    public static bool PendingReturnToFriends;

    // 跨页日志跳转：运行状态页点「打开日志」时置 "game"，日志页进入后消费一次
    public static string? PendingLogSource;

    /// <summary>跨页进房请求：好友聊天里的房卡点「加入房间」时置房间码，联机页进入后消费一次</summary>
    public static string? PendingRoomCode;

    /// <summary>nre.log 追加锁：FirstChance 可能任意线程并发触发</summary>
    private static readonly object NreLogGate = new();

    /// <summary>启动分段计时的零点：显式静态构造保证在 AppServices 构造（实例 ctor 第一行触发）之前开始
    /// （无显式 cctor 的类型是 beforefieldinit，初始化时机不保证落在实例构造之前）</summary>
    private static readonly System.Diagnostics.Stopwatch PerfSinceStart;

    static App() => PerfSinceStart = System.Diagnostics.Stopwatch.StartNew();

    public App()
    {
        // 界面语言必须在任何 XAML 解析之前确定（D3 多语言）
        Loc.Initialize(AppServices.Instance.Settings.UiLanguage);
        InitializeComponent();
        // 全局异常日志：WinUI stowed exception（0xc000027b）默认不留托管堆栈，落地到文件便于诊断
        // 诊断：首破异常记录 NRE 的真实抛出点（无堆栈的 NRE 靠这个定位）
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is not NullReferenceException nre) return;
            // 落盘放后台：FirstChance 在异常抛出点同步触发，若在 UI 线程高频路径上
            // 直接 AppendAllText，一次抖动就是一帧卡顿
            var message = nre.Message;
            var stack = nre.StackTrace ?? "(无堆栈)";
            Task.Run(() =>
            {
                try
                {
                    lock (NreLogGate)
                    {
                        var dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Starcloud");
                        Directory.CreateDirectory(dir);
                        File.AppendAllText(Path.Combine(dir, "nre.log"),
                            $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + "\n" + stack + "\n\n");
                    }
                }
                catch { }
            });
        };

        // 后台线程的未捕获异常不会走 UnhandledException（XAML 线程专用）——它们直接杀进程，
        // 且 stderr 只有一行 "Unhandled exception"。这里补上，保证有堆栈可查。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Starcloud");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [background] {e.ExceptionObject}" + "\n\n");
            }
            catch { }
        };

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
        // 启动分段计时：进程内第一条 [perf] 从这里打，AppServices ctor / 主窗口 ctor / 首页导航各一段，
        // 优化只对着最大的那段下手（见 MainWindow root.Loaded 里的收尾一条）
        Starcloud.Core.Services.LauncherLog.Write("perf",
            $"OnLaunched（进程内已过 {PerfSinceStart.ElapsedMilliseconds}ms）");
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
