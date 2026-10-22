// 邀请好友联机的小窗：选好友 + 填连接信息（房间码 / 地址端口，至少一项）+ 留言。
// 房间码默认带出当前陶瓦房间的邀请码，房主开好房后直接点好友行里的「邀请」即可发出。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Dialogs;

/// <summary>邀请弹窗的填写结果（UserId = 对方 user_id）</summary>
internal sealed record FriendInviteDraft(
    string UserId, string? Address, int? Port, string? RoomCode, string? Message);

internal static class InviteFriendDialog
{
    public static async Task<FriendInviteDraft?> ShowAsync(
        XamlRoot root, IReadOnlyList<FriendProfile> friends,
        string? presetRoomCode = null, string? presetUserId = null)
    {
        if (friends.Count == 0)
        {
            await new ThemedDialog
            {
                Title = Loc.T("邀请好友联机"),
                Content = Loc.T("还没有好友。先在右侧「添加好友」里搜索对方的 Starcloud ID。"),
                CloseButtonText = Loc.T("知道了"),
                XamlRoot = root,
            }.ShowAsync();
            return null;
        }

        var friendBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var f in friends)
            friendBox.Items.Add(new ComboBoxItem
            {
                Content = $"{f.DisplayName}（{f.Id}）",
                Tag = f.UserId,
            });
        var presetIndex = friends.ToList().FindIndex(f => f.UserId == presetUserId);
        friendBox.SelectedIndex = presetIndex >= 0 ? presetIndex : 0;

        var roomBox = new TextBox
        {
            Text = presetRoomCode ?? "",
            PlaceholderText = Loc.T("陶瓦联机邀请码（如 U/XXXX-XXXX-XXXX-XXXX）"),
        };
        var addressBox = new TextBox
        {
            PlaceholderText = Loc.T("直连地址（可选，如 example.com 或 1.2.3.4）"),
        };
        var portBox = new TextBox
        {
            PlaceholderText = Loc.T("端口（可选，如 25565）"),
            MaxLength = 5,
        };
        var messageBox = new TextBox
        {
            PlaceholderText = Loc.T("留言（可选，最多 100 字）"),
            MaxLength = 100,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 64,
        };
        var error = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("房间码与地址端口至少填一项；邀请 10 分钟内有效。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Labeled(Loc.T("好友"), friendBox));
        panel.Children.Add(Labeled(Loc.T("陶瓦邀请码"), roomBox));
        panel.Children.Add(Labeled(Loc.T("直连地址"), addressBox));
        panel.Children.Add(Labeled(Loc.T("端口"), portBox));
        panel.Children.Add(Labeled(Loc.T("留言"), messageBox));
        panel.Children.Add(error);

        while (true)
        {
            var dlg = new ThemedDialog
            {
                Title = Loc.T("邀请好友联机"),
                Content = panel,
                PrimaryButtonText = Loc.T("发送邀请"),
                CloseButtonText = Loc.T("取消"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

            var room = roomBox.Text.Trim();
            var address = addressBox.Text.Trim();
            var portText = portBox.Text.Trim();
            int? port = null;
            if (portText.Length > 0)
            {
                if (!int.TryParse(portText, out var p) || p < 1 || p > 65535)
                {
                    error.Text = Loc.T("端口需为 1-65535 的整数");
                    error.Visibility = Visibility.Visible;
                    continue;
                }
                port = p;
            }
            if (room.Length == 0 && address.Length == 0)
            {
                error.Text = Loc.T("请填写陶瓦邀请码，或直连地址");
                error.Visibility = Visibility.Visible;
                continue;
            }
            if (friendBox.SelectedItem is not ComboBoxItem { Tag: string userId })
            {
                error.Text = Loc.T("请选择要邀请的好友");
                error.Visibility = Visibility.Visible;
                continue;
            }

            return new FriendInviteDraft(
                userId,
                address.Length == 0 ? null : address,
                port,
                room.Length == 0 ? null : room,
                messageBox.Text.Trim().Length == 0 ? null : messageBox.Text.Trim());
        }
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(control);
        return panel;
    }
}
