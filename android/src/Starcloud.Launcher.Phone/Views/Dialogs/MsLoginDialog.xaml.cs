// 内置 Microsoft 登录页（正版登录用）。
//
// 为什么还是「设备码」而不是「授权码 + 回调」：启动器用的是 Minecraft 官方公开客户端
// （00000000402b5328），该 client_id 只在 live.com 的 legacy 端点上可用（见 MicrosoftAuthService
// 的类注释），不能自建 redirect_uri。所以协议一点没变——后台申请设备码、轮询兑现令牌；
// 变的只是「用户在哪完成授权」：从「复制验证码 → 跳系统浏览器」换成这个内置页。
//
// 移植自桌面 Starcloud.App.Views.Dialogs.MsLoginWindow.xaml.cs（协议与流程 1:1）：
// - 桌面是独立 Window（ResizeAndCenter 摆位、系统标题栏、Window.Closed 语义）；
//   安卓没有多窗口语义，改为全屏覆盖页（UserControl），由 AuthDialogs 经
//   AppNavigation.PushOverlay 承载、PopOverlay 收走；窗口叉换成抬头右侧的关闭按钮。
// - 「窗口只是显示器」的原则不变：它拿不到令牌，也不知道怎么兑现——轮询始终在
//   AuthDialogs 里跑，成功/失败由调用方 Finish() 收尾；用户自己点关闭 = 取消登录。
// - 内置网页控件初始化失败（InitFailed）时自行关闭，调用方据此把这次登录判为失败并
//   说明原因——不做自动降级，换登录方式是用户的决定（设置里可切回浏览器方式）。
// - 桌面 ResizeAndCenter / DisplayArea / AppWindow 一段删除（覆盖页无窗口语义，
//   尺寸就是整屏；微软验证页在整屏宽度下不会出现窄列排版问题）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views.Dialogs;

public sealed partial class MsLoginDialog : UserControl
{
    private readonly MicrosoftAuthService.DeviceCodeInfo _code;
    private readonly Uri _verifyUri;
    private readonly TaskCompletionSource<bool> _ready = new();
    /// <summary>调用方收尾关闭（登录成功 / 失败）</summary>
    private bool _finishedByCaller;
    /// <summary>内置网页控件起不来：调用方据此按登录失败处理</summary>
    private bool _initFailed;
    /// <summary>用户点了关闭按钮：调用方据此当作「取消登录」，不得回退弹窗</summary>
    private bool _userClosed;
    /// <summary>Close 只生效一次（页面被移除后不再触发 Closed）</summary>
    private bool _closed;

    public MsLoginDialog(MicrosoftAuthService.DeviceCodeInfo code)
    {
        InitializeComponent();
        _code = code;
        _verifyUri = BuildVerifyUri(code);

        CodeText.Text = code.UserCode;
        StatusText.Text = Loc.T("等待授权…");

        Web.Loaded += (_, _) => InitWeb();
        Web.NavigationCompleted += (_, _) => LoadRing.IsActive = false;
        CopyBtn.Click += (_, _) => CopyCode();
        BrowserBtn.Click += async (_, _) => await OpenInBrowserAsync();
    }

    /// <summary>本页被关闭（任何原因）时触发；对齐桌面 Window.Closed 语义，调用方据此取消轮询。</summary>
    public event EventHandler? Closed;

    /// <summary>
    /// 内置网页控件就绪（并已开始导航）后完成；初始化失败或页面被关掉时返回 false。
    /// 调用方 await 它来决定继续轮询还是按失败处理。
    /// </summary>
    public Task<bool> Ready => _ready.Task;

    /// <summary>用户自己关掉了页面：调用方按「取消登录」处理（绝不因此弹出浏览器弹窗）</summary>
    public bool UserClosed => _userClosed;

    /// <summary>内置网页控件初始化失败：调用方按登录失败处理</summary>
    public bool InitFailed => _initFailed;

    /// <summary>轮询状态回写到页面底栏（调用方保证在 UI 线程）</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>登录完成 / 失败时的收尾：关页面（已关过则无事发生）</summary>
    public void Finish()
    {
        _finishedByCaller = true;
        Close();
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        // 只有「不是调用方关的、也不是初始化失败自己关的」才算用户主动取消
        if (!_finishedByCaller && !_initFailed) _userClosed = true;
        _ready.TrySetResult(false);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void InitWeb()
    {
        try
        {
            // 桌面端关闭开发者工具/状态栏/浏览器快捷键三件套（AreDevToolsEnabled 等设置），
            // 安卓原生 WebView 没有这些 UI，无对应物，直接去除。
            Web.Load(_verifyUri);
            LauncherLog.Write("auth", $"内置登录页已打开验证页 {_verifyUri}");
            _ready.TrySetResult(true);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("auth", "内置登录页不可用，按登录失败处理", ex);
            _initFailed = true;
            _ready.TrySetResult(false);
            Close();
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
}
