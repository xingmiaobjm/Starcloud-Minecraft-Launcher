// 主窗口：Acrylic 毛玻璃背景（自定义透明度）+ NavigationView 导航

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;
using Starcloud.App.Views;
using Windows.UI;

namespace Starcloud.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new TransparentAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        Title = "Starcloud Minecraft Launcher";

        // Nova 内嵌（NOVA_INTEGRATION.md 五入口）：状态栏 / 顶栏 Orb 随全局状态机联动
        var rt = AppServices.Instance.NovaRuntime;
        rt.StateChanged += OnNovaStateChanged;
        rt.InstanceChanged += OnInstanceChanged;
        rt.OverlayToast += OnOverlayToast; // 浮窗一句话提醒 → 状态栏短暂展示（nova-overlay-plan.md T7）
        _toastResetTimer.IsRepeating = false;
        _toastResetTimer.Tick += (_, _) =>
        {
            _toastResetTimer.Stop();
            NovaStateText.Text = $"Nova: {rt.StateText}";
        };
        ApplyNovaState(rt.State);
        UpdateInstanceText(rt.CurrentInstance);

        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not { } item) return;
        var tag = item.Tag?.ToString();
        Type page = tag switch
        {
            "versions" => typeof(VersionsPage),
            "saves" => typeof(SavesPage),
            "download" => typeof(DownloadPage),
            "accounts" => typeof(AccountsPage),
            "ai" => typeof(NovaPage),
            "settings" => typeof(SettingsPage),
            "logs" => typeof(LogsPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
        // 选择完成后自动收起侧栏，保持内容区宽畅
        Nav.IsPaneOpen = false;
    }

    /// <summary>页面内跳转入口：通过设置导航栏选中项驱动 Frame 导航，保证左侧选中状态同步。</summary>
    public void NavigateTo(string tag)
    {
        var target = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == tag);
        if (target is not null && !ReferenceEquals(Nav.SelectedItem, target))
            Nav.SelectedItem = target;
    }

    /// <summary>版本详情子页：直接 Frame 导航（不占用侧栏选中项，返回即回来源页）。</summary>
    public void NavigateToVersionDetail(string versionId) =>
        ContentFrame.Navigate(typeof(VersionDetailPage), versionId);

    // ── Nova 内嵌（NOVA_INTEGRATION.md 五入口：① 顶栏 Orb / ④ 底部对话条 / ⑤ 状态栏） ──

    /// <summary>① NovaOrb：顶栏下方 8px 弹出 380px 毛玻璃面板，再点收回。</summary>
    private void NovaOrb_Click(object sender, RoutedEventArgs e)
    {
        if (NovaPopup.IsOpen) { NovaPopup.IsOpen = false; return; }
        // WinUI 3 的 Window 不继承 FrameworkElement，宽度从 Content 根元素取
        var root = Content as FrameworkElement;
        NovaPopup.HorizontalOffset = Math.Max(12, (root?.ActualWidth ?? 1200) - 380 - 12);
        NovaPopup.VerticalOffset = 48 + 8; // 顶栏(48) 下 8px
        NovaPopup.IsOpen = true;
    }

    /// <summary>④ 底部对话条：收起态一行提示 ↔ 展开态完整对话。</summary>
    private void NovaBarToggle_Click(object sender, RoutedEventArgs e)
    {
        var expand = NovaBarCollapsed.Visibility == Visibility.Visible;
        NovaBarCollapsed.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
        NovaBarExpandedHost.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>入口③「问 Nova」承接：展开底部对话条接收上下文输入（不跳页，少折腾）。</summary>
    public void OpenNovaBar()
    {
        NovaBarCollapsed.Visibility = Visibility.Collapsed;
        NovaBarExpandedHost.Visibility = Visibility.Visible;
    }

    /// <summary>协议激活（starcloud://join）：好友邀请直达 Nova 确认清单，并展开底部对话条。</summary>
    public async Task HandleJoinUrlAsync(string url)
    {
        OpenNovaBar();
        await AppServices.Instance.NovaRuntime.HandleJoinUrlAsync(url);
    }

    /// <summary>面板标头齿轮：关面板 → 去 Nova 页（许可层 / 流水 / LLM 设置）。</summary>
    private void NovaPanelSettings_Click(object sender, RoutedEventArgs e)
    {
        NovaPopup.IsOpen = false;
        NavigateTo("ai");
    }

    /// <summary>面板未登录引导：关面板 → 去 Nova 页登录 Starcloud ID。</summary>
    private void NovaPanelLogin_Click(object sender, RoutedEventArgs e)
    {
        NovaPopup.IsOpen = false;
        NavigateTo("ai");
    }

    private void OnNovaStateChanged(NovaState state) =>
        DispatcherQueue.TryEnqueue(() => ApplyNovaState(state));

    private void OnInstanceChanged(string? versionId) =>
        DispatcherQueue.TryEnqueue(() => UpdateInstanceText(versionId));

    // ── 浮窗提醒 → 状态栏短暂展示（8s 后恢复常规五态文案） ──

    private readonly DispatcherQueueTimer _toastResetTimer = DispatcherQueue.GetForCurrentThread()
        .CreateTimer(); // 构造在 UI 线程，可直接取

    private void OnOverlayToast(NovaOverlayToast toast) => DispatcherQueue.TryEnqueue(() =>
    {
        NovaStateText.Text = toast.Text;
        _toastResetTimer.Stop();
        _toastResetTimer.Interval = TimeSpan.FromSeconds(8);
        _toastResetTimer.Start();
    });

    /// <summary>⑤ 状态栏 + ① Orb 五态着色：待命灰 / 思考紫 / 有提案黄(角标) / 观察绿 / 离线暗灰。</summary>
    private void ApplyNovaState(NovaState state)
    {
        var rt = AppServices.Instance.NovaRuntime;
        var brush = StateBrush(state);

        NovaStateText.Text = $"Nova: {rt.StateText}";
        NovaStateText.Foreground = brush;
        NovaStateDot.Fill = brush;
        NovaOrbGlyph.Foreground = brush;
        NovaOrbDot.Visibility = state == NovaState.HasPlan ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(NovaOrb, $"Nova：{rt.StateText}");

        // 面板内容随登录态切换：离线显示引导，已登录显示对话
        var offline = state == NovaState.Offline;
        NovaPanelLogin.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        NovaPanelChat.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateInstanceText(string? versionId) =>
        InstanceText.Text = string.IsNullOrEmpty(versionId) ? "未选择实例" : $"实例：{versionId}";

    private static Brush StateBrush(NovaState state) => state switch
    {
        NovaState.Thinking => Solid(0x7C, 0x3A, 0xED),   // 星云紫
        NovaState.HasPlan => Solid(0xF5, 0x9E, 0x0B),    // 琥珀黄（提案待确认）
        NovaState.Observing => Solid(0x3D, 0xD3, 0x22),  // 品牌深绿（观察中）
        NovaState.Offline => (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
        _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b) =>
        new(Color.FromArgb(0xFF, r, g, b));
}

/// <summary>自定义亚克力背景：比系统默认毛玻璃更通透，深浅主题自动跟随。</summary>
internal sealed class TransparentAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_controller is null)
        {
            _controller = new DesktopAcrylicController();
            _configuration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
            };
            if (xamlRoot.Content is FrameworkElement root)
            {
                ApplyAcrylicTheme(root.ActualTheme);
                _configuration.Theme = ToSystemBackdropTheme(root.ActualTheme);
                root.ActualThemeChanged += OnRootThemeChanged;
            }
        }
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveAllSystemBackdropTargets();
    }

    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        ApplyAcrylicTheme(sender.ActualTheme);
        if (_configuration is not null)
            _configuration.Theme = ToSystemBackdropTheme(sender.ActualTheme);
    }

    /// <summary>深浅主题使用不同的亚克力配方：浅色通透偏白，深色为近黑暗玻璃并保留可见的模糊背景。</summary>
    private void ApplyAcrylicTheme(ElementTheme theme)
    {
        if (_controller is null) return;
        if (theme == ElementTheme.Dark)
        {
            _controller.TintColor = Color.FromArgb(0xFF, 0x14, 0x14, 0x14);
            _controller.TintOpacity = 0.45f;
            _controller.LuminosityOpacity = 0.30f;
            _controller.FallbackColor = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);
        }
        else
        {
            _controller.TintColor = Color.FromArgb(0xFF, 0xFC, 0xFC, 0xFC);
            _controller.TintOpacity = 0.25f;
            _controller.LuminosityOpacity = 0.18f;
            _controller.FallbackColor = Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        }
    }

    private static SystemBackdropTheme ToSystemBackdropTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        _ => SystemBackdropTheme.Light,
    };
}
