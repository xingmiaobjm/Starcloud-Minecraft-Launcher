// 内置 Microsoft 登录窗口（正版登录用）。
//
// 为什么还是「设备码」而不是「授权码 + 回调」：启动器用的是 Minecraft 官方公开客户端
// （00000000402b5328），该 client_id 只在 live.com 的 legacy 端点上可用（见 MicrosoftAuthService
// 的类注释），不能自建 redirect_uri。所以协议一点没变——后台申请设备码、轮询兑现令牌；
// 变的只是「用户在哪完成授权」：从「复制验证码 → 跳系统浏览器」换成这个内置窗口。
//
// 关键细节：验证地址是 https://www.microsoft.com/link，把设备码拼成 ?otc=<user_code> 后
// 该页会把验证码预填进输入框（实测线上返回的 HTML 里已带该码），用户在这个窗口里登录即可。
// 窗口只是「显示器」：它拿不到令牌，也不知道怎么兑现——轮询始终在 AuthDialogs 里跑，
// 成功/失败由调用方关闭本窗口，用户自己关掉窗口则视为取消。
//
// WebView2 不可用（未装运行时等）时 <see cref="Ready"/> 返回 false 并自行关闭，
// 调用方据此把这次登录判为失败并说明原因——不做自动降级，换登录方式是用户的决定
// （设置 → 通用 → 使用内置登录窗口 可切回「验证码 + 系统浏览器」）。

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Localization;
using Starcloud.Core.Services;
using Windows.Graphics;

namespace Starcloud.App.Views.Dialogs;

public sealed partial class MsLoginWindow : Window
{
    // 默认尺寸：微软验证页在窄窗口里会被压成一列小字，这里按「能完整看清页面」给足宽度；
    // 高度再给一档，登录页在窄而高的窗口里不用滚动就能看到「继续」按钮。
    // 屏幕工作区不够时按可用空间收缩（见 ResizeAndCenter），小屏笔记本也不会顶到屏幕外。
    private const int WindowW = 720;
    private const int WindowH = 1000;
    private const int MinW = 560;
    private const int MinH = 640;
    private const int EdgeMargin = 40;   // 与工作区四周留出的空隙

    private readonly MicrosoftAuthService.DeviceCodeInfo _code;
    private readonly Uri _verifyUri;
    private readonly TaskCompletionSource<bool> _ready = new();
    /// <summary>调用方收尾关闭（登录成功 / 失败）</summary>
    private bool _finishedByCaller;
    /// <summary>WebView2 起不来：调用方据此回退到浏览器方式</summary>
    private bool _initFailed;
    /// <summary>用户按了窗口的关闭按钮：调用方据此当作「取消登录」，不得回退弹窗</summary>
    private bool _userClosed;

    public MsLoginWindow(MicrosoftAuthService.DeviceCodeInfo code)
    {
        InitializeComponent();
        _code = code;
        _verifyUri = BuildVerifyUri(code);

        Title = Loc.T("登录 Microsoft 账户");
        CodeText.Text = code.UserCode;
        StatusText.Text = Loc.T("等待授权…");

        ResizeAndCenter();

        Web.Loaded += async (_, _) => await InitWebAsync();
        Web.NavigationCompleted += (_, e) => OnNavigationCompleted(Web, e);
        CopyBtn.Click += (_, _) => CopyCode();
        BrowserBtn.Click += async (_, _) => await OpenInBrowserAsync();
        Closed += (_, _) =>
        {
            // 只有「不是调用方关的、也不是初始化失败自己关的」才算用户主动取消
            if (!_finishedByCaller && !_initFailed) _userClosed = true;
            _ready.TrySetResult(false);
        };
    }

    /// <summary>
    /// WebView2 就绪（并已开始导航）后完成；初始化失败或窗口被关掉时返回 false。
    /// 调用方 await 它来决定走内置窗口还是回退。
    /// </summary>
    public Task<bool> Ready => _ready.Task;

