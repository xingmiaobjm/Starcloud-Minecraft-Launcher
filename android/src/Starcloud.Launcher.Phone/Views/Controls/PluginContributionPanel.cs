// 插件挂载区宿主控件（规范 2.0 §6.2）：
// 页面在固定位置放一个它，它把「该挂载点上所有插件的贡献」渲染出来；
//   · 2.0 声明式（contributes.slots / versionSections）→ PluginViewRenderer 画原生控件；
//   · 1.x（ctx.ui.registerSlot 的 HTML）→ WebView file://，与插件面板同安全基线。
// 外观三档：None（原样嵌入，可给固定高度）、Card（每份贡献一张卡）、Section（卡片 + 分区脊线与抬头）。
// 没有贡献时自身 Collapsed（不占位），因此宿主页面在没装插件时与从前完全一致。
//
// Uno 移植说明：桌面端网页控件在 Uno Skia 下未实现（Uno0001），改用 NativeWebViewer
// （原生 Android WebView）加载 file:// 页面；chrome.webview 消息桥由控件内垫片提供。

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Plugins;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Plugins;

namespace Starcloud.Launcher.Phone.Views.Controls;

/// <summary>挂载区外观</summary>
public enum PluginChrome
{
    /// <summary>原样嵌进页面（顶部横幅）</summary>
    None,

    /// <summary>每份贡献包一张卡</summary>
    Card,

    /// <summary>每份贡献是一段分区（卡片 + 脊线 + 抬头）</summary>
    Section,
}

public sealed class PluginContributionPanel : StackPanel
{
    /// <summary>挂载点 id（如 download.top / home.top / versions.top / version.detail.top）</summary>
    public static readonly DependencyProperty PointProperty = DependencyProperty.Register(
        nameof(Point), typeof(string), typeof(PluginContributionPanel),
        new PropertyMetadata("", OnLayoutChanged));

    /// <summary>外观</summary>
    public static readonly DependencyProperty ChromeProperty = DependencyProperty.Register(
        nameof(Chrome), typeof(PluginChrome), typeof(PluginContributionPanel),
        new PropertyMetadata(PluginChrome.None, OnLayoutChanged));

    public string Point
    {
        get => (string)GetValue(PointProperty);
        set => SetValue(PointProperty, value);
    }

    public PluginChrome Chrome
    {
        get => (PluginChrome)GetValue(ChromeProperty);
        set => SetValue(ChromeProperty, value);
    }

    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    public PluginContributionPanel()
    {
        Spacing = 10;
        Visibility = Visibility.Collapsed;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private PluginHost? _host;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 插件在后台加载：首页这类「启动即渲染」的页面首次 Loaded 时往往还没有贡献，
        // 订阅变化事件，等插件注册完再重建一次
        _host = AppServices.Instance.PluginHost;
        _host.ContributionsChanged += OnContributionsChanged;
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null) _host.ContributionsChanged -= OnContributionsChanged;
        _host = null;
    }

    /// <summary>贡献变化可能来自插件后台线程，必须回 UI 线程重建</summary>
    private void OnContributionsChanged() => DispatcherQueue.TryEnqueue(Render);

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PluginContributionPanel panel && panel.IsLoaded) panel.Render();
    }

    /// <summary>重建本挂载点的全部贡献（插件启停后重进页面即刷新）</summary>
    public void Render()
    {
        Children.Clear();
        _status.Text = "";
        _status.Visibility = Visibility.Collapsed;

        var host = AppServices.Instance.PluginHost;
        var registrations = host.GetSlots(Point);
        foreach (var reg in registrations)
        {
            FrameworkElement? element = reg.Declarative ? BuildDeclarative(reg) : BuildHtml(reg);
            if (element is null) continue;
            Children.Add(Chrome == PluginChrome.None ? element : Wrap(element, reg));
        }
        if (Children.Count > 0) Children.Add(_status);
        Visibility = Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>声明式贡献：解析视图 JSON → 原生控件（与首页卡片带共用渲染入口）</summary>
    private FrameworkElement? BuildDeclarative(PluginHost.SlotRegistration reg)
    {
        var element = PluginViewRenderer.RenderFile(reg.PluginId, reg.EntryPath, ShowStatus, Render);
        if (reg.Height > 0)
        {
            element.Height = reg.Height;
            element.VerticalAlignment = VerticalAlignment.Top;
        }
        return element;
    }

    /// <summary>1.x 的 HTML 贡献：NativeWebViewer 加载插件目录内页面（不暴露宿主对象）</summary>
    private FrameworkElement? BuildHtml(PluginHost.SlotRegistration reg)
    {
        var web = new NativeWebViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Res("LayerFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = web,
        };
        if (reg.Height > 0) frame.Height = Math.Clamp(reg.Height, 48, 320);
        NavigateHtml(web, reg.EntryPath);
        return frame;
    }

    private static void NavigateHtml(NativeWebViewer web, string entryPath)
    {
        try
        {
            // file:// 加载（NativeWebViewer.Load 内部完成读取与 chrome.webview 垫片注入）
            web.Load(new Uri(entryPath.Replace('\\', '/').Insert(0, "file:///")));
        }
        catch
        {
            // 挂载区是锦上添花，WebView 起不来不影响页面
        }
    }

    /// <summary>给贡献套一层宿主外观（卡 / 分区）</summary>
    private FrameworkElement Wrap(FrameworkElement content, PluginHost.SlotRegistration reg)
    {
        var stack = new StackPanel { Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(reg.Title))
        {
            if (Chrome == PluginChrome.Section)
            {
                var spine = new Border { Style = Res<Style>("ScSectionSpine") };
                var title = new TextBlock { Text = reg.Title, Style = Res<Style>("ScSectionTitle") };
                var row = new StackPanel { Style = Res<Style>("VdRow") };
                row.Children.Add(spine);
                row.Children.Add(title);
                stack.Children.Add(row);
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = reg.Title,
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                });
            }
        }
        stack.Children.Add(content);
        return new Border { Style = Res<Style>("ScCard"), Child = stack };
    }

    private void ShowStatus(string message, bool ok)
    {
        _status.Text = ok ? $"✓ {message}" : $"⚠ {message}";
        _status.Opacity = ok ? 0.75 : 1.0;
        _status.Visibility = Visibility.Visible;
        if (!Children.Contains(_status)) Children.Add(_status);
    }

    private static TextBlock Hint(string message) => new()
    {
        Text = message,
        FontSize = 12,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
    };

    private static T? Res<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;

    private static Brush? Res(string key) => Res<Brush>(key);
}
