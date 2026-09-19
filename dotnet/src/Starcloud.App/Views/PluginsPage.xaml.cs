// 插件管理页（plugin-spec.md）：安装 / 启停（免责确认）/ 卸载 / 面板入口。
// 免责提示为强制流程：第三方未验证插件启用前必须用户手动确认（规范 §4）。

using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Plugins;

namespace Starcloud.App.Views;

/// <summary>插件列表行（页面级模型，供 x:DataType）。
/// 属性显式声明为可变：XamlTypeInfo 会为 x:DataType 生成 setter，init-only 会 CS8852。</summary>
public sealed record PluginRow(
    string Id, string Name, string Version, string Author, string Description,
    string Detail, bool Official, bool Enabled)
{
    // 注意：positional record 体内声明属性必须用「= 参数名」初始化器显式绑定，
    // 否则构造器不赋值（CS8907），字段全空。
    public string Id { get; set; } = Id;
    public string Name { get; set; } = Name;
    public string Version { get; set; } = Version;
    public string Author { get; set; } = Author;
    public string Description { get; set; } = Description;
    public string Detail { get; set; } = Detail;
    public bool Official { get; set; } = Official;
    public bool Enabled { get; set; } = Enabled;

    public Visibility OfficialVisibility => Official ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnverifiedVisibility => Official ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PanelsVisibility =>
        AppServices.Instance.PluginHost.Panels.Any(p => p.PluginId == Id)
            ? Visibility.Visible
            : Visibility.Collapsed;
}

/// <summary>插件面板行</summary>
public sealed record PluginPanelRow(string Id, string Title, string PluginName)
{
    public string Id { get; set; } = Id;
    public string Title { get; set; } = Title;
    public string PluginName { get; set; } = PluginName;
}

public sealed partial class PluginsPage : Page, INotifyPropertyChanged
{
    private readonly AppServices _s = AppServices.Instance;
    private bool _syncing;

    /// <summary>PanelsCardVisible 走 OneWay x:Bind，需在面板集合变化后主动通知。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PluginRow> Rows { get; } = [];
    public ObservableCollection<PluginPanelRow> PanelRows { get; } = [];

    public Visibility PanelsCardVisible =>
        PanelRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public PluginsPage()
    {
        InitializeComponent();
        PluginList.ItemsSource = Rows;
        _s.PluginHost.PanelRegistered += OnPanelsChanged;
        _s.PluginHost.PanelUnregistered += (_, _) => EnqueueRefresh();
    }

    private void OnPanelsChanged(PluginHost.PanelRegistration reg) => EnqueueRefresh();

