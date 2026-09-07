// 云 AI 助手：测试通道 = 本地规则引擎（零网络依赖，响应即时）；
// 云端通道预留 POST /v1/ai/chat 协议（Bearer 会话 API Key 鉴权，服务未部署时自动回退本地引擎）。
// 能力编排：查找整合包（Modrinth）/ 游戏自动纠错（GameDiagnosticsService）/ 模组冲突自动处理（ModConflictService）。

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>一轮对话（cloud 通道随请求携带上下文）</summary>
public sealed record AiChatTurn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>AI 回复（FromCloud 标识是否来自云端模型；测试通道恒为本地引擎）</summary>
public sealed record AiChatResult(string Text, bool FromCloud);

public sealed class AiAssistantService
{
    private readonly StarcloudIdService _id;
    private readonly ModrinthService _mods;
    private readonly GameDiagnosticsService _diag;
    private readonly ModConflictService _conflicts;

    /// <summary>最近一次体检结果（用于「修复 <版本号>」的指代解析）</summary>
    private DiagReport? _lastDiag;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AiAssistantService(
        StarcloudIdService id, ModrinthService mods,
        GameDiagnosticsService diag, ModConflictService conflicts)
    {
        _id = id;
        _mods = mods;
        _diag = diag;
        _conflicts = conflicts;
    }

    /// <summary>发送一句话给 AI 助手；云端通道优先，失败自动回退本地引擎。</summary>
    public async Task<AiChatResult> ChatAsync(
        string message, LauncherSettings settings,
        IReadOnlyList<AiChatTurn>? history = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new AiChatResult("请输入你想让我做的事情。", false);

        var session = _id.Current;
        if (session is { Channel: StarcloudIdService.CloudChannel })
        {
            try
            {
                return await CloudChatAsync(session, message.Trim(), history, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 云端服务不可用时回退本地引擎，保证助手始终可用
            }
        }
        return new AiChatResult(await LocalChatAsync(message, settings, ct), false);
    }

    // ── 测试通道：本地规则引擎 ──
    private async Task<string> LocalChatAsync(
        string input, LauncherSettings settings, CancellationToken ct)
    {
        var lower = input.ToLowerInvariant();

        // 注意顺序：「处理冲突」要优先于泛化的「检查/修复」关键词
        if (lower.Contains("冲突"))
            return (lower.Contains("处理") || lower.Contains("解决") || lower.Contains("修复"))
                ? await _conflicts.ResolveAsync(settings.DotMinecraft, ct)
                : await ScanConflictsAsync(settings.DotMinecraft, ct);

        if (lower.Contains("修复"))
            return await TryRepairAsync(input, settings, ct);

        if (lower.Contains("整合包") || lower.Contains("modpack"))
            return await FindModpacksAsync(input, settings, ct);

        if (lower.Contains("体检") || lower.Contains("纠错") || lower.Contains("诊断") ||
            lower.Contains("检查") || lower.Contains("坏了") || lower.Contains("崩溃") ||
            lower.Contains("不能玩") || lower.Contains("启动不了"))
            return await DiagnoseAsync(settings, ct);

        if (lower.Contains("你好") || lower.Contains("您好") || lower.Contains("在吗") ||
            lower.Contains("hi") || lower.Contains("hello") || lower.Contains("帮助") ||
            lower.Contains("能做什么") || lower.Contains("你会") || lower.Contains("help"))
            return HelpText();

        return "暂时没听懂这句话。你可以这样问我：\n" +
               "• 「推荐整合包」或「帮我找个 1.20.1 的优化整合包」\n" +
               "• 「游戏体检」→ 扫描版本文件完整性，回复「修复 版本号」自动修复\n" +
               "• 「检查模组冲突」→ 扫描 mods 目录，回复「处理冲突」自动停用旧版本";
    }

    // ── 能力 1：查找整合包（Modrinth） ──
    private async Task<string> FindModpacksAsync(
        string raw, LauncherSettings settings, CancellationToken ct)
    {
        var query = ExtractQuery(raw);
        var gameVer = settings.LastVersion;

        var res = await _mods.SearchAsync(query, "modpack", null, gameVer, 0, 5, "downloads", null, ct);
        var relaxed = false;
        if (res.Hits.Count == 0 && !string.IsNullOrEmpty(gameVer))
        {
            res = await _mods.SearchAsync(query, "modpack", null, null, 0, 5, "downloads", null, ct);
            relaxed = true;
        }

        if (res.Hits.Count == 0)
            return $"没有找到与「{query}」相关的整合包，换个关键词试试？";

        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrEmpty(query)
            ? "为你挑了 Modrinth 下载量最高的整合包："
            : $"为你找到与「{query}」相关的整合包（按下载量排序）：");
        if (relaxed) sb.AppendLine($"（没有适配 {gameVer} 的结果，以下为通用结果）");
        var i = 1;
        foreach (var h in res.Hits)
        {
            var loaders = h.Loaders.Count > 0 ? string.Join('/', h.Loaders) : "通用";
            sb.AppendLine($"{i++}. {h.Title} —— {h.Downloads:N0} 次下载（作者 {h.Author}）");
            if (!string.IsNullOrWhiteSpace(h.Description)) sb.AppendLine($"   {h.Description}");
            sb.AppendLine($"   加载器 {loaders} · 安装：到「下载」页安装 .mrpack，或搜索 {h.Slug}");
        }
        return sb.ToString().TrimEnd();
    }

