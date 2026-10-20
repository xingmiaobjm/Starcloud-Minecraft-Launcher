// 插件一级导航页（contributes.navItems）：宿主按 (插件 id, 导航 id) 渲染该插件的声明式视图。
// 与「插件」页里的面板同源 —— 同一份解析、同一套能力层、同一套失败兜底；
// 区别只是这里没有返回按钮（导航栏本身就是返回），并多一个「去插件页」出路。
// 安卓适配：壳路由无导航参数 → (pluginId, navId) 经静态属性带入（ShellRootView 负责设置）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Plugins;

namespace Starcloud.Launcher.Phone.Views;

public sealed partial class PluginNavPage : Page, IShellPage
{
    private readonly AppServices _s = AppServices.Instance;

    /// <summary>壳路由传参：进入本页前由导航发起方设置 (插件 id, 导航 id)。</summary>
    public static string PendingPluginId = "";
    public static string PendingNavId = "";

    /// <summary>本页对应的插件 id（回落判定用：插件被停用时要离开这一页）</summary>
    public string PluginId { get; private set; } = "";

    private string _navId = "";

    public PluginNavPage() => InitializeComponent();

    /// <summary>壳导航显示时机（对齐桌面 OnNavigatedTo）。</summary>
    public void OnShown()
    {
        if (PendingPluginId.Length == 0) return;
        PluginId = PendingPluginId;
        _navId = PendingNavId;
        var item = _s.PluginHost.GetNavItems()
            .FirstOrDefault(n => n.PluginId == PluginId && n.NavId == _navId);
        if (item is null) { ShowGone(); return; }
        NavTitle.Text = item.Title;
        DrawView(item.EntryPath);
    }

    /// <summary>解析 + 渲染；插件已卸载、解析失败、渲染异常都在页面里说明</summary>
    private void DrawView(string entryPath)
    {
        ViewRoot.Children.Clear();
        var installed = _s.Plugins.Scan().FirstOrDefault(p => p.Manifest.Id == PluginId);
        if (installed is null) { ShowGone(); return; }
        var element = PluginViewRenderer.RenderFile(PluginId, entryPath, ShowAction, () => DrawView(entryPath));
        ViewRoot.Children.Add(element);
    }

    /// <summary>插件没了：说明 + 回「插件」页的出路（不留死页面）</summary>
    private void ShowGone()
    {
        ViewRoot.Children.Clear();
        ViewRoot.Children.Add(new TextBlock
        {
            Text = "插件已不在已安装列表中，这一页无法渲染。",
            FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
        });
        var back = new Starcloud.Launcher.Phone.Views.Controls.GlowButton
        {
            Content = Loc.T("去插件页"), Padding = new Thickness(12, 5, 12, 5),
        };
        back.Click += (_, _) => AppNavigation.Navigate("plugins");
        ViewRoot.Children.Add(back);
    }

    private void ShowAction(string message, bool ok)
    {
        ActionBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }
}
