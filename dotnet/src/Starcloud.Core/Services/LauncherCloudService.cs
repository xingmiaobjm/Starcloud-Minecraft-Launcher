// Nova 远程配置 + 活跃上报 + 一键反馈：与官方服务器的三个轻量交互。
//
// 都刻意做成"失败即静默"：配置拉不到用默认值、上报失败不影响启动、反馈失败把内容
// 复制到剪贴板兜底。服务端契约见 docs/api-launcher-services.md。
//
// 活跃上报只在已登录 Starcloud ID 时发（用户要求用 StarcloudID 作识别码）；
// 未登录不上报，也不造匿名 ID —— 少收集就是最好的隐私策略。

using System.IO;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>启动器远程配置（/v1/launcher/config）。拉不到时全部用默认值。</summary>
public sealed record LauncherRemoteConfig
{
    /// <summary>静默更新：发现新版本时不弹窗，直接下载并在退出时静默升级（默认关 = 弹窗征求同意）</summary>
    [JsonPropertyName("silent_update")]
    public bool SilentUpdate { get; init; }

    /// <summary>Starcloud S3 热门版本缓存镜像是否参与竞速（默认开；服务端故障时可关）</summary>
    [JsonPropertyName("s3_mirror")]
    public bool S3Mirror { get; init; } = true;

    /// <summary>联机功能总开关（大厅/房间/我的）。默认开；服务端维护或出事时可整体关掉</summary>
    [JsonPropertyName("multiplayer")]
    public bool Multiplayer { get; init; } = true;

    /// <summary>服务器公告（可空）：显示在设置页更新卡片上</summary>
    [JsonPropertyName("notice")]
    public string? Notice { get; init; }

    public static LauncherRemoteConfig Default { get; } = new();
}

public sealed class LauncherCloudService
{
    private const string Base = "https://starcloudfur.cn";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<StarcloudSession?> _session;
    private readonly Func<LauncherSettings> _settings;

    public LauncherCloudService(Func<StarcloudSession?> session, Func<LauncherSettings> settings)
    {
        _session = session;
        _settings = settings;
    }

    public LauncherRemoteConfig Config { get; private set; } = LauncherRemoteConfig.Default;

    /// <summary>配置刷新完成（含失败回落默认值）。UI 订阅它动态显隐受云控的功能入口。</summary>
    public event Action? ConfigChanged;