    private static readonly string[] StopWords =
    [
        "整合包", "modpack", "帮我", "我想", "想要", "找个", "找一个", "来个", "推荐",
        "查找", "搜索", "找下", "找", "适合", "好玩", "试试", "一下", "一个",
        "的", "了", "吗", "呢", "？", "?", "！", "!", "。", "，", ",", "、",
    ];

    private static string ExtractQuery(string raw)
    {
        var s = raw.ToLowerInvariant();
        foreach (var w in StopWords) s = s.Replace(w, " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    // ── 能力 2：游戏自动纠错（GameDiagnosticsService） ──
    private async Task<string> DiagnoseAsync(LauncherSettings settings, CancellationToken ct)
    {
        var report = await _diag.DiagnoseAsync(settings.DotMinecraft, ct);
        _lastDiag = report;

        if (report.Issues.Count == 0)
            return $"游戏体检完成：已扫描 {report.ScannedCount} 个版本，全部健康，无需修复。";

        var sb = new StringBuilder();
        sb.AppendLine($"游戏体检完成：已扫描 {report.ScannedCount} 个版本，发现 {report.Issues.Count} 个问题：");
        foreach (var issue in report.Issues)
            sb.AppendLine($"• [{issue.VersionId}] {issue.Kind} — {issue.Detail}{(issue.AutoFixable ? "（可自动修复）" : "")}");

        var fixable = report.Issues.Where(i => i.AutoFixable).Select(i => i.VersionId).Distinct().ToList();
        if (fixable.Count > 0)
            sb.Append($"回复「修复 {fixable[0]}」即可自动修复" +
                      (fixable.Count > 1 ? $"（共 {fixable.Count} 个版本可修复）" : "") + "。");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> TryRepairAsync(
        string input, LauncherSettings settings, CancellationToken ct)
    {
        var name = input.Replace("修复", " ").Trim();
        var fixable = _lastDiag?.Issues.Where(i => i.AutoFixable).ToList() ?? [];

        if (name.Length == 0 && fixable.Count == 0)
            return "请先回复「游戏体检」，我会扫描出可自动修复的问题。";

        var targets = name.Length == 0
            ? fixable
            : fixable.Where(i => i.VersionId.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                 i.VersionId.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                     .ToList();

        if (targets.Count == 0)
            return $"没有找到可自动修复的版本「{name}」。先回复「游戏体检」让我扫一遍。";

        var sb = new StringBuilder("开始自动修复：");
        foreach (var t in targets.Take(5))
        {
            try
            {
                var msg = await _diag.RepairAsync(t.VersionId, settings, ct);
                sb.Append($"\n• {t.VersionId}：{msg}");
            }
            catch (Exception ex)
            {
                sb.Append($"\n• {t.VersionId}：修复失败 —— {ex.Message}");
            }
        }
        return sb.ToString();
    }

    // ── 能力 3：模组冲突自动处理（ModConflictService） ──
    private async Task<string> ScanConflictsAsync(string dotMc, CancellationToken ct)
    {
        var report = await _conflicts.ScanAsync(dotMc, ct);
        if (report.Conflicts.Count == 0)
            return $"模组检查完成：{report.ScannedCount} 个模组，没有发现冲突。";

        var sb = new StringBuilder($"模组检查完成：{report.ScannedCount} 个模组，发现 {report.Conflicts.Count} 个冲突：");
        foreach (var c in report.Conflicts)
            sb.Append($"\n• [{c.Kind}] {c.Detail}\n  建议：{c.Suggestion}");
        if (report.Conflicts.Any(c => c.Kind == "重复安装"))
            sb.Append("\n回复「处理冲突」我会自动停用重复的旧版本。");
        return sb.ToString();
    }

    private static string HelpText() =>
        "我是 Starcloud 云 AI 助手，可以帮你：\n" +
        "1. 查找整合包 —— 「推荐整合包」或「找个 1.20.1 的生存整合包」\n" +
        "2. 游戏自动纠错 —— 「游戏体检」，发现损坏文件后回复「修复 版本号」\n" +
        "3. 模组冲突自动处理 —— 「检查模组冲突」，回复「处理冲突」自动停用旧版本";

    // ── 云端通道：POST /v1/ai/chat（Bearer 会话 API Key；协议预留） ──
    private static async Task<AiChatResult> CloudChatAsync(
        StarcloudSession session, string message,
        IReadOnlyList<AiChatTurn>? history, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StarcloudIdService.CloudBaseUrl) };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/ai/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.ApiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new ChatRequest(message, history ?? []), JsonOpts),
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("text").GetString()
                   ?? throw new InvalidOperationException("云端 AI 响应缺少 text");
        return new AiChatResult(text, true);
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("history")] IReadOnlyList<AiChatTurn> History);
}
