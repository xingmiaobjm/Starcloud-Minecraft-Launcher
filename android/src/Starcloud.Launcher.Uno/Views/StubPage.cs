// 临时占位页：壳移植期间的过渡页面，全部路由共用，居中显示路由名。
// 各功能页由移植代理逐个落地后，ShellRootView 的路由表再替换为真实页型。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Starcloud.Launcher.Uno.Views;

/// <summary>最小占位页：居中 TextBlock 显示路由名（样式取全局基样式 ScText）。</summary>
public sealed class StubPage : Page
{
    private readonly TextBlock _routeText;

    public StubPage()
    {
        // Frame 导航要求页型有公共无参构造；路由名经导航参数传入（见 OnNavigatedTo）
        _routeText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["ScText"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Content = _routeText;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _routeText.Text = e.Parameter as string ?? "";
    }
}
