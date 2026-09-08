// 首页：PCL2 式左卡（版本列表 → 启动 → 账户），版本条目支持右键菜单

using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class HomePage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private List<VersionRow> _rows = [];
    private string _mode = "offline";
    private Process? _gameProcess;

    public HomePage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        SetMode(_s.Settings.LastUsername is { } u && u.StartsWith("ms:") ? "microsoft" : "offline");
        LoadVersions();
        RefreshAccounts();
        RefreshGuide();
    }

    private void SetMode(string mode)
    {
        _mode = mode;
        ModeMsBtn.IsChecked = mode == "microsoft";
        ModeOfflineBtn.IsChecked = mode == "offline";
        RefreshAccounts();
    }

    private void ModeMs_Click(object s, RoutedEventArgs e) => SetMode("microsoft");
    private void ModeOffline_Click(object s, RoutedEventArgs e) => SetMode("offline");

    // ── 版本列表 ──
    private void LoadVersions()
    {
        try
        {
            var installed = _s.Manifest.ListInstalled(_s.Settings.DotMinecraft).ToList();
            installed.Sort((a, b) =>
            {
                // 上次使用的版本置顶，其余按版本号倒序
                var aLast = string.Equals(a.Id, _s.Settings.LastVersion, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                var bLast = string.Equals(b.Id, _s.Settings.LastVersion, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (aLast != bLast) return bLast - aLast;
                return string.Compare(b.Id, a.Id, StringComparison.OrdinalIgnoreCase);
            });
            _rows = installed.Select(i => new VersionRow(i.Id, FormatSize(i.SizeBytes))).ToList();
        }
        catch (Exception ex)
        {
            _rows = [];
            LocalErrorBar.Message = $"读取已安装版本失败：{ex.Message}";
            LocalErrorBar.IsOpen = true;
        }
        VersionList.ItemsSource = _rows;
        EmptyVersionHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var preferred = _s.Settings.LastVersion is { } last && _rows.Any(r => r.Id == last) ? last
            : _rows.FirstOrDefault()?.Id;
        VersionList.SelectedItem = _rows.FirstOrDefault(r => r.Id == preferred);
    }

    private void RefreshVersionsBtn_Click(object s, RoutedEventArgs e)
    {
        LoadVersions();
        RefreshGuide();
    }

    private void VersionList_SelectionChanged(object s, SelectionChangedEventArgs e) => UpdateVersionMeta();

    private void UpdateVersionMeta()
    {
        var id = (VersionList.SelectedItem as VersionRow)?.Id;
        var isRunning = _gameProcess is { HasExited: false };
        LaunchTitleText.Text = isRunning ? "停止游戏" : "启动游戏";
        LaunchIcon.Glyph = isRunning ? "\uE71A" : "\uE768";
        LaunchSubText.Text = id ?? "未选择版本";
        LaunchBtn.IsEnabled = isRunning || id is not null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "未完成",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    // ── 版本右键菜单 ──
    private void VersionRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionRow row })
            VersionList.SelectedItem = row;
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        var dir = Path.Combine(_s.Settings.DotMinecraft, "versions", id);
        if (!Directory.Exists(dir))
        {
            LocalErrorBar.Message = "版本目录不存在";
            LocalErrorBar.IsOpen = true;
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        var dlg = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定要删除版本 {id} 吗？\n将删除其版本文件夹，此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, id);
            if (string.Equals(_s.Settings.LastVersion, id, StringComparison.OrdinalIgnoreCase))
            {
                _s.Settings.LastVersion = null;
                _s.Settings.Save();
            }
            LoadVersions();
            RefreshGuide();
        }
        catch (Exception ex)
        {
            LocalErrorBar.Message = $"删除失败：{ex.Message}";
            LocalErrorBar.IsOpen = true;
        }
    }

    // ── 账户 ──
    private void RefreshAccounts()
    {
        var msAccounts = _s.Accounts.Accounts.Where(a => a.Type == "microsoft").ToList();
        var offlineAccounts = _s.Accounts.Accounts.Where(a => a.Type == "offline").ToList();
        var list = _mode == "microsoft" ? msAccounts : offlineAccounts;

        var def = _s.Accounts.Default;
        var showInput = _mode == "offline" && list.Count == 0;
        PlayerNameBox.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;
        AccountCombo.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count > 0)
        {
            AccountCombo.ItemsSource = list
                .Select(a => new ComboItem(a.Uuid, a.IsDefault ? $"{a.Username}（默认）" : a.Username))
                .ToList();
            AccountCombo.SelectedValue = def is { } d && list.Any(a => a.Uuid == d.Uuid)
                ? d.Uuid : list[0].Uuid;
        }
        else
        {
            AccountNameText.Text = _mode == "microsoft" ? "尚无正版账户" : "输入玩家名";
            AvatarText.Text = "?";
        }
        RefreshNameFromSelection();
    }

    private void RefreshNameFromSelection()
    {
        var uuid = AccountCombo.SelectedValue as string;
        var acc = _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        var name = acc?.Username ?? PlayerNameBox.Text.Trim();
        AccountNameText.Text = string.IsNullOrWhiteSpace(name) ? "未选择账户" : name;
        AvatarText.Text = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
    }

    private void AccountCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (AccountCombo.SelectedValue is string uuid)
            _s.Accounts.SetDefault(uuid);
        RefreshNameFromSelection();
    }

    private void PlayerName_TextChanged(object s, TextChangedEventArgs e) => RefreshNameFromSelection();

    // ── 启动 / 停止 ──
    private async void LaunchBtn_Click(object s, RoutedEventArgs e)
    {
        if (_gameProcess is { HasExited: false })
        {
            try { _gameProcess.Kill(true); } catch { /* ignore */ }
            return;
        }

        var versionId = (VersionList.SelectedItem as VersionRow)?.Id;
        if (string.IsNullOrEmpty(versionId)) return;

        LaunchBtn.IsEnabled = false;
        GlobalErrorBar.IsOpen = false;
        try
        {
            string playerName;
            LaunchCredentials? cred = null;
            if (_mode == "microsoft")
            {
                StatusText.Text = "正在验证 Microsoft 凭据…";
                StatusText.Visibility = Visibility.Visible;
                var ms = await ResolveMicrosoftAccountAsync();
                if (ms is null)
                {
                    StatusText.Visibility = Visibility.Collapsed;
                    return;
                }
                (playerName, cred) = ms.Value;
            }
            else
            {
                playerName = ResolvePlayerName();
                if (string.IsNullOrWhiteSpace(playerName))
                {
                    LocalErrorBar.Message = "请输入离线玩家名或选择账户";
                    LocalErrorBar.IsOpen = true;
                    return;
                }
            }

            var options = new LaunchOptions(
                versionId, playerName, _s.Settings.DotMinecraft,
                _s.Settings.DefaultMinMemoryMb, _s.Settings.DefaultMaxMemoryMb,
                _s.Settings.JavaExecutable, _s.Settings.ExtraJvmArgs,
                _s.Settings.UseBuiltinJvmArgs, cred);
            _gameProcess = await _s.Launcher.LaunchVersionAsync(
                _s.Settings.DotMinecraft, versionId, options);
            _gameProcess.Exited += (_, _) => _dq.TryEnqueue(() =>
            {
                StatusText.Text = "游戏已退出";
                StatusText.Visibility = Visibility.Visible;
                UpdateVersionMeta();
            });
            StatusText.Text = "游戏运行中";
            StatusText.Visibility = Visibility.Visible;
            _s.Settings.LastUsername = _mode == "microsoft" ? "ms:" + playerName : playerName;
            _s.Settings.LastVersion = versionId;
            _s.Settings.Save();
        }
        catch (Exception ex)
        {
            LocalErrorBar.Message = ex.Message;
            LocalErrorBar.IsOpen = true;
        }
        finally
        {
            LaunchBtn.IsEnabled = true;
            UpdateVersionMeta();
        }
    }

    private string ResolvePlayerName()
    {
        if (_mode == "microsoft")
        {
            var uuid = AccountCombo.SelectedValue as string;
            return _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)?.Username ?? "";
        }
        var fromCombo = _s.Accounts.Accounts.FirstOrDefault(
            a => a.Uuid == (AccountCombo.SelectedValue as string))?.Username;
        return fromCombo ?? PlayerNameBox.Text.Trim();
    }

    /// <summary>微软模式启动前解析凭据：优先 refresh_token 续期并回写存储；
    /// 续期失效或网络异常时降级用存量 accessToken（1 小时有效期内）；
    /// 无可用凭据返回 null（已设置错误提示）。</summary>
    private async Task<(string Name, LaunchCredentials Cred)?> ResolveMicrosoftAccountAsync()
    {
        var uuid = AccountCombo.SelectedValue as string;
        var acc = _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid && a.Type == "microsoft");
        if (acc is null)
        {
            LocalErrorBar.Message = "尚无微软账户，请先到账户页登录";
            LocalErrorBar.IsOpen = true;
            return null;
        }
        if (!string.IsNullOrEmpty(acc.RefreshToken))
        {
            try
            {
                var r = await _s.MicrosoftAuth.RefreshAsync(acc.RefreshToken);
                if (r is not null)
                {
                    _s.Accounts.AddOrUpdateMicrosoft(r.Uuid, r.Username, r.AccessToken, r.RefreshToken);
                    return (r.Username, new LaunchCredentials(r.Uuid, r.AccessToken, "msa", r.Xuid));
                }
            }
            catch { /* 网络异常：降级用存量 accessToken */ }
        }
        if (!string.IsNullOrEmpty(acc.AccessToken))
            return (acc.Username, new LaunchCredentials(acc.Uuid, acc.AccessToken, "msa"));
        LocalErrorBar.Message = $"{acc.Username} 的 Microsoft 登录已过期，请到账户页重新登录";
        LocalErrorBar.IsOpen = true;
        return null;
    }

    // ── 入口 ──
    private void PickVersion_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");

    private void VersionSettings_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("settings");

    private void TileDownload_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");
    private void TileMods_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");
    private void TileMultiplayer_Click(object s, RoutedEventArgs e) =>
        LocalErrorBar.Message = "联机功能将在后续版本提供";

    // ── 启动自检（版本 / 账户 / Java） ──
    private bool? _javaOk;   // null=检测中

    private void Recheck_Click(object s, RoutedEventArgs e) => RefreshGuide();

    private void CheckGoVersion_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("download");
    private void CheckGoAccount_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("accounts");
    private void CheckGoJava_Click(object s, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("settings");

    private void RefreshGuide()
    {
        var hasInstalled = _rows.Count > 0;
        var hasAccount = _s.Accounts.Accounts.Count > 0 || PlayerNameBox.Text.Trim().Length > 0;
        SetCheckRow(CheckIconVersion, CheckTextVersion, CheckGoVersion,
            hasInstalled, "游戏版本", hasInstalled ? "就绪" : "未安装", "前往下载");
        SetCheckRow(CheckIconAccount, CheckTextAccount, CheckGoAccount,
            hasAccount, "账户", hasAccount ? "就绪" : "未配置", "前往账户");
        // Java 检测需要拉起 java -version，较慢，异步进行
        _javaOk = null;
        CheckTextJava.Text = "Java 运行时：检测中…";
        _ = CheckJavaAsync();
        UpdateCheckCardVisibility();
    }

    private async Task CheckJavaAsync()
    {
        try
        {
            var runtimes = await _s.Java.ListRuntimesAsync();
            var ok = runtimes.Count > 0 || !string.IsNullOrWhiteSpace(_s.Settings.JavaExecutable);
            _javaOk = ok;
            SetCheckRow(CheckIconJava, CheckTextJava, CheckGoJava, ok, "Java 运行时",
                ok ? (runtimes.Count > 0 ? $"就绪（检测到 {runtimes.Count} 个）" : "就绪（使用手动指定路径）") : "未检测到",
                "前往设置");
        }
        catch
        {
            _javaOk = false;
            SetCheckRow(CheckIconJava, CheckTextJava, CheckGoJava, false, "Java 运行时", "检测失败", "前往设置");
        }
        UpdateCheckCardVisibility();
    }

    private void UpdateCheckCardVisibility()
    {
        var hasInstalled = _rows.Count > 0;
        var hasAccount = _s.Accounts.Accounts.Count > 0 || PlayerNameBox.Text.Trim().Length > 0;
        // 全部就绪后收起；Java 检测中保持显示以免闪动
        CheckCard.Visibility = !hasInstalled || !hasAccount || _javaOk == false
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetCheckRow(FontIcon icon, TextBlock text, HyperlinkButton go,
        bool ok, string label, string detail, string action)
    {
        icon.Glyph = ok ? "\uE73E" : "\uE7BA";
        icon.Foreground = ok
            ? ThemeBrush("SystemFillColorSuccessBrush")
            : ThemeBrush("SystemFillColorCriticalBrush") ?? icon.Foreground;
        text.Text = $"{label}：{detail}";
        go.Content = action;
        go.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
}
