// 我的 · 手机版聚合页：承载底部导航放不下的次级入口 + 插件导航组。
// 入口行直接 Tapped → AppNavigation.Navigate（次级路由不点亮底部 tab，见 ShellRootView）；
// 插件导航组（contributes.navItems）运行时按贡献重建，与平板侧栏同口径（最多 3 项，超出写日志）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Starcloud.Android.Host;
using Starcloud.Android.Host.Runtime;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views;

public sealed partial class MorePage : Page, IShellPage
{
    /// <summary>插件导航项展示上限（与平板侧栏同策略：按插件加载顺序取前 3，超出写日志）</summary>
    private const int MaxPluginNavItems = 3;

    private bool _pluginNavHooked;

    public MorePage()
    {
        InitializeComponent();
        // 插件是后台加载的：加载 / 停用都要重建导航组；页面启用导航缓存，
        // 订阅挂一次即可（回调只操作本页控件，实例常驻）
        Loaded += (_, _) =>
        {
            if (_pluginNavHooked) return;
            _pluginNavHooked = true;
            AppServices.Instance.PluginHost.ContributionsChanged += OnPluginContributionsChanged;
        };
    }

    /// <summary>页面被导航显示（含重复进入）：刷新登录态、运行圆点、插件导航组与版本脚注。</summary>
    public void OnShown()
    {
        RefreshAccount();
        RefreshRunningDot();
        RebuildPluginNavGroup();
        RefreshVersionFooter();
    }

    // ── 账户卡（对齐 Shell 顶栏头像的数据源：Starcloud ID 会话） ──

    private void RefreshAccount()
    {
        var session = AppServices.Instance.StarcloudId.Current;
        var logged = session is not null;
        var name = logged ? session!.DisplayName : "";
        AccountNameText.Text = logged ? name : Loc.T("未登录");
        AccountSubText.Text = logged
            ? Loc.T("已登录 · {0}", session!.Channel == StarcloudIdService.CloudChannel ? Loc.T("云端通道") : Loc.T("测试通道"))
            : Loc.T("点击前往账户页登录 Starcloud ID");

        var initial = !string.IsNullOrEmpty(name) ? name[..1]
            : logged && !string.IsNullOrEmpty(session!.UserId) ? session.UserId[..1] : "";
        AccountAvatarText.Text = initial;
        AccountAvatarText.Visibility = initial.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountAvatarGlyph.Visibility = initial.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── 运行圆点：游戏运行中点亮（数据源与平板侧栏圆点一致） ──

    private void RefreshRunningDot() =>
        RunningDot.Visibility = JvmHost.IsRunning ? Visibility.Visible : Visibility.Collapsed;

    // ── 插件导航组 ──

    /// <summary>
    /// 重建插件导航组：最多 3 项（宿主展示策略，与平板侧栏 / 首页卡片带同口径）；
    /// 没有任何插件导航项时整块收起。贡献变化可能来自插件后台线程，回 UI 线程执行。
    /// </summary>
    private void RebuildPluginNavGroup()
    {
        try
        {
            PluginNavList.Children.Clear();

            var all = AppServices.Instance.PluginHost.GetNavItems();
            if (all.Count == 0)
            {
                PluginNavHost.Visibility = Visibility.Collapsed;
                return;
            }
            PluginNavHost.Visibility = Visibility.Visible;

            var shown = all.Count > MaxPluginNavItems ? all.Take(MaxPluginNavItems).ToList() : all.ToList();
            if (all.Count > MaxPluginNavItems)
                LauncherLog.Write("plugin", $"插件导航项共 {all.Count} 个，超出上限，只展示前 {MaxPluginNavItems} 个（按插件加载顺序）");

            foreach (var reg in shown)
            {
                var row = BuildPluginNavRow(reg);
                PluginNavList.Children.Add(row);
            }
            // 行与行之间补分隔线（最后一行底下不画）
            for (var i = 0; i < PluginNavList.Children.Count - 1; i++)
            {
                if (PluginNavList.Children[i] is Grid)
                {
                    PluginNavList.Children.Insert(++i, new Rectangle
                    {
                        Height = 1, Margin = new Thickness(38, 0, 12, 0),
                        Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    });
                }
            }
            LauncherLog.Write("plugin", $"插件导航组：{shown.Count} 项（共 {all.Count} 个注册）");
        }
        catch (Exception ex)
        {
            LauncherLog.Error("plugin", "重建插件导航组失败", ex);
        }
    }

    /// <summary>插件导航行：图标 + 标题 + 右箭头，点击路由到 plugin:&lt;插件 id&gt;:&lt;导航 id&gt;。</summary>
    private Grid BuildPluginNavRow(PluginHost.NavItemRegistration reg)
    {
        var row = new Grid { Height = 48, ColumnSpacing = 12, Padding = new Thickness(12, 0, 12, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var icon = new FontIcon
        {
            FontFamily = IconFont.Family, Glyph = reg.IconGlyph, FontSize = 15,
            Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock { Text = reg.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new FontIcon
        {
            FontFamily = (FontFamily)Application.Current.Resources["ScIconFont"], Glyph = "\uE904", FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(chevron, 2);
        row.Children.Add(icon);
        row.Children.Add(title);
        row.Children.Add(chevron);

        var tag = PluginExtensionPoints.PluginNavTag(reg.PluginId, reg.NavId);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, reg.Title);
        row.Tapped += (_, _) => AppNavigation.Navigate(tag);
        return row;
    }

    /// <summary>贡献变化（可能来自插件后台线程）：回 UI 线程重建。</summary>
    private void OnPluginContributionsChanged() => UiThread.Run(RebuildPluginNavGroup);

    // ── 版本脚注（对齐平板顶栏版本徽章：读程序集版本） ──

    private void RefreshVersionFooter()
    {
        var ver = typeof(AppServices).Assembly.GetName().Version ?? new Version(0, 0, 0);
        VersionFooter.Text = Loc.T("Starcloud Phone 测试版 V{0}", $"{ver.Major}.{ver.Minor}.{ver.Build}");
    }

    // ── 入口点击 ──

    private void AccountCard_Tapped(object sender, TappedRoutedEventArgs e) =>
        AppNavigation.Navigate("accounts");
    private void RowSaves_Tapped(object sender, TappedRoutedEventArgs e) =>
        AppNavigation.Navigate("saves");
    private void RowRunning_Tapped(object sender, TappedRoutedEventArgs e) =>
        AppNavigation.Navigate("running");
    private void RowPlugins_Tapped(object sender, TappedRoutedEventArgs e) =>
        AppNavigation.Navigate("plugins");
    private void RowNova_Tapped(object sender, TappedRoutedEventArgs e) =>
        AppNavigation.Navigate("ai");
    private void RowLogs_Tapped(object sender, TappedRoutedEventArgs e) =>
        AppNavigation.Navigate("logs");
    private void RowSettings_Tapped(object sender, TappedRoutedEventArgs e) =>
        AppNavigation.Navigate("settings");
}
