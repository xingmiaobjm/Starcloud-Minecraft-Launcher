// 账户页（一级）：Starcloud ID 卡（登录 / 注册 / 会话管理都在这张卡里）+ 游戏账户列表。
//
// 账户列表照 HMCL 来：一行一个账户（头像 / 名字 / 类型 / 正在用的皮肤 / 进详情），
// 首选账户排在最前，其余操作都在二级页里：
//   点一行 → AccountDetailPage（形象与皮肤 / 账户信息）
//   右上「添加账户」→ AccountAddPage（左选身份、右填表单）
// 一级页本身不再弹任何对话框，只保留「退出登录」这种需要二次确认的动作。
//
// 移植自桌面 Starcloud.App.Views.AccountsPage.xaml.cs，适配点：
// - 壳导航无 Frame 回退栈：进二级页/返回都走 AppNavigation 路由；
// - 桌面在 Loaded 里刷新，安卓壳的显示时机回调是 IShellPage.OnShown，逻辑搬进去；
// - 弹窗宿主 Content.XamlRoot → Services/Ui.Root。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Dialogs;
using Starcloud.Launcher.Phone.Views.Rows;
using Starcloud.Launcher.Phone.Views.Shared;

namespace Starcloud.Launcher.Phone.Views;

public sealed partial class AccountsPage : Page, IShellPage
{
    private readonly AppServices _s = AppServices.Instance;

    /// <summary>
    /// 二级页（添加账户 / 账户详情）做完事回到本页时留下的结果，由显示时取走显示一次。
    /// 跨实例交接点：二级页写、本页 OnShown 读。
    /// </summary>
    internal static string? PendingStatus;

    public AccountsPage()
    {
        InitializeComponent();
    }

    /// <summary>壳导航显示时机（对齐桌面 Loaded 刷新时机）。</summary>
    public void OnShown()
    {
        RefreshStarcloudState();
        RefreshList();
        if (PendingStatus is { Length: > 0 } pending)
        {
            PendingStatus = null;
            ShowStatus(pending);
        }
    }

    // ── Starcloud ID：标题 + 登录 / 注册 / 会话管理 ──

    private void RefreshStarcloudState()
    {
        if (_s.StarcloudId.Current is not { } session)
        {
            ScLoginPanel.Visibility = ScRegisterPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            ScUserPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ScLoginPanel.Visibility = Visibility.Collapsed;
        ScRegisterPanel.Visibility = Visibility.Collapsed;
        ScUserPanel.Visibility = Visibility.Visible;
        ScAvatarText.Text = string.IsNullOrWhiteSpace(session.DisplayName)
            ? "?" : session.DisplayName[..1].ToUpperInvariant();
        ScNameText.Text = session.DisplayName;
        ScUserSubText.Text = Loc.T("{0} · {1}", session.UserId,
            session.Channel == StarcloudIdService.CloudChannel ? Loc.T("云端通道") : Loc.T("测试通道"));
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

    private async void ScLogout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ThemedDialog
        {
            Title = Loc.T("退出登录"),
            Content = Loc.T("退出后本地登录状态将被清除，确定退出吗？"),
            PrimaryButtonText = Loc.T("退出"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.StarcloudId.Logout();
        RefreshStarcloudState();
        _s.NovaRuntime.NotifyLoginChanged(); // 退出后 Orb 面板回到未登录引导
    }

    // ── 游戏账户：二级页入口 ──

    private void AddAccount_Click(object sender, RoutedEventArgs e) =>
        AppNavigation.Navigate("accountAdd");

    private void AccountList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AccountRow row) OpenDetail(row.Uuid);
    }

    /// <summary>进入账户详情页：参数走静态交接（对齐 VersionsPage → VersionDetailPage 的范式）。</summary>
    private void OpenDetail(string uuid)
    {
        AccountDetailPage.PendingUuid = uuid;
        AppNavigation.Navigate("accountDetail");
    }

    // ── 已保存账户列表 ──

    /// <summary>上一次账户列表签名：内容没变就不重建列表 / 不重演错峰（切页闪烁来源之一）。</summary>
    private string? _appliedListSig;

    private void RefreshList()
    {
        // 首选账户排在最前：一级页只回答"用哪个账户启动"，它得在第一眼看得到的地方
        var rows = _s.Accounts.Accounts
            .OrderByDescending(a => a.IsDefault)
            .Select(a => new AccountRow(a.Uuid, a.Username, TypeLabel(a), a.IsDefault,
                AvatarHelper.Resolve(a.Type, a.Uuid, a.Username, a.ServerBase,
                    AvatarHelper.LocalSkinPath(a)),
                SkinTextOf(a), DefaultActionTextOf(a)))
            .ToList();
        // 数据没变就不再重建 ItemsSource / 重演错峰：切页回来时列表原样呈现，零开销
        var sig = string.Join("|", rows.Select(r => $"{r.Uuid}:{r.Username}:{r.IsDefault}"));
        var empty = rows.Count == 0;
        if (sig != _appliedListSig || AccountList.ItemsSource is null)
        {
            _appliedListSig = sig;
            AccountList.ItemsSource = rows;
            MotionFx.StaggerListItems(AccountList);
        }
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

    /// <summary>「设为…」按钮的措辞随账户类型走：首选是按类型各记一个的</summary>
    private static string DefaultActionTextOf(StoredAccount account) => account.Type switch
    {
        "microsoft" => Loc.T("设为首选微软账户"),
        "external" => Loc.T("设为首选第三方账户"),
        "offline" => Loc.T("设为首选离线账户"),
        _ => Loc.T("设为首选账户"),
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
}
