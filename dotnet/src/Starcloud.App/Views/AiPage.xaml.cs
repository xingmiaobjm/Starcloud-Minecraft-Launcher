// AI 助手页：Starcloud ID 登录（登录后自动创建云 AI API Key）+ 对话式能力入口。
// 测试通道由本地规则引擎应答；云端通道调用 /v1/ai/chat（服务未部署时自动回退）。

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class AiPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly ObservableCollection<AiChatMessage> _messages = [];
    private readonly List<AiChatTurn> _history = [];
    private bool _busy;

    public AiPage()
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
        if (session is { } s)
        {
            UserTitle.Text = s.DisplayName;
            UserSub.Text = $"{s.UserId} · {(s.Channel == StarcloudIdService.CloudChannel ? "云端通道" : "测试通道")}";
            KeyText.Text = MaskKey(s.ApiKey);
        }
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
            var session = await _s.StarcloudId.LoginAsync(id, PwdBox.Password, channel);
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
        // 云端通道提示：服务未部署，登录将失败（协议已预留）
        PwdBox.PlaceholderText = ChannelBox.SelectedIndex == 1
            ? "云端通道密码"
            : "测试通道任意内容即可（至少 4 位）";
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "退出登录",
            Content = "退出后将删除本地保存的会话与 API Key，重新登录会再次创建。确定退出吗？",
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
        try
        {
            var result = await _s.Ai.ChatAsync(input, _s.Settings, _history);
            AddAssistant(result.Text);
            _history.Add(new AiChatTurn("user", input));
            _history.Add(new AiChatTurn("assistant", result.Text));
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

    private void AddUser(string text) =>
        AddMessage(new AiChatMessage("user", text, DateTime.Now.ToString("HH:mm")));

    private void AddAssistant(string text) =>
        AddMessage(new AiChatMessage("assistant", text, DateTime.Now.ToString("HH:mm")));

    private void AddMessage(AiChatMessage message)
    {
        _messages.Add(message);
        ChatList.ScrollIntoView(message);
    }

    private string GreetingText()
    {
        var s = _s.StarcloudId.Current!;
        return $"欢迎回来，{s.DisplayName}！云 AI 助手已就绪（{(s.Channel == StarcloudIdService.CloudChannel ? "云端通道" : "测试通道")}）。\n" +
               $"API Key 已在后台为你创建：{MaskKey(s.ApiKey)}\n" +
               "我可以帮你：查找整合包 / 游戏体检与自动修复 / 模组冲突检测与自动处理。试试下方快捷按钮。";
    }

    // ── API Key 展示与复制 ──
    private static string MaskKey(string key) =>
        key.Length <= 14 ? key : $"{key[..10]}…{key[^4..]}";

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (_s.StarcloudId.Current is not { } s || sender is not Button btn) return;
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        pkg.SetText(s.ApiKey);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

        var original = btn.Content;
        btn.Content = "已复制";
        btn.IsEnabled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            btn.Content = original;
            btn.IsEnabled = true;
            timer.Stop();
        };
        timer.Start();
    }
}

/// <summary>对话气泡模型（Bubble/TextBrush/Align 按 Role 一次性求值）</summary>
public sealed record AiChatMessage(string Role, string Content, string TimeText)
{
    public HorizontalAlignment Align => Role == "user"
        ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Brush Bubble => Role == "user"
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

    public Brush TextBrush => Role == "user"
        ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
}
