// 好友页：消息（会话 + 聊天）/ 好友申请 / 联机邀请 三个标签。
// 网络层复用 Starcloud.Core 的 FriendsService（与 Win 端同一份）：Bearer api_key 取自
// StarcloudIdService.Current，登录态变化时整体刷新；SSE 长连接后台跑，事件到达即重拉。
// 行内容代码构建（对齐 MultiplayerPage 的做法），联机邀请的房间码预填当前陶瓦房间。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class FriendsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly FriendsService _friends;

    private IReadOnlyList<FriendProfile> _friendList = Array.Empty<FriendProfile>();
    private FriendRequests _requests = new(Array.Empty<FriendRequestView>(), Array.Empty<FriendRequestView>());
    private FriendInvites _invites = new(Array.Empty<FriendInvite>(), Array.Empty<FriendInvite>());
    private FriendProfile? _selected;
    private PublicProfile? _inviteTarget;
    private CancellationTokenSource? _sseCts;

    public FriendsPage()
    {
        InitializeComponent();
        _friends = new FriendsService(() => _s.StarcloudId.Current?.ApiKey);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateIdentity();
        await RefreshAllAsync();
        StartSse();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _sseCts?.Cancel();
        _sseCts = null;
    }

    // ── 数据 ──

    private bool SignedIn => _s.StarcloudId.Current is not null;

    private async Task RefreshAllAsync()
    {
        if (!SignedIn)
        {
            _friendList = Array.Empty<FriendProfile>();
            _requests = new FriendRequests(Array.Empty<FriendRequestView>(), Array.Empty<FriendRequestView>());
            _invites = new FriendInvites(Array.Empty<FriendInvite>(), Array.Empty<FriendInvite>());
            RebuildLeft();
            ShowNotice(InfoBarSeverity.Warning, Loc.T("请先在「账户」页登录 Starcloud ID，好友功能才能使用。"));
            return;
        }
        try
        {
            var friendsTask = _friends.GetFriendsAsync();
            var requestsTask = _friends.GetRequestsAsync();
            var invitesTask = _friends.GetInvitesAsync();
            await Task.WhenAll(friendsTask, requestsTask, invitesTask);
            _friendList = friendsTask.Result;
            _requests = requestsTask.Result;
            _invites = invitesTask.Result;
            RebuildLeft();
            RebuildInvites();
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, Loc.T("好友数据加载失败：{0}", ex.Message));
        }
    }

    /// <summary>SSE：断线指数退避重连；事件到达就整体重拉（数据量小，简单可靠）。</summary>
    private void StartSse()
    {
        _sseCts?.Cancel();
        if (!SignedIn) return;
        var cts = new CancellationTokenSource();
        _sseCts = cts;
        _ = Task.Run(async () =>
        {
            var backoff = TimeSpan.FromSeconds(2);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await _friends.StreamEventsAsync(async _ =>
                    {
                        await DispatcherQueue.EnqueueAsync(RefreshAllAsync);
                    }, cts.Token);
                    backoff = TimeSpan.FromSeconds(2);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    try { await Task.Delay(backoff, cts.Token); } catch { return; }
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
                }
            }
        });
    }

    private void UpdateIdentity()
    {
        var session = _s.StarcloudId.Current;
        MyInitial.Text = string.IsNullOrWhiteSpace(session?.DisplayName)
            ? "?" : session!.DisplayName[..1].ToUpperInvariant();
        MyUidText.Text = session?.UserId ?? Loc.T("未登录");
    }

    // ── 左栏 ──

    private int CurrentTab =>
        TabRequests.IsChecked == true ? 1 : TabInvites.IsChecked == true ? 2 : 0;

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildLeft();
        if (CurrentTab == 2) ShowInvitePanel();
    }

    private void RebuildLeft()
    {
        LeftList.Items.Clear();
        switch (CurrentTab)
        {
            case 1: BuildRequestRows(); break;
            case 2: BuildInviteRows(); break;
            default: BuildFriendRows(); break;
        }
    }

    private void BuildFriendRows()
    {
        if (_friendList.Count == 0)
        {
            LeftList.Items.Add(HintRow(Loc.T("还没有好友，点右上「添加好友」。")));
            return;
        }
        foreach (var f in _friendList)
        {
            var row = Row(f.DisplayName, $"@{f.Id}", f.Unread > 0 ? f.Unread.ToString() : null);
            row.Tag = f;
            LeftList.Items.Add(row);
        }
    }

    private void BuildRequestRows()
    {
        if (_requests.Incoming.Count == 0 && _requests.Outgoing.Count == 0)
        {
            LeftList.Items.Add(HintRow(Loc.T("暂无好友申请。")));
            return;
        }
        foreach (var r in _requests.Incoming)
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(Row(r.User.DisplayName, Loc.T("想加你为好友"), null));
            if (!string.IsNullOrWhiteSpace(r.Message))
                body.Children.Add(Caption(r.Message!, 11));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var accept = SmallButton(Loc.T("接受"), true);
            accept.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.AcceptRequestAsync(r.RequestId), Loc.T("已添加好友"));
                await RefreshAllAsync();
            };
            var reject = SmallButton(Loc.T("拒绝"), false);
            reject.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.RejectRequestAsync(r.RequestId), null);
                await RefreshAllAsync();
            };
            actions.Children.Add(accept);
            actions.Children.Add(reject);
            body.Children.Add(actions);
            LeftList.Items.Add(WrapRow(body, r));
        }
        foreach (var r in _requests.Outgoing)
        {
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(Row(r.User.DisplayName, Loc.T("等待对方接受"), null));
            var withdraw = SmallButton(Loc.T("撤回"), false);
            withdraw.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.CancelRequestAsync(r.RequestId), null);
                await RefreshAllAsync();
            };
            body.Children.Add(withdraw);
            LeftList.Items.Add(WrapRow(body, r));
        }
    }

    private void BuildInviteRows()
    {
        var total = _invites.Incoming.Count + _invites.Outgoing.Count;
        if (total == 0)
        {
            LeftList.Items.Add(HintRow(Loc.T("暂无联机邀请。右栏可发起。")));
            return;
        }
        foreach (var inv in _invites.Incoming)
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(Row(inv.User.DisplayName, Loc.T("邀请你联机 · {0}", ConnectionText(inv)), null));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var accept = SmallButton(Loc.T("接受"), true);
            accept.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.AcceptInviteAsync(inv.InviteId), Loc.T("已接受，可在联机页加入房间"));
                await RefreshAllAsync();
            };
            var decline = SmallButton(Loc.T("拒绝"), false);
            decline.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.DeclineInviteAsync(inv.InviteId), null);
                await RefreshAllAsync();
            };
            actions.Children.Add(accept);
            actions.Children.Add(decline);
            body.Children.Add(actions);
            LeftList.Items.Add(WrapRow(body, inv));
        }
        foreach (var inv in _invites.Outgoing)
        {
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(Row(inv.User.DisplayName, Loc.T("等待接受 · {0}", ConnectionText(inv)), null));
            var cancel = SmallButton(Loc.T("撤回"), false);
            cancel.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.CancelInviteAsync(inv.InviteId), null);
                await RefreshAllAsync();
            };
            body.Children.Add(cancel);
            LeftList.Items.Add(WrapRow(body, inv));
        }
    }

    private async void LeftList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftList.SelectedItem is FrameworkElement { Tag: FriendProfile f })
        {
            _selected = f;
            ShowChat();
            await LoadConversationAsync();
        }
    }

    // ── 右栏：聊天 ──

    private void ShowChat()
    {
        EmptyPanel.Visibility = Visibility.Collapsed;
        InvitePanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        if (_selected is { } f)
        {
            PeerInitial.Text = f.DisplayName[..1].ToUpperInvariant();
            PeerName.Text = f.DisplayName;
            PeerId.Text = "@" + f.Id;
        }
    }

    private async Task LoadConversationAsync()
    {
        if (_selected is not { } f) return;
        try
        {
            var page = await _friends.GetConversationAsync(f.UserId);
            ChatStack.Children.Clear();
            foreach (var m in page.Items) ChatStack.Children.Add(Bubble(m));
            if (page.Items.Count == 0) ChatStack.Children.Add(Caption(Loc.T("还没有消息，打个招呼吧～"), 11));
            await _friends.MarkReadAsync(f.UserId);
            ChatScroll.UpdateLayout();
            ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null);
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, ex.Message);
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async void MessageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        if (_selected is not { } f) return;
        var text = MessageBox.Text.Trim();
        if (text.Length == 0) return;
        MessageBox.Text = "";
        await GuardAsync(() => _friends.SendMessageAsync(f.UserId, text), null);
        await LoadConversationAsync();
    }

    private async void RemoveFriend_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } f) return;
        var ok = await UiDialog.ConfirmAsync(Loc.T("删除好友"),
            Loc.T("确定删除 {0} 吗？", f.DisplayName), Loc.T("删除"));
        if (!ok) return;
        await GuardAsync(() => _friends.RemoveFriendAsync(f.UserId), Loc.T("已删除"));
        _selected = null;
        ChatPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        await RefreshAllAsync();
    }

    // ── 右栏：联机邀请 ──

    private void ShowInvitePanel()
    {
        EmptyPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Collapsed;
        InvitePanel.Visibility = Visibility.Visible;

        // 当前陶瓦房间码：有就预填，说明文字也跟着变
        var room = _s.Multiplayer.Snapshot?.State.Room;
        if (!string.IsNullOrWhiteSpace(room)) InviteRoomBox.Text = room;
        InviteHint.Text = string.IsNullOrWhiteSpace(room)
            ? Loc.T("填房间码或直连地址后发给好友；想用陶瓦房间，先去「联机」页建房。")
            : Loc.T("当前陶瓦房间已填入，发给好友即可加入。");

        RebuildFriendPicker();
    }

    private void RebuildFriendPicker()
    {
        FriendPicker.Children.Clear();
        if (_friendList.Count == 0)
        {
            FriendPicker.Children.Add(Caption(Loc.T("先添加好友。"), 11));
            return;
        }
        foreach (var f in _friendList.Take(6))
        {
            var chip = SmallButton(f.DisplayName, _inviteTarget?.UserId == f.UserId);
            chip.Click += (_, _) =>
            {
                _inviteTarget = new PublicProfile(f.UserId, f.Id, f.DisplayName, f.AvatarUrl);
                RebuildFriendPicker();
            };
            FriendPicker.Children.Add(chip);
        }
    }

    private void RebuildInvites()
    {
        IncomingInvites.Children.Clear();
        OutgoingInvites.Children.Clear();
        foreach (var inv in _invites.Incoming)
        {
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(Row(inv.User.DisplayName, ConnectionText(inv), null));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var accept = SmallButton(Loc.T("接受"), true);
            accept.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.AcceptInviteAsync(inv.InviteId), Loc.T("已接受邀请"));
                await RefreshAllAsync();
            };
            var decline = SmallButton(Loc.T("拒绝"), false);
            decline.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.DeclineInviteAsync(inv.InviteId), null);
                await RefreshAllAsync();
            };
            actions.Children.Add(accept);
            actions.Children.Add(decline);
            body.Children.Add(actions);
            IncomingInvites.Children.Add(WrapRow(body, inv));
        }
        if (IncomingInvites.Children.Count == 0) IncomingInvites.Children.Add(Caption(Loc.T("暂无"), 11));

        foreach (var inv in _invites.Outgoing)
        {
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(Row(inv.User.DisplayName, ConnectionText(inv), null));
            var cancel = SmallButton(Loc.T("撤回"), false);
            cancel.Click += async (_, _) =>
            {
                await GuardAsync(() => _friends.CancelInviteAsync(inv.InviteId), null);
                await RefreshAllAsync();
            };
            body.Children.Add(cancel);
            OutgoingInvites.Children.Add(WrapRow(body, inv));
        }
        if (OutgoingInvites.Children.Count == 0) OutgoingInvites.Children.Add(Caption(Loc.T("暂无"), 11));
    }

    private async void SendInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_inviteTarget is not { } target)
        {
            ShowNotice(InfoBarSeverity.Warning, Loc.T("先在上方选一位好友"));
            return;
        }
        var room = InviteRoomBox.Text.Trim();
        var address = InviteAddressBox.Text.Trim();
        if (room.Length == 0 && address.Length == 0)
        {
            ShowNotice(InfoBarSeverity.Warning, Loc.T("填房间码或直连地址"));
            return;
        }
        string? addr = null;
        int? port = null;
        if (address.Length > 0)
        {
            var parts = address.Split(':', 2);
            addr = parts[0];
            if (parts.Length == 2 && int.TryParse(parts[1], out var p)) port = p;
        }
        var ok = await GuardAsync(
            () => _friends.CreateInviteAsync(target.UserId, addr, port,
                room.Length > 0 ? room : null, null),
            Loc.T("邀请已发送"));
        if (ok) await RefreshAllAsync();
    }

    // ── 添加好友 ──

    private async void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        var input = await UiDialog.PromptAsync(Loc.T("添加好友"),
            Loc.T("输入对方的 Starcloud ID（用户名）"), Loc.T("搜索"),
            placeholder: Loc.T("Starcloud ID"));
        if (string.IsNullOrWhiteSpace(input)) return;
        try
        {
            var results = await _friends.SearchAsync(input.Trim());
            if (results.Count == 0)
            {
                ShowNotice(InfoBarSeverity.Warning, Loc.T("没有找到这个用户"));
                return;
            }
            var hit = results.FirstOrDefault(r => r.Relation != "friend") ?? results[0];
            if (hit.Relation == "friend")
            {
                ShowNotice(InfoBarSeverity.Informational, Loc.T("{0} 已经是你的好友", hit.DisplayName));
                return;
            }
            await _friends.SendRequestAsync(hit.UserId, null);
            ShowNotice(InfoBarSeverity.Success, Loc.T("已向 {0} 发送好友申请", hit.DisplayName));
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, ex.Message);
        }
    }

    // ── 小部件 ──

    private static string ConnectionText(FriendInvite inv) =>
        !string.IsNullOrWhiteSpace(inv.RoomCode) ? inv.RoomCode!
        : inv.Port is { } p ? $"{inv.Address}:{p}" : inv.Address ?? "";

    private static FrameworkElement WrapRow(FrameworkElement content, object tag) =>
        new Border
        {
            Padding = new Thickness(8),
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            Child = content,
            Tag = tag,
        };

    private static FrameworkElement Row(string title, string subtitle, string? badge)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(title) ? "?" : title[..1].ToUpperInvariant(),
                FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(avatar);

        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(Caption(subtitle, 10));
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        if (!string.IsNullOrEmpty(badge))
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 1, 5, 1),
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, FontSize = 9 },
            };
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }
        return grid;
    }

    private static TextBlock Caption(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button SmallButton(string text, bool accent)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(10, 3, 10, 3),
            MinWidth = 0,
        };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        return b;
    }

    private static FrameworkElement HintRow(string text) => new Border
    {
        Padding = new Thickness(8, 10, 8, 10),
        Child = Caption(text, 11),
    };

    private FrameworkElement Bubble(FriendMessage m)
    {
        var mine = m.FromUserId == _s.StarcloudId.Current?.UserId;
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 480,
            Background = mine
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            Child = new TextBlock
            {
                Text = m.Content,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        return bubble;
    }

    /// <summary>统一异常 → InfoBar；成功时可选提示一句</summary>
    private async Task<bool> GuardAsync(Func<Task> action, string? successMessage)
    {
        try
        {
            await action();
            if (!string.IsNullOrEmpty(successMessage))
                ShowNotice(InfoBarSeverity.Success, successMessage!);
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, ex.Message);
            return false;
        }
    }

    private void ShowNotice(InfoBarSeverity severity, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}

/// <summary>DispatcherQueue.EnqueueAsync 便捷封装（避免 UI 线程跨线程直调）。</summary>
internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        queue.TryEnqueue(async () =>
        {
            try { await action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }
}
