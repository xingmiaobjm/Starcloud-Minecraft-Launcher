// 插件管理页（plugin-spec.md）：安装 / 启停（免责确认）/ 卸载 / 面板入口。
// 免责提示为强制流程：第三方未验证插件启用前必须用户手动确认（规范 §4）。
// 移植自桌面 Starcloud.App.Views.PluginsPage，安卓适配：
// - OnNavigatedTo → IShellPage.OnShown（壳路由无导航参数）
// - 面板跳转：Frame.Navigate(typeof(PluginPanelPage), panel) → PendingPanelKey + AppNavigation
// - 安装入口：安卓无 FileOpenPicker 流程 → 引导说明（对齐版本页「导入整合包」的降级口径）

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Starcloud.Core.Plugins;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Starcloud.Launcher.Uno.Views;

/// <summary>插件列表行（页面级模型，供 x:DataType）。
/// 属性显式声明为可变：XamlTypeInfo 会为 x:DataType 生成 setter，init-only 会 CS8852。</summary>
public sealed record PluginRow(
    string Id, string Name, string Version, string Author, string Description,
    string Detail, bool Official, bool Enabled, string ApiChip, bool ShowApiChip)
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
    public string ApiChip { get; set; } = ApiChip;
    public bool ShowApiChip { get; set; } = ShowApiChip;

    public Visibility OfficialVisibility => Official ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnverifiedVisibility => Official ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ApiChipVisibility => ShowApiChip ? Visibility.Visible : Visibility.Collapsed;
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

public sealed partial class PluginsPage : Page, INotifyPropertyChanged, IShellPage
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

    /// <summary>壳导航显示时机（对齐桌面 OnNavigatedTo 刷新时机）。</summary>
    public void OnShown()
    {
        // 页顶免责声明条：看过一次后不再常驻（启用插件时仍会强制弹免责确认框）
        var showTip = !_s.Plugins.IsPageTipShown();
        DisclaimerBar.IsOpen = showTip;
        if (showTip) _s.Plugins.MarkPageTipShown();
        RefreshAll();
    }

    private void OnPanelsChanged(PluginHost.PanelRegistration reg) => EnqueueRefresh();

    private void EnqueueRefresh() => UiThread.Run(() =>
    {
        RefreshPanelRows();
        RefreshRows();
    });

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
            var isApi2 = p.Manifest.IsApi2;
            var perms = DescribePermissions(p.Manifest);
            // 2.0 插件运行时尚且未实现（§11）：徽标直说，避免用户以为是插件坏了
            var apiChip = isApi2
                ? PluginEnvironment.CanRunApi(p.Manifest.ApiVersion)
                    ? Loc.T("API {0}", p.Manifest.ApiVersion)
                    : Loc.T("API {0} · 暂不可启用", p.Manifest.ApiVersion)
                : "";
            Rows.Add(new PluginRow(
                p.Manifest.Id, p.Manifest.Name, p.Manifest.Version, p.Manifest.Author,
                p.Manifest.Description,
                isApi2
                    ? Loc.T("作者 {0} · 能力：{1}", p.Manifest.Author, perms)
                    : Loc.T("作者 {0} · 权限：{1}", p.Manifest.Author, perms),
                p.Official, enabled, apiChip, isApi2));
        }
        EmptyText.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _syncing = false;
        // 数据没变就不重演错峰：切页回来列表原样呈现（重演 = 肉眼可见的闪烁）
        var sig = string.Join("|", Rows.Select(r => $"{r.Id}:{r.Version}:{r.Enabled}"));
        if (sig != _appliedListSig)
        {
            _appliedListSig = sig;
            MotionFx.StaggerListItems(PluginList);
        }
    }

    /// <summary>上一次插件列表签名：内容没变就不重演错峰（切页闪烁来源之一）。</summary>
    private string? _appliedListSig;

    /// <summary>
    /// 权限摘要：1.x 原样列举；2.0 解析细粒度能力并标注风险等级
    /// （高风险来自 `allow_delete`、`network:any`、`settings:write`、`ui:html` 等，规范 2.0 §5.2）。
    /// </summary>
    private static string DescribePermissions(PluginManifest manifest)
    {
        var all = manifest.Permissions.Concat(manifest.OptionalPermissions).ToArray();
        if (all.Length == 0) return Loc.T("无特殊权限");
        if (!manifest.IsApi2) return string.Join(" · ", all);

        var parts = new List<string>(all.Length);
        foreach (var raw in all)
        {
            if (!PluginPermissions.TryParse(raw, out var capability, out _))
            {
                parts.Add(raw);
                continue;
            }
            parts.Add(PluginPermissions.RiskOf(capability) switch
            {
                PluginRiskLevel.High => Loc.T("{0}（高风险）", raw),
                PluginRiskLevel.Medium => Loc.T("{0}（{1}）", raw, Loc.T("中风险")),
                _ => Loc.T("{0}（{1}）", raw, Loc.T("低风险")),
            });
        }
        return string.Join(" · ", parts);
    }

    // ── 安装 ──

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        // 安卓端无 FileOpenPicker 流程（MainActivity 也没有 .mclplug 的 intent filter）：
        // 引导用户通过系统「用其他应用打开」分享给启动器的方案需系统侧接线，当前先给明确说明
        await ShowMessageAsync(Loc.T("安装插件"),
            Loc.T("安卓端暂不支持在应用内选择插件包文件。" +
                  "请把 .mclplug 插件包放入数据目录的 plugins/packs/ 文件夹后重启启动器，" +
                  "或使用桌面端安装后同步数据目录。"));
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
        var perms = DescribePermissions(m);
        var dlg = new ThemedDialog
        {
            XamlRoot = Ui.Root,
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
            XamlRoot = Ui.Root,
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
        // 壳路由无导航参数：面板身份经静态属性带过去（与 VersionDetailPage.PendingVersionId 同款）
        PluginPanelPage.PendingPanelKey = $"{panel.PluginId}::{panel.PanelId}";
        AppNavigation.Navigate("pluginPanel");
    }

    // ── 小工具 ──

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ThemedDialog
        {
            XamlRoot = Ui.Root,
            Title = title,
            Content = message,
            CloseButtonText = Loc.T("知道了"),
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }
}
