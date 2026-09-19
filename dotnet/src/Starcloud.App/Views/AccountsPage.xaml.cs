// 账户页：Starcloud ID 登录/注册（LauncherX 式大标题）+ 游戏账户管理（微软设备码 / 第三方外置登录）

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

    public AccountsPage()
    {
        InitializeComponent();
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
        ScSubtitleText.Text = Loc.T("Starcloud ID 已登录，在线功能已就绪");
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
            ShowStatus(Loc.T("已登录，在线功能已就绪"));
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

        var dlg = new ThemedDialog
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
        var dlg = new ThemedDialog
        {
            Title = Loc.T("退出登录"),
            Content = Loc.T("退出后本地登录状态将被清除，确定退出吗？"),
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
        var dlg = new ThemedDialog
        {
            Title = Loc.T("请选择您的登录方式"),
            Content = Loc.T("选择要添加的游戏账户类型：正版 Microsoft 账户，或你自己的第三方认证服务器账户。"),
            PrimaryButtonText = "Microsoft",
            SecondaryButtonText = Loc.T("第三方登录"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await StartMsLogin();
        else if (result == ContentDialogResult.Secondary)
            await AddExternalAccountAsync();
    }

    private async Task AddExternalAccountAsync()
    {
        var name = await AuthDialogs.ExternalPasswordAsync(Content.XamlRoot, _s.Yggdrasil, _s.Accounts);
        if (name is null) return;
        RefreshList();
        ShowStatus(Loc.T("第三方登录 {0} 已添加", name));
    }

    private async Task StartMsLogin()
    {
        var result = await AuthDialogs.MicrosoftDeviceCodeAsync(Content.XamlRoot, _s.MicrosoftAuth, _s.Accounts, ShowError);
        if (result is null) return;
        RefreshList();
        ShowStatus(Loc.T("Microsoft 账户已登录：{0}", result.Username));
    }

    // ── 已保存账户列表 ──

    private void RefreshList()
    {
        var rows = _s.Accounts.Accounts
            .Select(a => new AccountRow(a.Uuid, a.Username,
                a.Type switch
                {
                    "microsoft" => Loc.T("微软账户"),
                    "external" => a.ServerName is { Length: > 0 }
                        ? Loc.T("第三方账户 · {0}", a.ServerName)
                        : Loc.T("第三方账户"),
                    _ => a.Type,
                },
                a.IsDefault,
                AvatarHelper.Resolve(a.Type, a.Uuid, a.Username, a.ServerBase)))
            .ToList();
        AccountList.ItemsSource = rows;
        MotionFx.StaggerListItems(AccountList);
        var empty = rows.Count == 0;
        AccountEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AccountList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        // 右侧 3D 形象：跟随默认账户（无默认取第一个；无账户渲染内置 Steve）
        var current = _s.Accounts.Accounts.FirstOrDefault(a => a.IsDefault)
                      ?? _s.Accounts.Accounts.FirstOrDefault();
        PlayerView.WalkEnabled = _s.Settings.AccountWalkAnimation;
        PlayerView.SetAccount(current?.Type, current?.Uuid, current?.Username, current?.ServerBase);
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
        var dlg = new ThemedDialog
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
