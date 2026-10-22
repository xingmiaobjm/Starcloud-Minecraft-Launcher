// Starcloud ID 注册小窗（与「导出整合包」同一种做法：代码里搭表，ThemedDialog 承载）。
//
// 注册分两步，都在这个窗口里完成：
//   1) 填邮箱 → 发验证码（同邮箱 60 秒冷却；倒计时按服务端返回的 retry_after 走）；
//   2) 填 ID / 邮箱 / 验证码 / 密码 → 提交注册，成功后关窗并返回 ID，由调用方回填到登录框。
// 服务端错误体里的 message 已是中文，直接展示；本地只做「填没填全」的前置校验。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Views.Controls;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Dialogs;

internal static class RegisterDialog
{
    /// <summary>弹出注册小窗；成功后返回注册的 Starcloud ID，取消返回 null。</summary>
    public static async Task<string?> ShowAsync(XamlRoot root, StarcloudIdService service)
    {
        var idBox = new TextBox { PlaceholderText = Loc.T("1–64 个字符，不区分大小写"), MaxLength = 64 };
        var emailBox = new TextBox { PlaceholderText = Loc.T("用于接收验证码") };
        var codeBox = new TextBox
        {
            PlaceholderText = Loc.T("6 位数字"),
            MaxLength = 6,
            Width = 150,
        };
        var sendBtn = new GlowButton
        {
            Content = Loc.T("获取验证码"),
            MinWidth = 116,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var pwdBox = new PasswordBox { PlaceholderText = Loc.T("至少 6 位") };
        var pwd2Box = new PasswordBox { PlaceholderText = Loc.T("再次输入密码") };

        // 验证码只收数字：粘贴带空格 / 横线的验证码时顺手清掉
        codeBox.TextChanged += (_, _) =>
        {
            var digits = new string(codeBox.Text.Where(char.IsAsciiDigit).ToArray());
            if (digits == codeBox.Text) return;
            codeBox.Text = digits;
            codeBox.SelectionStart = digits.Length;
        };

        var errorText = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        var hintText = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        var progress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void ShowError(string message)
        {
            errorText.Text = message;
            errorText.Visibility = Visibility.Visible;
            hintText.Visibility = Visibility.Collapsed;
        }

        void ShowHint(string message)
        {
            hintText.Text = message;
            hintText.Visibility = Visibility.Visible;
            errorText.Visibility = Visibility.Collapsed;
        }

        // ── 发验证码：60 秒（或服务端给的秒数）倒计时，倒计时内按钮不可点 ──
        var timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        var remaining = 0;
        void SyncSendButton()
        {
            sendBtn.IsEnabled = remaining <= 0;
            sendBtn.Content = remaining > 0
                ? Loc.T("重新发送（{0}s）", remaining)
                : Loc.T("获取验证码");
        }

        if (timer is not null)
        {
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                if (remaining > 0) remaining--;
                SyncSendButton();
                if (remaining <= 0) timer.Stop();
            };
        }

        var sending = false;
        sendBtn.Click += async (_, _) =>
        {
            if (sending || remaining > 0) return;
            var email = emailBox.Text.Trim();
            if (!LooksLikeEmail(email))
            {
                ShowError(Loc.T("请输入有效的邮箱地址"));
                emailBox.Focus(FocusState.Programmatic);
                return;
            }

            sending = true;
            sendBtn.IsEnabled = false;
            try
            {
                var cooldown = await service.SendEmailCodeAsync(email);
                remaining = Math.Max(cooldown, 1);
                SyncSendButton();
                if (timer is not null && !timer.IsRunning) timer.Start();
                ShowHint(Loc.T("验证码已发送到 {0}，10 分钟内有效。", email));
                codeBox.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                sending = false;
                if (remaining <= 0) sendBtn.IsEnabled = true;
            }
        };

        // ── 组窗 ──
        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        codeRow.Children.Add(codeBox);
        codeRow.Children.Add(sendBtn);

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("注册后可使用联机、云 AI 助手等在线功能。验证码会发送到你的邮箱。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Labeled(Loc.T("Starcloud ID"), idBox));
        panel.Children.Add(Labeled(Loc.T("邮箱"), emailBox));
        panel.Children.Add(Labeled(Loc.T("验证码"), codeRow));
        panel.Children.Add(Labeled(Loc.T("密码"), pwdBox));
        panel.Children.Add(Labeled(Loc.T("确认密码"), pwd2Box));
        var messageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        messageRow.Children.Add(progress);
        messageRow.Children.Add(errorText);
        messageRow.Children.Add(hintText);
        panel.Children.Add(messageRow);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("注册 Starcloud ID"),
            Content = panel,
            PrimaryButtonText = Loc.T("注册"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        string? registeredId = null;
        var closed = false;
        dlg.Closed += (_, _) => { closed = true; timer?.Stop(); };

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            // 先在回调里拦下关闭，提交成功后再自己 Hide
            args.Cancel = true;
            errorText.Visibility = Visibility.Collapsed;

            var id = idBox.Text.Trim();
            var email = emailBox.Text.Trim();
            var code = codeBox.Text.Trim();
            var password = pwdBox.Password;

            string? localError = null;
            if (id.Length == 0) localError = Loc.T("Starcloud ID 不能为空");
            else if (id.Length > 64) localError = Loc.T("Starcloud ID 不能超过 64 个字符");
            else if (!LooksLikeEmail(email)) localError = Loc.T("请输入有效的邮箱地址");
            else if (code.Length != 6 || !code.All(char.IsAsciiDigit))
                localError = Loc.T("验证码为 6 位数字");
            else if (password.Length < 6) localError = Loc.T("密码至少 6 位");
            else if (password != pwd2Box.Password) localError = Loc.T("两次输入的密码不一致");
            if (localError is not null)
            {
                ShowError(localError);
                return;
            }

            dlg.IsPrimaryButtonEnabled = false;
            sendBtn.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            progress.IsActive = true;
            hintText.Visibility = Visibility.Collapsed;
            try
            {
                await service.RegisterAsync(id, password, email, code);
                pwdBox.Password = "";
                pwd2Box.Password = "";
                // 请求期间用户可能已经关掉了窗口，这时别再碰它
                if (!closed)
                {
                    registeredId = id;
                    dlg.Hide();
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                progress.IsActive = false;
                progress.Visibility = Visibility.Collapsed;
                dlg.IsPrimaryButtonEnabled = true;
                SyncSendButton();
            }
        };

        await dlg.ShowAsync();
        return registeredId;
    }

    /// <summary>与 StarcloudIdService 同口径的粗校验：只挡明显不像邮箱的输入。</summary>
    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;
        if (email.IndexOf('@', at + 1) >= 0) return false;
        var dot = email.IndexOf('.', at + 1);
        return dot > at + 1 && dot < email.Length - 1;
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(control);
        return panel;
    }
}
