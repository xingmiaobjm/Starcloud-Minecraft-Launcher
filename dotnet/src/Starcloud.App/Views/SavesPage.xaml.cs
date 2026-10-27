// 存档页：跨版本列出 / 搜索 / 筛选世界，支持备份 zip / 删除 / 打开文件夹 / 跨版本复制启动
// 设计：docs/designs/2026-09-10-saves-cross-version-design.md

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Dialogs;
using Starcloud.App.Views.Rows;
using Starcloud.App.Views.Shared;
using Starcloud.Core.Services;
using System.Diagnostics;
using System.IO;

namespace Starcloud.App.Views;

public sealed partial class SavesPage : Page
{
    // 全部：仅内存筛选哨兵，属界面文案，随语言变化
    private static string AllVersions => Loc.T("全部");
    // 共享：既是筛选哨兵，又是副本目录名（"{世界}-来自 共享"）与持久化记录 SourceVersion="" 的稳定标识，
    // 故保留中文常量不本地化，否则切换语言后 FindCopyFolder 前缀匹配会失败（属文件路径豁免）
    private const string SharedLabel = "共享";

    private readonly AppServices _s = AppServices.Instance;
    private List<SaveRow> _rows = [];
    private bool _busy;
    private bool _loading;   // 后台扫描存档中（与操作忙碌分开，避免互相吞掉）
    private bool _suppressFilter;
    private GameSession? _gameProcess;

    // 选中竖条的落点（NaN = 还没落过）与滚动同步入口，用法同主页版本列表
    private double _barY = double.NaN;
    private bool _barShown;
    private ScrollViewer? _savesScroll;

    public SavesPage()
    {
        InitializeComponent();
        // 容器回收会带着上一行的选中淡底：只按这一格重刷，避免整表遍历
        SaveList.ContainerContentChanging += (_, e) =>
        {
            if (e.InRecycleQueue) return;
            ListSelectionFx.SyncInsetFor(SaveList, e.ItemIndex, e.ItemContainer, animate: false);
            UpdateSelectionBar(animate: false);
        };
        Loaded += async (_, _) =>
        {
            await ResumePendingAsync();   // 中断补问须先于列表刷新（spec §3.5）
            await LoadSavesAsync();
        };
    }

    // ── 列表 ──

    /// <summary>同步入口（刷新按钮/删除后等）：后台加载，不阻塞调用方。</summary>
    private void LoadSaves() => _ = LoadSavesAsync();