    /// <summary>拉远程配置（启动时一次）。失败静默保留默认/上次值。</summary>
    public async Task RefreshConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/v1/launcher/config");
            if (_session()?.ApiKey is { Length: > 0 } key)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LauncherLog.Write("cloud", $"远程配置拉取失败：HTTP {(int)resp.StatusCode}（用默认值）");
                return;
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return;
            Config = JsonSerializer.Deserialize<LauncherRemoteConfig>(text, JsonOpts) ?? Config;
            LauncherLog.Write("cloud",
                $"远程配置：silent_update={Config.SilentUpdate} s3_mirror={Config.S3Mirror} "
                + $"multiplayer={Config.Multiplayer}"
                + (string.IsNullOrEmpty(Config.Notice) ? "" : $" notice={Config.Notice}"));
            ConfigChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LauncherLog.Write("cloud", $"远程配置拉取异常：{ex.Message}（用默认值）");
        }
    }

    /// <summary>
    /// 活跃上报：启动器启动后调用一次。未登录不上报（识别码 = StarcloudID 用户）。
    /// 服务端不存在 / 失败都静默 —— 这条链路永远不能影响启动。
    /// </summary>
    public async Task ReportActiveAsync(string launcherVersion, CancellationToken ct = default)
    {
        try
        {
            var session = _session();
            if (session is null)
            {
                LauncherLog.Write("cloud", "活跃上报跳过：未登录");
                return;
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/v1/telemetry/active");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", session.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                launcher_version = launcherVersion,
                os = Environment.OSVersion.VersionString,
                os_bits = Environment.Is64BitOperatingSystem ? 64 : 32,
            }, JsonOpts), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            LauncherLog.Write("cloud", $"活跃上报：HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            LauncherLog.Write("cloud", $"活跃上报失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>最近一次上传成功拿到的诊断码（一键反馈自动附带）。</summary>
    public string? LastDiagnosticCode { get; private set; }

    // ── 每日签到：签到领云端 token 额度 ──

    /// <summary>签到状态（LastCheckinStatus 由状态查询与签到动作共同刷新）。</summary>
    public sealed record CheckinStatus(
        [property: JsonPropertyName("signed_today")] bool SignedToday,
        [property: JsonPropertyName("reward_tokens")] int RewardTokens,
        [property: JsonPropertyName("streak")] int Streak,
        [property: JsonPropertyName("month_days")] int MonthDays)
    {
        public string Summary => SignedToday
            ? $"已签到，+{RewardTokens} token，连续 {Streak} 天，本月 {MonthDays} 天"
            : "今天还没签到";
    }

    /// <summary>签到状态变化（签到成功后触发；Nova 页 / 主窗口弹窗据此同步按钮）。</summary>
    public event Action? CheckinChanged;

    public CheckinStatus? LastCheckinStatus { get; private set; }

    /// <summary>查询签到状态；未登录 / 接口未上线 / 失败返回 null（调用方按"未知"处理）。</summary>
    public async Task<CheckinStatus?> GetCheckinStatusAsync(CancellationToken ct = default)
    {
        var session = _session();
        if (session is null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/v1/checkin");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", session.ApiKey);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var status = (await resp.Content.ReadFromJsonAsync<CheckinStatus>(JsonOpts, ct))
                ?? new CheckinStatus(false, 0, 0, 0);
            LastCheckinStatus = status;
            CheckinChanged?.Invoke();
            return status;
        }
        catch (Exception ex)
        {
            LauncherLog.Write("cloud", $"签到状态查询失败（忽略）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 签到。重复签到服务端返回幂等结果（signed_today=true）—— 按钮在已签状态本就是禁用的，
    /// 这里不特殊处理 409。
    /// </summary>
    public async Task<(bool Ok, string Message)> CheckInAsync(CancellationToken ct = default)
    {
        var session = _session();
        if (session is null) return (false, "请先登录 Starcloud ID");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/v1/checkin");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", session.ApiKey);
            using var resp = await Http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"签到失败（HTTP {(int)resp.StatusCode}）");
            var status = JsonSerializer.Deserialize<CheckinStatus>(text, JsonOpts)
                ?? new CheckinStatus(true, 0, 0, 0);
            LastCheckinStatus = status;
            CheckinChanged?.Invoke();
            LauncherLog.Write("cloud",
                $"签到成功：+{status.RewardTokens} token，连续 {status.Streak} 天，本月 {status.MonthDays} 天");
            return (true, $"+{status.RewardTokens} token，已连续签到 {status.Streak} 天");
        }
        catch (Exception ex)
        {
            LauncherLog.Write("cloud", $"签到失败：{ex.Message}");
            return (false, $"签到失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 上传诊断包换取诊断码：用户把码填进飞书问卷，开发者凭码查全文。
    /// 未登录也接受（服务端匿名归档，user_id 为空）；失败返回 false 与提示。
    /// </summary>
    public async Task<(bool Ok, string CodeOrMessage)> SubmitDiagnosticsAsync(
        string diagnostics, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/v1/diagnostics");
            if (_session()?.ApiKey is { Length: > 0 } key)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", key);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                launcher_version = SelfUpdateService.CurrentVersion(),
                os = Environment.OSVersion.VersionString,
                diagnostics,
            }, JsonOpts), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"上传失败（HTTP {(int)resp.StatusCode}）");
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(code))
                return (false, "服务端没有返回诊断码");
            LastDiagnosticCode = code;
            LauncherLog.Write("cloud", $"诊断包已上传，诊断码 {code}");
            return (true, code);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("cloud", $"诊断上传失败：{ex.Message}");
            return (false, $"上传失败：{ex.Message}");
        }
    }

    /// <summary>诊断包：版本 / 系统 / 配置 / 最近日志，反馈与「双击标题复制」共用。</summary>
    public string BuildDiagnostics(int logTailLines = 40)
    {
        var st = _settings();
        var sb = new StringBuilder();
        sb.AppendLine($"Starcloud 启动器 {SelfUpdateService.CurrentVersion()}");
        sb.AppendLine($"系统：{Environment.OSVersion.VersionString} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
        try
        {
            var (totalMb, usedMb) = SystemMemory.Snapshot();
            sb.AppendLine($"内存：{totalMb} MB（已用 {usedMb} MB）");
        }
        catch { }
        sb.AppendLine($"游戏目录：{st.DotMinecraft}");
        sb.AppendLine($"下载源：{st.DownloadSource ?? "auto"}（自定义镜像：{(string.IsNullOrWhiteSpace(st.DownloadCustomMirrorUrl) ? "未配置" : st.DownloadCustomMirrorUrl)}）");
        sb.AppendLine($"动效等级：{st.UiMotionLevel}");
        sb.AppendLine($"登录：{(_session() is { } s ? $"{s.DisplayName}（{s.UserId}）" : "未登录")}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var tail = string.Join("\n", LauncherLog.Tail(logTailLines));
        sb.AppendLine($"—— 最近 {logTailLines} 行日志 ——\n{tail}");
        return sb.ToString();
    }

    /// <summary>
    /// 一键反馈：描述 + 诊断包一起 POST。未登录 / 失败时把完整反馈复制到剪贴板兜底，
    /// 返回 (是否已提交到服务器, 给用户看的话)。
    /// </summary>
    public async Task<(bool Submitted, string Message)> SubmitFeedbackAsync(
        string content, string diagnostics, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (false, "请先写一句问题描述");
        var session = _session();
        var package = $"【反馈】{content.Trim()}\n\n{diagnostics}";
        if (session is null)
        {
            var saved = ToFile(package);
            return (false, saved is { } f
                ? $"未登录 Starcloud ID，无法在线提交；反馈已保存到 {f}"
                : "未登录 Starcloud ID，无法在线提交");
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/v1/feedback");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", session.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                content = content.Trim(),
                diagnostics,
                launcher_version = SelfUpdateService.CurrentVersion(),
                diagnostic_code = LastDiagnosticCode,
            }, JsonOpts), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return (true, "反馈已提交，感谢！");
            var f2 = ToFile(package);
            return (false, $"提交失败（HTTP {(int)resp.StatusCode}）" + (f2 is { } p2 ? $"，反馈已保存到 {p2}" : ""));
        }
        catch (Exception ex)
        {
            var f3 = ToFile(package);
            return (false, $"提交失败（{ex.Message}）" + (f3 is { } p3 ? $"，反馈已保存到 {p3}" : ""));
        }
    }

    /// <summary>反馈兜底：落盘到数据目录（Core 里没有剪贴板可用）。</summary>
    private static string? ToFile(string text)
    {
        try
        {
            LauncherPaths.EnsureDataDir();
            var path = Path.Combine(LauncherPaths.DataDir,
                $"feedback-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text);
            return path;
        }
        catch { return null; }
    }
}
