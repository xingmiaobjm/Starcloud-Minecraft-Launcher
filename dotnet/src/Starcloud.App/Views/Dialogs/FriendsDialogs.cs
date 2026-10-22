// 好友相关的三个小窗（按 QQ 的入口组织）：
//   添加好友   —— 左上「+」菜单进入：搜索 Starcloud ID / 昵称并发送申请（顶部 InfoBar 反馈结果）
//   好友申请   —— 只列「别人发来的」申请，可同意 / 拒绝；不提供撤回（申请发出后不可撤回）
//   联机邀请   —— 列表顶部系统会话；接受后关窗并把邀请返回给页面去进房
// 三个窗口都带明确的空态，不再出现一块空白。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Dialogs;

internal static class FriendsDialogs
{
    // 代码里直接查 TextFillColorSecondaryBrush 拿不到主题解析后的颜色（会得到透明占位），
    // 这些自建控件统一用一个中性灰，浅色/深色都看得清。
    private static readonly Brush SecondaryBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E));

    /// <summary>添加好友；返回是否发出过申请。</summary>
    public static async Task<bool> AddFriendAsync(XamlRoot root, FriendsService service)
    {
        var notice = new InfoBar
        {
            IsOpen = false,
            IsClosable = true,
            Severity = InfoBarSeverity.Success,
        };
        var query = new TextBox { PlaceholderText = Loc.T("Starcloud ID 或昵称") };
        var searchBtn = new Button
        {
            Content = Loc.T("搜索"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(14, 6, 14, 6),
        };
        var hint = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var results = new StackPanel { Spacing = 4 };
        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(query);
        Grid.SetColumn(searchBtn, 1);
        searchRow.Children.Add(searchBtn);

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(notice);
        panel.Children.Add(searchRow);
        panel.Children.Add(hint);
        panel.Children.Add(new ScrollViewer
        {
            MinHeight = 140,
            MaxHeight = 360,
            Content = results,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var dlg = new ThemedDialog
        {
            Title = Loc.T("添加好友"),
            Content = panel,
            CloseButtonText = Loc.T("关闭"),
            XamlRoot = root,
        };

        var sent = false;
        void SetHint(string? text)
        {
            hint.Text = text ?? "";
            hint.Foreground = text is null
                ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                : SecondaryBrush;
        }

        void ShowNotice(InfoBarSeverity severity, string title, string message)
        {
            notice.Severity = severity;
            notice.Title = title;
            notice.Message = message;
            notice.IsOpen = true;
        }

        async Task DoSearchAsync()
        {
            var q = query.Text.Trim();
            if (q.Length == 0)
            {
                SetHint(Loc.T("输入对方的 Starcloud ID 或昵称。"));
                return;
            }
            SetHint(Loc.T("搜索中…"));
            results.Children.Clear();
            try
            {
                var users = await service.SearchAsync(q);
                if (users.Count == 0)
                {
                    SetHint(Loc.T("没有找到匹配的用户。"));
                    return;
                }
                SetHint(null);
                foreach (var user in users)
                {
                    results.Children.Add(BuildSearchRow(service, user, () =>
                    {
                        sent = true;
                        ShowNotice(InfoBarSeverity.Success,
                            Loc.T("好友申请已发送"),
                            Loc.T("等待对方同意后即可开始聊天。"));
                    }, ex => ShowNotice(InfoBarSeverity.Error, Loc.T("操作失败"), ex.Message)));
                }
            }
            catch (Exception ex)
            {
                SetHint(ex.Message);
            }
        }

        searchBtn.Click += async (_, _) => await DoSearchAsync();
        query.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) await DoSearchAsync();
        };

        await dlg.ShowAsync();
        return sent;
    }

    private static UIElement BuildSearchRow(
        FriendsService service, FriendSearchResult user, Action onSent, Action<Exception> onError)
    {
        var action = new Button
        {
            Content = Loc.T("添加"),
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var relation = new TextBlock
        {
            Text = user.Relation switch
            {
                "friend" => Loc.T("已是好友"),
                "outgoing" => Loc.T("已申请"),
                "incoming" => Loc.T("待你处理"),
                _ => "",
            },
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SecondaryBrush,
            Visibility = user.Relation == "none" ? Visibility.Collapsed : Visibility.Visible,
        };
        action.Visibility = user.Relation == "none" ? Visibility.Visible : Visibility.Collapsed;

        action.Click += async (_, _) =>
        {
            action.IsEnabled = false;
            try
            {
                await service.SendRequestAsync(user.UserId, null);
                action.Visibility = Visibility.Collapsed;
                relation.Text = Loc.T("已申请");
                relation.Foreground = SecondaryBrush;
                relation.Visibility = Visibility.Visible;
                onSent();
            }
            catch (Exception ex)
            {
                action.IsEnabled = true;
                onError(ex);
            }
        };

        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Avatar(user.DisplayName, user.Id));

        var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = user.DisplayName, FontSize = 13 });
        info.Children.Add(new TextBlock
        {
            Text = user.Id,
            FontSize = 11,
            Foreground = SecondaryBrush,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        Grid.SetColumn(relation, 2);
        grid.Children.Add(relation);
        Grid.SetColumn(action, 3);
        grid.Children.Add(action);

        return grid;
    }

    /// <summary>好友申请：只显示别人发来的，可同意 / 拒绝；好友申请发出后不可撤回。</summary>
    public static async Task<bool> RequestsAsync(XamlRoot root, FriendsService service)
    {
        var list = new StackPanel { Spacing = 6 };
        var empty = EmptyState(
            "\uE716",
            Loc.T("暂时没有新的好友申请"),
            Loc.T("对方通过 Starcloud ID 找到你并发送申请后，会出现在这里。"));
        var body = new Grid { MinWidth = 420, MinHeight = 170 };
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            Content = list,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        body.Children.Add(empty);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("好友申请"),
            Content = body,
            CloseButtonText = Loc.T("关闭"),
            XamlRoot = root,
        };

        var changed = false;
        async Task ReloadAsync()
        {
            list.Children.Clear();
            try
            {
                var reqs = await service.GetRequestsAsync();
                foreach (var r in reqs.Incoming)
                    list.Children.Add(BuildIncomingRow(service, r, () => changed = true, ReloadAsync));
                empty.Visibility = reqs.Incoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                empty.Visibility = Visibility.Collapsed;
                var error = new TextBlock
                {
                    Text = ex.Message,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                };
                list.Children.Add(error);
            }
        }

        await ReloadAsync();
        await dlg.ShowAsync();
        return changed;
    }

    private static UIElement BuildIncomingRow(
        FriendsService service, FriendRequestView request, Action onChanged, Func<Task> reload)
    {
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(Avatar(request.User.DisplayName, request.User.Id));
        var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = request.User.DisplayName, FontSize = 13 });
        info.Children.Add(new TextBlock
        {
            Text = request.Message is { Length: > 0 } m ? m : Loc.T("请求添加你为好友"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryBrush,
        });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        async Task ActAsync(Func<Task> action)
        {
            try
            {
                await action();
                onChanged();
                await reload();
            }
            catch
            {
                // 失败保留列表，用户可再点一次
            }
        }

        var accept = new Button
        {
            Content = Loc.T("同意"),
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        accept.Click += async (_, _) => await ActAsync(() => service.AcceptRequestAsync(request.RequestId));

        var reject = new Button
        {
            Content = Loc.T("拒绝"),
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
        };
        reject.Click += async (_, _) => await ActAsync(() => service.RejectRequestAsync(request.RequestId));

        Grid.SetColumn(accept, 2);
        row.Children.Add(accept);
        Grid.SetColumn(reject, 3);
        row.Children.Add(reject);
        return row;
    }

    /// <summary>联机邀请：接受时关窗并返回该邀请（由页面负责进房），其余动作就地刷新。</summary>
    public static async Task<FriendInvite?> InvitesAsync(XamlRoot root, FriendsService service)
    {
        var list = new StackPanel { Spacing = 6 };
        var empty = EmptyState(
            "\uEB91",
            Loc.T("暂时没有新的联机邀请"),
            Loc.T("好友邀请你联机后，会出现在这里。"));
        var body = new Grid { MinWidth = 440, MinHeight = 170 };
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            Content = list,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        body.Children.Add(empty);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("联机邀请"),
            Content = body,
            CloseButtonText = Loc.T("关闭"),
            XamlRoot = root,
        };

        FriendInvite? accepted = null;

        async Task ReloadAsync()
        {
            list.Children.Clear();
            try
            {
                var invites = await service.GetInvitesAsync();
                foreach (var invite in invites.Incoming)
                {
                    list.Children.Add(BuildInviteRow(service, invite, incoming: true,
                        onAccepted: i => { accepted = i; dlg.Hide(); }, reload: ReloadAsync));
                }
                foreach (var invite in invites.Outgoing)
                {
                    list.Children.Add(BuildInviteRow(service, invite, incoming: false,
                        onAccepted: null, reload: ReloadAsync));
                }
                empty.Visibility = invites.Incoming.Count + invites.Outgoing.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                empty.Visibility = Visibility.Collapsed;
                var error = new TextBlock
                {
                    Text = ex.Message,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                };
                list.Children.Add(error);
            }
        }

        await ReloadAsync();
        await dlg.ShowAsync();
        return accepted;
    }

    private static UIElement BuildInviteRow(
        FriendsService service, FriendInvite invite,
        bool incoming, Action<FriendInvite>? onAccepted, Func<Task> reload)
    {
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(Avatar(invite.User.DisplayName, invite.User.Id));
        var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = incoming
                ? Loc.T("{0} 邀请你联机", invite.User.DisplayName)
                : Loc.T("已邀请 {0}", invite.User.DisplayName),
            FontSize = 13,
        });
        info.Children.Add(new TextBlock
        {
            Text = invite.ConnectionText,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryBrush,
        });
        if (invite.Message is { Length: > 0 } message)
        {
            info.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SecondaryBrush,
            });
        }
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        async Task ActAsync(Func<Task> action)
        {
            try
            {
                await action();
                await reload();
            }
            catch
            {
                // 失败保留列表
            }
        }

        if (incoming)
        {
            var accept = new Button
            {
                Content = Loc.T("接受"),
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            accept.Click += async (_, _) =>
            {
                try
                {
                    await service.AcceptInviteAsync(invite.InviteId);
                    onAccepted?.Invoke(invite);
                }
                catch
                {
                    await reload();
                }
            };

            var decline = new Button
            {
                Content = Loc.T("拒绝"),
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
            };
            decline.Click += async (_, _) =>
                await ActAsync(() => service.DeclineInviteAsync(invite.InviteId));

            Grid.SetColumn(accept, 2);
            row.Children.Add(accept);
            Grid.SetColumn(decline, 3);
            row.Children.Add(decline);
        }
        else
        {
            var cancel = new Button
            {
                Content = Loc.T("撤回"),
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
            };
            cancel.Click += async (_, _) =>
                await ActAsync(() => service.CancelInviteAsync(invite.InviteId));
            Grid.SetColumn(cancel, 3);
            row.Children.Add(cancel);
        }

        return row;
    }

    /// <summary>空态：图标 + 一句说明 + 一句提示，居中摆放（避免出现一块空白窗口）。</summary>
    private static UIElement EmptyState(string glyph, string title, string hint)
    {
        var secondary = SecondaryBrush;
        var panel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 12, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 30,
            Foreground = secondary,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            Foreground = secondary,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 11,
            MaxWidth = 360,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondary,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return panel;
    }

    private static Border Avatar(string displayName, string id)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        var initial = string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
        var border = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };
        border.Child = new TextBlock
        {
            Text = initial,
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return border;
    }
}