    /// <summary>用户自己关掉了窗口：调用方按「取消登录」处理（绝不因此弹出浏览器弹窗）</summary>
    public bool UserClosed => _userClosed;

    /// <summary>WebView2 初始化失败（未装运行时等）：调用方回退到浏览器方式</summary>
    public bool InitFailed => _initFailed;

    /// <summary>轮询状态回写到窗口底栏（调用方保证在 UI 线程）</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// 页面导航离开了「输入验证码」的站点：说明用户已经完成了授权流程，
    /// 调用方据此立刻补一次轮询，不再干等下一个 5 秒间隔。
    /// </summary>
    public event Action? AuthPageNavigated;

    private bool _navigatedAway;

    private void OnNavigationCompleted(WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadRing.IsActive = false;
        if (_navigatedAway) return;
        try
        {
            var host = sender.Source?.Host ?? "";
            // 起始页固定是 www.microsoft.com/link；授权完成后会跳到 login.live.com 等别的主机。
            // 误判的代价只是「立即补一次轮询」，所以这里不用抠得太细。
            if (host.Length > 0 && !host.Equals("www.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                _navigatedAway = true;
                AuthPageNavigated?.Invoke();
            }
        }
        catch
        {
            // 读 URL 失败不触发
        }
    }

    /// <summary>登录完成 / 失败时的收尾：关窗口（已关过则无事发生）</summary>
    public void Finish()
    {
        _finishedByCaller = true;
        try { Close(); } catch { /* 已关闭 */ }
    }

    private async Task InitWebAsync()
    {
        try
        {
            await Web.EnsureCoreWebView2Async();
            if (Web.CoreWebView2 is not { } core) throw new InvalidOperationException("WebView2 未就绪");

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Navigate(_verifyUri.ToString());
            LauncherLog.Write("auth", $"内置登录窗口已打开验证页 {_verifyUri}");
            _ready.TrySetResult(true);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("auth", "内置登录窗口不可用，回退到浏览器方式", ex);
            _initFailed = true;
            _ready.TrySetResult(false);
            try { Close(); } catch { /* 已关闭 */ }
        }
    }

    private void CopyCode()
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        pkg.SetText(_code.UserCode);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        CopyBtn.Content = Loc.T("已复制");
    }

    private async Task OpenInBrowserAsync()
    {
        CopyCode();
        await Windows.System.Launcher.LaunchUriAsync(_verifyUri);
    }

    /// <summary>验证地址 + 设备码：网页据此预填验证码（无 otc 参数时退回手动输入）</summary>
    private static Uri BuildVerifyUri(MicrosoftAuthService.DeviceCodeInfo code)
    {
        var sep = code.VerificationUri.Contains('?') ? '&' : '?';
        return Uri.TryCreate($"{code.VerificationUri}{sep}otc={Uri.EscapeDataString(code.UserCode)}",
            UriKind.Absolute, out var uri)
            ? uri
            : new Uri(code.VerificationUri);
    }

    /// <summary>小窗口居中于所在显示器工作区</summary>
    private void ResizeAndCenter()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            // 顶到工作区就收缩，但不再小于最小可用尺寸
            var w = Math.Min(WindowW, Math.Max(MinW, area.Width - EdgeMargin * 2));
            var h = Math.Min(WindowH, Math.Max(MinH, area.Height - EdgeMargin * 2));
            AppWindow.Resize(new SizeInt32(w, h));
            AppWindow.Move(new PointInt32(
                area.X + Math.Max(0, (area.Width - w) / 2),
                area.Y + Math.Max(0, (area.Height - h) / 2)));

            // 允许用户自己拉大 / 拉小，但不给拉到看不清页面
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.PreferredMinimumWidth = MinW;
                presenter.PreferredMinimumHeight = MinH;
            }
        }
        catch
        {
            // 拿不到工作区就不摆位置，用系统默认
        }
    }
}
