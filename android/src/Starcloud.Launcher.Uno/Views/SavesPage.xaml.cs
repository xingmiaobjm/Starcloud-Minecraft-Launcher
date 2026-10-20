// 存档页：跨版本列出 / 搜索 / 筛选世界，支持备份 zip / 删除 / 打开文件夹 / 跨版本复制启动
// 移植自桌面 Starcloud.App.Views.SavesPage（安卓侧适配）：
// - Process.Start 打开目录 → UiDialog 展示路径（安卓无桌面文件夹语义）
// - 桌面的运行状态条（RunBar / StopGame）与「世界成果归属」三选一（PendingWorldStore 补问）
//   依赖页内游戏进程句柄与退出回调；安卓启动链由 GamePage 覆盖层统一承接，
//   页内不再跟踪进程生命周期，相关流程随之下线
// - 跨版本启动不走 AuthDialogs 就地登录（Uno 树无该依赖）：账户与预检在启动链内处理，
//   复制完成后按首页同款入口 GameLauncherService.SelectedVersion + PushOverlay(GamePage(autoLaunch:true)) 拉起
// 设计：docs/designs/2026-09-10-saves-cross-version-design.md

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Starcloud.Launcher.Uno.Views.Rows;
using System.IO;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class SavesPage : Page, IShellPage
{
    // 全部：仅内存筛选哨兵，属界面文案，随语言变化
    private static string AllVersions => Loc.T("全部");
    // 共享：既是筛选哨兵，又是副本目录名（"{世界}-来自 共享"）与持久化记录 SourceVersion="" 的稳定标识，
    // 故保留中文常量不本地化，否则切换语言后前缀匹配会失败（属文件路径豁免）
    private const string SharedLabel = "共享";

    private readonly AppServices _s = AppServices.Instance;
    private List<SaveRow> _rows = [];
    private bool _busy;
    private bool _suppressFilter;
    /// <summary>上一次存档列表签名：内容没变就不重建列表 / 不重演错峰（切页闪烁来源之一）。</summary>
    private string? _appliedListSig;
    /// <summary>首次 Loaded 已跑完：页面启用导航缓存，之后每次进入由 OnShown 负责刷新。</summary>
    private bool _firstLoadDone;

    public SavesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_firstLoadDone) return; // 缓存实例再次挂树：刷新由 OnShown 负责，避免双份加载
            _firstLoadDone = true;
            LoadSaves();
        };
    }

    /// <summary>页面被导航显示（实例已缓存复用）：首次进入交给 Loaded，之后的每次进入在这里刷新。</summary>
    public void OnShown()
    {
        if (!_firstLoadDone) return;
        LoadSaves();
    }

    // ── 列表 ──

    private void LoadSaves()
    {
        if (_busy) return;
        var dotMc = _s.Settings.DotMinecraft;
        try
        {
            _rows = _s.SaveLibrary.ListAll(dotMc, _s.Settings).Select(v => new SaveRow(
                v.FolderName, v.LevelName, v.IconPath,
                v.LastPlayed is { } t ? Loc.T("上次游玩 {0:yyyy-MM-dd HH:mm}", t) : Loc.T("无游玩记录"),
                FormatSize(v.SizeBytes),
                v.Isolated ? v.VersionId : Loc.T("共享"),
                v.VersionId,
                v.Isolated,
                v.GameRoot,
                $"{v.LevelName} {v.FolderName} {v.VersionId}".ToLowerInvariant())).ToList();
        }
        catch (Exception ex)
        {
            _rows = [];
            ErrorBar.Message = Loc.T("读取存档失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }

        FillVersionFilter(dotMc);
        ApplyFilter();
        UpdateRowActions();
    }

    /// <summary>填充版本筛选（全部 / 共享 / 各已装版本），尽量保留当前选中项</summary>
    private void FillVersionFilter(string dotMc)
    {
        var prev = VersionFilter.SelectedItem as string ?? AllVersions;
        var items = new List<string> { AllVersions, Loc.T("共享") };
        try
        {
            items.AddRange(_s.SaveLibrary.ListVersionIds(dotMc));
        }
        catch { /* 版本清单读取失败时只留 全部 / 共享 */ }

        _suppressFilter = true;
        VersionFilter.ItemsSource = items;
        VersionFilter.SelectedItem = items.Contains(prev) ? prev : AllVersions;
        _suppressFilter = false;
    }

    /// <summary>搜索词 × 版本筛选（AND 过滤）</summary>
    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var version = VersionFilter.SelectedItem as string ?? AllVersions;
        var filtered = _rows.Where(r =>
            (query.Length == 0 || r.SearchKey.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (version == AllVersions || r.VersionLabel == version)).ToList();

        // 内容没变就不重建列表 / 不重演错峰：切页回来列表原样呈现（重演 = 肉眼可见的闪烁）
        var sig = query + "#" + version + "#" + string.Join("|", filtered.Select(r => r.FolderName));
        if (sig != _appliedListSig || SaveList.ItemsSource is null)
        {
            _appliedListSig = sig;
            SaveList.ItemsSource = filtered;
            MotionFx.StaggerListItems(SaveList);
        }

        if (_rows.Count == 0)
        {
            EmptyHint.Title = Loc.T("暂无存档");
            EmptyHint.Subtitle = Loc.T("启动游戏创建世界后，可在此进行备份与管理");
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (filtered.Count == 0)
        {
            EmptyHint.Title = Loc.T("没有匹配的存档");
            EmptyHint.Subtitle = Loc.T("试试换个关键词或版本筛选");
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplyFilter();

    private void VersionFilter_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        ApplyFilter();
    }

    private void UpdateRowActions()
    {
        var has = SaveList.SelectedItem is SaveRow;
        LaunchOtherBtn.IsEnabled = has && !_busy;
        CopyOtherBtn.IsEnabled = has && !_busy;
        BackupBtn.IsEnabled = has && !_busy;
        DeleteBtn.IsEnabled = has && !_busy;
        OpenFolderBtn.IsEnabled = has;
    }

    private void SaveList_SelectionChanged(object s, SelectionChangedEventArgs e) => UpdateRowActions();

    private void Refresh_Click(object s, RoutedEventArgs e) => LoadSaves();

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => Loc.T("空"),
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    // 安卓无资源管理器语义：弹窗展示目录路径（与版本页 / Nova 页同一口径）
    private static Task ShowDir(string title, string path) =>
        UiDialog.AlertAsync(title, path);

    // ── 单存档操作（均以行的实例根为基准） ──

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row) return;
        var dir = Path.Combine(row.GameRoot, "saves", row.FolderName);
        if (Directory.Exists(dir)) _ = ShowDir(Loc.T("世界文件夹"), dir);
    }

    private void OpenSavesDir_Click(object s, RoutedEventArgs e)
    {
        var dir = Path.Combine(_s.Settings.DotMinecraft, "saves");
        Directory.CreateDirectory(dir);
        _ = ShowDir(Loc.T("存档目录"), dir);
    }

    private async void Backup_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        var dlg = new ThemedDialog
        {
            Title = Loc.T("备份世界"),
            Content = Loc.T("将「{0}」压缩备份到 backups 文件夹，可能需要一些时间，继续吗？", row.Name),
            PrimaryButtonText = Loc.T("开始备份"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Ui.Root!,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        BackupProgress.Visibility = Visibility.Visible;
        UpdateRowActions();
        try
        {
            var zipPath = await Task.Run(() => _s.Saves.Backup(
                row.GameRoot, Path.Combine(row.GameRoot, "backups"), row.FolderName));
            OkBar.Message = Loc.T("已备份到 {0}", zipPath);
            OkBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("备份失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            BackupProgress.Visibility = Visibility.Collapsed;
            UpdateRowActions();
        }
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        var dlg = new ThemedDialog
        {
            Title = Loc.T("删除世界"),
            Content = Loc.T("确定要删除「{0}」吗？\n世界文件夹将被移除，建议先备份。此操作不可恢复。", row.Name),
            PrimaryButtonText = Loc.T("删除"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root!,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _s.Saves.Delete(row.GameRoot, row.FolderName);
            LoadSaves();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("删除失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
    }

    // ── 跨版本启动 / 复制 ──

    private async void LaunchOther_Click(object s, RoutedEventArgs e) =>
        await ShowCrossVersionDialogAsync(startAfterCopy: true);

    private async void CopyOther_Click(object s, RoutedEventArgs e) =>
        await ShowCrossVersionDialogAsync(startAfterCopy: false);

    private async Task ShowCrossVersionDialogAsync(bool startAfterCopy)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        ErrorBar.IsOpen = false;

        var dotMc = _s.Settings.DotMinecraft;
        var sourceLabel = row.Isolated ? row.VersionId : SharedLabel;
        var candidates = _s.SaveLibrary.ListVersionIds(dotMc).Where(v => v != sourceLabel).ToList();
        if (candidates.Count == 0)
        {
            ErrorBar.Message = Loc.T("没有其他已安装版本，请先在下载页安装一个版本");
            ErrorBar.IsOpen = true;
            return;
        }

        // 桌面蓝本此处有 AuthDialogs.EnsurePlayableAccountAsync 就地登录；
        // 安卓侧账户与预检统一由 GamePage 启动链处理，对话框只负责「选目标版本 + 风险确认」
        var store = new InstanceSettingsStore(dotMc);
        var warn = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false };
        var combo = new ComboBox
        {
            ItemsSource = candidates,
            SelectedIndex = 0,
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var basic = new TextBlock
        {
            Text = Loc.T("基础启动模式：不含文件校验与自动修复，失败请回首页启动。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var confirm = new CheckBox { Content = Loc.T("我已了解跨版本风险") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(warn);
        panel.Children.Add(combo);
        panel.Children.Add(basic);
        panel.Children.Add(confirm);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("用其他版本打开世界"),
            Content = panel,
            CloseButtonText = Loc.T("取消"),
            XamlRoot = Ui.Root!,
        };

        bool SameRoot(string targetId)
        {
            var isolated = InstanceLayout.IsIsolated(store.Get(targetId), _s.Settings);
            return string.Equals(InstanceLayout.GameRoot(dotMc, targetId, isolated), row.GameRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        void Sync()
        {
            var targetId = combo.SelectedItem as string ?? candidates[0];
            var same = SameRoot(targetId);
            var sourceDisplay = row.Isolated ? row.VersionId : Loc.T("共享");
            warn.Message = same
                ? Loc.T("此世界来自 {0}，与 {1} 使用同一游戏目录，无需复制。", sourceDisplay, targetId)
                : Loc.T("此世界来自 {0}，将由 {1} 打开。跨版本打开可能触发世界数据升级，且改动不可逆。", sourceDisplay, targetId);
            confirm.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
            dlg.PrimaryButtonText = same ? Loc.T("启动") : Loc.T("复制并启动");
            dlg.SecondaryButtonText = same ? "" : Loc.T("仅复制");
            dlg.DefaultButton = same || startAfterCopy ? ContentDialogButton.Primary : ContentDialogButton.Secondary;
            dlg.IsPrimaryButtonEnabled = same || confirm.IsChecked == true;
        }

        combo.SelectionChanged += (_, _) => Sync();
        confirm.Checked += (_, _) => dlg.IsPrimaryButtonEnabled = true;
        confirm.Unchecked += (_, _) => Sync();
        Sync();

        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.None) return;

        var targetId = combo.SelectedItem as string ?? candidates[0];
        var sameRoot = SameRoot(targetId);
        var doLaunch = result == ContentDialogResult.Primary;
        var targetIsolated = InstanceLayout.IsIsolated(store.Get(targetId), _s.Settings);
        var targetRoot = InstanceLayout.GameRoot(dotMc, targetId, targetIsolated);

        string? copyFolder = null;
        if (!sameRoot)
        {
            var targetSaves = Path.Combine(targetRoot, "saves");
            Directory.CreateDirectory(targetSaves);
            var finalName = NextAvailableDir(targetSaves, $"{row.FolderName}-来自 {sourceLabel}");
            var srcDir = Path.Combine(row.GameRoot, "saves", row.FolderName);
            var dstDir = Path.Combine(targetSaves, finalName);

            _busy = true;
            BackupProgress.Visibility = Visibility.Visible;
            UpdateRowActions();
            try
            {
                await Task.Run(() => CopyDirectory(srcDir, dstDir));
                copyFolder = finalName;
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(dstDir)) Directory.Delete(dstDir, true); } catch { /* 清理失败留给用户手工处理 */ }
                ErrorBar.Message = Loc.T("复制失败：{0}", ex.Message);
                ErrorBar.IsOpen = true;
                return;
            }
            finally
            {
                _busy = false;
                BackupProgress.Visibility = Visibility.Collapsed;
                UpdateRowActions();
            }
        }

        if (!doLaunch)
        {
            OkBar.Message = Loc.T("已复制到 {0}：{1}", targetId, copyFolder);
            OkBar.IsOpen = true;
            LoadSaves();
            return;
        }

        LaunchCopied(targetId);
    }

    /// <summary>
    /// 跨版本启动（安卓简化）：走首页同款入口，由 GamePage 覆盖层自动拉起启动链
    /// （账户 / 预检 / 进程管理都在启动链内）。桌面蓝本的 LaunchOptions 就地登录、
    /// 进程挂接与副本归属补问不再在页内进行（见文件头说明）。
    /// </summary>
    private void LaunchCopied(string targetId)
    {
        // 下载目标 = 当前选中版本（Settings.LastVersion），启动即视为选中
        _s.Settings.LastVersion = targetId;
        GameLauncherService.Instance.SelectedVersion = targetId;
        _s.SaveSettings();
        AppNavigation.PushOverlay(new GamePage(autoLaunch: true));
    }

    /// <summary>目录重名递增 " (n)" 后缀（副本名含版本点号，不能用扩展名拆分的通用重命名）</summary>
    private static string NextAvailableDir(string parentDir, string folderName)
    {
        var candidate = Path.Combine(parentDir, folderName);
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = Path.Combine(parentDir, $"{folderName} ({i})");
        return Path.GetFileName(candidate);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
