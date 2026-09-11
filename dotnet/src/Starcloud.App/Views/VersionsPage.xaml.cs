// 版本页：本地已安装版本列表（搜索/分类过滤、实例档案徽章、右键/「…」完整菜单）
// 计划：docs/superpowers/plans/2026-09-09-version-management-plan.md T4/T7

using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Starcloud.App.Views;

public sealed partial class VersionsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private List<LocalVersionRow> _all = [];

    public VersionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        DirText.Text = _s.Settings.DotMinecraft;
        var dotMc = _s.Settings.DotMinecraft;
        var store = new InstanceSettingsStore(dotMc);
        try
        {
            var installed = _s.Manifest.ListInstalled(dotMc);
            _all = installed.Select(i =>
            {
                var profile = store.Get(i.Id);
                var category = profile.Category ?? "";
                return new LocalVersionRow(
                    i.Id, VersionRowHelpers.DetectKind(dotMc, i.Id), FormatSize(i.SizeBytes),
                    VersionRowHelpers.ResolveIconPath(dotMc, profile.IconPath),
                    category,
                    InstanceLayout.IsIsolated(profile, _s.Settings),
                    VersionRowHelpers.FormatLastPlayed(profile.LastPlayedAt),
                    (i.Id + " " + category).ToLowerInvariant());
            }).ToList();
        }
        catch
        {
            _all = [];
        }
        RebuildCategoryFilter();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<LocalVersionRow> query = _all;
        var kw = SearchBox.Text.Trim().ToLowerInvariant();
        if (kw.Length > 0) query = query.Where(r => r.SearchKey.Contains(kw));
        var cat = (CategoryFilter.SelectedItem as string) ?? "全部";
        if (cat == "未分类") query = query.Where(r => string.IsNullOrEmpty(r.Category));
        else if (cat != "全部") query = query.Where(r => r.Category == cat);
        var list = query.ToList();
        VersionListView.ItemsSource = list;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0
            ? "还没有已安装的版本，去「下载」页安装一个吧"
            : "没有匹配的版本，试试调整搜索或筛选";
    }

    private void RebuildCategoryFilter()
    {
        var current = CategoryFilter.SelectedItem as string ?? "全部";
        var categories = _all.Select(r => r.Category)
            .Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
        var items = new List<string> { "全部", "未分类" };
        items.AddRange(categories);
        CategoryFilter.ItemsSource = items;
        CategoryFilter.SelectedItem = items.Contains(current) ? current : "全部";
    }

    private void Refresh_Click(object s, RoutedEventArgs e) => RefreshList();
    private void SearchBox_TextChanged(object s, TextChangedEventArgs e) => ApplyFilter();
    private void CategoryFilter_SelectionChanged(object s, SelectionChangedEventArgs e) => ApplyFilter();

    private void OpenDir_Click(object s, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _s.Settings.DotMinecraft,
            UseShellExecute = true,
        });
    }

    private void VersionList_ItemClick(object s, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LocalVersionRow row)
            (App.MainWindow as MainWindow)?.NavigateToVersionDetail(row.Id);
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
        App.PendingLaunchVersionId = row.Id;
        (App.MainWindow as MainWindow)?.NavigateTo("home");
    }

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        (App.MainWindow as MainWindow)?.NavigateToVersionDetail(row.Id);
    }

    private void OpenVersionDir_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var dir = InstanceLayout.GameRoot(_s.Settings.DotMinecraft, row.Id, row.Isolated);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var result = await InstanceActions.RenameAsync(Content.XamlRoot,
            _s.Settings.DotMinecraft, row.Id);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "重命名失败", result.Error);
            return;
        }
        RefreshList();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var result = await InstanceActions.CopyAsync(Content.XamlRoot,
            _s.Settings.DotMinecraft, row.Id, row.Isolated);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "复制失败", result.Error);
            return;
        }
        RefreshList();
    }

    private async void ViewJson_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        await InstanceActions.ShowJsonAsync(Content.XamlRoot, _s.Settings.DotMinecraft, row.Id);
    }

    /// <summary>入口③「问 Nova」：选定实例上下文 → 展开 Nova 面板承接，不跳页。</summary>
    private void AskNova_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _s.NovaRuntime.SetCurrentInstance(row.Id);
        _s.NovaRuntime.RequestInput($"帮我给 {row.Id} 做一次游戏体检，有隐患直接说");
        (App.MainWindow as MainWindow)?.OpenNovaPanel();
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        // 快键按钮：DataContext 即行数据；菜单路径：Target.DataContext
        var row = (s as FrameworkElement)?.DataContext as LocalVersionRow ?? RowOf(s);
        if (row is null) return;
        var running = App.RunningVersionId == row.Id;
        var dlg = new ContentDialog
        {
            Title = "删除版本",
            Content = running
                ? $"版本 {row.Id} 正在运行，请先停止游戏再删除。"
                : $"确定要删除版本 {row.Id} 吗？\n将删除其版本文件夹，此操作不可恢复。",
            PrimaryButtonText = running ? string.Empty : "删除",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (running || await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, row.Id);
            new InstanceSettingsStore(_s.Settings.DotMinecraft).Remove(row.Id);
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                Title = "删除失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot,
            };
            await err.ShowAsync();
        }
        RefreshList();
    }

    // ── 整合包导出 / 导入 / 备份回档（B 组：整合包与版本资产） ──

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        await ModPackDialogs.ExportAsync(Content.XamlRoot, _s, row);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".mrpack");
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await ModPackDialogs.ImportAsync(Content.XamlRoot, _s, file.Path);
        RefreshList();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        await ModPackDialogs.BackupAsync(Content.XamlRoot, _s, row);
    }

    // 整页拖拽：.mrpack 或 .zip → 导入流程
    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "导入整合包";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        IReadOnlyList<IStorageItem> items;
        try { items = await e.DataView.GetStorageItemsAsync(); }
        catch { return; }
        var file = items.OfType<StorageFile>().FirstOrDefault(f =>
            f.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
            f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (file is null) return;
        await ModPackDialogs.ImportAsync(Content.XamlRoot, _s, file.Path);
        RefreshList();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "未完成",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}
