// Nova 设置页（原聊天区已移除——对话入口在顶栏 NovaOrb 面板 / 底部 NovaBar，共用 NovaRuntime 消息流）：
// 账户（登录 / 退出）/ 模型设置（第三方 LLM 添加 + 多模型档案切换）/ 游戏内辅助浮窗 /
// 游戏记录记忆（战报 / 时刻统计）/ 动手许可层（开启先弹确认）/ 可审计的操作日志 / 能力路线。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Dialogs;
using Starcloud.Core.Services;
using System.Diagnostics;
using System.IO;

namespace Starcloud.App.Views;

public sealed partial class NovaPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private bool _toggling; // 代码设置开关状态时抑制 Toggled 确认弹窗
    private NovaQuota? _quota; // 最近一次拿到的额度快照（主题切换 / 开关切换时重画用）

    // 自检按钮用的共享 HttpClient（低频手动请求）；_llmProbedModels = 上次自检列出的模型，
    // 保存时拿来校验模型名（写错模型名是 400 的头号原因）。
    private static readonly HttpClient LlmProbeHttp = new();
    private List<string> _llmProbedModels = [];

    public NovaPage()
    {
        InitializeComponent();
        // 输入控件 Header 无法用 XAML 附加属性本地化（Loc.Header 仅支持 ToggleSwitch/Expander）→ 在此赋值
        PwdBox.Header = Loc.T("密码");
        ChannelBox.Header = Loc.T("通道");
        LlmPresetBox.Header = Loc.T("服务商预设");
        LlmBaseUrlBox.Header = Loc.T("接入地址（Base URL，OpenAI 兼容）");
        ProfileNameBox.Header = Loc.T("备注名");
        LlmModelBox.Header = Loc.T("模型");
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _s.NovaRuntime.LogChanged -= OnLogChanged;
            _s.Recording.StatusChanged -= OnRecordingStatusChanged;
            NovaGatewayClient.QuotaChanged -= OnQuotaChanged;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
    // 确认执行 / 取消 / 授权开关都会写日志 → 日志卡片跟着刷新
        _s.NovaRuntime.LogChanged += OnLogChanged;
        _s.Recording.StatusChanged += OnRecordingStatusChanged;
        NovaGatewayClient.QuotaChanged += OnQuotaChanged; // 任何一次云端调用带回的额度都即时反映到卡片
        UpdateAccountState();

        // 许可层开关回显（默认关闭；代码设置期间抑制 Toggled）
        _toggling = true;
        RecordingSwitch.IsOn = _s.NovaPermissions.IsOn(NovaPermissionStore.Recording);
        OverlaySwitch.IsOn = _s.Settings.NovaOverlayEnabled;
        GatewaySwitch.IsOn = _s.Settings.NovaGatewayEnabled;
        _toggling = false;

        RefreshProfiles();
        RefreshMemory();
        RefreshLog();
        RefreshRecording();

        // 主题切换后芯片配色要按新主题重取（画刷是查表得来的，不会自己跟着换）
        ActualThemeChanged += (_, _) => ApplyQuota(_quota);
    }

    private void OnLogChanged() => DispatcherQueue.TryEnqueue(RefreshLog);

    // ── 账户：登录表单 / 已登录信息切换（登录态变化 → NovaRuntime 清空全局消息流并重新问候） ──
    private void UpdateAccountState()
    {
        var session = _s.StarcloudId.Current;
        LoginForm.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        AccountInfo.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (session is { } s)
            UserSub.Text = $"{s.UserId} · {Loc.T(s.Channel == StarcloudIdService.CloudChannel ? "云端通道" : "测试通道")} · Key {MaskKey(s.ApiKey)}";
        ApplyQuota(_s.Settings.CachedQuota()); // 先显示上次数值，再异步刷新
        MaybeRefreshQuota();
    }

    // ── 云端模型额度：显示 + 刷新（数值以服务端为准，本地只缓存上次结果） ──

    /// <summary>任何一次云端调用带回的额度都即时反映到卡片（不用等用户点刷新）。</summary>
    private void OnQuotaChanged(NovaQuota quota)
    {
        _s.Settings.CacheQuota(quota);
        _s.Settings.Save();
        DispatcherQueue.TryEnqueue(() => ApplyQuota(quota));
    }

    private async void QuotaRefresh_Click(object sender, RoutedEventArgs e) => await RefreshQuotaAsync();

    /// <summary>进页面时刷新一次；缓存不到 1 分钟就直接用缓存，省掉一次网关往返。</summary>
    private void MaybeRefreshQuota()
    {
        // 接了第三方模型：云端不是当前通道 → 一次额度请求都不发
        if (!NovaGatewayClient.IsQuotaRelevant(_s.Settings, _s.StarcloudId.Current)) return;
        var fresh = _s.Settings.NovaQuotaCheckedAt is { } at
            && DateTimeOffset.Now - at < TimeSpan.FromMinutes(1);
        if (!fresh) _ = RefreshQuotaAsync();
    }

    /// <summary>取本周额度：没有可用会话就不发请求（只更新说明文字）。</summary>
    private async Task RefreshQuotaAsync()
    {
        var session = _s.StarcloudId.Current;
        if (!NovaGatewayClient.IsQuotaRelevant(_s.Settings, session))
        {
            ApplyQuota(_s.Settings.CachedQuota());
            return;
        }

        QuotaRefreshBtn.IsEnabled = false;
        try
        {
            var quota = await NovaGatewayClient.FetchQuotaAsync(session!.ApiKey);
            ApplyQuota(quota ?? _s.Settings.CachedQuota()); // 拉不到就保留上次数值，不显示成 0
        }
        finally
        {
            QuotaRefreshBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 额度卡渲染：能显示数值就显示数值，不能就说明为什么（未登录 / 测试通道）。
    /// 进度条按已用比例填充，剩余量用芯片表达，低于 20% 换提示色、用尽换危险色。
    /// </summary>
    private void ApplyQuota(NovaQuota? quota)
    {
        _quota = quota;
        var session = _s.StarcloudId.Current;

        if (session is null)
        {
            ShowQuotaHint("还没登录 Starcloud ID：登录后 Nova 走云端模型，额度记在账号上。");
            return;
        }
        if (session.Channel != StarcloudIdService.CloudChannel)
        {
            ShowQuotaHint("测试通道是本地模拟账号，不连云端也没有额度；切到「云端通道」登录即可用云端模型。");
            return;
        }

        // 接了第三方模型：云端不是当前通道，额度既不查询也不显示
        if (NovaGatewayClient.HasOwnModel(_s.Settings))
        {
            ShowQuotaHint("已接入你自己的模型：Nova 优先用它，云端模型只在它不可用时兜底，所以这里不显示云端额度。");
            return;
        }

        QuotaHintText.Visibility = Visibility.Collapsed;
        QuotaBody.Visibility = Visibility.Visible;

        if (quota is not { Limit: > 0 } q)
        {
            QuotaUsedText.Text = Loc.T("还没取到额度数值（可点右上角刷新）");
            QuotaRemainingText.Text = Loc.T("未知");
            QuotaBar.Value = 0;
            QuotaBar.Visibility = Visibility.Collapsed; // 没有数值时不留一条空进度槽
            QuotaResetText.Text = NovaGatewayClient.ResetText(null);
            SetQuotaChip(QuotaChipTone.Unknown);
            return;
        }

        QuotaBar.Visibility = Visibility.Visible;
        QuotaUsedText.Text = NovaGatewayClient.UsedText(q);
        QuotaRemainingText.Text = NovaGatewayClient.RemainingText(q);
        QuotaBar.Value = Math.Round(q.UsedRatio * 100, 1);
        var checkedText = NovaGatewayClient.CheckedText(_s.Settings.NovaQuotaCheckedAt);
        QuotaResetText.Text = NovaGatewayClient.ResetText(q.ResetAt)
            + (checkedText.Length > 0 ? " · " + checkedText : "");
        SetQuotaChip(q.Exhausted ? QuotaChipTone.Empty
            : q.RemainingRatio < 0.2 ? QuotaChipTone.Low
            : QuotaChipTone.Normal);
    }

    private void ShowQuotaHint(string text)
    {
        QuotaBody.Visibility = Visibility.Collapsed;
        QuotaHintText.Text = Loc.T(text);
        QuotaHintText.Visibility = Visibility.Visible;
    }

    private enum QuotaChipTone { Normal, Low, Empty, Unknown }

    /// <summary>剩余量芯片配色：充足=品牌色，偏低=提示色，用尽=危险色（白字）。</summary>
    private void SetQuotaChip(QuotaChipTone tone)
    {
        var (background, foreground) = tone switch
        {
            QuotaChipTone.Normal => (
                LookupBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 245, 158, 11)),
                LookupBrush("TextOnAccentFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 255, 255, 255))),
            QuotaChipTone.Low => (
                LookupBrush("CautionChipBackgroundBrush", Windows.UI.Color.FromArgb(31, 245, 158, 11)),
                LookupBrush("CautionChipTextBrush", Windows.UI.Color.FromArgb(255, 180, 83, 9))),
            QuotaChipTone.Empty => (
                LookupBrush("DangerBrush", Windows.UI.Color.FromArgb(255, 244, 67, 54)),
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))),
            _ => (
                LookupBrush("SubtleFillColorTertiaryBrush", Windows.UI.Color.FromArgb(20, 128, 128, 128)),
                LookupBrush("TextFillColorSecondaryBrush", Windows.UI.Color.FromArgb(255, 110, 110, 110))),
        };
        QuotaChip.Background = background;
        QuotaRemainingText.Foreground = foreground;
    }

    /// <summary>
    /// 取主题画刷：直接 <c>Application.Current.Resources[key]</c> 不走 ThemeDictionaries 的当前主题解析，
    /// 所以先按 ActualTheme 查对应字典，再退回直接查找（与 LogsPage 同口径）。
    /// </summary>
    private Brush LookupBrush(string key, Windows.UI.Color fallback)
    {
        var res = Application.Current.Resources;
        var themeKey = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (res.ThemeDictionaries.TryGetValue(themeKey, out var themeObj) &&
            themeObj is ResourceDictionary theme &&
            theme.TryGetValue(key, out var themed) && themed is Brush themedBrush)
            return themedBrush;
        return res[key] as Brush ?? new SolidColorBrush(fallback);
    }

    /// <summary>「使用云端模型」开关：关闭后 Nova 只用自接模型（额度仍在账号上）。</summary>
    private void Gateway_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        _s.Settings.NovaGatewayEnabled = GatewaySwitch.IsOn;
        _s.Settings.Save();
        ApplyQuota(_quota);
    }

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
            UpdateAccountState();
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
            ? Loc.T("云端通道密码")
            : Loc.T("测试通道任意内容即可（至少 4 位）");
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ThemedDialog
        {
            Title = "退出登录",
            Content = "退出后将删除本地保存的会话与 API Key。Nova 的操作日志会保留。确定退出吗？",
            PrimaryButtonText = "退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.StarcloudId.Logout();
        _s.NovaRuntime.NotifyLoginChanged();
        UpdateAccountState();
    }

    // ── 模型设置：第三方 LLM 添加 + 多模型档案（添加 / 切换 / 删除） ──
    // 服务商预设模板：选预设自动带出接入地址与模型列表，只需填 Key 选模型（主流 OpenAI 兼容端点）
    private sealed record NovaLlmPreset(string Name, string BaseUrl, bool KeyRequired, string[] Models);

    private static readonly NovaLlmPreset[] LlmPresets =
    [
        new("DeepSeek", "https://api.deepseek.com/v1", true,
            ["deepseek-chat", "deepseek-reasoner"]),
        new("OpenAI", "https://api.openai.com/v1", true,
            ["gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "o4-mini"]),
        new("通义千问 Qwen（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1", true,
            ["qwen-plus", "qwen-turbo", "qwen-max", "qwq-plus"]),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", true,
            ["glm-4-plus", "glm-4-air", "glm-4-flash", "glm-4.5"]),
        new("Moonshot Kimi", "https://api.moonshot.cn/v1", true,
            ["kimi-k2-0905-preview", "moonshot-v1-128k", "moonshot-v1-32k", "moonshot-v1-8k"]),
        new("OpenRouter", "https://openrouter.ai/api/v1", true,
            ["openai/gpt-4o-mini", "anthropic/claude-sonnet-4", "google/gemini-2.5-flash", "deepseek/deepseek-chat"]),
        new("硅基流动 SiliconFlow", "https://api.siliconflow.cn/v1", true,
            ["deepseek-ai/DeepSeek-V3.1", "deepseek-ai/DeepSeek-R1", "Qwen/Qwen2.5-72B-Instruct"]),
        new("Ollama（本机，无需 Key）", "http://127.0.0.1:11434/v1", false,
            ["qwen3", "llama3.1", "deepseek-r1"]),
        new("自定义（手填接入地址）", "", true, []),
    ];

    private void RefreshProfiles()
    {
        var activeId = _s.Settings.ActiveLlmProfile()?.Id;
        ProfileList.ItemsSource = _s.Settings.NovaLlmProfiles
            .Select(p => new NovaLlmProfileRow(
                p.Id,
                string.IsNullOrWhiteSpace(p.Name) ? p.Model : p.Name,
                $"{p.BaseUrl} · {p.Model} · Key {MaskKey(p.ApiKey)}",
                p.Id == activeId))
            .ToList();
        ProfileEmptyText.Visibility = _s.Settings.NovaLlmProfiles.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // 「新增模型」展开预设表单（默认选第一个预设 DeepSeek，自动带出地址与模型列表）
    private void LlmAddOpen_Click(object sender, RoutedEventArgs e)
    {
        LlmAddPanel.Visibility = Visibility.Visible;
        LlmAddOpenBtn.Visibility = Visibility.Collapsed;
        ResetLlmProbe();
        if (LlmPresetBox.Items.Count == 0)
        {
            foreach (var p in LlmPresets) LlmPresetBox.Items.Add(p.Name);
            LlmPresetBox.SelectedIndex = 0;
        }
    }

    // 选预设 → 自动带出备注名 / 接入地址 / 模型下拉（可手输，自定义时改为手填地址）
    private void LlmPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = LlmPresetBox.SelectedIndex;
        if (idx < 0 || idx >= LlmPresets.Length) return;
        var preset = LlmPresets[idx];
        var custom = preset.BaseUrl.Length == 0;
        ResetLlmProbe(); // 换了服务商 = 换了地址，上一次的自检结果作废

        ProfileNameBox.Text = custom ? "" : PresetShortName(preset.Name);
        PresetUrlText.Text = $"接入地址：{preset.BaseUrl}";
        PresetUrlText.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        LlmBaseUrlBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom) LlmBaseUrlBox.Text = "";
        LlmKeyBox.PlaceholderText = preset.KeyRequired ? "sk-…" : "本机 Ollama 无需 Key，留空即可";

        LlmModelBox.Items.Clear();
        foreach (var m in preset.Models) LlmModelBox.Items.Add(m);
        LlmModelBox.Text = preset.Models.Length > 0 ? preset.Models[0] : "";
        LlmModelBox.PlaceholderText = preset.Models.Length > 0
            ? "从列表选择或手输模型名"
            : "填写模型名，例如 my-model";
    }

    private void LlmAdd_Click(object sender, RoutedEventArgs e)
    {
        var idx = LlmPresetBox.SelectedIndex;
        var preset = idx >= 0 && idx < LlmPresets.Length ? LlmPresets[idx] : null;
        // 归一化：补协议、去掉误粘的 /chat/completions、根路径补 /v1（本机 Ollama 手输 127.0.0.1:11434 也能用）
        var baseUrl = LlmEndpoint.NormalizeBaseUrl(
            preset is { BaseUrl.Length: > 0 } ? preset.BaseUrl : LlmBaseUrlBox.Text);
        var key = LlmKeyBox.Password.Trim();
        var model = LlmModelBox.Text.Trim();

        if (baseUrl.Length == 0) { ShowLlmHint("请填写接入地址（Base URL）"); return; }
        if (model.Length == 0) { ShowLlmHint("请选择或填写模型名"); return; }
        if (key.Length == 0 && preset is { KeyRequired: true })
        {
            ShowLlmHint("请填入该服务商的 API Key");
            return;
        }
        if (key.Length == 0) key = "local-ollama"; // Ollama 本机无需 Key，占位保证链路一致
        // 自检过且模型名不在列表里 → 保存后必须再提醒一次（模型名写错是 400 的头号原因）
        var badModel = _llmProbedModels.Count > 0 && !_llmProbedModels.Contains(model);

        var profile = new NovaLlmProfile
        {
            Name = ProfileNameBox.Text.Trim(),
            BaseUrl = baseUrl,
            ApiKey = key,
            Model = model,
        };
        var first = _s.Settings.NovaLlmProfiles.Count == 0;
        _s.Settings.NovaLlmProfiles.Add(profile);
        if (first) _s.Settings.NovaLlmActiveId = profile.Id; // 第一个自动启用
        _s.Settings.Save();

        LlmAddPanel.Visibility = Visibility.Collapsed;
        LlmAddOpenBtn.Visibility = Visibility.Visible;
        LlmKeyBox.Password = "";
        LlmTestHint.Visibility = Visibility.Collapsed; // 面板收起，自检提示跟着收
        ShowLlmHint(badModel
            ? $"已保存，但这个地址上没有「{model}」；可用：{string.Join(" / ", _llmProbedModels)}"
            : first ? "已添加并启用（当前使用）"
            : $"已添加「{(profile.Name.Length > 0 ? profile.Name : profile.Model)}」，点「设为当前」启用");
        RefreshProfiles();
    }

    /// <summary>
    /// 「测试连接」：拉一次该地址的模型列表，把地址 / Key / 模型名三个问题一次性暴露出来。
    /// 接本机模型（Ollama、LM Studio）最容易错的就是这三样，而以前错了只会静默降级。
    /// </summary>
    private async void LlmTest_Click(object sender, RoutedEventArgs e)
    {
        var preset = LlmPresetBox.SelectedIndex is >= 0 && LlmPresetBox.SelectedIndex < LlmPresets.Length
            ? LlmPresets[LlmPresetBox.SelectedIndex]
            : null;
        var baseUrl = LlmEndpoint.NormalizeBaseUrl(
            preset is { BaseUrl.Length: > 0 } ? preset.BaseUrl : LlmBaseUrlBox.Text);
        var key = LlmKeyBox.Password.Trim();

        if (baseUrl.Length == 0) { ShowLlmTestHint("请先填写接入地址（Base URL）", ok: false); return; }

        LlmTestBtn.IsEnabled = false;
        ShowLlmTestHint("正在连接…", ok: null);
        try
        {
            var result = await LlmEndpoint.ProbeAsync(LlmProbeHttp, baseUrl, key);
            _llmProbedModels = [.. result.Models];
            if (!result.Ok)
            {
                ShowLlmTestHint(result.Message, ok: false);
                return;
            }

            // 列出模型 → 直接用下拉选，不靠手打（模型名写错是 400 的头号原因）
            var typed = LlmModelBox.Text.Trim();
            LlmModelBox.Items.Clear();
            foreach (var m in result.Models) LlmModelBox.Items.Add(m);
            LlmModelBox.Text = typed.Length > 0 && result.Models.Contains(typed) ? typed
                : result.Models.Count > 0 ? result.Models[0]
                : typed;
            LlmBaseUrlBox.Text = baseUrl;

            var badModel = typed.Length > 0 && !result.Models.Contains(typed) && result.Models.Count > 0;
            ShowLlmTestHint(badModel
                ? $"连接成功，但没有「{typed}」这个模型；可用的有：{string.Join(" / ", result.Models)}"
                : result.Message, ok: !badModel);
        }
        catch (Exception ex)
        {
            ShowLlmTestHint($"自检失败：{ex.Message}", ok: false);
        }
        finally
        {
            LlmTestBtn.IsEnabled = true;
        }
    }

    /// <summary>自检结果：成功绿 / 失败红 / 进行中次要色（不自动消失，用户要能照着改）。</summary>
    private void ShowLlmTestHint(string text, bool? ok)
    {
        LlmTestHint.Text = text;
        LlmTestHint.Visibility = Visibility.Visible;
        var brushKey = ok switch
        {
            true => "SystemFillColorSuccessBrush",
            false => "SystemFillColorCriticalBrush",
            null => "TextFillColorSecondaryBrush",
        };
        if (Application.Current.Resources.TryGetValue(brushKey, out var brush) && brush is Brush b)
            LlmTestHint.Foreground = b;
    }

    private void LlmAddCancel_Click(object sender, RoutedEventArgs e)
    {
        LlmAddPanel.Visibility = Visibility.Collapsed;
        LlmAddOpenBtn.Visibility = Visibility.Visible;
        LlmKeyBox.Password = "";
        ResetLlmProbe();
    }

    /// <summary>清掉上一次自检的模型列表与提示（换服务商 / 重开面板时调用）。</summary>
    private void ResetLlmProbe()
    {
        _llmProbedModels = [];
        LlmTestHint.Visibility = Visibility.Collapsed;
        LlmTestHint.Text = "";
    }

    // 备注名取服务商名第一段：「通义千问 Qwen（阿里云百炼）」→「通义千问」
    private static string PresetShortName(string name)
    {
        var cut = name.IndexOfAny([' ', '（']);
        return cut > 0 ? name[..cut] : name;
    }

    private void ProfileActivate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        _s.Settings.NovaLlmActiveId = id;
        _s.Settings.Save();
        ShowLlmHint("已切换当前模型");
        RefreshProfiles();
    }

    private async void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var dlg = new ThemedDialog
        {
            Title = "删除模型",
            Content = "删除后该模型的 API Key 也会从本机移除，确定吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var profile = _s.Settings.NovaLlmProfiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        _s.Settings.NovaLlmProfiles.Remove(profile);
        if (_s.Settings.NovaLlmActiveId == id)
            _s.Settings.NovaLlmActiveId = _s.Settings.NovaLlmProfiles.FirstOrDefault()?.Id;
        _s.Settings.Save();
        ShowLlmHint("已删除");
        RefreshProfiles();
    }

    private void ShowLlmHint(string text)
    {
        LlmSaveHint.Text = text;
        LlmSaveHint.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { LlmSaveHint.Visibility = Visibility.Collapsed; timer.Stop(); };
        timer.Start();
    }

    // ── 游戏内辅助浮窗（nova_overlay_enabled：默认关 = 开启即显式授权） ──
    private void Overlay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        _s.Settings.NovaOverlayEnabled = OverlaySwitch.IsOn;
        _s.Settings.Save();
    }

    // ── 游戏记录记忆：战报 / 时刻统计 ──
    private void RefreshMemory()
    {
        MemoryStatsText.Text =
            $"已记录对局 {_s.NovaMoments.Reports.Count} 场 · 收藏时刻 {_s.NovaMoments.Moments.Count} 个";
        var last = _s.NovaMoments.LastReport;
        MemoryReportText.Text = last is null
            ? "暂无回顾。完成一局后会自动生成。"
            : _s.NovaMoments.BuildReportText(last);
    }

    // ── 动手许可层：开启 = 显式授权（确认弹窗 + 记入操作日志） ──
    private async void Permission_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toggling || sender is not ToggleSwitch { Tag: string key } ts) return;
        var on = ts.IsOn;

        // 先还原，确认后才真正切换（开启命令写进确认清单——设计文档交付约定 2）
        _toggling = true;
        ts.IsOn = !on;
        _toggling = false;

        var (title, boundary) = NovaPermissionStore.Describe(key);
        var dlg = new ThemedDialog
        {
            Title = on ? $"授权开启「{title}」" : $"关闭「{title}」许可",
            Content = on
                ? $"开启后 Nova 将：{boundary}。\n\n此授权会记入操作日志，可随时在这里关闭。"
                : "关闭后 Nova 立即停用该能力，关闭动作同样记入操作日志。",
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

    // ── 录屏：许可开关 + 手动起停 + 状态播报 ──
    private void OnRecordingStatusChanged() => DispatcherQueue.TryEnqueue(RefreshRecording);

    private void RefreshRecording()
    {
        var status = _s.Recording.Snapshot();
        RecordNowBtn.Content = status.Active ? Loc.T("停止录屏") : Loc.T("立即录屏");
        RecordingStatusText.Text = status.Active
            ? status.Describe()
            : BuildIdleRecordingText();
    }

    private string BuildIdleRecordingText()
    {
        if (!RecordingService.IsSupported) return Loc.T("这台系统不支持硬件录屏（需要 Windows 10 1903 及以上）。");
        var encoders = _s.Recording.HardwareEncoders;
        // 编码器名字有十几个（NVIDIA/Intel 各自的 HEVC/H264/AV1…），整串铺在卡片里是一堵墙；
        // 这里只报"有几个"，完整清单本来就会写进启动器日志（[record] 硬件编码器：…）。
        var encodersNote = encoders.Count > 0
            ? Loc.T("检测到 {0} 个硬件编码器", encoders.Count)
            : Loc.T("未检测到硬件编码器，将使用软件编码（更占 CPU）");
        return Loc.T("录像保存到：{0}（{1}）", RecordingService.OutputDirectory, encodersNote);
    }

    private async void RecordNow_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Recording.IsRecording)
        {
            var path = await _s.Recording.StopAsync();
            RecordingStatusText.Text = path.Length > 0
                ? Loc.T("已停止，录像已保存：{0}", path)
                : Loc.T("已停止。");
            RefreshLog();
            return;
        }

        if (_s.Recording.StartManual(out var message)) RefreshLog();
        RecordingStatusText.Text = message;
        RefreshRecording();
    }

    private void OpenRecordings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RecordingService.OutputDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{RecordingService.OutputDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = Loc.T("打开录像目录失败：{0}", ex.Message);
        }
    }

    // ── 操作日志 ──
    private void RefreshLog()
    {
        var entries = _s.NovaLog.Entries.Take(10).ToList();
        LogList.ItemsSource = entries;
        LogEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ThemedDialog
        {
            Title = "清空操作日志",
            Content = "日志记录 Nova 执行过的每一步操作，清空后不可恢复，确定吗？",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.NovaLog.Clear();
        RefreshLog();
    }

    private static string MaskKey(string key) =>
        key.Length <= 14 ? key : $"{key[..10]}…{key[^4..]}";
}

/// <summary>模型档案列表行视图模型（NovaPage ProfileList 的 x:Bind 数据源）</summary>
public sealed class NovaLlmProfileRow
{
    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public Visibility ActiveVisibility { get; }

    public NovaLlmProfileRow(string id, string name, string detail, bool isActive)
    {
        Id = id;
        Name = name;
        Detail = detail;
        ActiveVisibility = isActive ? Visibility.Visible : Visibility.Collapsed;
    }
}
