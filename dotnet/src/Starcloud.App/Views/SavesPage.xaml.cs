// 存档页：跨版本列出 / 搜索 / 筛选世界，支持备份 zip / 删除 / 打开文件夹 / 跨版本复制启动
// 设计：docs/superpowers/specs/2026-09-10-saves-cross-version-design.md

using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class SavesPage : Page
{
    private const string AllVersions = "全部";
    private const string SharedLabel = "共享";

    private readonly AppServices _s = AppServices.Instance;
    private List<SaveRow> _rows = [];
    private bool _busy;
    private bool _suppressFilter;
    private GameSession? _gameProcess;
    private GameSessionHandle? _sessionHandle;  // 防双开会话锁（spec §8.3）

    public SavesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ResumePendingAsync();   // 中断补问须先于列表刷新（spec §3.5）
            LoadSaves();
        };
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
                v.LastPlayed is { } t ? $"上次游玩 {t:yyyy-MM-dd HH:mm}" : "无游玩记录",
                FormatSize(v.SizeBytes),
                v.Isolated ? v.VersionId : SharedLabel,
                v.VersionId,
                v.Isolated,
                v.GameRoot,
                $"{v.LevelName} {v.FolderName} {v.VersionId}".ToLowerInvariant())).ToList();
        }
        catch (Exception ex)
        {
            _rows = [];
            ErrorBar.Message = $"读取存档失败：{ex.Message}";
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
        var items = new List<string> { AllVersions, SharedLabel };
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

        SaveList.ItemsSource = filtered;

        if (_rows.Count == 0)
        {
            EmptyHint.Title = "暂无存档";
            EmptyHint.Subtitle = "启动游戏创建世界后，可在此进行备份与管理";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (filtered.Count == 0)
        {
            EmptyHint.Title = "没有匹配的存档";
            EmptyHint.Subtitle = "试试换个关键词或版本筛选";
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
        <= 0 => "空",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    private static void OpenInExplorer(string path, bool select)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"",
            UseShellExecute = true,
        });
    }

    // ── 单存档操作（均以行的实例根为基准） ──

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row) return;
        var dir = Path.Combine(row.GameRoot, "saves", row.FolderName);
        if (Directory.Exists(dir)) OpenInExplorer(dir, select: false);
    }

    private void OpenSavesDir_Click(object s, RoutedEventArgs e)
    {
        var dir = Path.Combine(_s.Settings.DotMinecraft, "saves");
        Directory.CreateDirectory(dir);
        OpenInExplorer(dir, select: false);
    }

    private async void Backup_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        var dlg = new ContentDialog
        {
            Title = "备份世界",
            Content = $"将「{row.Name}」压缩备份到 backups 文件夹，可能需要一些时间，继续吗？",
            PrimaryButtonText = "开始备份",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        BackupProgress.Visibility = Visibility.Visible;
        UpdateRowActions();
        try
        {
            var zipPath = await Task.Run(() => _s.Saves.Backup(
                row.GameRoot, Path.Combine(row.GameRoot, "backups"), row.FolderName));
            OkBar.Message = $"已备份到 {zipPath}";
            OkBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"备份失败：{ex.Message}";
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
        var dlg = new ContentDialog
        {
            Title = "删除世界",
            Content = $"确定要删除「{row.Name}」吗？\n世界文件夹将被移除，建议先备份。此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _s.Saves.Delete(row.GameRoot, row.FolderName);
            LoadSaves();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"删除失败：{ex.Message}";
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
            ErrorBar.Message = "没有其他已安装版本，请先在下载页安装一个版本";
            ErrorBar.IsOpen = true;
            return;
        }

        ResolvedPlayer player;
        try
        {
            player = await _s.LaunchAccount.ResolveAsync();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }

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
            Text = "基础启动模式：不含文件校验与自动修复，失败请回首页启动。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var account = new TextBlock
        {
            Text = $"使用账户：{player.Name}（{(player.Credentials is null ? "离线" : "正版")}）",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var confirm = new CheckBox { Content = "我已了解跨版本风险" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(warn);
        panel.Children.Add(combo);
        panel.Children.Add(basic);
        panel.Children.Add(account);
        panel.Children.Add(confirm);

        var dlg = new ContentDialog
        {
            Title = "用其他版本打开世界",
            Content = panel,
            CloseButtonText = "取消",
            XamlRoot = Content.XamlRoot,
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
            warn.Message = same
                ? $"此世界来自 {sourceLabel}，与 {targetId} 使用同一游戏目录，无需复制。"
                : $"此世界来自 {sourceLabel}，将由 {targetId} 打开。跨版本打开可能触发世界数据升级，且改动不可逆。";
            confirm.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
            dlg.PrimaryButtonText = same ? "启动" : "复制并启动";
            dlg.SecondaryButtonText = same ? "" : "仅复制";
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
                ErrorBar.Message = $"复制失败：{ex.Message}";
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
            OkBar.Message = $"已复制到 {targetId}：{copyFolder}";
            OkBar.IsOpen = true;
            LoadSaves();
            return;
        }

        await LaunchCopiedAsync(row, targetId, targetRoot, copyFolder, sameRoot);
    }

    /// <summary>基础启动模式：不做预检 / 自动修复，也不执行版本级启动前后命令（spec §5）</summary>
    private async Task LaunchCopiedAsync(SaveRow row, string targetId, string targetRoot,
        string? copyFolder, bool sameRoot)
    {
        var dotMc = _s.Settings.DotMinecraft;
        ReleaseSession();
        _sessionHandle = GameSessionGuard.TryAcquire(dotMc, targetId, out var blockedBy);
        if (_sessionHandle is null)
        {
            ErrorBar.Message = $"版本 {blockedBy} 正在运行中，请先退出游戏";
            ErrorBar.IsOpen = true;
            return;
        }

        _busy = true;
        UpdateRowActions();
        try
        {
            var store = new InstanceSettingsStore(dotMc);
            var eff = InstanceLayout.Resolve(dotMc, targetId, store.Get(targetId), _s.Settings);
            var player = await _s.LaunchAccount.ResolveAsync();
            var options = new LaunchOptions(targetId, player.Name, eff.GameRoot, dotMc,
                eff.MinMem, eff.MaxMem, eff.Java, eff.ExtraJvmArgs, _s.Settings.UseBuiltinJvmArgs,
                player.Credentials, eff.ServerAddress, eff.Language, eff.LanguageOverride,
                eff.WindowW, eff.WindowH, eff.Fullscreen, eff.EnvVars, null, null);

            var rec = new PendingWorldRecord
            {
                WorldFolder = row.FolderName,
                WorldName = row.Name,
                SourceVersion = row.Isolated ? row.VersionId : "",
                TargetVersion = targetId,
                SourceGameRoot = row.GameRoot,
                TargetGameRoot = targetRoot,
                Copied = !sameRoot,
                StartedAt = DateTime.Now,
            };

            try
            {
                _gameProcess = await _s.Launcher.LaunchVersionAsync(dotMc, targetId, options);
            }
            catch (Exception ex)
            {
                // 启动抛异常不回滚副本（spec §5）：留记录，用户可稍后重试或另选归属
                ReleaseSession();
                if (!sameRoot) new PendingWorldStore(dotMc).Save(rec);
                ErrorBar.Message = sameRoot
                    ? $"启动失败：{ex.Message}"
                    : $"启动失败：{ex.Message}。副本已保留在 {targetId}，可稍后重试";
                ErrorBar.IsOpen = true;
                return;
            }

            _sessionHandle?.Bind(_gameProcess.Pid);
            _gameProcess.Exited += _ => DispatcherQueue.TryEnqueue(async () => await OnGameExitedAsync());
            rec.ProcessId = _gameProcess.Pid;
            new PendingWorldStore(dotMc).Save(rec);

            App.RunningVersionId = targetId;
            store.TouchPlayed(targetId);
            RunBar.Message = $"正在以 {targetId} 运行「{row.Name}」";
            RunBar.IsOpen = true;
            LoadSaves();
        }
        catch (Exception ex)
        {
            ReleaseSession();
            ErrorBar.Message = $"启动失败：{ex.Message}";
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            UpdateRowActions();
        }
    }

    /// <summary>释放防双开会话锁（退出 / 失败 / 取消都要调用，spec §8.3）。</summary>
    private void ReleaseSession()
    {
        try { _sessionHandle?.Dispose(); } catch { /* 锁文件被占用时忽略 */ }
        _sessionHandle = null;
    }

    private void StopGame_Click(object s, RoutedEventArgs e)
    {
        try { _gameProcess?.Kill(); } catch { /* 进程可能已退出 */ }
        RunBar.IsOpen = false;
    }

    // ── 退出归属三选一 + 中断补问 ──

    private async Task OnGameExitedAsync()
    {
        App.RunningVersionId = null;
        ReleaseSession();
        RunBar.IsOpen = false;

        var dotMc = _s.Settings.DotMinecraft;
        var store = new PendingWorldStore(dotMc);
        var rec = store.Load();
        if (rec is null ||
            string.Equals(rec.SourceGameRoot, rec.TargetGameRoot, StringComparison.OrdinalIgnoreCase))
        {
            store.Clear();   // 源根 = 目标根时没有副本概念，不弹窗（spec §5）
            LoadSaves();
            return;
        }

        var copyFolder = FindCopyFolder(rec.TargetGameRoot, rec.WorldFolder, SourceLabelOf(rec));
        await AskWorldOutcomeAsync(rec, copyFolder);
    }

    /// <summary>启动器在游戏运行期间被关闭：下次进入存档页补问（spec §3.5）</summary>
    private async Task ResumePendingAsync()
    {
        if (App.RunningVersionId is not null) return;   // 游戏仍在运行，交给 Exited 回调
        try
        {
            var dotMc = _s.Settings.DotMinecraft;
            var store = new PendingWorldStore(dotMc);
            var rec = store.Load();
            if (rec is null) return;
            if (string.Equals(rec.SourceGameRoot, rec.TargetGameRoot, StringComparison.OrdinalIgnoreCase))
            {
                store.Clear();
                return;
            }

            var copyFolder = FindCopyFolder(rec.TargetGameRoot, rec.WorldFolder, SourceLabelOf(rec));
            if (copyFolder is null)
            {
                store.Clear();   // 副本已不在磁盘上，无需补问
                return;
            }
            await AskWorldOutcomeAsync(rec, copyFolder);
        }
        catch
        {
            // 补问失败不阻塞列表加载
        }
    }

    /// <summary>成果归属三选一；结束后清记录并刷新列表</summary>
    private async Task AskWorldOutcomeAsync(PendingWorldRecord rec, string? copyFolder)
    {
        var xamlRoot = (App.MainWindow?.Content as FrameworkElement)?.XamlRoot ?? Content.XamlRoot;
        if (xamlRoot is null) return;

        var detail = copyFolder is null
            ? $"「{rec.WorldName}」已复制到 {rec.TargetVersion}，但游戏未成功启动。这份副本要如何处理？"
            : $"「{rec.WorldName}」在 {rec.TargetVersion} 中的改动将如何处理？";

        var dlg = new ContentDialog
        {
            Title = "世界成果归属",
            Content = detail,
            PrimaryButtonText = "保存到当前版本",
            SecondaryButtonText = "保留在原本的版本",
            CloseButtonText = "丢弃本次改动",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        // 三选一：Primary=保存到当前版本；Secondary=保留在原本的版本；Close（None）=丢弃
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Secondary)
            await ReturnCopyToSourceAsync(rec, copyFolder);
        else if (result == ContentDialogResult.None && copyFolder is not null)
            _s.Saves.Delete(rec.TargetGameRoot, copyFolder);

        new PendingWorldStore(_s.Settings.DotMinecraft).Clear();
        LoadSaves();
    }

    /// <summary>用副本替换源存档：先 zip 兜底备份 → 替换源目录 → 删目标副本</summary>
    private async Task ReturnCopyToSourceAsync(PendingWorldRecord rec, string? copyFolder)
    {
        if (copyFolder is null) return;
        var copyDir = Path.Combine(rec.TargetGameRoot, "saves", copyFolder);
        var srcDir = Path.Combine(rec.SourceGameRoot, "saves", rec.WorldFolder);
        string? zip = null;
        try
        {
            zip = await Task.Run(() => _s.Saves.Backup(rec.SourceGameRoot,
                Path.Combine(rec.SourceGameRoot, "backups"), rec.WorldFolder));
            await Task.Run(() =>
            {
                if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
                CopyDirectory(copyDir, srcDir);
            });
            _s.Saves.Delete(rec.TargetGameRoot, copyFolder);
            OkBar.Message = $"已用副本替换源存档，原存档备份在 {zip}";
            OkBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = zip is null
                ? $"回传失败：{ex.Message}（副本仍在 {rec.TargetVersion}）"
                : $"回传失败：{ex.Message}；原存档已备份到 {zip}，副本仍在 {rec.TargetVersion}";
            ErrorBar.IsOpen = true;
        }
    }

    private static string SourceLabelOf(PendingWorldRecord rec) =>
        string.IsNullOrEmpty(rec.SourceVersion) ? SharedLabel : rec.SourceVersion;

    /// <summary>在目标版本 saves 下定位副本目录（重名时实际名可能带「 (n)」后缀）</summary>
    private static string? FindCopyFolder(string targetGameRoot, string worldFolder, string sourceLabel)
    {
        var saves = Path.Combine(targetGameRoot, "saves");
        if (!Directory.Exists(saves)) return null;
        var prefix = $"{worldFolder}-来自 {sourceLabel}";
        string? partial = null;
        foreach (var dir in Directory.EnumerateDirectories(saves))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase)) return name;
            if (partial is null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) partial = name;
        }
        return partial;
    }

    /// <summary>目录重名递增 " (n)" 后缀（副本名含版本点号，不能用 NextAvailablePath 的扩展名拆分）</summary>
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