    /// <summary>
    /// 存档列表：页面先把空壳交出来，扫描（多目录 + 读 level.dat / 图标）放后台线程。
    /// 之前同步跑 ListAll，存档一多整次页面切换都卡在导航动画里 —— 先出界面再出内容。
    /// </summary>
    private async Task LoadSavesAsync()
    {
        if (_loading) return;
        _loading = true;
        var dotMc = _s.Settings.DotMinecraft;
        // 首次加载先给一个「读取中」的空态：否则页面先闪一下「暂无存档」再跳出列表
        if (_rows.Count == 0)
        {
            EmptyHint.Title = Loc.T("正在读取存档…");
            EmptyHint.Subtitle = "";
            EmptyHint.Visibility = Visibility.Visible;
        }
        try
        {
            var found = await Task.Run(() => _s.SaveLibrary.ListAll(dotMc, _s.Settings).ToList());
            _rows = found.Select(v => new SaveRow(
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
        finally
        {
            _loading = false;
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

        SaveList.ItemsSource = filtered;
        // 从左往右羽化：位移 + 淡入 + 从左展开的裁剪（MotionFx.FeatherIn）
        MotionFx.StaggerListItems(SaveList);
        // 列表已重建：竖条落点作废，等容器真正生成后直接给终态（不播位移动画）
        _barY = double.NaN;
        _barShown = false;
        SaveList.UpdateLayout();
        HookSaveScroll();
        SyncSelectionMark(animate: false);

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

    /// <summary>启动选中存档对应的版本（不复制存档，直接进这个版本）。</summary>
    private async void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row) return;
        var targetId = row.VersionId;
        if (string.IsNullOrEmpty(targetId))
        {
            ErrorBar.Message = Loc.T("这个存档在共享目录里，没有对应的版本。");
            ErrorBar.IsOpen = true;
            return;
        }

        var dotMc = _s.Settings.DotMinecraft;
        _busy = true;
        UpdateRowActions();
        try
        {
            var store = new InstanceSettingsStore(dotMc);
            var eff = InstanceLayout.Resolve(dotMc, targetId, store.Get(targetId), _s.Settings);
            var player = await AuthDialogs.EnsurePlayableAccountAsync(Content.XamlRoot, _s);
            if (player is null) return; // 用户取消就地登录

            string? agentJar = null;
            if (player.ExternalBase is not null)
            {
                agentJar = AppServices.AgentJarPath()
                    ?? throw new InvalidOperationException(
                        "authlib-injector 组件缺失，无法启动第三方账户，请重新安装启动器");
            }

            var options = new LaunchOptions(targetId, player.Name, eff.GameRoot, dotMc,
                eff.MinMem, eff.MaxMem, eff.Java, eff.ExtraJvmArgs, _s.Settings.UseBuiltinJvmArgs,
                player.Credentials, eff.ServerAddress, eff.Language, eff.LanguageOverride,
                eff.WindowW, eff.WindowH, eff.Fullscreen, eff.EnvVars, null, null,
                AgentJarPath: agentJar, AgentBase: player.ExternalBase,
                PlayerSkinUrl: player.SkinUrl,
                PluginHook: AppServices.Instance.PluginHost);

            _gameProcess = await _s.Launcher.LaunchVersionAsync(dotMc, targetId, options);
            _s.Runtime.Attach(_gameProcess, GameLaunchContextStore.Capture(targetId, eff,
                GameLaunchContextStore.CountFiles(Path.Combine(eff.GameRoot, "mods")),
                GameLaunchContextStore.CountFiles(Path.Combine(eff.GameRoot, "shaderpacks")),
                GameLaunchContextStore.CountFiles(Path.Combine(eff.GameRoot, "resourcepacks"))),
                _gameProcess.CommandLine);
            _gameProcess.Exited += _ => DispatcherQueue.TryEnqueue(async () => await OnGameExitedAsync());
            store.TouchPlayed(targetId);
            RunBar.Message = Loc.T("正在启动 {0}…", targetId);
            RunBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("启动失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            UpdateRowActions();
        }
    }

    private void UpdateRowActions()
    {
        var has = SaveList.SelectedItem is SaveRow;
        LaunchGameBtn.IsEnabled = has && !_busy;
        SyncBtn.IsEnabled = has && !_busy;
        ShareBtn.IsEnabled = has && !_busy;
        DeleteBtn.IsEnabled = has && !_busy;
        ImportShareBtn.IsEnabled = !_busy;
    }

    private void SaveList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        UpdateRowActions();
        SyncSelectionMark();
    }

    /// <summary>选中态：行内淡底 + 列表左缘那根共用竖条（与主页版本列表同款）。</summary>
    private void SyncSelectionMark(bool animate = true)
    {
        ListSelectionFx.SyncInset(SaveList, animate);
        UpdateSelectionBar(animate);
    }

    private void UpdateSelectionBar(bool animate) =>
        ListSelectionFx.UpdateBar(SelectionBarLayer, SelectionBar, SaveList,
            ref _barY, ref _barShown, animate);

    /// <summary>滚动时竖条跟着行一起走（不播动画，直接同步位置）。</summary>
    private void HookSaveScroll()
    {
        if (_savesScroll is not null) return;
        _savesScroll = ListSelectionFx.FindDescendant<ScrollViewer>(SaveList);
        if (_savesScroll is not null)
            _savesScroll.ViewChanged += (_, _) => UpdateSelectionBar(animate: false);
    }

    private void Refresh_Click(object s, RoutedEventArgs e) => LoadSaves();

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => Loc.T("空"),
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

    /// <summary>
    /// 同步到设备：把所选世界（连同其版本配置）推送到选定的同账号在线设备。
    /// 冲突保护在链路层（新者胜、旧者改名保留），推送结果落 OkBar / ErrorBar 与通知中心。
    /// </summary>
    private async void SyncToDevice_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        if (!_s.StarcloudId.IsLoggedIn)
        {
            ErrorBar.Message = Loc.T("同步需要先登录 Starcloud ID（设置 → 账户）");
            ErrorBar.IsOpen = true;
            return;
        }
        var peers = _s.DeviceLink.Peers;
        if (peers.Count == 0)
        {
            ErrorBar.Message = Loc.T("当前没有在线设备：对端需登录同一 Starcloud ID，并在设置里开启「跨设备存档同步」。");
            ErrorBar.IsOpen = true;
            return;
        }

        // 设备选择对话框
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 180, Padding = new Thickness(0) };
        foreach (var p in peers)
            list.Items.Add(new ListViewItem
            {
                Content = $"{p.Name}（{p.Address}）",
                Tag = p,
                Padding = new Thickness(8, 6, 8, 6),
            });
        list.SelectedIndex = 0;
        var dlg = new ThemedDialog
        {
            Title = Loc.T("同步到设备"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T("把「{0}」推送到哪台设备？对端同名世界更新时会拒收（不会覆盖对端进度）。", row.Name),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    list,
                },
            },
            PrimaryButtonText = Loc.T("开始同步"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary ||
            list.SelectedItem is not ListViewItem item || item.Tag is not PeerInfo peer) return;

        // 行里只有展示文本：按 folder+version 回到聚合列表拿原始 SaveInfo（LastPlayed 参与冲突裁决）
        var save = _s.SaveLibrary.ListAll(_s.Settings.DotMinecraft, _s.Settings)
            .FirstOrDefault(v => v.FolderName == row.FolderName && v.VersionId == row.VersionId);
        if (save is null) return;

        _busy = true;
        BackupProgress.Visibility = Visibility.Visible;
        UpdateRowActions();
        try
        {
            var result = await Task.Run(() => _s.SaveSync.PushAsync(peer, save));
            if (result.Ok)
            {
                OkBar.Message = result.Message;
                OkBar.IsOpen = true;
                NotificationCenter.Instance.Info(Loc.T("世界「{0}」已同步到 {1}", row.Name, peer.Name));
            }
            else
            {
                ErrorBar.Message = result.Message;
                ErrorBar.IsOpen = true;
            }
        }
        finally
        {
            _busy = false;
            BackupProgress.Visibility = Visibility.Collapsed;
            UpdateRowActions();
        }
    }

    /// <summary>
    /// 分享给陌生人：把世界导出为分享包文件（zip），经由聊天工具等任意渠道发送；
    /// 对方在存档页「导入分享包」即可，无需登录任何账号（跨账号分享走文件，不经云端）。
    /// </summary>
    private async void ExportShare_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedFileName = Loc.T("{0}-分享包", row.Name);
        picker.DefaultFileExtension = ".zip";
        picker.FileTypeChoices.Add(Loc.T("世界分享包"), new List<string> { ".zip" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        _busy = true;
        BackupProgress.Visibility = Visibility.Visible;
        UpdateRowActions();
        try
        {
            await Task.Run(() => _s.Saves.ExportForShare(row.GameRoot, row.FolderName, file.Path));
            OkBar.Message = Loc.T("已导出分享包：{0}。把它发给任何人，对方点「导入分享包」即可。", file.Path);
            OkBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("导出失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            BackupProgress.Visibility = Visibility.Collapsed;
            UpdateRowActions();
        }
    }

    /// <summary>导入分享包：选一个 zip（自己导出的或别人发来的），落到共享根的 saves。</summary>
    private async void ImportShare_Click(object s, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        if (!SaveService.IsValidSharePackage(file.Path))
        {
            ErrorBar.Message = Loc.T("这不是有效的世界分享包（zip 根目录缺少 level.dat）");
            ErrorBar.IsOpen = true;
            return;
        }

        _busy = true;
        BackupProgress.Visibility = Visibility.Visible;
        UpdateRowActions();
        try
        {
            var folder = await Task.Run(() => _s.Saves.ImportShared(file.Path, _s.Settings.DotMinecraft));
            OkBar.Message = Loc.T("已导入世界「{0}」到共享存档；可在列表里选中它并用「用其他版本启动」或「复制到其他版本」。", folder);
            OkBar.IsOpen = true;
            NotificationCenter.Instance.Info(Loc.T("已导入世界「{0}」", folder));
            LoadSaves();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("导入失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            BackupProgress.Visibility = Visibility.Collapsed;
            UpdateRowActions();
        }
    }

    private async void Backup_Click(object s, RoutedEventArgs e)
    {        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        var dlg = new ThemedDialog
        {
            Title = Loc.T("备份世界"),
            Content = Loc.T("将「{0}」压缩备份到 backups 文件夹，可能需要一些时间，继续吗？", row.Name),
            PrimaryButtonText = Loc.T("开始备份"),
            CloseButtonText = Loc.T("取消"),
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

        ResolvedPlayer? resolved;
        try
        {
            // 共享就地登录流程：会话内无凭证时弹登录框，登录后自动重试
            resolved = await AuthDialogs.EnsurePlayableAccountAsync(Content.XamlRoot, _s);
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }
        if (resolved is not { } player) return; // 用户取消就地登录

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
        var account = new TextBlock
        {
            Text = Loc.T("使用账户：{0}（{1}）", player.Name,
                player.ExternalBase is not null ? Loc.T("第三方") : Loc.T("正版")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var confirm = new CheckBox { Content = Loc.T("我已了解跨版本风险") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(warn);
        panel.Children.Add(combo);
        panel.Children.Add(basic);
        panel.Children.Add(account);
        panel.Children.Add(confirm);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("用其他版本打开世界"),
            Content = panel,
            CloseButtonText = Loc.T("取消"),
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

        await LaunchCopiedAsync(row, targetId, targetRoot, copyFolder, sameRoot);
    }

    /// <summary>基础启动模式：不做预检 / 自动修复，也不执行版本级启动前后命令（spec §5）</summary>
    private async Task LaunchCopiedAsync(SaveRow row, string targetId, string targetRoot,
        string? copyFolder, bool sameRoot)
    {
        var dotMc = _s.Settings.DotMinecraft;
        _busy = true;
        UpdateRowActions();
        try
        {
            var store = new InstanceSettingsStore(dotMc);
            var eff = InstanceLayout.Resolve(dotMc, targetId, store.Get(targetId), _s.Settings);
            var player = await AuthDialogs.EnsurePlayableAccountAsync(Content.XamlRoot, _s);
            if (player is null) return; // 用户取消就地登录

            // 第三方账户：注入 authlib-injector（Assets 内置 jar + 用户自己的认证服务器）
            string? agentJar = null;
            if (player.ExternalBase is not null)
            {
                agentJar = AppServices.AgentJarPath()
                    ?? throw new InvalidOperationException(
                        "authlib-injector 组件缺失，无法启动第三方账户，请重新安装启动器");
            }

            var options = new LaunchOptions(targetId, player.Name, eff.GameRoot, dotMc,
                eff.MinMem, eff.MaxMem, eff.Java, eff.ExtraJvmArgs, _s.Settings.UseBuiltinJvmArgs,
                player.Credentials, eff.ServerAddress, eff.Language, eff.LanguageOverride,
                eff.WindowW, eff.WindowH, eff.Fullscreen, eff.EnvVars, null, null,
                AgentJarPath: agentJar, AgentBase: player.ExternalBase,
                PlayerSkinUrl: player.SkinUrl,
                PluginHook: AppServices.Instance.PluginHost);

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
                if (!sameRoot) new PendingWorldStore(dotMc).Save(rec);
                ErrorBar.Message = sameRoot
                    ? Loc.T("启动失败：{0}", ex.Message)
                    : Loc.T("启动失败：{0}。副本已保留在 {1}，可稍后重试", ex.Message, targetId);
                ErrorBar.IsOpen = true;
                return;
            }

            // 运行状态监控：上下文取自本次已解析的 eff（不再重复 Resolve）
            var gameRoot = eff.GameRoot;
            _s.Runtime.Attach(_gameProcess, GameLaunchContextStore.Capture(targetId, eff,
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "mods")),
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "shaderpacks")),
                GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "resourcepacks"))),
                _gameProcess.CommandLine);
            _gameProcess.Exited += _ => DispatcherQueue.TryEnqueue(async () => await OnGameExitedAsync());
            rec.ProcessId = _gameProcess.Pid;
            new PendingWorldStore(dotMc).Save(rec);

            store.TouchPlayed(targetId);
            RunBar.Message = Loc.T("正在以 {0} 运行「{1}」", targetId, row.Name);
            RunBar.IsOpen = true;
            LoadSaves();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("启动失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            UpdateRowActions();
        }
    }

    private void StopGame_Click(object s, RoutedEventArgs e)
    {
        try { _gameProcess?.Kill(); } catch { /* 进程可能已退出 */ }
        RunBar.IsOpen = false;
    }

    // ── 退出归属三选一 + 中断补问 ──

    private async Task OnGameExitedAsync()
    {
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
        try
        {
            var dotMc = _s.Settings.DotMinecraft;
            var store = new PendingWorldStore(dotMc);
            var rec = store.Load();
            if (rec is null) return;
            // 那一次启动的实例仍在跑 → 交给它的 Exited 回调补问（多开：不能只看「有没有游戏在跑」）
            if (rec.ProcessId > 0 && _s.Runtime.Instances.Any(i => i.Pid == rec.ProcessId)) return;
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
            ? Loc.T("「{0}」已复制到 {1}，但游戏未成功启动。这份副本要如何处理？", rec.WorldName, rec.TargetVersion)
            : Loc.T("「{0}」在 {1} 中的改动将如何处理？", rec.WorldName, rec.TargetVersion);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("世界成果归属"),
            Content = detail,
            PrimaryButtonText = Loc.T("保存到当前版本"),
            SecondaryButtonText = Loc.T("保留在原本的版本"),
            CloseButtonText = Loc.T("丢弃本次改动"),
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
            OkBar.Message = Loc.T("已用副本替换源存档，原存档备份在 {0}", zip);
            OkBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = zip is null
                ? Loc.T("回传失败：{0}（副本仍在 {1}）", ex.Message, rec.TargetVersion)
                : Loc.T("回传失败：{0}；原存档已备份到 {1}，副本仍在 {2}", ex.Message, zip, rec.TargetVersion);
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
