// 添加游戏账户（二级页）：左边选身份，右边就地填表。
// 三条身份路径互不替代：微软与第三方是在线身份（走各自会话服务器），离线身份不参与任何在线验证。
// 登录/添加成功后不弹任何提示框，而是回到账户列表并把结果交给它显示（AccountsPage.PendingStatus）。
//
// 移植自桌面 Starcloud.App.Views.AccountAddPage.xaml.cs，适配点：
// - 壳导航无 Frame 回退栈：返回统一走 AppNavigation.Navigate("accounts")；
// - 弹窗宿主 Content.XamlRoot → Services/Ui.Root（MicrosoftDeviceCodeAsync 的入参）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Dialogs;
using Starcloud.Launcher.Phone.Views.Shared;

namespace Starcloud.Launcher.Phone.Views;

public sealed partial class AccountAddPage : Page
{
    private readonly AppServices _s = AppServices.Instance;

    public AccountAddPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshOfflineTip();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MsPanel is null) return; // XAML 初始化期间触发
        Show(kind: (KindList.SelectedItem as ListViewItem)?.Tag as string ?? "microsoft");
    }

    private void Show(string kind)
    {
        MsPanel.Visibility = kind == "microsoft" ? Visibility.Visible : Visibility.Collapsed;
        ExternalPanel.Visibility = kind == "external" ? Visibility.Visible : Visibility.Collapsed;
        OfflinePanel.Visibility = kind == "offline" ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.IsOpen = false;
    }

    // ── 微软正版 ──

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        MsLoginBtn.IsEnabled = false;
        MsProgress.Visibility = Visibility.Visible;
        MsProgress.IsActive = true;
        MsStatus.Text = Loc.T("请在登录窗口中完成验证…");
        ErrorBar.IsOpen = false;
        try
        {
            var result = await AuthDialogs.MicrosoftDeviceCodeAsync(
                Ui.Root!, _s.MicrosoftAuth, _s.Accounts, ShowError);
            if (result is null)
            {
                MsStatus.Text = ""; // 取消 / 失败：错误条已给出原因
                return;
            }
            _s.NovaRuntime.NotifyLoginChanged();
            Finish(Loc.T("Microsoft 账户已登录：{0}", result.Username));
        }
        finally
        {
            MsLoginBtn.IsEnabled = true;
            MsProgress.Visibility = Visibility.Collapsed;
            MsProgress.IsActive = false;
        }
    }

    // ── 第三方（外置登录） ──

    private async void ExtPwdBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await DoExternalLoginAsync();
    }

    private async void ExtLogin_Click(object sender, RoutedEventArgs e) => await DoExternalLoginAsync();

    private async Task DoExternalLoginAsync()
    {
        ErrorBar.IsOpen = false;
        var url = ExtUrlBox.Text.Trim();
        var name = ExtNameBox.Text.Trim();
        var password = ExtPwdBox.Password;
        if (url.Length == 0 || name.Length == 0 || password.Length == 0)
        {
            ShowError(Loc.T("请填写认证服务器地址、用户名和密码"));
            return;
        }

        ExtLoginBtn.IsEnabled = false;
        ExtProgress.Visibility = Visibility.Visible;
        ExtProgress.IsActive = true;
        try
        {
            ExtStatus.Text = Loc.T("正在登录…");
            var player = await AccountFlows.LoginExternalAsync(_s, url, name, password);
            ExtPwdBox.Password = "";
            _s.NovaRuntime.NotifyLoginChanged();
            Finish(Loc.T("第三方登录 {0} 已添加", player));
        }
        catch (Exception ex)
        {
            ExtStatus.Text = "";
            ShowError(Loc.T("登录失败：{0}", ex.Message));
        }
        finally
        {
            ExtLoginBtn.IsEnabled = true;
            ExtProgress.Visibility = Visibility.Collapsed;
            ExtProgress.IsActive = false;
        }
    }

    // ── 离线 ──

    private void OfflineName_TextChanged(object sender, TextChangedEventArgs e) => RefreshOfflineTip();

    private void OfflineName_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) DoAddOffline();
    }

    private void OfflineAdd_Click(object sender, RoutedEventArgs e) => DoAddOffline();

    private void DoAddOffline()
    {
        ErrorBar.IsOpen = false;
        var name = OfflineNameBox.Text.Trim();
        if (!AccountStore.IsValidOfflineName(name))
        {
            ShowError(Loc.T("用户名不合法：需为 3-16 位字母、数字或下划线"));
            return;
        }
        try
        {
            var account = _s.Accounts.AddOrUpdateOffline(name);
            OfflineNameBox.Text = "";
            Finish(Loc.T("离线账户 {0} 已添加", account.Username));
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("添加失败：{0}", ex.Message));
        }
    }

    /// <summary>玩家名实时提示：合法时直接算出离线 UUID，让用户看清自己的身份从哪来。</summary>
    private void RefreshOfflineTip()
    {
        if (OfflineTip is null) return;
        var name = OfflineNameBox.Text.Trim();
        OfflineTip.Text = AccountStore.IsValidOfflineName(name)
            ? Loc.T("离线 UUID：{0}（各启动器通用算法，本地存档与局域网身份保持一致）",
                AccountStore.OfflineUuid(name))
            : Loc.T("离线账户无需登录，可用于单人游戏、局域网联机与允许离线登录的服务器。");
    }

    // ── 收尾 ──

    /// <summary>成功：把结果交接给账户列表显示，然后返回。不在这里弹提示框。</summary>
    private void Finish(string status)
    {
        AccountsPage.PendingStatus = status;
        GoBack();
    }

    /// <summary>返回账户列表：安卓壳没有 Frame 回退栈，统一走路由（添加账户页只会从账户列表进来）。</summary>
    private void GoBack() => AppNavigation.Navigate("accounts");

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
