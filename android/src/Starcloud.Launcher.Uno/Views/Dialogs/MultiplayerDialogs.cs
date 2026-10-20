// 联机相关弹窗：加入房间的邀请码输入。
//
// 为什么从页面里挪出来：邀请码是「别人发给你的、偶尔才用一次」的输入，
// 常驻在操作卡上会让「创建房间 / 加入好友的房间 / 查看日志」这排主路径显得拥挤，
// 而且展开后页面上会多出一个只在进房时才用的输入框。收进弹窗后页面只留按钮，
// 输入这一步按需出现——与正版登录、第三方登录同一种交互形态。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Launcher.Uno.Localization;

namespace Starcloud.Launcher.Uno.Views.Dialogs;

public static class MultiplayerDialogs
{
    /// <summary>
    /// 邀请码输入弹窗：返回规范化后的邀请码；用户取消返回 null。
    /// </summary>
    public static async Task<string?> AskJoinCodeAsync(XamlRoot root)
    {
        var box = new TextBox
        {
            PlaceholderText = Loc.T("粘贴房主分享的邀请码…"),
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 320,
        };
        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("房主把邀请码发给你后，粘贴到这里即可进房。"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(box);
        panel.Children.Add(errorText);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("加入好友的房间"),
            Content = panel,
            PrimaryButtonText = Loc.T("加入房间"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = root,
        };

        // 回车提交走的是 dlg.Hide()（结果不是 Primary），用一个标记把这条路径记住
        var accepted = false;

        bool TryAccept()
        {
            var code = Normalize(box.Text);
            if (code.Length == 0)
            {
                errorText.Text = Loc.T("请先粘贴房主分享的邀请码。");
                errorText.Visibility = Visibility.Visible;
                return false;
            }
            box.Text = code;
            return true;
        }

        box.TextChanged += (_, _) =>
        {
            errorText.Visibility = Visibility.Collapsed;
            dlg.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true; // 别再让回车冒泡成默认按钮的其它行为
            if (!TryAccept()) return;
            accepted = true;
            dlg.Hide();
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            if (!TryAccept()) args.Cancel = true; // 校验不过就不关窗
        };

        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary || accepted ? Normalize(box.Text) : null;
    }

    /// <summary>
    /// 规范化粘贴内容：去掉首尾空白与成对引号；粘贴的是带 room= 的链接/整段文字时取参数值。
    /// 好友常常直接复制一整条消息，这里多兜一层，用户不必自己抠出那串码。
    /// </summary>
    private static string Normalize(string raw)
    {
        var text = raw.Replace("\r", "").Replace("\n", "").Trim().Trim('"', '\'', '「', '」', '“', '”');
        var at = text.IndexOf("room=", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            text = text[(at + "room=".Length)..];
            var amp = text.IndexOf('&');
            if (amp >= 0) text = text[..amp];
        }
        return text.Trim();
    }
}
