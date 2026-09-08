// Nova 页（按 NOVA_DESIGN.md v1.0 重建）：
// 对话即执行（能力指令 → 确认清单 → 点头执行 → 记入动手流水）+
// 右栏：能力阶段条 / 动手许可层三档开关（开启先弹确认）/ 可审计流水 / LLM 降级链设置。

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class NovaPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly ObservableCollection<NovaChatMessage> _messages = [];
    private readonly List<NovaChatTurn> _history = [];
    private bool _busy;
    private bool _toggling; // 代码设置开关状态时抑制 Toggled 确认弹窗

    public NovaPage()
    {
        InitializeComponent();
        ChatList.ItemsSource = _messages;
        Loaded += (_, _) =>
        {
            UpdateAuthState();
            if (_s.StarcloudId.IsLoggedIn)
                AddAssistant(GreetingText());
        };
    }

    private void UpdateAuthState()
    {
        var session = _s.StarcloudId.Current;
        LoginPanel.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        ChatPanel.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (session is not { } s) return;
        UserSub.Text = $"{s.UserId} · {(s.Channel == StarcloudIdService.CloudChannel ? "云端通道" : "测试通道")} · Key {MaskKey(s.ApiKey)}";

        // 许可层开关回显（默认关闭；代码设置期间抑制 Toggled）
        _toggling = true;
        CaptureSwitch.IsOn = _s.NovaPermissions.IsOn(NovaPermissionStore.Capture);
        ClipSwitch.IsOn = _s.NovaPermissions.IsOn(NovaPermissionStore.Clip);
        ShareSwitch.IsOn = _s.NovaPermissions.IsOn(NovaPermissionStore.Share);
        _toggling = false;

        // LLM 降级链第 1 级回显
        LlmBaseUrlBox.Text = _s.Settings.NovaLlmBaseUrl ?? "";
        LlmKeyBox.Password = _s.Settings.NovaLlmApiKey ?? "";
        LlmModelBox.Text = _s.Settings.NovaLlmModel ?? "";

        RefreshLog();
    }

    // ── 登录 / 退出 ──
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var id = IdBox.Text.Trim();
        var channel = ChannelBox.SelectedIndex == 1
            ? StarcloudIdService.CloudChannel
            : StarcloudIdService.TestChannel;

        LoginBtn.IsEnabled = false;
        LoginProgress.IsActive = true;
        LoginError.Visibility = Visibility.Collapsed;
        try
        {
            await _s.StarcloudId.LoginAsync(id, PwdBox.Password, channel);
            UpdateAuthState();
            _messages.Clear();
            _history.Clear();
            AddAssistant(GreetingText());
        }
        catch (Exception ex)
        {
            LoginError.Text = ex.Message;
            LoginError.Visibility = Visibility.Visible;
        }
        finally
        {
            LoginBtn.IsEnabled = true;
            LoginProgress.IsActive = false;
        }
    }

    private void ChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PwdBox.PlaceholderText = ChannelBox.SelectedIndex == 1
            ? "云端通道密码"
            : "测试通道任意内容即可（至少 4 位）";
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "退出登录",
            Content = "退出后将删除本地保存的会话与 API Key。Nova 的可审计流水会保留。确定退出吗？",
            PrimaryButtonText = "退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.StarcloudId.Logout();
        _messages.Clear();
        _history.Clear();
        UpdateAuthState();
    }

    // ── 对话 ──
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        InputBox.Text = (sender as Button)?.Tag?.ToString() ?? "";
        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        var input = InputBox.Text.Trim();
        if (input.Length == 0 || _busy || _s.StarcloudId.Current is null) return;

        _busy = true;
        SendBtn.IsEnabled = false;
        SendProgress.IsActive = true;
        InputBox.Text = "";
        AddUser(input);
        _history.Add(new NovaChatTurn("user", input));
        try
        {
            var reply = await _s.Nova.ChatAsync(input, _s.Settings, _history);
            var msg = new NovaChatMessage("assistant", reply.Text, Now());
            if (reply.Plan is not null) msg = msg with { Plan = reply.Plan };
            _messages.Add(msg);
            ChatList.ScrollIntoView(msg);
            _history.Add(new NovaChatTurn("assistant", reply.Text));
            if (_history.Count > 20) _history.RemoveRange(0, _history.Count - 20);
        }
        catch (Exception ex)
        {
            AddAssistant($"出错了：{ex.Message}");
        }
        finally
        {
            _busy = false;
            SendBtn.IsEnabled = true;
            SendProgress.IsActive = false;
            InputBox.Focus(FocusState.Programmatic);
        }
    }

    // ── 确认清单：点头才动手，执行记流水 ──
    private async void PlanConfirm_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NovaChatMessage { Plan: { } plan } msg ||
            msg.PlanState != "pending") return;
        try
        {
            var result = await _s.NovaLog.ExecuteConfirmedAsync(plan);
            ReplaceMessage(msg, msg with { PlanState = "done", ResultText = result });
        }
        catch (Exception ex)
        {
            ReplaceMessage(msg, msg with { PlanState = "failed", ResultText = ex.Message });
        }
        finally
        {
            RefreshLog();
        }
    }

    private void PlanCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NovaChatMessage { Plan: { } plan } msg ||
            msg.PlanState != "pending") return;
        _s.NovaLog.LogCancelled(plan);
        ReplaceMessage(msg, msg with { PlanState = "cancelled" });
        RefreshLog();
    }

    private void ReplaceMessage(NovaChatMessage oldMsg, NovaChatMessage newMsg)
    {
        var idx = _messages.IndexOf(oldMsg);
        if (idx < 0) return;
        _messages[idx] = newMsg;
        ChatList.ScrollIntoView(newMsg);
    }

    // ── 动手许可层：开启 = 显式授权（确认弹窗 + 记入流水） ──
    private async void Permission_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toggling || sender is not ToggleSwitch { Tag: string key } ts) return;
        var on = ts.IsOn;

        // 先还原，确认后才真正切换（开启命令写进确认清单——设计文档交付约定 2）
        _toggling = true;
        ts.IsOn = !on;
        _toggling = false;

        var (title, boundary) = NovaPermissionStore.Describe(key);
        var dlg = new ContentDialog
        {
            Title = on ? $"授权开启「{title}」" : $"关闭「{title}」许可",
            Content = on
                ? $"开启后 Nova 将：{boundary}。\n\n此授权会记入可审计流水，可随时在这里关闭。"
                : "关闭后 Nova 立即停用该能力，关闭动作同样记入流水。",
            PrimaryButtonText = on ? "授权开启" : "确认关闭",
            CloseButtonText = "取消",
            DefaultButton = on ? ContentDialogButton.Primary : ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        await _s.NovaPermissions.SetAsync(key, on, _s.NovaLog);
        _toggling = true;
        ts.IsOn = on; // UI 开关同步到已确认状态（还原动作只是等待确认，不代表结果）
        _toggling = false;
        RefreshLog();
    }

    // ── 可审计流水 ──
    private void RefreshLog()
    {
        var entries = _s.NovaLog.Entries.Take(10).ToList();
        LogList.ItemsSource = entries;
        LogEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "清空动手流水",
            Content = "流水是「Nova 能动手但每次都能被看到干了啥」的凭证。清空后不可恢复，确定吗？",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.NovaLog.Clear();
        RefreshLog();
    }

    // ── LLM 降级链第 1 级：用户自填 Key ──
    private void LlmSave_Click(object sender, RoutedEventArgs e)
    {
        _s.Settings.NovaLlmBaseUrl = LlmBaseUrlBox.Text.Trim();
        _s.Settings.NovaLlmApiKey = LlmKeyBox.Password.Trim();
        _s.Settings.NovaLlmModel = LlmModelBox.Text.Trim();
        _s.Settings.Save();
        LlmSaveHint.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { LlmSaveHint.Visibility = Visibility.Collapsed; timer.Stop(); };
        timer.Start();
    }

    // ── 消息辅助 ──
    private void AddUser(string text) =>
        _messages.Add(new NovaChatMessage("user", text, Now()));

    private void AddAssistant(string text)
    {
        var msg = new NovaChatMessage("assistant", text, Now());
        _messages.Add(msg);
        ChatList.ScrollIntoView(msg);
    }

    private string GreetingText()
    {
        var s = _s.StarcloudId.Current!;
        return $"你好，{s.DisplayName}！我是 Nova——你说一句人话，我打理启动器里的脏活累活；所有落盘动作先给确认清单，点头才执行。\n" +
               "试试：崩了帮我看看 / 游戏体检 / 检查模组冲突 / 开箱即玩 / 找个整合包";
    }

    private static string Now() => DateTime.Now.ToString("HH:mm");

    private static string MaskKey(string key) =>
        key.Length <= 14 ? key : $"{key[..10]}…{key[^4..]}";
}

