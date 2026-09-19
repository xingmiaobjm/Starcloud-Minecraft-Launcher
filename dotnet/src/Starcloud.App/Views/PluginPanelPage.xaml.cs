// 插件面板宿主页：WebView2 file:// 加载插件 HTML（入口由 PluginHost 面板注册表给出）。
// 安全：仅加载插件解压目录内的相对路径（注册时已校验）；不向页面暴露宿主对象。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Starcloud.App.Services;
using Starcloud.Core.Plugins;

namespace Starcloud.App.Views;

public sealed partial class PluginPanelPage : Page
{
    private readonly AppServices _s = AppServices.Instance;

    public PluginPanelPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PluginHost.PanelRegistration panel) return;
        PanelTitle.Text = panel.Title;
        SourceBar.Title = panel.PluginId;
        // 来源声明只展示一次（按插件持久化，看过就不再弹）
        var show = !_s.Plugins.IsPanelTipShown(panel.PluginId);
        SourceBar.IsOpen = show;
        if (show) _s.Plugins.MarkPanelTipShown(panel.PluginId);
        // file:/// 加载插件目录内 HTML；面板关闭即页面销毁（NavigationCacheMode=Disabled）
        Web.Source = new Uri(panel.EntryPath.Replace('\\', '/').Insert(0, "file:///"));
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }
}
