// Nova 设置页（原聊天区已移除——对话入口在顶栏 NovaOrb 面板 / 底部 NovaBar，共用 NovaRuntime 消息流）：
// 账户（登录 / 退出）/ 模型设置（第三方 LLM 添加 + 多模型档案切换）/ 游戏内辅助浮窗 /
// 游戏记录记忆（战报 / 时刻统计）/ 动手许可层（开启先弹确认）/ 可审计流水 / 能力路线。

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
        // 确认执行 / 取消 / 授权开关都会写流水 → 流水卡片跟着刷新
        _s.NovaRuntime.LogChanged += OnLogChanged;
        UpdateAccountState();

        // 许可层开关回显（默认关闭；代码设置期间抑制 Toggled）
        _toggling = true;
        CaptureSwitch.IsOn = _s.NovaPermissions.IsOn(NovaPermissionStore.Capture);
        ClipSwitch.IsOn = _s.NovaPermissions.IsOn(NovaPermissionStore.Clip);
        ShareSwitch.IsOn = _s.NovaPermissions.IsOn(NovaPermissionStore.Share);
        OverlaySwitch.IsOn = _s.Settings.NovaOverlayEnabled;
        _toggling = false;

        RefreshProfiles();
        RefreshMemory();
        RefreshLog();
    }

    private void OnLogChanged() => DispatcherQueue.TryEnqueue(RefreshLog);

    // ── 账户：登录表单 / 已登录信息切换（登录态变化 → NovaRuntime 清空全局消息流并重新问候） ──
    private void UpdateAccountState()
    {
        var session = _s.StarcloudId.Current;
        LoginForm.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        AccountInfo.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (session is not { } s) return;
        UserSub.Text = $"{s.UserId} · {(s.Channel == StarcloudIdService.CloudChannel ? "云端通道" : "测试通道")} · Key {MaskKey(s.ApiKey)}";
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
        var baseUrl = (preset is { BaseUrl.Length: > 0 } ? preset.BaseUrl : LlmBaseUrlBox.Text)
            .Trim().TrimEnd('/');
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
        ShowLlmHint(first ? "已添加并启用（当前使用）" : $"已添加「{(profile.Name.Length > 0 ? profile.Name : profile.Model)}」，点「设为当前」启用");
        RefreshProfiles();
    }

    private void LlmAddCancel_Click(object sender, RoutedEventArgs e)
    {
        LlmAddPanel.Visibility = Visibility.Collapsed;
        LlmAddOpenBtn.Visibility = Visibility.Visible;
        LlmKeyBox.Password = "";
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
        var dlg = new ContentDialog
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
            ? "暂无战报。打完一局后 Nova 会自动生成「这盘干了啥」。"
            : _s.NovaMoments.BuildReportText(last);
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
