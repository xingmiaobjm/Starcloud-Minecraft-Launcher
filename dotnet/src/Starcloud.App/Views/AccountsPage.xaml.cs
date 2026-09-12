// 账户页：Starcloud ID 登录/注册（LauncherX 式大标题）+ 游戏账户管理（离线 / 微软设备码）

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class AccountsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private MicrosoftAuthService.DeviceCodeInfo? _deviceCode;
    private CancellationTokenSource? _pollCts;

    public AccountsPage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        Loaded += (_, _) =>
        {
            RefreshStarcloudState();
            RefreshList();
        };
    }

    // ── Starcloud ID：标题 + 登录/注册/会话管理 ──

    private void RefreshStarcloudState()
    {
        if (_s.StarcloudId.Current is not { } session)
        {
            ScTitleText.Text = Loc.T("登录 Starcloud 账号以使用更多功能");
            ScSubtitleText.Text = Loc.T("在此登录或注册 Starcloud ID，也可管理用于启动的游戏账户");
            ScLoginPanel.Visibility = Visibility.Visible;
            ScUserPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ScTitleText.Text = Loc.T("欢迎回来，{0}", session.DisplayName);
        ScSubtitleText.Text = Loc.T("Starcloud ID 已登录，Nova 云端模型已就绪");
        ScLoginPanel.Visibility = Visibility.Collapsed;
        ScUserPanel.Visibility = Visibility.Visible;
        ScAvatarText.Text = string.IsNullOrWhiteSpace(session.DisplayName)
            ? "?" : session.DisplayName[..1].ToUpperInvariant();
        ScNameText.Text = session.DisplayName;
        ScUserSubText.Text = Loc.T("{0} · {1}", session.UserId, session.Channel == StarcloudIdService.CloudChannel ? Loc.T("云端通道") : Loc.T("测试通道"));
        ScKeyText.Text = MaskKey(session.ApiKey);
    }

    private string SelectedChannel =>
        (ScChannelBox.SelectedItem as ComboBoxItem)?.Content.ToString() == Loc.T("云端通道")
            ? StarcloudIdService.CloudChannel : StarcloudIdService.TestChannel;

    private void ScChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScPwdBox is null) return; // XAML 初始化期间触发
        ScPwdBox.PlaceholderText = SelectedChannel == StarcloudIdService.CloudChannel
            ? Loc.T("密码") : Loc.T("密码（测试通道任意内容）");
    }

    private async void ScPwdBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await DoScLoginAsync();
    }

    private async void ScLogin_Click(object sender, RoutedEventArgs e) => await DoScLoginAsync();

    private async Task DoScLoginAsync()
    {
        ScErrorText.Visibility = Visibility.Collapsed;
        ScLoginBtn.IsEnabled = false;
        ScLoginProgress.Visibility = Visibility.Visible;
        ScLoginProgress.IsActive = true;
        try
        {
            await _s.StarcloudId.LoginAsync(ScIdBox.Text.Trim(), ScPwdBox.Password, SelectedChannel);
            ScPwdBox.Password = "";
            RefreshStarcloudState();
            _s.NovaRuntime.NotifyLoginChanged(); // 登录态变了：Nova 状态机/问候/Orb 面板同步
            ShowStatus(Loc.T("已登录，Nova 云端模型 Key 已就绪"));
        }
        catch (Exception ex)
        {
            ScErrorText.Text = ex.Message;
            ScErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            ScLoginBtn.IsEnabled = true;
            ScLoginProgress.Visibility = Visibility.Collapsed;
            ScLoginProgress.IsActive = false;
        }
    }

    private async void ScRegister_Click(object sender, RoutedEventArgs e)
    {
        var idBox = new TextBox { PlaceholderText = Loc.T("用户名或邮箱") };
        var pwdBox = new PasswordBox { PlaceholderText = Loc.T("至少 6 位") };
        var pwd2Box = new PasswordBox { PlaceholderText = Loc.T("再次输入密码") };
        var errText = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Loc.T("Starcloud ID") });
        panel.Children.Add(idBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("密码") });
        panel.Children.Add(pwdBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("确认密码") });
        panel.Children.Add(pwd2Box);
        panel.Children.Add(errText);

        var dlg = new ContentDialog
        {
            Title = Loc.T("注册 Starcloud ID"),
            Content = panel,
            PrimaryButtonText = Loc.T("创建账户"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        dlg.Closing += (s, args) =>
        {
            if (args.Result != ContentDialogResult.Primary) return;
            string? err = null;
            if (string.IsNullOrWhiteSpace(idBox.Text)) err = Loc.T("Starcloud ID 不能为空");
            else if (pwdBox.Password.Length < 6) err = Loc.T("密码至少 6 位");
            else if (pwdBox.Password != pwd2Box.Password) err = Loc.T("两次输入的密码不一致");
            if (err is not null)
            {
                args.Cancel = true;
                errText.Text = err;
                errText.Visibility = Visibility.Visible;
            }
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _s.StarcloudId.RegisterAsync(idBox.Text.Trim(), pwdBox.Password, SelectedChannel);
            ScIdBox.Text = idBox.Text.Trim();
            ScPwdBox.Password = "";
            ScPwdBox.Focus(FocusState.Programmatic);
            ShowStatus(Loc.T("注册成功，请登录"));
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ScCopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (_s.StarcloudId.Current is not { } session || sender is not Button btn) return;
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        pkg.SetText(session.ApiKey);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

        var original = btn.Content;
        btn.Content = Loc.T("已复制");
        btn.IsEnabled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            btn.Content = original;
            btn.IsEnabled = true;
            timer.Stop();
        };
        timer.Start();
    }

    private async void ScLogout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = Loc.T("退出登录"),
            Content = Loc.T("退出后本地会话与 Nova 云端模型 Key 将被删除，确定退出吗？"),
            PrimaryButtonText = Loc.T("退出"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.StarcloudId.Logout();
        RefreshStarcloudState();
        _s.NovaRuntime.NotifyLoginChanged(); // 退出后 Orb 面板回到未登录引导
    }

    // ── 游戏账户：添加入口 + 登录方式选择 ──

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = Loc.T("请选择您的登录方式"),
            Content = Loc.T("选择要添加的游戏账户类型。如果不清楚该使用哪种方式，建议使用离线账户先体验。"),
            PrimaryButtonText = Loc.T("离线账户"),
            SecondaryButtonText = "Microsoft",
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ShowOfflineDialogAsync();
        else if (result == ContentDialogResult.Secondary)
            StartMsLogin();
    }

    private async Task ShowOfflineDialogAsync()
    {
        var nameBox = new TextBox { PlaceholderText = Loc.T("3-16 位字母、数字、下划线"), MaxLength = 16 };
        var errText = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Loc.T("玩家名称") });
        panel.Children.Add(nameBox);
        panel.Children.Add(errText);

        var dlg = new ContentDialog
        {
            Title = Loc.T("创建新账户"),
            Content = panel,
            PrimaryButtonText = Loc.T("保存账户"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        dlg.Closing += (s, args) =>
        {
            if (args.Result != ContentDialogResult.Primary) return;
            if (!ValidateOfflineName(nameBox.Text.Trim(), out var err))
            {
                args.Cancel = true;
                errText.Text = err;
                errText.Visibility = Visibility.Visible;
            }
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.Accounts.AddOffline(nameBox.Text.Trim());
        RefreshList();
        ShowStatus(Loc.T("离线账户 {0} 已添加", nameBox.Text.Trim()));
    }

    private static bool ValidateOfflineName(string name, out string error)
    {
        if (name.Length is < 3 or > 16 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            error = Loc.T("玩家名只能包含字母、数字、下划线，长度 3-16");
            return false;
        }
        error = "";
        return true;
    }

    // ── Microsoft 设备码登录（对话框内完成，LauncherX 式一次性验证码） ──

    private async void StartMsLogin()
    {
        try
        {
            _deviceCode = await _s.MicrosoftAuth.StartDeviceCodeAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        var code = _deviceCode;
        var completed = false;

        var codeText = new TextBlock
        {
            Text = code.UserCode,
            FontSize = 32,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var statusText = new TextBlock
        {
            Text = Loc.T("等待授权…"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var openBtn = new Button
        {
            Content = Loc.T("复制并打开验证网站"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        openBtn.Click += async (_, _) =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            pkg.SetText(code.UserCode);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            openBtn.Content = Loc.T("已复制并打开");
            await Windows.System.Launcher.LaunchUriAsync(new Uri(code.VerificationUri));
        };

        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("使用一次性验证代码登录到你的 Microsoft 账户"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(codeText);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("使用浏览器打开 {0}，输入以上代码并按照网页提示完成授权。完成后启动器将自动把账户添加至启动器内。", code.VerificationUri),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(openBtn);
        panel.Children.Add(new ProgressRing { Width = 24, Height = 24, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(statusText);

        var dlg = new ContentDialog
        {
            Title = Loc.T("登录 Microsoft 账户"),
            Content = panel,
            CloseButtonText = Loc.T("取消"),
            XamlRoot = Content.XamlRoot,
        };
        dlg.Closed += (_, _) =>
        {
            if (!completed) _pollCts?.Cancel();
        };
        _ = dlg.ShowAsync(); // 对话框保持打开，后台轮询

        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        await PollMsAsync(dlg, statusText, () => completed = true);
    }

    private async Task PollMsAsync(ContentDialog dlg, TextBlock statusText, Action markCompleted)
    {
        var ct = _pollCts!.Token;
        var interval = Math.Max(_deviceCode?.Interval ?? 5, 3);
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            try
            {
                var (state, message, result) = await _s.MicrosoftAuth.PollAsync(_deviceCode!, ct);
                if (state == "success" && result is not null)
                {
                    _s.Accounts.AddOrUpdateMicrosoft(result.Uuid, result.Username,
                        result.AccessToken, result.RefreshToken);
                    markCompleted();
                    _dq.TryEnqueue(() =>
                    {
                        dlg.Hide();
                        RefreshList();
                        ShowStatus(Loc.T("Microsoft 账户已登录：{0}", result.Username));
                    });
                    return;
                }
                if (state == "error")
                {
                    markCompleted();
                    _dq.TryEnqueue(() =>
                    {
                        dlg.Hide();
                        ShowError(message);
                    });
                    return;
                }
                consecutiveFailures = 0;
                _dq.TryEnqueue(() => statusText.Text = Loc.T("等待授权…（{0}）", message));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (++consecutiveFailures < 3)
            {
                // 网络波动：连续 3 次才终止
                Starcloud.Core.Services.LauncherLog.Write("auth",
                    $"轮询第 {consecutiveFailures} 次失败（继续重试）：{ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Starcloud.Core.Services.LauncherLog.Write("auth", $"轮询终止：{ex.GetType().Name}: {ex.Message}");
                markCompleted();
                _dq.TryEnqueue(() =>
                {
                    dlg.Hide();
                    ShowError(ex.Message);
                });
                return;
            }
        }
    }

    // ── 已保存账户列表 ──

    private void RefreshList()
    {
        var rows = _s.Accounts.Accounts
            .Select(a => new AccountRow(a.Uuid, a.Username,
                a.Type == "microsoft" ? Loc.T("微软账户") : Loc.T("离线账户"), a.IsDefault))
            .ToList();
        AccountList.ItemsSource = rows;
        var empty = rows.Count == 0;
        AccountEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AccountList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string uuid) return;
        _s.Accounts.SetDefault(uuid);
        RefreshList();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string uuid) return;
        var acc = _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        var dlg = new ContentDialog
        {
            Title = Loc.T("删除账户"),
            Content = Loc.T("确定要删除账户 {0} 吗？", acc?.Username ?? uuid),
            PrimaryButtonText = Loc.T("删除"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.Accounts.Remove(uuid);
        RefreshList();
    }

    // ── 工具 ──

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        StatusInfo.IsOpen = false;
    }

    private void ShowStatus(string message)
    {
        StatusInfo.Message = message;
        StatusInfo.IsOpen = true;
        ErrorBar.IsOpen = false;
    }

    private static string MaskKey(string key) =>
        key.Length <= 14 ? key : key[..10] + "…" + key[^4..];
}
