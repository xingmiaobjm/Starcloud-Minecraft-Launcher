// 账户页：离线账户管理 + 微软设备码登录

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class AccountsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private MicrosoftAuthService.DeviceCodeInfo? _deviceCode;
    private CancellationTokenSource? _pollCts;

    public AccountsPage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        AccountList.ItemsSource = _s.Accounts.Accounts
            .Select(a => new AccountRow(a.Uuid, a.Username,
                a.Type == "microsoft" ? "微软正版" : "离线", a.IsDefault))
            .ToList();
    }

    private async void AddOffline_Click(object s, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length is < 3 or > 16 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            ErrorBar.Message = "玩家名只能包含字母、数字、下划线，长度 3-16";
            ErrorBar.IsOpen = true;
            return;
        }
        _s.Accounts.AddOffline(name);
        NameBox.Text = "";
        RefreshList();
    }

    private void SetDefault_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not string uuid) return;
        _s.Accounts.SetDefault(uuid);
        RefreshList();
    }

    private async void Remove_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not string uuid) return;
        var acc = _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        var dlg = new ContentDialog
        {
            Title = "删除账户",
            Content = $"确定要删除账户 {acc?.Username ?? uuid} 吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.Accounts.Remove(uuid);
        RefreshList();
    }

    private async void MsLogin_Click(object s, RoutedEventArgs e)
    {
        try
        {
            _deviceCode = await _s.MicrosoftAuth.StartDeviceCodeAsync();
            MsCodeText.Text = _deviceCode.UserCode;
            MsCodeText.Visibility = Visibility.Visible;
            MsStatusText.Text = $"打开 {_deviceCode.VerificationUri} 并输入以上代码";
            MsStatusText.Visibility = Visibility.Visible;
            MsBar.IsOpen = true;

            _pollCts?.Cancel();
            _pollCts = new CancellationTokenSource();
            await PollLoopAsync(_pollCts.Token);
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = Math.Max(_deviceCode?.Interval ?? 5, 3);
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            try
            {
                var (state, message, result) = await _s.MicrosoftAuth.PollAsync(_deviceCode!, ct);
                if (state == "success" && result is not null)
                {
                    _s.Accounts.AddOrUpdateMicrosoft(result.Uuid, result.Username,
                        result.AccessToken, result.RefreshToken);
                    _dq.TryEnqueue(() =>
                    {
                        MsBar.Message = $"已登录：{result.Username}";
                        MsBar.IsOpen = true;
                        MsCodeText.Visibility = Visibility.Collapsed;
                        RefreshList();
                    });
                    return;
                }
                if (state == "error")
                {
                    _dq.TryEnqueue(() =>
                    {
                        ErrorBar.Message = message;
                        ErrorBar.IsOpen = true;
                    });
                    return;
                }
                consecutiveFailures = 0;
                _dq.TryEnqueue(() => MsStatusText.Text = $"等待授权…（{message}）");
            }
            catch (Exception) when (++consecutiveFailures < 3)
            {
                // 网络波动：连续 3 次才终止
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _dq.TryEnqueue(() =>
                {
                    ErrorBar.Message = ex.Message;
                    ErrorBar.IsOpen = true;
                });
                return;
            }
        }
    }
}
