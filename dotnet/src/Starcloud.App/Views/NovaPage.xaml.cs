// Nova 页（NOVA_DESIGN.md v1.0）：Nova 的「大本营」——登录 / 能力阶段条 / 动手许可层
// 三档开关（开启先弹确认）/ 可审计流水 / LLM 降级链设置。
// 对话区复用 NovaChatView（与顶栏面板 / 底部对话条共用 NovaRuntime 同一份消息流），
// 本页不再持有会话状态；登录 / 退出通过 NovaRuntime.NotifyLoginChanged 同步全局。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class NovaPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private bool _toggling; // 代码设置开关状态时抑制 Toggled 确认弹窗

    public NovaPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _s.NovaRuntime.LogChanged -= OnLogChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 确认执行 / 取消 / 授权开关都会写流水 → 右栏跟着刷新
        _s.NovaRuntime.LogChanged += OnLogChanged;
        UpdateAuthState();
    }

    private void OnLogChanged() => DispatcherQueue.TryEnqueue(RefreshLog);

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

    // ── 登录 / 退出（登录态变化 → NovaRuntime 清空全局消息流并重新问候） ──
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
            _s.NovaRuntime.NotifyLoginChanged();
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
        _s.NovaRuntime.NotifyLoginChanged();
        UpdateAuthState();
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

    private static string MaskKey(string key) =>
        key.Length <= 14 ? key : $"{key[..10]}…{key[^4..]}";
}