/// <summary>
/// 对话消息模型（Bubble/TextBrush/Align 按 Role 一次性求值）；
/// Plan 非空 = 确认清单卡，PlanState 跟踪 pending/done/cancelled/failed。
/// </summary>
public sealed record NovaChatMessage(
    string Role, string Content, string TimeText,
    NovaPlan? Plan = null, string PlanState = "pending", string ResultText = "")
{
    public HorizontalAlignment Align => Role == "user"
        ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    // 用户气泡右下角收口、AI 气泡左下角收口（Fluent 会话样式）
    public CornerRadius Corners => Role == "user"
        ? new CornerRadius(12, 12, 4, 12) : new CornerRadius(12, 12, 12, 4);

    public Brush Bubble => Role == "user"
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

    public Brush TextBrush => Role == "user"
        ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    public Visibility TextVisible => Plan is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasPlanVisible => Plan is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PlanButtonsVisible =>
        Plan is not null && PlanState == "pending" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlanStateVisible =>
        Plan is not null && PlanState != "pending" ? Visibility.Visible : Visibility.Collapsed;

    public string PlanStateText => PlanState switch
    {
        "done" => "✔ 已确认执行（已记入动手流水）" +
                  (string.IsNullOrEmpty(ResultText) ? "" : $"\n{ResultText}"),
        "cancelled" => "已取消，未做任何改动",
        "failed" => $"执行失败（已记入动手流水）：{ResultText}",
        _ => "",
    };
}
