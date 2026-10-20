// 应用内导航：Uno 壳采用「NavigationView 内容区 + 全窗覆盖层」结构（对齐桌面 MainWindow 的
// Frame 导航 + 游戏页覆盖语义）。ShellRootView 构造时注册，AppNavigation 全局可用。

using Microsoft.UI.Xaml;
using Starcloud.Launcher.Phone.Views;

namespace Starcloud.Launcher.Phone;

/// <summary>壳内页面可感知「被显示」的时机（对齐桌面页面的导航显示回调）</summary>
internal interface IShellPage
{
    /// <summary>页面被导航显示（含重复进入）时调用</summary>
    void OnShown();
}

internal static class AppNavigation
{
    private static ShellRootView? _shell;

    public static void Attach(ShellRootView shell) => _shell = shell;

    /// <summary>切换内容区路由页面（home/versions/download/...，与桌面 NavigationView tag 一致；
    /// 功能页移植完成前路由表全部指向临时占位页 Views/StubPage）</summary>
    public static void Navigate(string route) => _shell?.Navigate(route);

    /// <summary>推入全窗覆盖页（游戏页，覆盖在 NavigationView 之上）</summary>
    public static void PushOverlay(FrameworkElement content) => _shell?.PushOverlay(content);

    /// <summary>弹出全窗覆盖页</summary>
    public static void PopOverlay() => _shell?.PopOverlay();
}
