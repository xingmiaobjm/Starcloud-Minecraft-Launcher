// 登录对话框（只用于「启动游戏时就地登录」这个没有页面可承载表单的场景；
// 账户页添加账户走 AccountAddPage 的内联表单）：
// - 微软登录：走内置登录页（WebView 里完成验证）。内置页起不来就是登录失败，
//   **不做自动降级**；换登录方式由用户决定。设备码与轮询逻辑与桌面完全一致。
// - 第三方（Yggdrasil）账密登录（登录动作与账户页共用 AccountFlows）
// 合规：鉴权 token 只进内存会话（AccountStore.SetSession），绝不落盘；
// 第三方密码只作参数传递，不落盘、不写日志。
//
// 移植自桌面 Starcloud.App.Views.Dialogs.AuthDialogs.cs。唯一结构性差异：
// 桌面的内置登录是独立 Window（embedded.Activate / Window.Closed），安卓没有多窗口
// 语义，改为全屏覆盖页——PushOverlay 承载、PopOverlay 收走（finally 兜底），
// 关闭语义（Closed 事件 / UserClosed / InitFailed）保持一致。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Controls;
using Starcloud.Launcher.Uno.Views.Shared;

namespace Starcloud.Launcher.Uno.Views.Dialogs;

public static class AuthDialogs
{
    /// <summary>同一时刻只允许一个内置登录页（覆盖页可以重复叠加，重复点「登录」会叠页）</summary>
    private static bool _loginWindowOpen;

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
                    : await ExternalPasswordAsync(root, presetUrl: acc?.ServerAlias ?? acc?.ServerBase,
                        presetUsername: acc?.Username) is not null;
                if (!ok) return null;
            }
        }
    }

    /// <summary>
    /// 微软设备码登录弹窗（内置登录页内完成，一次性验证码）。成功返回登录结果
    /// （身份已写入账户存储、token 已写入内存会话）；取消或失败返回 null（失败已在页面/错误条提示）。
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

        var dq = DispatcherQueue.GetForCurrentThread();

        // ── 登录方式：内置登录页（WebView 内完成验证，不跳系统浏览器）。
        //    内置页起不来就是登录失败（明说原因），不偷偷换成外置流程。
        //    设备码与轮询逻辑唯一，区别只在「用户在哪一步完成授权」。 ──
        if (AppServices.Instance.Settings.MicrosoftLoginEmbedded)
        {
            if (_loginWindowOpen)
            {
                LauncherLog.Write("auth", "已有登录窗口在等待授权，忽略重复的登录请求");
                return null;
            }
            var embedded = new MsLoginDialog(deviceCode);
            _loginWindowOpen = true;
            AppNavigation.PushOverlay(embedded);
            try
            {
                if (await embedded.Ready)
                {
                    using var winCts = new CancellationTokenSource();
                    var settled = false;
                    // 用户关页 = 取消这次登录（只取消轮询，不再弹任何窗口）
                    embedded.Closed += (_, _) => { if (!settled) winCts.Cancel(); };
                    try
                    {
                        var viaWindow = await PollDeviceCodeAsync(
                            auth, accounts, deviceCode, embedded.SetStatus, onError, winCts.Token);
                        settled = true;
                        embedded.Finish(); // 收尾关页（用户已自己关掉时无事发生）
                        return viaWindow;
                    }
                    finally
                    {
                        settled = true;
                    }
                }
                // 用户先关掉了页面：就是取消登录
                if (embedded.UserClosed) return null;
                // 页面没起来 = 这次登录失败。不降级到浏览器：失败得说清楚原因。
                LauncherLog.Write("auth", $"内置登录页无法启动（InitFailed={embedded.InitFailed}），按登录失败处理");
                onError?.Invoke(Loc.T(
                    "内置登录页无法启动，可能是系统 WebView 不可用。请改用浏览器方式登录（去「设置 → 通用 → 使用内置登录窗口」关闭它）。"));
                return null;
            }
            catch (Exception ex)
            {
                LauncherLog.Write("auth", $"内置登录页异常：{ex.GetType().Name}: {ex.Message}");
                onError?.Invoke(Loc.T(
                    "内置登录页无法启动：{0}。请改用浏览器方式登录（去「设置 → 通用 → 使用内置登录窗口」关闭它）。",
                    ex.Message));
                return null;
            }
            finally
            {
                _loginWindowOpen = false;
                AppNavigation.PopOverlay(); // 覆盖页收走（用户已自己关掉时是空操作）
            }
        }
        else
        {
            LauncherLog.Write("auth", "设置里已关闭内置登录页，使用浏览器方式");
        }

        // ── 回退：复制验证码 + 系统浏览器 ──
        var completed = false;
        var code = deviceCode;
        var codeText = new TextBlock
        {
            Text = code.UserCode,
            FontSize = 32,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            FontFamily = new FontFamily("monospace"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var statusText = new TextBlock
        {
            Text = Loc.T("等待授权…"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var openBtn = new GlowButton
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
        using var pollCts = new CancellationTokenSource();
        dlg.Closed += (_, _) => { if (!completed) pollCts.Cancel(); };
        _ = dlg.ShowAsync(); // 对话框保持打开，后台轮询

        var fallbackResult = await PollDeviceCodeAsync(
            auth, accounts, deviceCode, t => statusText.Text = t, onError, pollCts.Token);
        completed = true;
        dq.TryEnqueue(dlg.Hide);
        return fallbackResult;
    }

    /// <summary>
    /// 设备码轮询：等待用户在（内置页或浏览器里）完成授权，成功后兑现令牌并写入账户存储。
    /// <para>两条路径（内置页 / 浏览器回退）共用这一段：轮询到成功返回结果，
    /// 用户取消（<paramref name="ct"/> 被取消）或失败返回 null，失败原因走 <paramref name="onError"/>。
    /// 界面收尾（关页面 / 关弹窗）由调用方负责——这里只管协议。</para>
    /// </summary>
    private static async Task<MicrosoftAuthService.LoginResult?> PollDeviceCodeAsync(
        MicrosoftAuthService auth, AccountStore accounts, MicrosoftAuthService.DeviceCodeInfo deviceCode,
        Action<string> setStatus, Action<string>? onError, CancellationToken ct)
    {
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
                    return result;
                }
                if (state == "error")
                {
                    onError?.Invoke(message);
                    return null;
                }
                consecutiveFailures = 0;
                setStatus(Loc.T("等待授权…（{0}）", message));
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
                onError?.Invoke(ex.Message);
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 第三方（Yggdrasil）账密登录弹窗。presetUrl/presetUsername 非空时预填且只读（就地登录场景）。
    /// 只在「启动游戏时就地登录」用（那个场景没有可承载表单的页面）；账户页添加账户走 AccountAddPage。
    /// 成功返回角色名（身份已写入账户存储、token 已写入内存会话）；取消或失败返回 null（失败已在弹窗内提示）。
    /// </summary>
    public static async Task<string?> ExternalPasswordAsync(
        XamlRoot root, string? presetUrl = null, string? presetUsername = null)
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
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
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
                statusText.Text = Loc.T("正在登录…");
                // 与账户页「添加账户」二级页共用同一段登录动作，避免两处实现漂移
                var profileName = await AccountFlows.LoginExternalAsync(
                    AppServices.Instance, url, name, pwdBox.Password);
                pwdBox.Password = "";
                loggedInName = profileName;
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
