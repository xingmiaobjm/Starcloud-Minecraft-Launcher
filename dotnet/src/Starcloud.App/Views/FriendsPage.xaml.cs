// 好友页：严格按 QQ 的两栏交互 —— 左「会话列表」、右「聊天」。
//   · 好友申请 / 联机邀请是列表顶部的系统会话，点击弹对应小窗
//   · 添加好友走左上「+」菜单；邀请联机 / 删除好友在聊天头「⋯」菜单里
//   · 消息按 QQ 习惯：自己的靠右、对方的靠左，头像在气泡外侧，超过 5 分钟插一条居中时间
// 数据来自 FriendsService（/v1/friends/*）；SSE 到达时增量更新消息与会话预览。

using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Dialogs;
using Starcloud.Core.Services;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace Starcloud.App.Views;

public sealed partial class FriendsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private DispatcherQueue? _dq;
    private bool _refreshing;
    private bool _refreshQueued;
    private bool _suppressSelection;

    private IReadOnlyList<FriendProfile> _friends = [];
    private readonly Dictionary<string, FriendMessage?> _lastMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unread = new(StringComparer.Ordinal);
    private List<FriendMessage> _messages = [];
    private List<ConversationRowModel> _allRows = [];
    private string? _selectedUserId;
    private FriendProfile? _selectedFriend;
    private int _requestCount;
    private int _inviteCount;

    // 系统会话图标与空会话水印都用 SVG 资源（跟随输出目录发布）
    private static readonly ImageSource RequestIcon =
        new SvgImageSource(new Uri("ms-appx:///Assets/Brand/friends-request.svg"));
    private static readonly ImageSource InviteIcon =
        new SvgImageSource(new Uri("ms-appx:///Assets/Brand/friends-invite.svg"));
    private bool _hasMoreHistory;
    private long? _nextBeforeId;

    public FriendsPage()
    {
        InitializeComponent();
        SectionSelector.SelectionChanged += SectionSelector_SelectionChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        ApplyTabText();
        Loc.LanguageChanged += ApplyTabText;
        _s.FriendsRuntime.EventReceived += OnFriendEvent;
        _s.StarcloudId.SessionChanged += OnSessionChanged;
        UpdateChatHeader(); // 没选会话时也要有占位标题（否则头部只剩头像和「⋯」）
        _ = RefreshAllAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loc.LanguageChanged -= ApplyTabText;
        _s.FriendsRuntime.EventReceived -= OnFriendEvent;
        _s.StarcloudId.SessionChanged -= OnSessionChanged;
    }

    // ── 板块切换：好友 / 联机（联机页整页嵌进来，不再单独占一项导航） ──

    private void ApplyTabText()
    {
        FriendsTab.Text = Loc.T("好友");
        MultiplayerTab.Text = Loc.T("联机");
    }

    private void SectionSelector_SelectionChanged(
        SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var showMultiplayer = ReferenceEquals(sender.SelectedItem, MultiplayerTab);
        ChatRoot.Visibility = showMultiplayer ? Visibility.Collapsed : Visibility.Visible;
        MultiplayerSection.Visibility = showMultiplayer ? Visibility.Visible : Visibility.Collapsed;
        if (showMultiplayer)
        {
            SignedOutCard.Visibility = Visibility.Collapsed;
            if (MultiplayerHost.Content is null)
                MultiplayerHost.Navigate(typeof(MultiplayerPage));
        }
        else if (!_s.StarcloudId.IsLoggedIn)
        {
            SignedOutCard.Visibility = Visibility.Visible;
        }
    }

    private void ShowMultiplayerTab() => SectionSelector.SelectedItem = MultiplayerTab;

    // ── 刷新 ──

    private async Task RefreshAllAsync()
    {
        if (!_s.StarcloudId.IsLoggedIn)
        {
            SignedOutCard.Visibility = Visibility.Visible;
            ChatRoot.Visibility = Visibility.Collapsed;
            return;
        }
        SignedOutCard.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(SectionSelector.SelectedItem, MultiplayerTab))
            ChatRoot.Visibility = Visibility.Visible;

        if (_refreshing)
        {
            _refreshQueued = true;
            return;
        }
        _refreshing = true;
        try
        {
            await RefreshFriendsAsync();
            await RefreshCountsAsync();
        }
        finally
        {
            _refreshing = false;
            if (_refreshQueued)
            {
                _refreshQueued = false;
                _ = RefreshAllAsync();
            }
        }
    }

    private async Task RefreshFriendsAsync()
    {
        try
        {
            _friends = await _s.Friends.GetFriendsAsync();
        }
        catch (Exception ex)
        {
            _friends = [];
            ShowError(ex.Message);
        }

        foreach (var friend in _friends) _unread[friend.UserId] = friend.Unread;
        await EnsurePreviewsAsync();
        RenderConversations();
    }

    /// <summary>只取数量：系统会话的徽标用（大头像列表由小窗自己拉）。</summary>
    private async Task RefreshCountsAsync()
    {
        try
        {
            var requests = await _s.Friends.GetRequestsAsync();
            // 好友申请系统会话只统计别人发来的（发出的申请不在页面展示，也不可撤回）
            _requestCount = requests.Incoming.Count;
        }
        catch
        {
            _requestCount = 0;
        }
        try
        {
            var invites = await _s.Friends.GetInvitesAsync();
            _inviteCount = invites.Incoming.Count + invites.Outgoing.Count;
        }
        catch
        {
            _inviteCount = 0;
        }
        RenderConversations();
    }

    /// <summary>好友列表不返回最后一条消息：按需补拉（每人 limit=1），拉过就缓存。</summary>
    private async Task EnsurePreviewsAsync()
    {
        var ids = _friends.Select(f => f.UserId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _lastMessages.Keys.Where(k => !ids.Contains(k)).ToList())
            _lastMessages.Remove(stale);

        var missing = _friends.Where(f => !_lastMessages.ContainsKey(f.UserId)).ToList();
        if (missing.Count == 0) return;

        using var gate = new SemaphoreSlim(4);
        var tasks = missing.Select(async friend =>
        {
            await gate.WaitAsync();
            try
            {
                var page = await _s.Friends.GetConversationAsync(friend.UserId, limit: 1);
                _lastMessages[friend.UserId] = page.Items.LastOrDefault();
            }
            catch
            {
                _lastMessages[friend.UserId] = null; // 一条预览失败不拖住整页
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private void RenderConversations()
    {
        _allRows = new List<ConversationRowModel>
        {
            new("requests", "", Loc.T("好友申请"), "申",
                _requestCount > 0 ? Loc.T("{0} 条待处理", _requestCount) : Loc.T("没有新的申请"),
                "", "", Visibility.Collapsed, DateTime.MaxValue)
            {
                IconSource = RequestIcon,
            },
            new("invites", "", Loc.T("联机邀请"), "邀",
                _inviteCount > 0 ? Loc.T("{0} 个待处理", _inviteCount) : Loc.T("没有新的邀请"),
                "",
                _inviteCount > 99 ? "99+" : _inviteCount.ToString(),
                _inviteCount > 0 ? Visibility.Visible : Visibility.Collapsed, DateTime.MaxValue)
            {
                IconSource = InviteIcon,
            },
        };

        var friends = _friends.Select(f =>
        {
            var last = _lastMessages.GetValueOrDefault(f.UserId);
            var unread = Math.Max(_unread.GetValueOrDefault(f.UserId), 0);
            return new ConversationRowModel(
                "friend",
                f.UserId,
                f.DisplayName,
                InitialOf(f.DisplayName, f.Id),
                PreviewOf(last),
                last is null ? "" : FormatStamp(last.CreatedAt),
                unread > 99 ? "99+" : unread.ToString(),
                unread > 0 ? Visibility.Visible : Visibility.Collapsed,
                ParseTime(last?.CreatedAt) ?? DateTime.MinValue)
            {
                Id = f.Id,
            };
        })
        .OrderByDescending(r => r.SortTime)
        .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);

        _allRows.AddRange(friends);
        ApplyFilter();
        FriendsEmpty.Visibility = _friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FriendCountText.Text = _friends.Count == 0 ? "" : Loc.T("{0} 位好友", _friends.Count);
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var rows = filter.Length == 0
            ? _allRows
            : _allRows.Where(r =>
                r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                r.Id.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        _suppressSelection = true;
        ConversationList.ItemsSource = rows;
        if (_selectedUserId is { Length: > 0 } selected)
            ConversationList.SelectedItem = rows.FirstOrDefault(r => r.Kind == "friend" && r.UserId == selected);
        _suppressSelection = false;
    }

    // ── 会话与聊天 ──

    private async void Conversation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (ConversationList.SelectedItem is not ConversationRowModel row) return;

        if (row.Kind != "friend")
        {
            RestoreFriendSelection();
            if (row.Kind == "requests") await OpenRequestsDialogAsync();
            else await OpenInvitesDialogAsync();
            return;
        }

        await OpenConversationAsync(row.UserId);
    }

    private void RestoreFriendSelection()
    {
        _suppressSelection = true;
        ConversationList.SelectedItem = _selectedUserId is { Length: > 0 } selected
            ? _allRows.FirstOrDefault(r => r.Kind == "friend" && r.UserId == selected)
            : null;
        _suppressSelection = false;
    }

    private async Task OpenConversationAsync(string userId)
    {
        _selectedUserId = userId;
        _selectedFriend = _friends.FirstOrDefault(f => f.UserId == userId);
        UpdateChatHeader();

        // 先清掉上一位好友的消息，避免请求期间串台
        _messages = [];
        RenderMessages(scrollToEnd: false);

        try
        {
            var page = await _s.Friends.GetConversationAsync(userId, limit: 100);
            if (_selectedUserId != userId) return; // 期间切到了别的会话
            _messages = page.Items.ToList();
            _hasMoreHistory = page.HasMore;
            _nextBeforeId = page.NextBeforeId;
            LoadOlderItem.IsEnabled = _hasMoreHistory;
            if (_messages.Count > 0) _lastMessages[userId] = _messages[^1];
            RenderMessages(scrollToEnd: true);

            await _s.Friends.MarkReadAsync(userId);
            _unread[userId] = 0;
            RenderConversations();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void UpdateChatHeader()
    {
        var hasFriend = _selectedFriend is not null;
        ChatAvatarText.Text = hasFriend ? InitialOf(_selectedFriend!.DisplayName, _selectedFriend.Id) : "?";
        ChatNameText.Text = hasFriend ? _selectedFriend!.DisplayName : Loc.T("选择一个好友");
        ChatIdText.Text = hasFriend ? _selectedFriend!.Id : "";
        InviteSelectedItem.IsEnabled = hasFriend;
        RemoveFriendItem.IsEnabled = hasFriend;
        ChatHeader.Visibility = hasFriend ? Visibility.Visible : Visibility.Collapsed;
        Composer.Visibility = hasFriend ? Visibility.Visible : Visibility.Collapsed;
        ChatEmpty.Visibility = hasFriend ? Visibility.Collapsed : Visibility.Visible;
        MessageList.Visibility = hasFriend ? Visibility.Visible : Visibility.Collapsed;

        if (!hasFriend)
        {
            _messages = [];
            _hasMoreHistory = false;
            _nextBeforeId = null;
            LoadOlderItem.IsEnabled = false;
            RenderMessages(scrollToEnd: false);
        }
    }

    private async void LoadOlder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUserId is not { Length: > 0 } userId || _nextBeforeId is not { } before) return;
        try
        {
            var page = await _s.Friends.GetConversationAsync(userId, beforeId: before, limit: 100);
            var existing = _messages.Select(m => m.Id).ToHashSet();
            var older = page.Items.Where(m => existing.Add(m.Id)).ToList();
            _messages.InsertRange(0, older);
            _hasMoreHistory = page.HasMore;
            _nextBeforeId = page.NextBeforeId;
            LoadOlderItem.IsEnabled = _hasMoreHistory;
            RenderMessages(scrollToEnd: false);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RenderMessages(bool scrollToEnd)
    {
        var me = _s.StarcloudId.Current;
        var meId = me?.UserId ?? "";
        var myInitial = InitialOf(me?.DisplayName ?? "", meId);
        var friendInitial = _selectedFriend is null
            ? "?" : InitialOf(_selectedFriend.DisplayName, _selectedFriend.Id);

        var rows = new List<ChatMessageRowModel>();
        DateTime? previous = null;
        foreach (var message in _messages)
        {
            var time = ParseTime(message.CreatedAt);
            // QQ 习惯：相邻消息超过 5 分钟才插一条居中时间
            if (time is { } current && (previous is null || current - previous > TimeSpan.FromMinutes(5)))
                rows.Add(new ChatMessageRowModel(true, "", FormatSeparator(current), "", true));
            if (time is { } stamp) previous = stamp;

            var mine = message.FromUserId == meId;
            rows.Add(new ChatMessageRowModel(
                mine, message.Content, "", mine ? myInitial : friendInitial, false));
        }

        MessageList.ItemsSource = rows;
        if (scrollToEnd && rows.Count > 0)
        {
            var last = rows[^1];
            _dq?.TryEnqueue(() => MessageList.ScrollIntoView(last));
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || IsShiftDown()) return;
        e.Handled = true;
        await SendMessageAsync();
    }

    private bool _sending;

    private async Task SendMessageAsync()
    {
        if (_sending) return;
        if (_selectedUserId is not { Length: > 0 } userId) return;
        var text = MessageInput.Text.Trim();
        if (text.Length == 0) return;

        _sending = true;
        SendBtn.IsEnabled = false;
        try
        {
            var message = await _s.Friends.SendMessageAsync(userId, text);
            MessageInput.Text = "";
            _messages.Add(message);
            _lastMessages[userId] = message;
            RenderMessages(scrollToEnd: true);
            RenderConversations();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _sending = false;
            SendBtn.IsEnabled = true;
            MessageInput.Focus(FocusState.Programmatic);
        }
    }

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ── 「+」菜单：添加好友 / 好友申请 / 联机邀请 ──

    private async void AddFriendMenu_Click(object sender, RoutedEventArgs e)
    {
        if (await FriendsDialogs.AddFriendAsync(Content.XamlRoot, _s.Friends))
            await RefreshCountsAsync();
    }

    private async Task OpenRequestsDialogAsync()
    {
        if (await FriendsDialogs.RequestsAsync(Content.XamlRoot, _s.Friends))
        {
            await RefreshFriendsAsync();
            await RefreshCountsAsync();
        }
    }

    private async Task OpenInvitesDialogAsync()
    {
        var accepted = await FriendsDialogs.InvitesAsync(Content.XamlRoot, _s.Friends);
        await RefreshCountsAsync();
        if (accepted is not null) await JoinAcceptedInviteAsync(accepted);
    }

    private async void RequestsMenu_Click(object sender, RoutedEventArgs e) => await OpenRequestsDialogAsync();

    private async void InvitesMenu_Click(object sender, RoutedEventArgs e) => await OpenInvitesDialogAsync();

    // ── 聊天头「⋯」菜单 ──

    private async void InviteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUserId is not { Length: > 0 } userId) return;
        if (_friends.Count == 0) await RefreshFriendsAsync();
        await _s.Multiplayer.RefreshAsync(); // 拿最新的陶瓦邀请码（不必先去联机页）
        var room = _s.Multiplayer.Snapshot?.State.Room;
        var draft = await InviteFriendDialog.ShowAsync(Content.XamlRoot, _friends, room, userId);
        if (draft is null) return;

        try
        {
            await _s.Friends.CreateInviteAsync(
                draft.UserId, draft.Address, draft.Port, draft.RoomCode, draft.Message);
            ShowStatus(Loc.T("联机邀请已发送，对方接受后即可加入。"));
            await RefreshCountsAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void RemoveFriend_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUserId is not { Length: > 0 } userId) return;
        var name = _selectedFriend?.DisplayName ?? userId;
        var dlg = new ThemedDialog
        {
            Title = Loc.T("删除好友"),
            Content = Loc.T("确定删除好友「{0}」吗？聊天与邀请记录不会自动清理。", name),
            PrimaryButtonText = Loc.T("删除"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await _s.Friends.RemoveFriendAsync(userId);
            ShowStatus(Loc.T("已删除好友「{0}」", name));
            _selectedUserId = null;
            _selectedFriend = null;
            UpdateChatHeader();
            await RefreshFriendsAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task JoinAcceptedInviteAsync(FriendInvite invite)
    {
        if (invite.RoomCode is { Length: > 0 } room)
        {
            try
            {
                await _s.Multiplayer.JoinAsync(room, CancellationToken.None);
                ShowMultiplayerTab();
                ShowStatus(Loc.T("正在连接房间…进入游戏后到「多人游戏」即可看到对方世界。"));
            }
            catch (Exception ex)
            {
                ShowError(Loc.T("已接受邀请，但自动进房失败：{0}。可到联机页手动粘贴邀请码。", ex.Message));
            }
        }
        else
        {
            Copy(invite.ConnectionText);
            ShowStatus(Loc.T("已接受邀请。地址 {0} 已复制，进游戏「多人游戏 → 直接连接」粘贴即可。",
                invite.ConnectionText));
        }
    }

    // ── SSE 事件 ──

    private void OnFriendEvent(FriendEvent evt) => _dq?.TryEnqueue(() => _ = HandleEventAsync(evt));

    private async Task HandleEventAsync(FriendEvent evt)
    {
        if (evt.Type == "friend_message")
        {
            var message = FriendEventData.Parse<FriendMessage>(evt.Data);
            if (message is null) return;
            var me = _s.StarcloudId.Current?.UserId;
            var peer = message.FromUserId == me ? message.ToUserId : message.FromUserId;
            _lastMessages[peer] = message;

            if (peer == _selectedUserId)
            {
                if (_messages.All(m => m.Id != message.Id)) _messages.Add(message);
                RenderMessages(scrollToEnd: true);
                if (message.FromUserId != me)
                {
                    _unread[peer] = 0;
                    try { await _s.Friends.MarkReadAsync(peer); } catch { /* 已读失败不打断展示 */ }
                }
            }
            else if (message.FromUserId != me)
            {
                _unread[peer] = _unread.GetValueOrDefault(peer) + 1;
            }

            RenderConversations();
            return;
        }

        await RefreshAllAsync();
    }

    private void OnSessionChanged() => _dq?.TryEnqueue(() => _ = RefreshAllAsync());

    // ── 其它 ──

    private void GoLogin_Click(object sender, RoutedEventArgs e) =>
        (App.MainWindow as MainWindow)?.NavigateTo("accounts");

    private void ShowError(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void ShowStatus(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static void Copy(string text)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        pkg.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }

    private static string InitialOf(string displayName, string id)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        return string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
    }

    private static string PreviewOf(FriendMessage? last) =>
        last is null
            ? Loc.T("还没有消息")
            : last.Content.Replace('\r', ' ').Replace('\n', ' ');

    private static DateTime? ParseTime(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime()
            : null;

    /// <summary>会话列表时间：今天只显示时分，昨天显示「昨天」，更早显示日期。</summary>
    private static string FormatStamp(string? iso)
    {
        if (ParseTime(iso) is not { } time) return "";
        var today = DateTime.Today;
        if (time.Date == today) return time.ToString("HH:mm");
        if (time.Date == today.AddDays(-1)) return Loc.T("昨天");
        return time.Year == today.Year ? time.ToString("MM-dd") : time.ToString("yyyy-MM-dd");
    }

    /// <summary>聊天区时间分隔：今天「HH:mm」，昨天「昨天 HH:mm」，更早带日期。</summary>
    private static string FormatSeparator(DateTime time)
    {
        var today = DateTime.Today;
        if (time.Date == today) return time.ToString("HH:mm");
        if (time.Date == today.AddDays(-1)) return Loc.T("昨天 {0}", time.ToString("HH:mm"));
        return time.Year == today.Year
            ? time.ToString("MM-dd HH:mm")
            : time.ToString("yyyy-MM-dd HH:mm");
    }
}

/// <summary>会话行；Kind = requests / invites / friend</summary>
internal sealed record ConversationRowModel(
    string Kind, string UserId, string Name, string Initial,
    string Preview, string TimeText, string UnreadText,
    Visibility UnreadVisibility, DateTime SortTime)
{
    public string Id { get; init; } = "";
    public ImageSource? IconSource { get; init; }
    public Visibility LetterVisibility => IconSource is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IconSource is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>聊天区一行：普通气泡或居中的时间分隔</summary>
internal sealed record ChatMessageRowModel(
    bool IsMine, string Content, string TimeText, string SenderInitial, bool IsSeparator);
