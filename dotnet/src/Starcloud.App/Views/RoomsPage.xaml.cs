// 联机（房间系统）页面：大厅 / 房间 / 我的 三个页签。
//
// 职责：只做「把 RoomRuntime 的状态画出来 + 把点击翻译成调用」。
// 房间状态、临时聊天、陶瓦码都在 RoomRuntime（跨页面存活），本页重建不丢。
// 「我的」页签：状态面板（核心/会话/状态三格 + 急停）+ 房间历史，
// 原有能力一件不丢。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Shared;
using Starcloud.App.Views.Dialogs;
using Starcloud.App.Views.Rows;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Rooms;

namespace Starcloud.App.Views;

public sealed partial class RoomsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private RoomRuntime Runtime => _s.RoomRuntime;
    private RoomService Rooms => _s.Rooms;

    private bool _loaded;
    private bool _busy;
    private string _tab = "lobby";
    /// <summary>搜索命中的房间（非空时大厅只显示这一张卡；刷新/清空搜索回到全量）。</summary>
    private RoomSummary? _searchResult;

    public RoomsPage()
    {
        InitializeComponent();
        // Enter 发送 / Ctrl+Enter 换行：handledEventsToo 注册（XAML 特性收不到被
        // TextBox 类处理器标记 Handled 的 Enter —— 实际踩过「按回车没反应」）
        RoomInput.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(RoomInput_KeyDown), handledEventsToo: true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        // 联机云控：服务端把 multiplayer 关掉时整页只留维护提示（配置可能晚到，订阅变更再刷新）
        if (!_s.Cloud.Config.Multiplayer)
        {
            ShowMaintenance();
            _s.Cloud.ConfigChanged += OnCloudConfigChanged;
            return;
        }

        Runtime.Changed += OnRuntimeChanged;
        Runtime.Notify += OnRuntimeNotify;
        Runtime.TerracottaReady += OnTerracottaReady;
        // 「我的」状态面板要实时刷新：轮询由页面驱动（不在房间时）；
        // 房间内的轮询归 RoomRuntime 管（EnterAsync 开 / LeaveAsync 停）
        _s.Multiplayer.Changed += OnMultiplayerSnapshot;
        _s.Multiplayer.StartPolling();
        NoticeFx.Attach(NoticeBar);   // 提示条：掉落回弹入场 / 三次方缓出收起

        MockBadge.Visibility = Rooms.IsMock ? Visibility.Visible : Visibility.Collapsed;

        // 从好友聊天的房卡点「加入房间」过来：进入页面后自动带码进房（含密码询问）
        if (App.PendingRoomCode is { Length: > 0 } pendingCode)
        {
            App.PendingRoomCode = null;
            DispatcherQueue.TryEnqueue(async () => await EnterByCodeAsync(pendingCode));
        }

        RenderRoom();
        _ = RefreshLobbyAsync();
        _ = RefreshMineAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Runtime.Changed -= OnRuntimeChanged;
        Runtime.Notify -= OnRuntimeNotify;
        Runtime.TerracottaReady -= OnTerracottaReady;
        _s.Multiplayer.Changed -= OnMultiplayerSnapshot;
        // 房间内的轮询归 RoomRuntime：不能在这里停（否则进房后切页会断掉 HostReady 感知）
        if (!Runtime.InRoom) _s.Multiplayer.StopPolling();
        _s.Cloud.ConfigChanged -= OnCloudConfigChanged;
        _loaded = false;
    }

    /// <summary>联机被云控关闭：盖一层维护提示（盖在内容上，恢复时移除即可）。</summary>
    private void ShowMaintenance()
    {
        NoticeBar.Severity = InfoBarSeverity.Warning;
        NoticeBar.Title = Loc.T("联机功能维护中");
        NoticeBar.Message = Loc.T("联机功能暂时关闭，请稍后再来。");
        NoticeBar.IsOpen = true;
        NoticeBar.IsClosable = false;
        // 整块内容禁用：变暗 + 不吃点击（Panel 没有 IsEnabled，用 IsHitTestVisible + Opacity）
        ContentHost.IsHitTestVisible = false;
        ContentHost.Opacity = 0.45;
    }

    /// <summary>云控把联机重新打开：恢复页面正常初始化（用户切走再切回来即可）。</summary>
    private void OnCloudConfigChanged()
    {
        if (_s.Cloud.Config.Multiplayer is false) return;
        _s.Cloud.ConfigChanged -= OnCloudConfigChanged;
        NoticeBar.IsClosable = true;
        NoticeBar.IsOpen = false;
        ContentHost.IsHitTestVisible = true;
        ContentHost.Opacity = 1;
        NoticeBar.Title = Loc.T("联机");

        Runtime.Changed += OnRuntimeChanged;
        Runtime.Notify += OnRuntimeNotify;
        Runtime.TerracottaReady += OnTerracottaReady;
        _s.Multiplayer.Changed += OnMultiplayerSnapshot;
        _s.Multiplayer.StartPolling();
        MockBadge.Visibility = Rooms.IsMock ? Visibility.Visible : Visibility.Collapsed;
        RenderRoom();
        _ = RefreshLobbyAsync();
        _ = RefreshMineAsync();
        DispatcherQueue.TryEnqueue(() => ShowNotice(Loc.T("联机功能已恢复")));
    }

    private void OnRuntimeChanged() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            RenderRoom();
            UpdateNavCurrent();
        });

    private void OnRuntimeNotify(string message) =>
        DispatcherQueue.TryEnqueue(() => ShowNotice(message, warning: true));

    private void OnTerracottaReady(string code) =>
        DispatcherQueue.TryEnqueue(() =>
            ShowNotice(Loc.T("房主已开房，可以进入游戏了（陶瓦码 {0}）", code)));

    // ── 导航 ──

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 解析期（IsSelected="True"）就会触发一次选择变化，那时 x:Name 字段还没赋值 ——
        // 在这里判空会直接空引用、页面构造失败。加载完成前的选择交给默认可见性（大厅）。
        if (!_loaded) return;
        if (NavList.SelectedItem is not ListViewItem { Tag: string tag }) return;
        _tab = tag;

        NavLobby.IsActive = tag == "lobby";
        NavRoom.IsActive = tag == "room";
        NavMine.IsActive = tag == "mine";

        LobbyPanel.Visibility = tag == "lobby" ? Visibility.Visible : Visibility.Collapsed;
        RoomPanel.Visibility = tag == "room" ? Visibility.Visible : Visibility.Collapsed;
        MinePanel.Visibility = tag == "mine" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "lobby") _ = RefreshLobbyAsync();
        if (tag == "mine") _ = RefreshMineAsync();
    }

    private void GoHost_Click(object sender, RoutedEventArgs e) => SelectTab("room");
    private void GoPlayer_Click(object sender, RoutedEventArgs e) => SelectTab("room");

    private void SelectTab(string tag)
    {
        foreach (var item in NavList.Items.OfType<ListViewItem>())
        {
            if (item.Tag as string == tag)
            {
                NavList.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>当前房间小卡：任何页签都看得到自己在哪。</summary>
    private void UpdateNavCurrent()
    {
        if (Runtime.Current is { } room)
        {
            NavCurrentCard.Visibility = Visibility.Visible;
            NavRoomName.Text = room.Room.Name;
            NavRoomCode.Text = Loc.T("联机码 {0}", room.Room.Code);
        }
        else
        {
            NavCurrentCard.Visibility = Visibility.Collapsed;
        }
    }

    // ── 大厅 ──

    private async Task RefreshLobbyAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            try
            {
                await LoadLobbyAsync();
            }
            catch (RoomApiException ex) when (!Rooms.IsMock && IsNotDeployed(ex))
            {
                // 服务端还没有 /v1/rooms（接口未上线）：切到本地演示数据，让界面可用。
                // 注意不能在这里递归调 RefreshLobbyAsync —— _busy 还是 true，会被守卫直接吞掉。
                Rooms.UseMock();
                MockBadge.Visibility = Visibility.Visible;
                ShowNotice(Loc.T("联机服务器暂不可达，已切换到演示数据"), warning: true);
                await LoadLobbyAsync();
            }
        }
        catch (RoomApiException ex)
        {
            ShowNotice(ex.Message, warning: true);
            LobbySummary.Text = Loc.T("大厅加载失败");
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("大厅加载失败：{0}", ex.Message), warning: true);
            LobbySummary.Text = Loc.T("大厅加载失败");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task LoadLobbyAsync()
    {
        var rooms = await Rooms.ListAsync();
        RoomList.ItemsSource = rooms;
        LobbyEmpty.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LobbySummary.Text = rooms.Count == 0
            ? Loc.T("暂时没有开放中的房间")
            : Loc.T("共 {0} 个房间开放中", rooms.Count);
    }

    private async void LobbyRefresh_Click(object sender, RoutedEventArgs e)
    {
        _searchResult = null;
        await RefreshLobbyAsync();
    }

    /// <summary>服务端把未知路由当成 API 错误返回：404 + 「未找到接口」。</summary>
    private static bool IsNotDeployed(RoomApiException ex) =>
        ex.Status == 404 || string.Equals(ex.Code, "network", StringComparison.OrdinalIgnoreCase);

    private void LobbySearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = SearchRoomAsync();
    }

    private void LobbySearchGo_Click(object sender, RoutedEventArgs e) => _ = SearchRoomAsync();

    /// <summary>
    /// 搜索：按 6 位码查房间。命中后大厅只显示这一张卡（点它加入），
    /// 搜索框清空再点搜索 / 点刷新就回到全量列表。
    /// </summary>
    private async Task SearchRoomAsync()
    {
        var code = RoomCodeRules.Normalize(LobbySearchBox.Text);
        if (code.Length == 0)
        {
            // 空输入 = 退出搜索，回到全量大厅
            _searchResult = null;
            await RefreshLobbyAsync();
            return;
        }
        if (!RoomCodeRules.IsValid(code))
        {
            ShowNotice(Loc.T("联机码应为 6 位数字"), warning: true);
            return;
        }

        try
        {
            var detail = await Rooms.GetAsync(code);
            _searchResult = detail.Room;
            RoomList.ItemsSource = new[] { detail.Room };
            LobbyEmpty.Visibility = Visibility.Collapsed;
            LobbySummary.Text = Loc.T("找到房间「{0}」，点击卡片加入", detail.Room.Name);
        }
        catch (RoomApiException ex)
        {
            _searchResult = null;
            RoomList.ItemsSource = Array.Empty<RoomSummary>();
            LobbyEmpty.Visibility = Visibility.Collapsed;
            LobbySummary.Text = Loc.T("没找到联机码为 {0} 的房间", code);
            ShowNotice(ex.IsGone ? Loc.T("没有这个房间，或它已经关闭了") : ex.Message, warning: true);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("搜索失败：{0}", ex.Message), warning: true);
        }
    }

    /// <summary>点卡片加入（有密码会先弹输入框）。</summary>
    private void RoomCard_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RoomSummary room) _ = EnterByCodeAsync(room.Code);
    }

    private void PlayerCard_Tapped(object sender, TappedRoutedEventArgs e) =>
        _ = ShowJoinMethodDialogAsync();

    /// <summary>
    /// 「我要当玩家」：弹窗里先用两张卡片选择进入方式（联机码 / 陶瓦直连），
    /// 点选后在同一弹窗切到对应输入视图，确认后走各自的加入链路。
    /// </summary>
    private async Task ShowJoinMethodDialogAsync()
    {
        string? mode = null;   // "code" | "terracotta"，点卡片时确定
        var accepted = false;  // 输入框里按 Enter 直接确认（ContentDialog 无编程式 Primary 关闭）

        var codeBox = new TextBox { PlaceholderText = Loc.T("输入 6 位联机码"), MaxLength = 12 };
        var tcBox = new TextBox { PlaceholderText = Loc.T("例如 U/XXXX-XXXX-XXXX-XXXX") };

        var dlg = new ThemedDialog
        {
            Title = Loc.T("我要当玩家"),
            CloseButtonText = Loc.T("取消"),
            XamlRoot = Content.XamlRoot,
        };

        // 输入框里按 Enter = 确认（ContentDialog 没有编程式 Primary 关闭）
        void EnterConfirms(KeyRoutedEventArgs args)
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            accepted = true;
            dlg.Hide();
        }
        codeBox.KeyDown += (_, args) => EnterConfirms(args);
        tcBox.KeyDown += (_, args) => EnterConfirms(args);

        FrameworkElement InputView(string target)
        {
            var box = target == "code" ? (FrameworkElement)codeBox : tcBox;
            return new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = target == "code"
                            ? Loc.T("输入房主的 6 位联机码，进房后会自动安装房主的整合包。")
                            : Loc.T("这种方式不经过房间系统：没有临时群聊，也不会自动安装整合包。"),
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            };
        }

        Button MakeOption(string icon, string title, string desc, string target)
        {
            var iconImage = new Image { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
            Controls.SvgIcon.SetSource(iconImage, icon);

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
            text.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(iconImage);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var button = new Button
            {
                Content = row,
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += (_, _) =>
            {
                mode = target;
                dlg.Content = InputView(target);
                dlg.PrimaryButtonText = Loc.T(target == "code" ? "加入房间" : "直连进入");
                dlg.IsPrimaryButtonEnabled = true;
                dlg.DefaultButton = ContentDialogButton.Primary;
            };
            return button;
        }

        dlg.Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = Loc.T("选择进入方式"),
                    FontSize = 12,
                    Opacity = 0.7,
                },
                MakeOption("room-code", Loc.T("输入联机码加入"),
                    Loc.T("6 位房间码，进房后自动安装房主的整合包"), "code"),
                MakeOption("terracotta", Loc.T("陶瓦邀请码直连"),
                    Loc.T("不进房间，直接连接房主开好的局域网世界"), "terracotta"),
            },
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary && !accepted) return;
        if (mode == "code") await EnterByCodeAsync(codeBox.Text);
        else if (mode == "terracotta") await JoinTerracottaAsync(tcBox.Text);
    }

    /// <summary>按联机码进房：先查房间，需要密码就弹一次，然后 join。</summary>
    private async Task EnterByCodeAsync(string? rawCode)
    {
        var code = RoomCodeRules.Normalize(rawCode);
        if (!RoomCodeRules.IsValid(code))
        {
            ShowNotice(Loc.T("联机码应为 6 位数字"), warning: true);
            return;
        }

        try
        {
            string? password = null;
            RoomDetail detail;
            try
            {
                detail = await Rooms.GetAsync(code);
            }
            catch (RoomApiException ex)
            {
                ShowNotice(ex.IsGone ? Loc.T("房间不存在或已关闭") : ex.Message, warning: true);
                return;
            }

            if (detail.Room.HasPassword)
            {
                password = await PromptPasswordAsync(detail.Room);
                if (password is null) return;   // 用户取消
            }

            var result = await Runtime.EnterAsync(code, password);
            LobbySearchBox.Text = "";
            ShowNotice(Loc.T("已进入房间「{0}」", result.Room.Room.Name));
            SelectTab("room");
            RenderRoom();
            UpdateNavCurrent();
        }
        catch (RoomApiException ex)
        {
            var text = ex.IsBadPassword ? Loc.T("密码不正确")
                : ex.IsFull ? Loc.T("房间人数已满")
                : ex.IsGone ? Loc.T("房间不存在或已关闭")
                : ex.Message;
            ShowNotice(text, warning: true);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("进入房间失败：{0}", ex.Message), warning: true);
        }
    }

    private async Task<string?> PromptPasswordAsync(RoomSummary room)
    {
        var box = new PasswordBox { PlaceholderText = Loc.T("房间密码") };
        var dlg = new ThemedDialog
        {
            Title = Loc.T("进入「{0}」", room.Name),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T("这个房间设置了密码，请输入后加入。"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = Loc.T("加入"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    // ── 房间 ──

    /// <summary>把 RoomRuntime 的状态画到房间页。</summary>
    private void RenderRoom()
    {
        var inRoom = Runtime.Current is { } room;
        NoRoomPanel.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        InRoomPanel.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
        if (Runtime.Current is not { } current) return;

        RoomTitle.Text = current.Room.Name;
        RoomCodeText.Text = Loc.T("联机码 {0}", current.Room.Code);
        var terracotta = Runtime.TerracottaCode is { Length: > 0 } tc
            ? Loc.T("陶瓦码 {0}", tc)
            : Loc.T("房主尚未开房");
        RoomMetaText.Text = Loc.T("{0} · {1} · {2}",
            current.Room.VersionText, current.Room.MembersText, terracotta);

        DissolveBtn.Visibility = current.IsOwner ? Visibility.Visible : Visibility.Collapsed;
        // 房主不能「退出」：服务端要求走解散（leave 会 400 owner_must_dissolve），
        // 本地静默退出只会让 SSE 断开、房间 60 秒后被动关掉 —— 看着就像「退出界面就关房」。
        LeaveBtn.Visibility = current.IsOwner ? Visibility.Collapsed : Visibility.Visible;

        // 成员列表：房主在前
        MemberHeader.Text = Loc.T("成员（{0}）", current.Members.Count);
        var rows = current.Members
            .OrderByDescending(m => m.IsOwner)
            .Select(m => (object)BuildMemberRow(m, current.IsOwner))
            .ToList();
        MemberList.ItemsSource = rows;

        RenderMessages();
    }

    private FrameworkElement BuildMemberRow(RoomMember member, bool iAmOwner)
    {
        var avatar = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Child = new TextBlock
            {
                Text = member.Initial,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = member.DisplayName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (member.IsOwner)
            text.Children.Add(new TextBlock
            {
                Text = Loc.T("房主"),
                FontSize = 10,
                Opacity = 0.6,
            });

        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(6, 4, 6, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(avatar);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // 右键：加好友 / 房主可踢人（对自己不显示菜单）
        var isMe = member.UserId == "me";
        if (!isMe)
        {
            var menu = new MenuFlyout();
            var addFriend = new MenuFlyoutItem { Text = Loc.T("加为好友") };
            addFriend.Click += (_, _) => _ = AddFriendAsync(member);
            menu.Items.Add(addFriend);
            if (iAmOwner && !member.IsOwner)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var kick = new MenuFlyoutItem { Text = Loc.T("移出房间") };
                kick.Click += (_, _) => _ = KickAsync(member);
                menu.Items.Add(kick);
            }
            row.ContextFlyout = menu;
        }

        return row;
    }

    private async Task AddFriendAsync(RoomMember member)
    {
        try
        {
            await _s.Friends.SendRequestAsync(member.UserId, null);
            ShowNotice(Loc.T("已向 {0} 发送好友申请", member.DisplayName));
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("加好友失败：{0}", ex.Message), warning: true);
        }
    }

    private async Task KickAsync(RoomMember member)
    {
        var dlg = new ThemedDialog
        {
            Title = Loc.T("移出房间"),
            Content = Loc.T("把 {0} 移出房间？对方会立即断开房间。", member.DisplayName),
            PrimaryButtonText = Loc.T("移出"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await Runtime.KickAsync(member.UserId);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("移出失败：{0}", ex.Message), warning: true);
        }
    }

    private void RenderMessages()
    {
        var rows = new List<object>();
        foreach (var message in Runtime.Messages)
        {
            var isMine = message.FromUserId == "me";
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock
            {
                Text = isMine ? Loc.T("我") : message.DisplayName ?? message.FromUserId,
                FontSize = 11,
                Opacity = 0.6,
            });
            stack.Children.Add(new TextBlock
            {
                Text = message.Content,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            });
            rows.Add(stack);
        }
        RoomMessageList.ItemsSource = rows;
        if (rows.Count > 0) RoomMessageList.ScrollIntoView(rows[^1]);
    }

    private async void RoomInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl)
        {
            // Ctrl+Enter 换行：AcceptsReturn=False，光标处手动插换行符
            var caret = RoomInput.SelectionStart;
            RoomInput.Text = (RoomInput.Text ?? "").Insert(Math.Min(caret, RoomInput.Text?.Length ?? 0), "\n");
            RoomInput.SelectionStart = caret + 1;
            return;
        }
        await SendAsync();
    }

    private void RoomSend_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        var text = RoomInput.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        RoomInput.Text = "";
        try
        {
            await Runtime.SendAsync(text);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("发送失败：{0}", ex.Message), warning: true);
        }
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        await Runtime.LeaveAsync();
        RenderRoom();
        UpdateNavCurrent();
        ShowNotice(Loc.T("已退出房间"));
        _ = RefreshLobbyAsync();
    }

    private async void Dissolve_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ThemedDialog
        {
            Title = Loc.T("解散房间"),
            Content = Loc.T("解散后所有成员会被移出，房间码立即失效。确定解散吗？"),
            PrimaryButtonText = Loc.T("解散"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await Runtime.DissolveAsync();
            RenderRoom();
            UpdateNavCurrent();
            ShowNotice(Loc.T("房间已解散"));
            _ = RefreshLobbyAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("解散失败：{0}", ex.Message), warning: true);
        }
    }

    /// <summary>
    /// 房间右上「邀请好友」：选一位好友，在私聊里发一张房卡（房间码 + 版本），
    /// 对方点卡片上的「加入房间」即可进来 —— 不再走单独的一套邀请系统。
    /// </summary>
    private async void InviteFriend_Click(object sender, RoutedEventArgs e)
    {
        if (Runtime.Current is not { } room) return;
        var friends = await _s.Friends.GetFriendsAsync();
        if (friends.Count == 0)
        {
            ShowNotice(Loc.T("还没有好友，先在好友页添加对方。"), warning: true);
            return;
        }

        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var f in friends)
            picker.Items.Add(new ComboBoxItem { Content = $"{f.DisplayName}（{f.Id}）", Tag = f.UserId });
        picker.SelectedIndex = 0;

        var dialog = new ThemedDialog
        {
            Title = Loc.T("邀请好友联机"),
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.T("把这张房卡发到与对方的聊天里，对方点一下就能进房。"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                    },
                    picker,
                },
            },
            PrimaryButtonText = Loc.T("发送邀请"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (picker.SelectedItem is not ComboBoxItem { Tag: string userId }) return;

        try
        {
            var payload = Starcloud.Core.Services.FriendsService.EncodeRoomInviteMessage(
                room.Room.Code, room.Room.Name, room.Room.McVersion, room.Room.Loader,
                _s.StarcloudId.Current?.UserId);
            await _s.Friends.SendMessageAsync(userId, payload);
            ShowNotice(Loc.T("邀请已发出，等对方进房。"));
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("邀请发送失败：{0}", ex.Message), warning: true);
        }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (Runtime.Current is not { } room) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(room.Room.Code);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        ShowNotice(Loc.T("联机码已复制：{0}", room.Room.Code));
    }

    // ── 房主：选版本 → 打包上传 → 建房 ──

    private async void HostCard_Tapped(object sender, TappedRoutedEventArgs e) =>
        await CreateRoomAsync();

    /// <summary>建房对话框里的版本项（ComboBox 直接显示 TOString）。</summary>
    private sealed record VersionPick(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private async Task CreateRoomAsync()
    {
        var dotMc = _s.Settings.DotMinecraft;
        // 版本列表：与版本页同一口径（依赖版本不单列）
        var picks = new List<VersionPick>();
        InstanceSettingsStore? store = null;
        try
        {
            store = new InstanceSettingsStore(dotMc);
            foreach (var v in VersionVisibility.Filter(dotMc, _s.Manifest.ListInstalled(dotMc)))
                picks.Add(new VersionPick(v.Id, $"{v.Id} · {VersionRowHelpers.DetectKind(dotMc, v.Id)}"));
        }
        catch
        {
            // 读不到版本列表：仍允许建房（不带整合包）
        }

        var nameBox = new TextBox { PlaceholderText = Loc.T("房间名（默认用玩家名）"), MaxLength = 32 };
        var players = new NumberBox
        {
            Minimum = CreateRoomRequest.MinPlayers,
            Maximum = CreateRoomRequest.MaxPlayersLimit,
            Value = 8,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var passwordBox = new PasswordBox { PlaceholderText = Loc.T("房间密码（可留空）") };
        var versionCombo = new ComboBox
        {
            ItemsSource = picks,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = Loc.T("选择要一起玩的版本"),
            IsEnabled = picks.Count > 0,
        };
        if (picks.Count > 0)
        {
            var last = _s.Settings.LastVersion;
            versionCombo.SelectedItem = picks.FirstOrDefault(p => p.Id == last) ?? picks[0];
        }

        var dlg = new ThemedDialog
        {
            Title = Loc.T("我要当房主"),
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    nameBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = Loc.T("人数上限"),
                                VerticalAlignment = VerticalAlignment.Center,
                                FontSize = 12,
                            },
                            players,
                        },
                    },
                    passwordBox,
                    versionCombo,
                    new TextBlock
                    {
                        Text = Loc.T("建房时会把所选版本打包上传，玩家加入后自动安装。每个版本首次上传较慢，之后直接复用。"),
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = Loc.T("创建房间"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var picked = versionCombo.SelectedItem as VersionPick;
        try
        {
            var me = _s.Accounts.DefaultOf(_s.LaunchAccount.Mode)?.Username
                     ?? _s.Accounts.Accounts.FirstOrDefault()?.Username ?? Loc.T("房主");
            var request0 = new CreateRoomRequest(
                string.IsNullOrWhiteSpace(nameBox.Text) ? Loc.T("{0} 的房间", me) : nameBox.Text.Trim(),
                (int)Math.Clamp(double.IsNaN(players.Value) ? 8 : players.Value,
                    CreateRoomRequest.MinPlayers, CreateRoomRequest.MaxPlayersLimit),
                string.IsNullOrEmpty(passwordBox.Password) ? null : passwordBox.Password,
                null);

            var room = await BuildPackAndCreateAsync(request0, picked, store);
            if (room is not null) await EnterCreatedRoomAsync(room, request0.Password, picked?.Id);
        }
        catch (RoomApiException ex)
        {
            ShowNotice(ex.IsRoomLimit
                ? Loc.T("你已有一间开放中的房间，先去「我的」解散它")
                : Loc.T("创建房间失败：{0}", ex.Message), warning: true);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("创建房间失败：{0}", ex.Message), warning: true);
        }
    }

    /// <summary>打包上传 → 建房。invalid_pack（缓存里的 file_id 失效）时清缓存重传一次。</summary>
    private async Task<RoomSummary?> BuildPackAndCreateAsync(CreateRoomRequest request,
        VersionPick? picked, InstanceSettingsStore? store)
    {
        RoomPackCandidate? pack = null;
        if (picked is not null && store is not null)
        {
            // 演示数据（接口不可达时的降级）没有真实上传链路：跳过打包上传，
            // 否则点一下房主卡会真的去打包整个实例，卡好几分钟还没意义。
            if (Rooms.IsMock)
            {
                ShowProgress(Loc.T("演示数据：跳过整合包上传"));
            }
            else
            {
                var isolated = InstanceLayout.IsIsolated(store.Get(picked.Id), _s.Settings);
                ShowProgress(Loc.T("正在准备整合包…"));
                var progress = new Progress<string>(msg =>
                    DispatcherQueue.TryEnqueue(() => ShowProgress(msg)));
                try
                {
                    pack = await _s.RoomPackFlow.EnsurePackAsync(picked.Id, isolated, progress);
                }
                catch (RoomApiException)
                {
                    NoticeBar.IsOpen = false;
                    throw;
                }
                catch (Exception ex)
                {
                    NoticeBar.IsOpen = false;
                    throw new RoomApiException(Loc.T("打包上传失败：{0}", ex.Message));
                }
            }
        }

        var info = picked is null ? null : _s.RoomPackFlow.ReadVersion(picked.Id);
        var built = request0WithPack(request, pack, info);
        try
        {
            var room = await Rooms.CreateAsync(built);
            NoticeBar.IsOpen = false;
            return room;
        }
        catch (RoomApiException ex) when (picked is not null &&
            string.Equals(ex.Code, "invalid_pack", StringComparison.OrdinalIgnoreCase))
        {
            // 缓存里的 file_id 已经失效（服务端清过文件）：清掉缓存重传一次
            _s.RoomPackFlow.InvalidateCache(picked.Id);
            var isolated = store is null || InstanceLayout.IsIsolated(store.Get(picked.Id), _s.Settings);
            var progress = new Progress<string>(msg =>
                DispatcherQueue.TryEnqueue(() => ShowProgress(msg)));
            pack = await _s.RoomPackFlow.EnsurePackAsync(picked.Id, isolated, progress);
            var room = await Rooms.CreateAsync(request0WithPack(request, pack, info));
            NoticeBar.IsOpen = false;
            return room;
        }

        static CreateRoomRequest request0WithPack(CreateRoomRequest r,
            RoomPackCandidate? pack, RoomVersionInfo? info) =>
            r with { Pack = pack?.ToPackInfo(), McVersion = info?.McVersion, Loader = info?.Loader };
    }

    private async Task EnterCreatedRoomAsync(RoomSummary room, string? password,
        string? versionId = null)
    {
        await Runtime.EnterAsync(room.Code, password);
        ShowNotice(Loc.T("房间已创建，联机码 {0}", room.Code));
        SelectTab("room");
        RenderRoom();
        UpdateNavCurrent();
        _ = RefreshMineAsync();

        if (versionId is not { Length: > 0 }) return;
        // 房主侧自动开房：先让陶瓦核心开始扫描局域网，再拉起游戏；
        // 房主在游戏里「对局域网开放」后核心会发现世界并进入 host-ready，
        // RoomRuntime 随即把邀请码上报到房间（见 OnMultiplayerChanged）。
        try
        {
            await Runtime.HostLanAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("联机核心未就绪：{0}（可到「我的」安装核心）", ex.Message), warning: true);
        }
        App.PendingLaunchVersionId = versionId;
        if (App.MainWindow is MainWindow mw) mw.NavigateTo("home");
        ShowNotice(Loc.T("正在启动游戏：进去后点「对局域网开放」，房间会自动开好"));
    }

    // ── 开始游戏（玩家：带房主地址自动进服） ──

    private async void StartGame_Click(object sender, RoutedEventArgs e) => await StartGameAsync();

    private async Task StartGameAsync()
    {
        if (Runtime.Current is not { } room) return;

        if (room.IsOwner)
        {
            // 房主：游戏在建房时就已拉起，这里只提醒还差哪一步
            ShowNotice(Runtime.HostReady
                ? Loc.T("房间已开好，玩家可以进来了")
                : Loc.T("请在游戏里点「对局域网开放」，房间会自动开好"));
            return;
        }

        if (Runtime.GuestAddress is not { Length: > 0 } address)
        {
            ShowNotice(Loc.T("还在连接房主的世界，稍等再点一次"), warning: true);
            await Runtime.JoinGuestAsync();
            return;
        }

        var versionId = await ResolveRoomVersionAsync(room.Room);
        // 解析失败（原因已在内部提示过）：别再带着空版本跳主页，否则会变成"假启动"
        if (versionId is null) return;

        // 走主页的启动编排（预检/账户/内存档都复用），并带上房主地址自动进服
        App.PendingLaunchServer = address;
        App.PendingLaunchVersionId = versionId;
        App.PendingReturnToFriends = true;   // 游戏窗口出现后自动跳回联机页
        if (App.MainWindow is MainWindow mw) mw.NavigateTo("home");
        ShowNotice(Loc.T("正在启动游戏，进游戏后自动连到房主的世界"));
    }

    /// <summary>
    /// 玩家端解析用哪个版本进房：
    /// ① 房间没带整合包 → 按「MC 版本 + 加载器」直接用本地版本；
    /// ② 带了包 → 下载包（轻量索引，几 MB）并校验 sha256，逐一对比候选版本的
    ///    Mod 清单（mods/ 文件的 sha1 集合全等）——一致直接用本地版本启动；
    /// ③ 全都不一致 → 用已下载的包安装为隔离实例再启动。
    /// </summary>
    private async Task<string?> ResolveRoomVersionAsync(RoomSummary room)
    {
        var candidates = PickRoomVersionCandidates(room);
        var pack = room.Pack;
        if (pack?.Url is not { Length: > 0 } packUrl)
        {
            if (candidates.Count == 0)
            {
                ShowNotice(Loc.T("本地没有 {0}{1} 版本，请先在下载页安装",
                    room.McVersion ?? Loc.T("对应"),
                    room.Loader is { Length: > 0 } loader ? " · " + loader : ""), warning: true);
            }
            return candidates.FirstOrDefault();
        }

        // 下载整合包（轻量：索引 + 少量 overrides）——比对和（必要时）安装都用它
        var temp = Path.Combine(Path.GetTempPath(), $"starcloud-room-{Guid.NewGuid():N}.mrpack");
        try
        {
            await DownloadRoomPackAsync(pack, temp);
            var packMods = ReadPackModsSha(temp);
            if (packMods is not null)
            {
                foreach (var id in candidates)
                {
                    var local = LocalModsSha(id);
                    if (local is null) continue;
                    if (SameMods(local, packMods))
                    {
                        LauncherLog.Write("rooms",
                            $"本地版本 {id} 的 Mod 清单与房间一致，直接使用（{local.Count} 个 mod）");
                        try { File.Delete(temp); } catch { /* 临时包删不掉可忽略 */ }
                        return id;
                    }
                }
                LauncherLog.Write("rooms",
                    $"本地 {candidates.Count} 个候选版本的 Mod 清单与房间不一致，转整合包安装");
            }
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("整合包比对失败：{0}", ex.Message), warning: true);
            try { File.Delete(temp); } catch { }
            return null;
        }

        // 安装（复用已下载的包，不二次下载）
        var installed = await InstallRoomPackAsync(pack, temp);
        try { File.Delete(temp); } catch { }
        return installed;
    }

    /// <summary>下载房主整合包到指定路径（1MB 节流百分比）+ sha256 校验；失败抛异常。</summary>
    private async Task DownloadRoomPackAsync(RoomPackInfo pack, string dest)
    {
        ShowProgress(Loc.T("正在下载房主的整合包…"));
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var resp = await http.GetAsync(pack.Url!, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        var total = pack.Size > 0 ? pack.Size : (resp.Content.Headers.ContentLength ?? 0);
        long sent = 0, nextMark = 0;
        await using (var dst = File.Create(dest))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                sent += n;
                if (sent >= nextMark)
                {
                    nextMark = sent + 1024 * 1024;
                    if (total > 0) ShowProgress(Loc.T("正在下载整合包… {0}%", sent * 100 / total));
                }
            }
        }
        if (!string.IsNullOrEmpty(pack.Sha256))
        {
            ShowProgress(Loc.T("正在校验整合包…"));
            await using var fs = File.OpenRead(dest);
            var sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
            if (!string.Equals(sha, pack.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    Loc.T("整合包校验失败（下载不完整或内容不符），请重新进入房间重试"));
        }
    }

    /// <summary>
    /// 读整合包里 Mod 清单（modrinth.index.json 的 files[]，mods/ 前缀 → sha1）。
    /// 非 Modrinth 格式（无索引，如旧 MultiMC 缓存）返回 null → 走「MC 版本+加载器」匹配兜底。
    /// </summary>
    private static Dictionary<string, string>? ReadPackModsSha(string packPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(packPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("modrinth.index.json",
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open());
            using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            if (!doc.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.EnumerateArray())
            {
                var path = f.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(path) ||
                    !path.Replace('\\', '/').StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var sha1 = "";
                if (f.TryGetProperty("hashes", out var hashes) &&
                    hashes.TryGetProperty("sha1", out var h)) sha1 = h.GetString() ?? "";
                map[path.Replace('\\', '/').ToLowerInvariant()] = sha1.ToLowerInvariant();
            }
            return map;
        }
        catch (Exception ex)
        {
            LauncherLog.Write("rooms", $"读取整合包 Mod 清单失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>候选版本实例的 mods/ 目录清单（mods/ 相对路径 → sha1）。</summary>
    private Dictionary<string, string>? LocalModsSha(string versionId)
    {
        try
        {
            var store = new InstanceSettingsStore(_s.Settings.DotMinecraft);
            var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _s.Settings);
            var gameRoot = InstanceLayout.GameRoot(_s.Settings.DotMinecraft, versionId, isolated);
            var modsDir = Path.Combine(gameRoot, "mods");
            if (!Directory.Exists(modsDir)) return [];

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories))
            {
                var rel = "mods/" + Path.GetRelativePath(modsDir, file).Replace('\\', '/');
                using var fs = File.OpenRead(file);
                map[rel.ToLowerInvariant()] =
                    Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs)).ToLowerInvariant();
            }
            return map;
        }
        catch (Exception ex)
        {
            LauncherLog.Write("rooms", $"读取本地版本 {versionId} 的 mods 清单失败：{ex.Message}");
            return null;
        }
    }

    private static bool SameMods(Dictionary<string, string> local, Dictionary<string, string> pack)
    {
        if (local.Count != pack.Count) return false;
        foreach (var (path, sha) in pack)
        {
            if (!local.TryGetValue(path, out var localSha) || localSha != sha) return false;
        }
        return true;
    }

    /// <summary>
    /// 玩家端自动安装：整合包（已下载到 tempPath）→ 按 format 安装为隔离实例
    /// → 联动装原版/加载器 → 写版本骨架。返回可启动的版本 id；失败弹提示返回 null。
    /// </summary>
    private async Task<string?> InstallRoomPackAsync(RoomPackInfo pack, string temp)
    {
        string? targetId = null;
        try
        {
            // ── 安装为隔离实例（房间维度命名，重复加入同一房间直接覆盖重装）；
            // 包已在 ResolveRoomVersionAsync 阶段下载并校验过 sha256，不再二次下载 ──
            targetId = $"room-{Runtime.Current?.Room.Code ?? "pack"}";
            var dotMc = _s.Settings.DotMinecraft;
            var installRoot = Path.Combine(dotMc, "versions", targetId);
            Directory.CreateDirectory(installRoot);
            var batch = new Progress<BatchProgress>(p =>
                DispatcherQueue.TryEnqueue(() => ShowProgress($"{p.Stage} {p.Percentage}%")));
            var status = new Progress<string>(msg => DispatcherQueue.TryEnqueue(() => ShowProgress(msg)));

            MrPackInstallResult result = pack.Format switch
            {
                "multimc" => await _s.MultiMc.InstallAsync(temp, _s.Settings, installRoot, progress: batch),
                "curseforge" => await _s.CurseForgePacks.InstallAsync(temp, _s.Settings, installRoot, progress: batch),
                _ => await _s.MrPack.InstallAsync(temp, _s.Settings, installRoot, status: status),
            };

            // ── 联动装原版与加载器（下载可能较久，进度持续走） ──
            ShowProgress(Loc.T("正在安装前置（原版 / 加载器）…"));
            await MrPackFlow.EnsureGameAndLoaderAsync(result, _s, batch);

            // ── 版本骨架：**必须写**。ListInstalled 只认 versions/<id>/<id>.json，
            // 缺了这个文件实例就是"装了但列表里没有"，启动时报「版本不存在」──
            var parent = ModPackDialogs.LoaderVersionId(dotMc, result.GameVersion, result.Dependencies)
                ?? (string.IsNullOrEmpty(result.GameVersion) ? pack.McVersion : result.GameVersion);
            var jsonPath = Path.Combine(installRoot, $"{targetId}.json");
            if (!File.Exists(jsonPath))
            {
                object skeleton = string.IsNullOrWhiteSpace(parent)
                    ? new { id = targetId, type = "release" }
                    : new { id = targetId, inheritsFrom = parent, type = "release" };
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(skeleton));
            }
            var store = new InstanceSettingsStore(dotMc);
            store.Set(targetId, new VersionProfile { Isolation = "isolated" });
            store.Save();

            ShowNotice(Loc.T("整合包已安装为版本「{0}」，正在启动…", targetId));
            return targetId;
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("整合包自动安装失败：{0}", ex.Message), warning: true);
            if (targetId is not null)
            {
                // 半截安装清掉，别在版本列表里留坏版本
                try { Directory.Delete(Path.Combine(_s.Settings.DotMinecraft, "versions", targetId), true); }
                catch { /* 清理失败忽略 */ }
            }
            return null;
        }
        // 临时包由调用方（ResolveRoomVersionAsync）统一删：它还要在"清单一致"分支里提前用掉
    }

    /// <summary>
    /// 按「MC 版本 + 加载器」列出所有可进房间的本地版本（不看名字），按优先级排序：
    /// id 与 mc_version 完全相同的在前，其余按安装清单顺序。
    /// </summary>
    private List<string> PickRoomVersionCandidates(RoomSummary room)
    {
        var dotMc = _s.Settings.DotMinecraft;
        List<string> installed;
        try
        {
            installed = VersionVisibility.Filter(dotMc, _s.Manifest.ListInstalled(dotMc))
                .Select(v => v.Id).ToList();
        }
        catch
        {
            return [];
        }

        var want = room.McVersion;
        if (string.IsNullOrWhiteSpace(want)) return installed;

        var matches = new List<string>();
        // 完全同名（常见：装过同名原版）最优先
        if (installed.FirstOrDefault(id => string.Equals(id, want, StringComparison.OrdinalIgnoreCase))
            is { } exact)
            matches.Add(exact);
        foreach (var id in installed)
        {
            if (matches.Contains(id)) continue;
            try
            {
                if (string.Equals(_s.RoomPackFlow.ReadVersion(id).McVersion, want,
                        StringComparison.OrdinalIgnoreCase))
                    matches.Add(id);
            }
            catch
            {
                // 单个版本读不出来就跳过
            }
        }
        return matches;
    }

    /// <summary>陶瓦邀请码直连：不进房间系统，直接让联机会话去加入。</summary>
    private async Task JoinTerracottaAsync(string? rawCode)
    {
        var code = rawCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            ShowNotice(Loc.T("请输入陶瓦邀请码"), warning: true);
            return;
        }
        try
        {
            await _s.Multiplayer.JoinAsync(code, CancellationToken.None);
            ShowNotice(Loc.T("已发起陶瓦联机，进入游戏后稍候即可连上"));
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("加入失败：{0}", ex.Message), warning: true);
        }
    }

    // ── 我的 ──

    /// <summary>核心快照变化：刷新「我的」状态面板三格与错误行（后台线程，需回 UI）。</summary>
    private void OnMultiplayerSnapshot(MultiplayerSnapshot snap) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            MineCoreText.Text = snap.Core.Installed
                ? $"{snap.DisplayName} {snap.Core.Version}".Trim()
                : Loc.T("未安装");
            InstallCoreBtn.Visibility = snap.Core.Installed ? Visibility.Collapsed : Visibility.Visible;
            CoreHintText.Text = snap.Core.Installed
                ? Loc.T("核心异常 / 升级时可重装，会重新下载官方核心包")
                : Loc.T("首次使用联机需要下载官方联机核心，点击安装（下载后自动就绪）");
            MineSessionText.Text = snap.Core.SessionRunning ? Loc.T("运行中") : Loc.T("未运行");
            MinePhaseText.Text = PhaseText(snap.State);
            var error = snap.State.Error ?? snap.LastError;
            MineErrorText.Text = error ?? "";
            MineErrorText.Visibility = error is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        });

    private static string PhaseText(MultiplayerState state) => state.Phase switch
    {
        MultiplayerPhase.HostScanning => Loc.T("正在扫描局域网世界"),
        MultiplayerPhase.HostStarting => Loc.T("房间启动中"),
        MultiplayerPhase.HostReady => Loc.T("房间已开"),
        MultiplayerPhase.GuestConnecting => Loc.T("连接房主…"),
        MultiplayerPhase.GuestEntering => Loc.T("进入世界…"),
        MultiplayerPhase.GuestReady => Loc.T("已连接"),
        MultiplayerPhase.Failed => Loc.T("出错"),
        _ => Loc.T("空闲"),
    };

    /// <summary>安装 / 重装联机核心：下载官方核心包（进度走提示条），装完刷新状态面板。</summary>
    private async void InstallCore_Click(object sender, RoutedEventArgs e)
    {
        InstallCoreBtn.IsEnabled = false;
        ReinstallCoreBtn.IsEnabled = false;
        var progress = new Progress<MultiplayerProgress>(p =>
            DispatcherQueue.TryEnqueue(() =>
            {
                var pct = p.Total is { } total && total > 0
                    ? Loc.T("… {0}%（{1:F1}/{2:F1} MB）", p.Bytes * 100 / total, p.Bytes / 1048576.0, total / 1048576.0)
                    : "";
                ShowProgress($"{p.Stage}{pct}");
            }));
        try
        {
            ShowProgress(Loc.T("正在准备下载联机核心…"));
            await _s.Multiplayer.InstallCoreAsync(progress, CancellationToken.None);
            ShowNotice(Loc.T("联机核心安装完成"));
            _ = _s.Multiplayer.RefreshAsync();
        }
        catch (Exception ex)
        {
            // 核心包要连外网（Gitee / GitHub 镜像）：把"换网络再试"和日志位置一起说清，
            // 否则用户只看到一句失败，不知道该检查什么
            LauncherLog.Write("multiplayer", $"核心安装失败：{ex}");
            ShowNotice(Loc.T("联机核心安装失败：{0}（可能被网络拦截，可换网络重试；详见日志「联机」分类）",
                ex.Message), warning: true);
        }
        finally
        {
            InstallCoreBtn.IsEnabled = true;
            ReinstallCoreBtn.IsEnabled = true;
        }
    }

    /// <summary>急停：退房 + 强制结束联机核心进程（不管它处在什么状态）。</summary>
    private async void MineKill_Click(object sender, RoutedEventArgs e)
    {
        MineKillBtn.IsEnabled = false;
        try
        {
            if (Runtime.InRoom)
            {
                await Runtime.LeaveAsync();
                RenderRoom();
                UpdateNavCurrent();
            }
            await _s.Multiplayer.EmergencyStopAsync(CancellationToken.None);
            ShowNotice(Loc.T("联机核心已强制结束"));
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.T("急停失败：{0}", ex.Message), warning: true);
        }
        finally
        {
            MineKillBtn.IsEnabled = true;
        }
    }

    private async Task RefreshMineAsync()
    {
        try
        {
            var rooms = await Rooms.MineAsync();
            var rows = rooms.Select(r => (object)BuildMineRow(r)).ToList();
            MineList.ItemsSource = rows;
            MineEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            // 历史列表失败不影响其它功能
        }
    }

    private FrameworkElement BuildMineRow(RoomSummary room)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 4, 6, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = room.Name, FontSize = 13 });
        text.Children.Add(new TextBlock
        {
            Text = Loc.T("联机码 {0} · {1} · {2}", room.Code, room.VersionText, room.MembersText),
            FontSize = 11,
            Opacity = 0.6,
        });
        grid.Children.Add(text);

        var enter = new Button
        {
            Content = Loc.T("进入"),
            Tag = room.Code,
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        enter.Click += (_, _) => _ = EnterByCodeAsync(room.Code);
        Grid.SetColumn(enter, 1);
        grid.Children.Add(enter);
        return grid;
    }

    // ── 提示 ──

    /// <summary>持续显示的进度提示（不自动收起）：打包/上传这类多秒操作期间用。</summary>
    private void ShowProgress(string message)
    {
        // 同文案已在显示中：什么都不做 —— 重开会重播掉落动画，视觉上就是闪
        if (NoticeBar.IsOpen && NoticeBar.Severity == InfoBarSeverity.Informational
            && NoticeBar.Message == message)
            return;
        NoticeBar.Severity = InfoBarSeverity.Informational;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    /// <summary>
    /// 悬浮提示：常驻到被下一条替换或手动关闭。
    /// <para>原来 5 秒自动收起，结果房间状态每几秒刷新一次又把同一句打开 ——
    /// 关了再开就是掉落动画重播，用户看到的是「提示一直在闪」。</para>
    /// </summary>
    private void ShowNotice(string message, bool warning = false)
    {
        var severity = warning ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        if (NoticeBar.IsOpen && NoticeBar.Severity == severity && NoticeBar.Message == message)
            return;   // 同一句已在显示：什么都不做（不重开、不重播动画）

        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
