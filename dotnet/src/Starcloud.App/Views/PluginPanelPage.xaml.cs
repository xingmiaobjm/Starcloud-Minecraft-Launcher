// 插件面板宿主页：
//   · 1.x：WebView2 file:// 加载插件 HTML（入口由 PluginHost 面板注册表给出）；
//   · 2.0：contributes.panels 的声明式视图 JSON → PluginViewRenderer 画原生控件。
// 安全：两种入口都只读取插件解压目录内的相对路径（注册/校验时已把关）；
// 声明式分支不执行插件代码，动作走 PluginHost.InvokeAction（同一套权限校验）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Starcloud.App.Services;
using Starcloud.App.Views.Plugins;
using Starcloud.Core.Plugins;

namespace Starcloud.App.Views;

public sealed partial class PluginPanelPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private PluginHost.PanelRegistration? _panel;
    private PluginManifest? _manifest;
    private string _pluginDir = "";

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

        if (!panel.Declarative)
        {
            // file:/// 加载插件目录内 HTML；面板关闭即页面销毁（NavigationCacheMode=Disabled）
            Web.Source = new Uri(panel.EntryPath.Replace('\\', '/').Insert(0, "file:///"));
            return;
        }

        var installed = _s.Plugins.Scan()
            .FirstOrDefault(p => p.Manifest.Id == panel.PluginId);
        if (installed is null)
        {
            ShowDeclarative(panel, manifest: null, dir: "");
            return;
        }
        ShowDeclarative(panel, installed.Manifest, installed.Dir);
    }

    private void ShowDeclarative(PluginHost.PanelRegistration panel, PluginManifest? manifest, string dir)
    {
        _panel = panel;
        _manifest = manifest;
        _pluginDir = dir;
        Web.Visibility = Visibility.Collapsed;
        DeclarativeHost.Visibility = Visibility.Visible;
        DrawView();
    }

    /// <summary>把面板引用的视图 JSON 解析并渲染；解析失败或渲染异常都在页面里说明</summary>
    private void DrawView()
    {
        ViewRoot.Children.Clear();
        if (_panel is null) return;
        if (_manifest is null)
        {
            ViewRoot.Children.Add(Message("插件已不在已安装列表中，面板无法渲染。"));
            return;
        }
        if (!PluginViewParser.TryParseFile(_panel.EntryPath, out var root, out var errors) || root is null)
        {
            ViewRoot.Children.Add(Message("视图无法渲染：\n" + string.Join("\n", errors)));
            return;
        }
        var ctx = new PluginViewContext
        {
            PluginId = _panel.PluginId,
            Manifest = _manifest,
            Host = _s.PluginHost,
            PluginDir = _pluginDir,
            Notify = ShowAction,
            RequestRefresh = DrawView,
        };
        try
        {
            ViewRoot.Children.Add(PluginViewRenderer.Render(root, ctx));
        }
        catch (Exception ex)
        {
            ViewRoot.Children.Clear();
            ViewRoot.Children.Add(Message($"视图渲染失败：{ex.Message}"));
        }
    }

    private void ShowAction(string message, bool ok)
    {
        ActionBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private static TextBlock Message(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
    };

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }
}
