// 账户页（一级）：Starcloud ID 卡（登录 / 注册 / 会话管理都在这张卡里）+ 游戏账户列表。
//
// 账户列表照 HMCL 来：一行一个账户（头像 / 名字 / 类型 / 正在用的皮肤 / 进详情），
// 默认账户排在最前，其余操作都在二级页里：
//   点一行 → AccountDetailPage（形象与皮肤 / 账户信息）
//   右上「添加账户」→ AccountAddPage（左选身份、右填表单）
// 一级页本身不再弹任何对话框，只保留「退出登录」这种需要二次确认的动作。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Dialogs;
using Starcloud.App.Views.Rows;
using Starcloud.App.Views.Shared;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class AccountsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;

    /// <summary>
    /// 二级页（添加账户 / 账户详情）做完事回到本页时留下的结果，由 Loaded 取走显示一次。
    /// 页面是 NavigationCacheMode=Disabled，返回时整页重建，所以需要一个跨实例的交接点。
    /// </summary>
    internal static string? PendingStatus;

    public AccountsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshStarcloudState();
            RefreshList();
            if (PendingStatus is { Length: > 0 } pending)
            {
                PendingStatus = null;
                ShowStatus(pending);
            }
        };
    }

    // ── Starcloud ID：标题 + 登录 / 注册 / 会话管理 ──

    private void RefreshStarcloudState()
    {
        if (_s.StarcloudId.Current is not { } session)
        {
            ScTitleText.Text = Loc.T("账户");
            ScSubtitleText.Text = Loc.T("管理用于启动的游戏账户与 Starcloud ID；点一行进入详情");
            ScLoginPanel.Visibility = ScRegisterPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            ScUserPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ScTitleText.Text = Loc.T("欢迎回来，{0}", session.DisplayName);
        ScSubtitleText.Text = Loc.T("Starcloud ID 已登录，在线功能已就绪");
        ScLoginPanel.Visibility = Visibility.Collapsed;
        ScRegisterPanel.Visibility = Visibility.Collapsed;
        ScUserPanel.Visibility = Visibility.Visible;
        ScAvatarText.Text = string.IsNullOrWhiteSpace(session.DisplayName)
            ? "?" : session.DisplayName[..1].ToUpperInvariant();
        ScNameText.Text = session.DisplayName;
        ScUserSubText.Text = Loc.T("{0} · {1}", session.UserId,
            session.Channel == StarcloudIdService.CloudChannel ? Loc.T("云端通道") : Loc.T("测试通道"));
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

    // ── 注册：在同一张卡内展开，不再弹对话框 ──

    private void ScToRegister_Click(object sender, RoutedEventArgs e)
    {
        ScLoginPanel.Visibility = Visibility.Collapsed;
        ScRegisterPanel.Visibility = Visibility.Visible;
        ScRegisterBtn.IsEnabled = true;
        ScRegErrorText.Visibility = Visibility.Collapsed;
        ScRegIdBox.Focus(FocusState.Programmatic);
        ClearBars();
    }

    private void ScCancelRegister_Click(object sender, RoutedEventArgs e)
    {
        ScRegErrorText.Visibility = Visibility.Collapsed;
        ScRegisterPanel.Visibility = Visibility.Collapsed;
        ScLoginPanel.Visibility = Visibility.Visible;
    }

    private async void ScRegister_Click(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(ScRegIdBox.Text)) error = Loc.T("Starcloud ID 不能为空");
        else if (ScRegPwdBox.Password.Length < 6) error = Loc.T("密码至少 6 位");
        else if (ScRegPwdBox.Password != ScRegPwd2Box.Password) error = Loc.T("两次输入的密码不一致");
        if (error is not null)
        {
            ScRegErrorText.Text = error;
            ScRegErrorText.Visibility = Visibility.Visible;
            return;
        }

        ScRegisterBtn.IsEnabled = false;
        try
        {
            await _s.StarcloudId.RegisterAsync(ScRegIdBox.Text.Trim(), ScRegPwdBox.Password, SelectedChannel);
            ScIdBox.Text = ScRegIdBox.Text.Trim();
            ScPwdBox.Password = "";
            ScRegPwdBox.Password = "";
            ScRegPwd2Box.Password = "";
            ScRegisterPanel.Visibility = Visibility.Collapsed;
            ScLoginPanel.Visibility = Visibility.Visible;
            ScPwdBox.Focus(FocusState.Programmatic);
            ShowStatus(Loc.T("注册成功，请登录"));
        }
        catch (Exception ex)
        {
            ScRegErrorText.Text = ex.Message;
            ScRegErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            ScRegisterBtn.IsEnabled = true;
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

    // ── 游戏账户：二级页入口 ──

    private void AddAccount_Click(object sender, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateToAddAccount();

    private void AccountList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AccountRow row) OpenDetail(row.Uuid);
    }

    private void OpenDetail(string uuid) =>
        (App.MainWindow as MainWindow)?.NavigateToAccountDetail(uuid);

    // ── 已保存账户列表 ──

    private void RefreshList()
    {
        // 默认账户排在最前：一级页只回答"用哪个账户启动"，它得在第一眼看得到的地方
        var rows = _s.Accounts.Accounts
            .OrderByDescending(a => a.IsDefault)
            .Select(a => new AccountRow(a.Uuid, a.Username, TypeLabel(a), a.IsDefault,
                AvatarHelper.Resolve(a.Type, a.Uuid, a.Username, a.ServerBase,
                    AvatarHelper.LocalSkinPath(a)),
                SkinTextOf(a)))
            .ToList();
        AccountList.ItemsSource = rows;
        MotionFx.StaggerListItems(AccountList);
        var empty = rows.Count == 0;
        AccountEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AccountList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        AccountCountText.Text = Loc.T("{0} 个账户", rows.Count);
        AccountCountChip.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string TypeLabel(StoredAccount account) => account.Type switch
    {
        "microsoft" => Loc.T("微软账户"),
        "external" => account.ServerName is { Length: > 0 }
            ? Loc.T("第三方账户 · {0}", account.ServerName)
            : Loc.T("第三方账户"),
        "offline" => Loc.T("离线账户"),
        _ => account.Type,
    };

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string uuid) return;
        _s.Accounts.SetDefault(uuid);
        RefreshList();
    }

    /// <summary>行尾那一列显示的皮肤名（没指定皮肤时为空，整列随之隐藏）</summary>
    private string SkinTextOf(StoredAccount account)
    {
        if (account.SkinId is not { Length: > 0 } skinId) return "";
        return _s.Skins.Find(skinId)?.Name ?? "";
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

    private void ClearBars()
    {
        ErrorBar.IsOpen = false;
        StatusInfo.IsOpen = false;
    }

    private static string MaskKey(string key) =>
        key.Length <= 14 ? key : key[..10] + "…" + key[^4..];
}