    private void EnqueueRefresh() =>
        DispatcherQueue?.TryEnqueue(() =>
        {
            RefreshPanelRows();
            RefreshRows();
        });

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 页顶免责声明条：看过一次后不再常驻（启用插件时仍会强制弹免责确认框）
        var showTip = !_s.Plugins.IsPageTipShown();
        DisclaimerBar.IsOpen = showTip;
        if (showTip) _s.Plugins.MarkPageTipShown();
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshPanelRows();
        RefreshRows();
    }

    private void RefreshPanelRows()
    {
        PanelRows.Clear();
        var names = _s.Plugins.Scan().ToDictionary(p => p.Manifest.Id, p => p.Manifest.Name);
        foreach (var panel in _s.PluginHost.Panels)
            PanelRows.Add(new PluginPanelRow(
                $"{panel.PluginId}::{panel.PanelId}", panel.Title,
                names.GetValueOrDefault(panel.PluginId, panel.PluginId)));
        Bindings.Update();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PanelsCardVisible)));
    }

    private void RefreshRows()
    {
        _syncing = true;
        Rows.Clear();
        foreach (var p in _s.Plugins.Scan())
        {
            var enabled = _s.Plugins.IsEnabled(p.Manifest.Id);
            var perms = p.Manifest.Permissions.Length > 0
                ? string.Join(" · ", p.Manifest.Permissions)
                : Loc.T("无特殊权限");
            Rows.Add(new PluginRow(
                p.Manifest.Id, p.Manifest.Name, p.Manifest.Version, p.Manifest.Author,
                p.Manifest.Description,
                Loc.T("作者 {0} · 权限：{1}", p.Manifest.Author, perms),
                p.Official, enabled));
        }
        EmptyText.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _syncing = false;
        MotionFx.StaggerListItems(PluginList);
    }

    // ── 安装 ──

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".mclplug");
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var (plugin, errors) = _s.Plugins.Install(file.Path);
        if (plugin is null)
        {
            await ShowMessageAsync(Loc.T("插件安装失败"),
                string.Join('\n', errors));
            return;
        }
        ShowNotify(Loc.T("插件已安装：{0} {1}", plugin.Manifest.Name, plugin.Manifest.Version),
            success: true); // 安装成功即为成功态，官方与否只影响徽标
        RefreshAll();
    }

    // ── 启用 / 禁用（免责确认） ──

    private async void PluginToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (sender is not ToggleSwitch toggle || toggle.Tag is not string id) return;
        var plugin = _s.Plugins.Find(id);
        if (plugin is null) return;

        // x:Bind 绑定 IsOn 同样会触发 Toggled；与持久化状态一致说明不是用户操作，忽略
        if (toggle.IsOn == _s.Plugins.IsEnabled(id)) return;

        if (!toggle.IsOn)
        {
            _s.Plugins.Disable(id);
            return;
        }

        // 规范 §4：非官方插件启用前必须免责确认（每个插件 id+版本确认一次）
        if (!plugin.Official && !_s.Plugins.IsDisclaimerAccepted(id))
        {
            _syncing = true;
            toggle.IsOn = false;
            _syncing = false;
            if (!await ShowDisclaimerAsync(plugin))
                return; // 用户取消，保持关闭
            _syncing = true;
            toggle.IsOn = true;
            _syncing = false;
            _s.Plugins.AcceptDisclaimer(id, plugin.Manifest.Version);
        }

        var err = _s.Plugins.Enable(plugin);
        if (err is not null)
        {
            _syncing = true;
            toggle.IsOn = false;
            _syncing = false;
            await ShowMessageAsync(Loc.T("插件启动失败"), err);
        }
    }

    /// <summary>免责确认弹窗（规范 §4 声明文案）；true = 用户确认</summary>
    private async Task<bool> ShowDisclaimerAsync(PluginService.InstalledPlugin plugin)
    {
        var m = plugin.Manifest;
        var perms = m.Permissions.Length > 0 ? string.Join("、", m.Permissions) : Loc.T("无特殊权限");
        var dlg = new ThemedDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.T("加载第三方插件「{0}」？", m.Name),
            Content = Loc.T(
                "该插件由第三方（{0}）独立开发，启动器本体不对插件内容、合法性负责。" +
                "\n\n插件声明的权限：{1}" +
                "\n插件无权修改游戏认证、UUID 与登录校验逻辑；越权参数会被启动器拦截丢弃。" +
                "\n\n确定启用吗？", m.Author, perms),
            PrimaryButtonText = Loc.T("我已知晓风险，启用"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    // ── 卸载 ──

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var plugin = _s.Plugins.Find(id);
        if (plugin is null) return;
        var dlg = new ThemedDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.T("卸载插件「{0}」？", plugin.Manifest.Name),
            Content = Loc.T("插件文件与启用状态将被删除，此操作不可撤销。"),
            PrimaryButtonText = Loc.T("卸载"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.Plugins.Uninstall(id);
        RefreshAll();
    }

    // ── 面板 ──

    private void OpenPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var panel = _s.PluginHost.Panels.FirstOrDefault(p => $"{p.PluginId}::{p.PanelId}" == id)
                    ?? _s.PluginHost.Panels.FirstOrDefault(p => p.PluginId == id);
        if (panel is null) return;
        Frame.Navigate(typeof(PluginPanelPage), panel,
            MotionService.TransitionFor(_s.Motion.Enabled));
    }

    // ── 小工具 ──

    private void ShowNotify(string message, bool success)
    {
        NotifyBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        NotifyBar.Message = message;
        NotifyBar.IsOpen = true;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ThemedDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = Loc.T("知道了"),
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }
}
