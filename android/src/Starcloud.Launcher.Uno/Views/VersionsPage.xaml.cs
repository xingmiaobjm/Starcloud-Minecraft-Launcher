// 版本页：本地已安装版本列表（搜索/分类过滤、实例档案徽章、长按/「…」完整菜单）
// 移植自桌面 Starcloud.App.Views.VersionsPage（仅做安卓侧适配）：
// - 桌面 Process.Start 打开目录 → UiDialog 展示路径（安卓无桌面文件夹语义）
// - 启动入口：GameLauncherService.SelectedVersion + PushOverlay(GamePage(autoLaunch:true))
// - 详情页跳转：VersionDetailPage.PendingVersionId + AppNavigation.Navigate("versionDetail")
// - 整页拖拽导入与文件选择器在触屏端无对应交互，已按公约移除（见报告）

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Starcloud.Launcher.Uno.Views.Rows;
using Starcloud.Launcher.Uno.Views.Shared;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class VersionsPage : Page, IShellPage
{
    private readonly AppServices _s = AppServices.Instance;
    private List<LocalVersionRow> _all = [];

    public VersionsPage()
    {
        InitializeComponent();
    }

    /// <summary>上一次筛选结果签名：数据没变就不重建 ItemsSource、不重演错峰入场
    /// （页面已缓存复用，每次进入都重建列表是切换卡顿的主因之一）。</summary>
    private string? _appliedSig;

    /// <summary>壳导航显示时机（对齐桌面 Loaded 刷新时机）。页面已缓存复用：搜索词 / 分类筛选
    /// 是上次离开时留下的界面态，先复位（桌面新实例进来总是初始态）再刷新数据。</summary>
    public void OnShown()
    {
        SearchBox.Text = "";
        CategoryFilter.SelectedItem = Loc.T("全部");
        _ = RefreshListAsync();
    }

    private void RefreshList() => _ = RefreshListAsync();

    /// <summary>刷新已安装版本。目录扫描 + 逐实例档案读取是磁盘 IO，放后台线程，
    /// 完成后回 UI 线程重建列表——同步扫盘会让切页瞬间掉帧。</summary>
    private async Task RefreshListAsync()
    {
        var dotMc = _s.Settings.DotMinecraft;
        var all = await Task.Run(() =>
        {
            var store = new InstanceSettingsStore(dotMc);
            try
            {
                return _s.Manifest.ListInstalled(dotMc).Select(i =>
                {
                    var profile = store.Get(i.Id);
                    var category = profile.Category ?? "";
                    return new LocalVersionRow(
                        i.Id, VersionRowHelpers.DetectKind(dotMc, i.Id), FormatSize(i.SizeBytes),
                        VersionRowHelpers.DisplayIconPath(dotMc, i.Id, profile.IconPath),
                        category,
                        InstanceLayout.IsIsolated(profile, _s.Settings),
                        VersionRowHelpers.FormatLastPlayed(profile.LastPlayedAt),
                        (i.Id + " " + category).ToLowerInvariant());
                }).ToList();
            }
            catch
            {
                return new List<LocalVersionRow>();
            }
        });
        _all = all;
        DirText.Text = dotMc;
        RebuildCategoryFilter();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<LocalVersionRow> query = _all;
        var kw = SearchBox.Text.Trim().ToLowerInvariant();
        if (kw.Length > 0) query = query.Where(r => r.SearchKey.Contains(kw));
        var cat = (CategoryFilter.SelectedItem as string) ?? Loc.T("全部");
        if (cat == Loc.T("未分类")) query = query.Where(r => string.IsNullOrEmpty(r.Category));
        else if (cat != Loc.T("全部")) query = query.Where(r => r.Category == cat);
        var list = query.ToList();
        // 数据没变就不再重建列表 / 重演错峰：切页回来时列表原样呈现，零开销
        var sig = string.Join("|", list.Select(r => $"{r.Id}:{r.Category}:{r.LastPlayedText}:{r.SizeText}"))
            + "#" + kw + "#" + cat;
        if (sig == _appliedSig && VersionListView.ItemsSource is not null) return;
        _appliedSig = sig;
        VersionListView.ItemsSource = list;
        MotionFx.StaggerListItems(VersionListView);
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Title = _all.Count == 0
            ? Loc.T("还没有已安装的版本，去「下载」页安装一个吧")
            : Loc.T("没有匹配的版本，试试调整搜索或筛选");
    }

    private void RebuildCategoryFilter()
    {
        var current = CategoryFilter.SelectedItem as string ?? Loc.T("全部");
        var categories = _all.Select(r => r.Category)
            .Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
        var items = new List<string> { Loc.T("全部"), Loc.T("未分类") };
        items.AddRange(categories);
        CategoryFilter.ItemsSource = items;
        CategoryFilter.SelectedItem = items.Contains(current) ? current : Loc.T("全部");
    }

    private void Refresh_Click(object s, RoutedEventArgs e) => RefreshList();
    private void SearchBox_TextChanged(object s, TextChangedEventArgs e) => ApplyFilter();
    private void CategoryFilter_SelectionChanged(object s, SelectionChangedEventArgs e) => ApplyFilter();

    private void OpenDir_Click(object s, RoutedEventArgs e) =>
        _ = UiDialog.AlertAsync(Loc.T("打开游戏目录"), _s.Settings.DotMinecraft);

    private void VersionList_ItemClick(object s, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LocalVersionRow row)
            NavigateToDetail(row.Id);
    }

    /// <summary>进入版本详情页：壳路由由主控在 ShellRootView 注册（versionDetail → VersionDetailPage）。</summary>
    private static void NavigateToDetail(string versionId)
    {
        VersionDetailPage.PendingVersionId = versionId;
        AppNavigation.Navigate("versionDetail");
    }

    // ── 行菜单（共用：Opening 时缓存 Target.DataContext = LocalVersionRow） ──

    private LocalVersionRow? _menuRow;

    private void RowMenu_Opening(object? sender, object e) =>
        _menuRow = (sender as MenuFlyout)?.Target is FrameworkElement fe
            ? fe.DataContext as LocalVersionRow : null;

    private LocalVersionRow? RowOf(object? sender) =>
        _menuRow ?? (sender as FrameworkElement)?.DataContext as LocalVersionRow;

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        LaunchVersion(row.Id);
    }

    /// <summary>启动入口（对齐首页规则）：选定版本 → 游戏页覆盖层自动拉起启动链。</summary>
    private void LaunchVersion(string versionId)
    {
        // 下载目标 = 当前选中版本（Settings.LastVersion），启动即视为选中
        _s.Settings.LastVersion = versionId;
        GameLauncherService.Instance.SelectedVersion = versionId;
        _s.SaveSettings();
        AppNavigation.PushOverlay(new GamePage(autoLaunch: true));
    }

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        NavigateToDetail(row.Id);
    }

    // 行菜单里的文件夹入口：版本目录固定为 versions/<id>；
    // 存档 / 模组跟随实例根 —— 隔离实例各自持有一份，共享实例落在 .minecraft 下
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var dotMc = _s.Settings.DotMinecraft;
        var root = InstanceLayout.GameRoot(dotMc, row.Id, row.Isolated);
        var dir = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "saves" => Path.Combine(root, "saves"),
            "mods" => Path.Combine(root, "mods"),
            _ => Path.Combine(dotMc, "versions", row.Id),
        };
        Directory.CreateDirectory(dir);
        _ = UiDialog.AlertAsync(Loc.T("版本文件夹"), dir);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var result = await InstanceActions.RenameAsync(Ui.Root!,
            _s.Settings.DotMinecraft, row.Id);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("重命名失败"), result.Error);
            return;
        }
        RefreshList();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var result = await InstanceActions.CopyAsync(Ui.Root!,
            _s.Settings.DotMinecraft, row.Id, row.Isolated);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("复制失败"), result.Error);
            return;
        }
        RefreshList();
    }

    /// <summary>入口③「问 Nova」：选定实例上下文 → 跳转 Nova 页承接。</summary>
    private void AskNova_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _s.NovaRuntime.SetCurrentInstance(row.Id);
        // 送入 Nova 的请求文本同时是 Core 侧中文关键词路由（NovaService）的匹配依据，故不本地化
        _s.NovaRuntime.RequestInput($"帮我给 {row.Id} 做一次游戏体检，有隐患直接说");
        AppNavigation.Navigate("ai");
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        // 快键按钮：DataContext 即行数据；菜单路径：Target.DataContext
        var row = (s as FrameworkElement)?.DataContext as LocalVersionRow ?? RowOf(s);
        if (row is null) return;
        var running = _s.Runtime.Instances.Any(i => i.VersionId == row.Id);
        var dlg = new ThemedDialog
        {
            Title = Loc.T("删除版本"),
            Content = running
                ? Loc.T("版本 {0} 正在运行，请先停止游戏再删除。", row.Id)
                : Loc.T("确定要删除版本 {0} 吗？\n将删除其版本文件夹，此操作不可恢复。", row.Id),
            PrimaryButtonText = running ? string.Empty : Loc.T("删除"),
            CloseButtonText = Loc.T("关闭"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        if (running || await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, row.Id);
            new InstanceSettingsStore(_s.Settings.DotMinecraft).Remove(row.Id);
        }
        catch (Exception ex)
        {
            var err = new ThemedDialog
            {
                Title = Loc.T("删除失败"),
                Content = ex.Message,
                CloseButtonText = Loc.T("确定"),
                XamlRoot = Ui.Root,
            };
            await err.ShowAsync();
        }
        RefreshList();
    }

    // ── 整合包导出 / 导入 / 备份回档（B 组：整合包与版本资产） ──

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        await ModPackDialogs.ExportAsync(Ui.Root!, _s, row);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        // 安卓无 FileOpenPicker 流程：引导走「从链接导入」或下载页
        await UiDialog.AlertAsync(Loc.T("导入整合包"),
            Loc.T("安卓端暂不支持本地文件选择，请使用「从链接导入」或到下载页安装整合包。"));
    }

    private async void ImportFromLink_Click(object sender, RoutedEventArgs e)
    {
        await ModPackDialogs.ImportFromLinkAsync(Ui.Root!, _s);
        RefreshList();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        await ModPackDialogs.BackupAsync(Ui.Root!, _s, row);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => Loc.T("未完成"),
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}
