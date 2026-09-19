// 登录对话框（账户页与启动前「就地登录」共用）：
// - 微软设备码登录
// - 第三方（Yggdrasil）账密登录
// 合规：鉴权 token 只进内存会话（AccountStore.SetSession），绝不落盘；
// 第三方密码只作参数传递，不落盘、不写日志。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public static class AuthDialogs
{
    /// <summary>
    /// 启动前共享流程：解析启动身份；会话内无凭证（token 不落盘）时弹对应登录框（微软设备码 /
    /// 第三方密码，地址预填只读），登录成功后重试解析。成功返回解析结果；
    /// 用户取消登录返回 null；无可用账户等错误原样抛出由调用方展示。
    /// </summary>
    public static async Task<ResolvedPlayer?> EnsurePlayableAccountAsync(
        XamlRoot root, AppServices s, Action<string>? onError = null)
    {
        while (true)
        {
            try
            {
                return await s.LaunchAccount.ResolveAsync();
            }
            catch (AccountLoginRequiredException ex)
            {
                var acc = s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == ex.Uuid);
                var ok = ex.Kind == "microsoft"
                    ? await MicrosoftDeviceCodeAsync(root, s.MicrosoftAuth, s.Accounts, onError) is not null
                    : await ExternalPasswordAsync(root, s.Yggdrasil, s.Accounts,
                        presetUrl: acc?.ServerAlias ?? acc?.ServerBase,
                        presetUsername: acc?.Username) is not null;
                if (!ok) return null;
            }
        }
    }

    /// <summary>
    /// 微软设备码登录弹窗（对话框内完成，一次性验证码）。成功返回登录结果
    /// （身份已写入账户存储、token 已写入内存会话）；取消或失败返回 null（失败已在弹窗/错误条提示）。
    /// </summary>
    public static async Task<MicrosoftAuthService.LoginResult?> MicrosoftDeviceCodeAsync(
        XamlRoot root, MicrosoftAuthService auth, AccountStore accounts, Action<string>? onError = null)
    {
        MicrosoftAuthService.DeviceCodeInfo? deviceCode;
        try
        {
            deviceCode = await auth.StartDeviceCodeAsync();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
            return null;
        }

        var completed = false;
        var code = deviceCode;
        var codeText = new TextBlock
        {
            Text = code.UserCode,
            FontSize = 32,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var statusText = new TextBlock
        {
            Text = Loc.T("等待授权…"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var openBtn = new Button
        {
            Content = Loc.T("复制并打开验证网站"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        openBtn.Click += async (_, _) =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            pkg.SetText(code.UserCode);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            openBtn.Content = Loc.T("已复制并打开");
            await Windows.System.Launcher.LaunchUriAsync(new Uri(code.VerificationUri));
        };

        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("使用一次性验证代码登录到你的 Microsoft 账户"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(codeText);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("使用浏览器打开 {0}，输入以上代码并按照网页提示完成授权。完成后启动器将自动把账户添加至启动器内。", code.VerificationUri),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(openBtn);
        panel.Children.Add(new ProgressRing { Width = 24, Height = 24, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(statusText);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("登录 Microsoft 账户"),
            Content = panel,
            CloseButtonText = Loc.T("取消"),
            XamlRoot = root,
        };
        CancellationTokenSource? pollCts = null;
        dlg.Closed += (_, _) =>
        {
            if (!completed) pollCts?.Cancel();
        };
        _ = dlg.ShowAsync(); // 对话框保持打开，后台轮询

        pollCts = new CancellationTokenSource();
        var ct = pollCts.Token;
        var dq = DispatcherQueue.GetForCurrentThread();
        var interval = Math.Max(deviceCode.Interval, 3);
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            try
            {
                var (state, message, result) = await auth.PollAsync(deviceCode, ct);
                if (state == "success" && result is not null)
                {
                    // 合规：身份落盘，token 只进内存会话（进程退出即失效）
                    accounts.AddOrUpdateMicrosoft(result.Uuid, result.Username);
                    accounts.SetSession(result.Uuid,
                        new AccountSession(result.AccessToken, result.RefreshToken, result.Xuid));
                    completed = true;
                    dq.TryEnqueue(dlg.Hide);
                    return result;
                }
                if (state == "error")
                {
                    completed = true;
                    dq.TryEnqueue(() =>
                    {
                        dlg.Hide();
                        onError?.Invoke(message);
                    });
                    return null;
                }
                consecutiveFailures = 0;
                dq.TryEnqueue(() => statusText.Text = Loc.T("等待授权…（{0}）", message));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            catch (Exception ex) when (++consecutiveFailures < 3)
            {
                // 网络波动：连续 3 次才终止
                LauncherLog.Write("auth",
                    $"轮询第 {consecutiveFailures} 次失败（继续重试）：{ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                LauncherLog.Write("auth", $"轮询终止：{ex.GetType().Name}: {ex.Message}");
                completed = true;
                dq.TryEnqueue(() =>
                {
                    dlg.Hide();
                    onError?.Invoke(ex.Message);
                });
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 第三方（Yggdrasil）账密登录弹窗。presetUrl/presetUsername 非空时预填且只读（就地登录场景）。
    /// 成功返回角色名（身份已写入账户存储、token 已写入内存会话）；取消或失败返回 null（失败已在弹窗内提示）。
    /// </summary>
    public static async Task<string?> ExternalPasswordAsync(
        XamlRoot root, YggdrasilService ygg, AccountStore accounts,
        string? presetUrl = null, string? presetUsername = null)
    {
        var urlBox = new TextBox
        {
            PlaceholderText = Loc.T("认证服务器地址"),
            Text = presetUrl ?? "",
            IsReadOnly = presetUrl is not null,
        };
        var nameBox = new TextBox
        {
            PlaceholderText = Loc.T("邮箱或用户名"),
            Text = presetUsername ?? "",
            IsReadOnly = presetUsername is not null,
        };
        var pwdBox = new PasswordBox { PlaceholderText = Loc.T("密码") };
        var errText = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        var statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = Visibility.Collapsed,
        };
        var progress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };
        var loginBtn = new Button
        {
            Content = Loc.T("登录"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        string? loggedInName = null;
        ContentDialog dlg = null!; // 先声明后赋值：Click 闭包需要引用
        loginBtn.Click += async (_, _) =>
        {
            errText.Visibility = Visibility.Collapsed;
            var url = urlBox.Text.Trim();
            var name = nameBox.Text.Trim();
            if (url.Length == 0 || name.Length == 0 || pwdBox.Password.Length == 0)
            {
                errText.Text = Loc.T("请填写认证服务器地址、用户名和密码");
                errText.Visibility = Visibility.Visible;
                return;
            }
            loginBtn.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            progress.IsActive = true;
            statusText.Visibility = Visibility.Visible;
            try
            {
                statusText.Text = Loc.T("正在解析认证服务器…");
                var server = await ygg.ResolveServerAsync(url);

                // clientToken 是设备标识（非鉴权 token）：同服务器同用户复用已存的，否则新生成
                var clientToken = accounts.Accounts.FirstOrDefault(a =>
                    a.Type == "external" && a.Username == name &&
                    (a.ServerBase == server.Base || a.ServerAlias == url))?.ClientToken;
                statusText.Text = Loc.T("正在登录…");
                var r = await ygg.AuthenticateAsync(server.Base, name, pwdBox.Password,
                    string.IsNullOrEmpty(clientToken) ? Guid.NewGuid().ToString("N") : clientToken!);

                accounts.AddOrUpdateExternal(r.ProfileId, r.ProfileName, server.Base, url, server.ServerName);
                accounts.SetSession(r.ProfileId, new AccountSession(r.AccessToken));
                pwdBox.Password = "";
                loggedInName = r.ProfileName;
                dlg.Hide();
            }
            catch (Exception ex)
            {
                errText.Text = Loc.T("登录失败：{0}", ex.Message);
                errText.Visibility = Visibility.Visible;
                loginBtn.IsEnabled = true;
                progress.Visibility = Visibility.Collapsed;
                progress.IsActive = false;
                statusText.Visibility = Visibility.Collapsed;
            }
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Loc.T("认证服务器地址") });
        panel.Children.Add(urlBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("用户名") });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("密码") });
        panel.Children.Add(pwdBox);
        panel.Children.Add(loginBtn);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { progress, statusText },
        });
        panel.Children.Add(errText);

        dlg = new ThemedDialog
        {
            Title = Loc.T("第三方登录"),
            Content = panel,
            CloseButtonText = Loc.T("取消"),
            XamlRoot = root,
        };
        await dlg.ShowAsync();
        return loggedInName;
    }
}
