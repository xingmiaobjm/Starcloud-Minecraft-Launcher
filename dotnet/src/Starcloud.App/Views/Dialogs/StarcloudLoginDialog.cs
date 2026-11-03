// Starcloud ID 登录对话框：账户页 / Nova 页的登录引导按钮统一从这里弹。
// ThemedDialog 承载（ContentDialog 系，主题钉定正确）；注册链接跳独立注册小窗。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Dialogs;

public static class StarcloudLoginDialog
{
    /// <summary>弹出登录对话框：ID/密码 + 登录（主按钮）+ 注册链接。成功返回 true（会话已建立）。</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, StarcloudIdService starcloudId)
    {
        var idBox = new TextBox
        {
            Header = "Starcloud ID",
            PlaceholderText = Loc.T("用户名或邮箱"),
        };
        var pwdBox = new PasswordBox { Header = Loc.T("密码"), PlaceholderText = Loc.T("密码") };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(idBox);
        panel.Children.Add(pwdBox);

        var registerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        registerRow.Children.Add(new TextBlock
        {
            Text = Loc.T("没有账号？"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        });
        var registerBtn = new HyperlinkButton
        {
            Content = Loc.T("注册"),
            Padding = new Thickness(4, 0, 4, 0),
        };
        registerRow.Children.Add(registerBtn);
        panel.Children.Add(registerRow);
        panel.Children.Add(error);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("登录 Starcloud ID"),
            Content = panel,
            PrimaryButtonText = Loc.T("登录"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        registerBtn.Click += async (_, _) =>
        {
            var id = await RegisterDialog.ShowAsync(root, starcloudId);
            if (id is null) return;
            idBox.Text = id;
            pwdBox.Password = "";
            pwdBox.Focus(FocusState.Programmatic);
        };

        while (true)
        {
            error.Visibility = Visibility.Collapsed;
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;
            if (string.IsNullOrWhiteSpace(idBox.Text))
            {
                error.Text = Loc.T("请输入 Starcloud ID");
                error.Visibility = Visibility.Visible;
                continue;
            }
            dlg.IsPrimaryButtonEnabled = false;
            try
            {
                await starcloudId.LoginAsync(idBox.Text.Trim(), pwdBox.Password);
                return true;
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
            }
            finally
            {
                dlg.IsPrimaryButtonEnabled = true;
            }
        }
    }
}
