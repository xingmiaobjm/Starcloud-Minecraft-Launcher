// Nova — 被允许动手的 AI 助手（按 NOVA_DESIGN.md v1.0 重建）。
//
// 能力阶段（信任递进，每一步只在用户点头之后）：
//   阶段一 顾问：崩溃自愈 / 兼容侦探 / 游戏体检 / 整合包推荐 —— 只给建议，不落盘
//   阶段二 动手：版本修复 / 模组停用 / 开箱即玩配置 —— 先给确认清单，点头才执行，执行写流水
//   （阶段三「主动」Nova 时刻、阶段四「在场」游戏内浮窗：视觉通路未上线，仅提供许可层开关与说明）
//
// 产品基石（不可跳过）：任何落盘动作先出 NovaPlan 确认清单，用户确认后执行并记入可审计流水。
//
// LLM 降级链（严禁请求因无额度而失败卡死）：
//   1. 用户自填 Key（OpenAI 兼容 /chat/completions，零成本）
//   2. StarCloud 云端 LLM（登录会话 API Key，服务未部署自动回退）
//   3. 本地规则引擎兜底（能力指令不走 LLM，永不失败）

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>确认清单里的一个具体动作（标题 + 具体数值/影响说明）</summary>
public sealed record NovaActionItem(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>
/// 确认清单：动手前展示给用户的动作列表；ExecuteAsync 仅在用户点头后由 UI 调用。
/// </summary>
public sealed record NovaPlan(
    string Id,
    string Title,
    List<NovaActionItem> Items,
    Func<CancellationToken, Task<string>> ExecuteAsync);

/// <summary>Nova 一轮回复：文本 + 可选确认清单（Plan 非空 = 需要用户点头才动手）；Trim 非空 = 本次发送做过裁剪</summary>
public sealed record NovaReply(string Text, NovaPlan? Plan, bool FromCloud, TrimOutcome? Trim = null);

/// <summary>一轮对话（云端通道随请求携带上下文）</summary>
public sealed record NovaChatTurn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed class NovaService
{
    private readonly StarcloudIdService _id;
    private readonly ModrinthService _mods;
    private readonly GameDiagnosticsService _diag;
    private readonly ModConflictService _conflicts;
    private readonly NovaPermissionStore _permissions;
    private readonly NovaActionLog _log;
    private readonly NovaMomentsService _moments;
    private readonly NovaInviteService _invites;
    private readonly SaveService _saves;

    /// <summary>最近一次体检结果（用于「修复 <版本号>」的指代解析）</summary>
    private DiagReport? _lastDiag;

    /// <summary>当前实例上下文（VersionsPage/HomePage 选中时同步；回退 settings.LastVersion）</summary>
    private string? _currentInstance;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // max_tokens 未指定时不发该字段
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public NovaService(
        StarcloudIdService id, ModrinthService mods,
        GameDiagnosticsService diag, ModConflictService conflicts,
        NovaPermissionStore permissions, NovaActionLog log,
        NovaMomentsService moments, NovaInviteService invites, SaveService saves)
    {
        _id = id;
        _mods = mods;
        _diag = diag;
        _conflicts = conflicts;
        _permissions = permissions;
        _log = log;
        _moments = moments;
        _invites = invites;
        _saves = saves;
    }

    /// <summary>同步当前实例上下文（供「体检 / 找整合包 / 搬家」等能力指代使用）</summary>
    public void SetCurrentInstance(string? versionId) => _currentInstance = versionId;

    /// <summary>当前实例：UI 上下文优先，回退最近启动版本</summary>
    private string? CurrentInstance(LauncherSettings settings) =>
        _currentInstance ?? settings.LastVersion;

    /// <summary>
    /// 发送一句话给 Nova；能力指令走本地引擎（具体不空谈），其余走 LLM 降级链。
    /// 发送前对崩溃日志做结构化裁剪（丢弃与病因无关的段落）；
    /// <paramref name="fullLog"/> = 用户显式要求「发送完整日志」（跳过裁剪，消耗更多积分）。
    /// </summary>
    public async Task<NovaReply> ChatAsync(
        string message, LauncherSettings settings,
        IReadOnlyList<NovaChatTurn>? history = null, CancellationToken ct = default,
        bool fullLog = false, int? maxTokens = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new NovaReply("说一句人话就行，比如「崩了帮我看看」「找个 1.20.1 的优化整合包」。", null, false);

        var local = await RouteLocalAsync(message, settings, ct);
        if (local is not null) return local;

        // 崩溃日志裁剪：只把因果链（栈顶 / Caused by: / System Details / Suspected Mods）发给模型；
        // fullLog=true 时原样发送（用户已知会更贵）。非崩溃文本裁剪函数会逐字节原样返回。
        var body = message.Trim();
        var trim = fullLog ? CrashLogTrimmer.Passthrough(body) : CrashLogTrimmer.TrimCrashLog(body);
        var sendHistory = TrimHistory(history, fullLog);

        // 未命中能力指令 → LLM 降级链
        var session = _id.Current;
        if (session is { Channel: StarcloudIdService.CloudChannel })
        {
            try
            {
                // 先探活：llm_active 为空表示上游模型未接入，聊天必返 501，直接跳过云端降级。
                if (await CloudLlmReadyAsync(ct))
                {
                    var text = await CloudChatAsync(session, trim.Text, sendHistory, ct, fullLog, maxTokens);
                    return new NovaReply(text, null, true, trim);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (NovaCloudException ex) { return new NovaReply(ex.Message, null, true, trim); }
            catch { /* 云端不可用 → 继续降级 */ }
        }

        try
        {
            var text = await UserLlmChatAsync(settings, trim.Text, sendHistory, ct, maxTokens);
            if (text is not null) return new NovaReply(text, null, true, trim);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 用户 Key 失效/欠费 → 降级本地兜底，不卡死 */ }

        return new NovaReply(LocalFallback(message), null, false, trim);
    }

    /// <summary>
    /// 发送一句话给 Nova；能力指令走本地引擎，其余走 LLM 流式降级链。
    /// 返回逐字增量；本地命中或失败时一次性 yield 完整文本。
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string message, LauncherSettings settings,
        IReadOnlyList<NovaChatTurn>? history = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        bool fullLog = false, int? maxTokens = null,
        Action<NovaReply>? onFullReply = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            yield return "说一句人话就行，比如「崩了帮我看看」「找个 1.20.1 的优化整合包」。";
            yield break;
        }

        var local = await RouteLocalAsync(message, settings, ct);
        if (local is not null)
        {
            onFullReply?.Invoke(local);
            yield return local.Text;
            yield break;
        }

        var body = message.Trim();
        var trim = fullLog ? CrashLogTrimmer.Passthrough(body) : CrashLogTrimmer.TrimCrashLog(body);
        var sendHistory = TrimHistory(history, fullLog);

        var session = _id.Current;
        if (session is { Channel: StarcloudIdService.CloudChannel })
        {
            string? cloudError = null;
            IAsyncEnumerable<string>? cloudStream = null;
            try
            {
                if (await CloudLlmReadyAsync(ct))
                    cloudStream = CloudChatStreamAsync(session, trim.Text, sendHistory, ct, fullLog, maxTokens);
            }
            catch (OperationCanceledException) { throw; }
            catch (NovaCloudException ex) { cloudError = ex.Message; }
            catch { /* 云端不可用 → 继续降级 */ }

            if (cloudError is not null)
            {
                yield return cloudError;
                yield break;
            }

            if (cloudStream is not null)
            {
                await foreach (var delta in cloudStream) yield return delta;
                yield break;
            }
        }

        IAsyncEnumerable<string>? userStream = null;
        try
        {
            userStream = UserLlmChatStreamAsync(settings, trim.Text, sendHistory, ct, maxTokens);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 用户 Key 失效/欠费 → 降级本地兜底 */ }

        if (userStream is not null)
        {
            await foreach (var delta in userStream) yield return delta;
            yield break;
        }

        yield return LocalFallback(message);
    }

    /// <summary>历史里的崩溃日志同样裁剪（只作用于发给模型的副本，本地会话仍保留原文）。</summary>
    private static IReadOnlyList<NovaChatTurn>? TrimHistory(
        IReadOnlyList<NovaChatTurn>? history, bool fullLog)
    {
        if (fullLog || history is null || history.Count == 0) return history;

        List<NovaChatTurn>? copy = null;
        for (var i = 0; i < history.Count; i++)
        {
            var turn = history[i];
            if (string.IsNullOrEmpty(turn.Content)) continue;
            var outcome = CrashLogTrimmer.TrimCrashLog(turn.Content);
            if (!outcome.Applied) continue;
            copy ??= [.. history];
            copy[i] = turn with { Content = outcome.Text };
        }
        return copy ?? history;
    }

    // ── 本地能力路由（命中返回 null 表示未命中） ──
    private async Task<NovaReply?> RouteLocalAsync(
        string input, LauncherSettings settings, CancellationToken ct)
    {
        var lower = input.ToLowerInvariant();

        // starcloud://join 邀请链接（出现在任何消息里都优先处理）
        var joinIdx = input.IndexOf(NovaInviteService.JoinScheme, StringComparison.OrdinalIgnoreCase);
        if (joinIdx < 0)
            joinIdx = input.IndexOf("starcloud://join", StringComparison.OrdinalIgnoreCase);
        if (joinIdx >= 0)
        {
            var url = input[joinIdx..].Split(' ', '\n', '\t')[0].Trim();
            return NovaInviteService.TryParseJoinUrl(url, out var token) && token is not null
                ? await PlanJoinAsync(token, settings, ct)
                : new NovaReply("这个邀请链接不完整或已损坏——让好友重新复制一次完整的 starcloud://join 链接再发我。",
                    null, false);
        }

        // 注意顺序：「处理冲突」优先于泛化的「检查/修复」关键词
        if (lower.Contains("冲突"))
        {
            if (lower.Contains("处理") || lower.Contains("解决") || lower.Contains("停用"))
                return await PlanConflictResolveAsync(settings.DotMinecraft, ct);
            return new NovaReply(await ScanConflictsTextAsync(settings.DotMinecraft, ct), null, false);
        }

        if (lower.Contains("崩溃") || lower.Contains("闪退") || lower.Contains("崩了") ||
            lower.Contains("打不开") || lower.Contains("起不来") || lower.Contains("启动不了"))
            return new NovaReply(await CrashTriageAsync(settings, ct), null, false);

        if (lower.Contains("修复"))
            return PlanRepair(input, settings) ?? new NovaReply(await DiagnoseTextAsync(settings, ct), null, false);

        if (lower.Contains("体检") || lower.Contains("纠错") || lower.Contains("诊断") ||
            lower.Contains("检查版本") || lower.Contains("坏了") || lower.Contains("损坏"))
            return new NovaReply(await DiagnoseTextAsync(settings, ct), null, false);

        if (lower.Contains("帧") || lower.Contains("fps") || lower.Contains("卡顿") ||
            lower.Contains("流畅"))
            return new NovaReply(FpsCheckText(settings), null, false);

        if (lower.Contains("搬家") || lower.Contains("移动存档") ||
            (lower.Contains("存档") && (lower.Contains("搬") || lower.Contains("移"))))
            return await PlanSaveMoveAsync(input, settings, ct);

        if (lower.Contains("战报") || lower.Contains("战绩") || lower.Contains("上局"))
        {
            var report = _moments.LastReport;
            return new NovaReply(
                report is null
                    ? "还没有战报——启动一局游戏，下机时我自动汇总这局的高光与翻车瞬间。"
                    : _moments.BuildReportText(report),
                null, false);
        }

        if (lower.Contains("拉人") || lower.Contains("邀请") ||
            (lower.Contains("好友") && (lower.Contains("玩") || lower.Contains("联机") || lower.Contains("一起"))) ||
            lower.Contains("联机") || lower.Contains("开黑"))
            return new NovaReply(InviteGuideText(), null, false);

        if (lower.Contains("开箱即玩") || lower.Contains("小白") || lower.Contains("傻瓜") ||
            lower.Contains("优化配置") || lower.Contains("推荐配置") || lower.Contains("帮我调"))
            return PlanSmoothStart(settings);

        if (lower.Contains("整合包") || lower.Contains("modpack"))
            return new NovaReply(await FindModpacksTextAsync(input, settings, ct), null, false);

        if (lower.Contains("时刻") || lower.Contains("捕捉") || lower.Contains("剪辑") ||
            lower.Contains("分享") || lower.Contains("录像"))
            return new NovaReply(MomentsOverviewText(), null, false);

        if (lower.Contains("你好") || lower.Contains("您好") || lower.Contains("在吗") ||
            lower.Contains("hi") || lower.Contains("hello") || lower.Contains("帮助") ||
            lower.Contains("能做什么") || lower.Contains("你会") || lower.Contains("help"))
            return new NovaReply(HelpText(), null, false);

        return null;
    }

    // ── 能力 1：崩溃自愈（阶段一 · 顾问，建议级：只读日志不落盘） ──
    private async Task<string> CrashTriageAsync(LauncherSettings settings, CancellationToken ct)
    {
        var dotMc = settings.DotMinecraft;

        // 最新崩溃报告 → latest.log 尾部兜底
        string? text = null, source = null;
        var crashDir = Path.Combine(dotMc, "crash-reports");
        if (Directory.Exists(crashDir))
        {
            var newest = Directory.EnumerateFiles(crashDir, "*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (newest is not null)
            {
                text = await File.ReadAllTextAsync(newest.FullName, ct);
                source = newest.Name;
            }
        }
        if (text is null)
        {
            var latestLog = Path.Combine(dotMc, "logs", "latest.log");
            if (File.Exists(latestLog))
            {
                var lines = await File.ReadAllLinesAsync(latestLog, ct);
                text = string.Join('\n', lines.TakeLast(120));
                source = "logs/latest.log";
            }
        }
        if (text is null)
            return "没有找到崩溃报告（.minecraft/crash-reports）和 latest.log。下次闪退后直接跟我说「崩了」，我来分析。";

        var physMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var memHint = $"把最大内存调到 {Math.Clamp(physMb / 2, 2048, 8192):F0} MB（物理内存的一半），最小内存 512 MB";

        // 模式匹配 → 具体方案（不泛泛而谈）
        (string Match, string Advice)[] rules =
        [
            ("outofmemoryerror", $"内存不足。{memHint}；回复「开箱即玩」我直接帮你调好。"),
            ("heap space", $"堆内存溢出。{memHint}；回复「开箱即玩」我直接帮你调好。"),
            ("unsupportedclassversionerror",
                "Java 版本不对。class file version 65 = Java 21，61 = Java 17，60 = Java 16，52 = Java 8。装对应版本后到「设置」选择它。"),
            ("incompatible mod set", "模组集合不兼容（缺前置或版本冲突）。回复「检查模组冲突」我来定位红灯项。"),
            ("mismatched mod list", "客户端与服务端模组清单不一致。核对两侧 mods 目录是否一致。"),
            ("modresolutionexception", "模组依赖解析失败（缺前置库）。回复「检查模组冲突」我来定位缺哪个。"),
            ("nosuchmethoderror", "模组之间版本不匹配。把最近新装/升级的模组退回与前置匹配的版本。"),
            ("noclassdeffounderror", "缺前置库或前置版本不符。回复「检查模组冲突」我来定位。"),
            ("iris", "疑似光影（Iris）引发冲突。先移除光影包试试；回复「检查模组冲突」可定位 Iris 与 OptiFine 混装。"),
            ("optifine", "疑似 OptiFine 冲突。OptiFine 与 Iris/Fabric API 常互斥，二选一。"),
            ("glfw error", "窗口/显卡初始化失败。更新显卡驱动；集显机器可把最大内存下调并关闭光影。"),
            ("failed to create window", "窗口创建失败。更新显卡驱动，或检查分辨率参数是否超出屏幕范围。"),
        ];
        foreach (var (match, advice) in rules)
        {
            if (!text.Contains(match, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = $"看了 {source}，问题定位：";
            return $"{prefix}\n• {advice}\n（建议级：我只给方案不动手；涉及落盘的修复我都会先给确认清单。）";
        }

        return $"看了 {source}，没有命中已知模式。把报错里「Description」一段贴给我，我来继续定位。" +
               $"\n崩溃报告路径：.minecraft/crash-reports";
    }

    // ── 能力 2：游戏体检（阶段一 · 顾问）+ 修复确认清单（阶段二 · 动手） ──
    private async Task<string> DiagnoseTextAsync(LauncherSettings settings, CancellationToken ct)
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
            sb.Append($"回复「修复 {fixable[0]}」我给你确认清单，点头后才动手修复" +
                      (fixable.Count > 1 ? $"（共 {fixable.Count} 个版本可修复）" : "") + "。");
        return sb.ToString().TrimEnd();
    }

    private NovaReply? PlanRepair(string input, LauncherSettings settings)
    {
        var name = input.Replace("修复", " ").Trim();
        var fixable = _lastDiag?.Issues.Where(i => i.AutoFixable).ToList() ?? [];

        if (name.Length == 0 && fixable.Count == 0)
            return new NovaReply("先回复「游戏体检」让我扫一遍，我把可修复的问题列成确认清单给你。", null, false);

        var targets = name.Length == 0
            ? fixable
            : fixable.Where(i => i.VersionId.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                 i.VersionId.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                     .ToList();
        if (targets.Count == 0)
            return new NovaReply($"体检结果里没有可自动修复的版本「{name}」。先回复「游戏体检」让我扫一遍。", null, false);

        var snapshot = targets.ToList();
        var plan = new NovaPlan(Guid.NewGuid().ToString("N")[..12],
            $"自动修复 {snapshot.Count} 个版本",
            snapshot.Select(t => new NovaActionItem($"重装 {t.VersionId}", $"{t.Kind} — {t.Detail}，重装会重新下载客户端与依赖文件"))
                    .ToList(),
            ct => ExecuteRepairAsync(snapshot, settings, ct));
        return new NovaReply("这是修复清单，确认后我马上动手：", plan, false);
    }

    private async Task<string> ExecuteRepairAsync(
        List<DiagIssue> targets, LauncherSettings settings, CancellationToken ct)
    {
        var sb = new StringBuilder("已按清单修复：");
        foreach (var t in targets)
        {
            try
            {
                sb.Append($"\n• {t.VersionId}：{await _diag.RepairAsync(t.VersionId, settings, ct)}");
            }
            catch (Exception ex)
            {
                sb.Append($"\n• {t.VersionId}：修复失败 —— {ex.Message}");
            }
        }
        return sb.ToString();
    }

    // ── 能力 3：兼容侦探（阶段一 · 顾问）+ 冲突停用确认清单（阶段二 · 动手） ──
    private async Task<string> ScanConflictsTextAsync(string dotMc, CancellationToken ct)
    {
        var report = await _conflicts.ScanAsync(dotMc, ct);
        if (report.Conflicts.Count == 0)
            return $"模组检查完成：{report.ScannedCount} 个模组，没有发现冲突。";

        var sb = new StringBuilder($"模组检查完成：{report.ScannedCount} 个模组，发现 {report.Conflicts.Count} 个冲突：");
        foreach (var c in report.Conflicts)
            sb.Append($"\n• [{c.Kind}] {c.Detail}\n  建议：{c.Suggestion}");
        if (report.Conflicts.Any(c => c.Kind == "重复安装"))
            sb.Append("\n回复「处理冲突」我会把停用清单列给你，点头后才动手。");
        return sb.ToString();
    }

    private async Task<NovaReply> PlanConflictResolveAsync(string dotMc, CancellationToken ct)
    {
        var report = await _conflicts.ScanAsync(dotMc, ct);
        var dups = report.Conflicts.Where(c => c.Kind == "重复安装").ToList();
        if (dups.Count == 0)
            return new NovaReply(
                report.Conflicts.Count == 0
                    ? $"模组处理完成：{report.ScannedCount} 个模组，没有需要处理的冲突。"
                    : "冲突都不是「重复安装」类，无法自动停用——需要你按建议手动处理（混装类我不擅自动你的文件）。",
                null, false);

        // 与 ModConflictService 同规则：每组保留最新（按修改时间），其余停用
        var planned = dups.SelectMany(c => report.Mods
                .Where(m => c.Files.Contains(m.FileName))
                .OrderByDescending(m => m.ModifiedAt).ThenBy(m => m.FileName)
                .Skip(1))
            .Select(m => (m.FileName, m.FullPath))
            .ToList();
        if (planned.Count == 0)
            return new NovaReply("没有需要停用的重复模组。", null, false);

        var plan = new NovaPlan(Guid.NewGuid().ToString("N")[..12],
            "停用重复的旧版本模组",
            planned.Select(p => new NovaActionItem(
                    $"停用 {p.FileName}",
                    $"改名 {p.FileName}.disabled（不删除，随时可改回来）"))
                .ToList(),
            ct => ExecuteConflictResolveAsync(planned, ct));
        return new NovaReply($"发现 {dups.Count} 组重复模组，停用清单如下，确认后我动手：", plan, false);
    }

    private async Task<string> ExecuteConflictResolveAsync(
        List<(string FileName, string FullPath)> planned, CancellationToken ct)
    {
        var disabled = new List<string>();
        foreach (var (fileName, fullPath) in planned)
        {
            ct.ThrowIfCancellationRequested();
            var target = fullPath + ".disabled";
            if (File.Exists(target)) File.Delete(target);
            File.Move(fullPath, target);
            disabled.Add(fileName);
        }
        return "已按清单停用 " + disabled.Count + " 个旧版本模组：\n" +
               string.Join("\n", disabled.Select(f => $"• {f} → .disabled"));
    }

    // ── 能力 4：开箱即玩（阶段二 · 动手：生成傻瓜配置 + 确认后一键应用） ──
    private NovaReply PlanSmoothStart(LauncherSettings settings)
    {
        var physMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var maxMem = (int)Math.Clamp((long)physMb / 2, 2048, 8192);
        var items = new List<NovaActionItem>
        {
            new($"最大内存 {settings.DefaultMaxMemoryMb} MB → {maxMem} MB",
                $"物理内存 {physMb / 1024} GB 的一半，给系统与集显留余量"),
            new("启用内置 JVM 参数（G1GC 低停顿）",
                "G1 垃圾回收 + 软引用上限，现代版本卡顿明显减少"),
            new("开启版本隔离",
                "每个版本的存档/模组/配置独立存放，互不污染"),
        };
        var plan = new NovaPlan(Guid.NewGuid().ToString("N")[..12],
            "开箱即玩：一键傻瓜配置",
            items,
            ct =>
            {
                settings.DefaultMinMemoryMb = 512;
                settings.DefaultMaxMemoryMb = maxMem;
                settings.UseBuiltinJvmArgs = true;
                settings.VersionIsolated = true;
                settings.Save();
                return Task.FromResult(
                    $"已应用：最大内存 {maxMem} MB · 最小 512 MB · G1GC 内置参数已启用 · 版本隔离已开启。重新启动游戏生效。");
            });
        return new NovaReply($"看了你的机器配置（物理内存 {physMb / 1024} GB），傻瓜配置如下，点头我就写进设置：", plan, false);
    }

    // ── 能力 5：查找整合包（阶段一 · 顾问，Modrinth） ──
    private async Task<string> FindModpacksTextAsync(
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

    // ── Nova 时刻概览（阶段三「主动」：观察已上线，帧剪辑待视觉通路） ──
    private string MomentsOverviewText()
    {
        var (capT, capB) = NovaPermissionStore.Describe(NovaPermissionStore.Capture);
        var (clipT, clipB) = NovaPermissionStore.Describe(NovaPermissionStore.Clip);
        var (shT, shB) = NovaPermissionStore.Describe(NovaPermissionStore.Share);

        var sb = new StringBuilder();
        sb.AppendLine(_moments.IsSessionActive
            ? $"我正在观察 {_moments.SessionVersion} 的游戏日志（从 {_moments.SessionStart:HH:mm} 开始），" +
              "达成进度/完成挑战/翻车瞬间都会被我注意到。"
            : "当前没有进行中的游戏会话；游戏启动后我自动开始观察。");
        sb.AppendLine("帧剪辑（视觉通路）还在开发中，现在记录的是文字时刻卡——坐标、时间、事件都真实可查。");

        var pending = _moments.Candidates.Where(c => c.State == "proposed").ToList();
        if (pending.Count > 0)
        {
            sb.AppendLine($"我有 {pending.Count} 个候选瞬间等你点头（在 Nova 面板处理）：");
            foreach (var c in pending.Take(3))
                sb.Append($"• {c.TimeText} {c.Caption}\n");
        }
        else if (_moments.Moments.Count > 0)
        {
            sb.AppendLine($"已收藏 {_moments.Moments.Count} 个时刻；说「战报」看最近一局的汇总。");
        }

        sb.Append("动手许可层三档开关（默认全关，开启 = 显式授权）：\n" +
                  $"• {capT}：{capB}\n• {clipT}：{clipB}\n• {shT}：{shB}");
        return sb.ToString();
    }

    // ── 拉人即玩（阶段二 · 动手）：收到链接 → 确认清单 → 一键装好进服 ──
    private async Task<NovaReply> PlanJoinAsync(
        NovaInviteToken token, LauncherSettings settings, CancellationToken ct)
    {
        var plan = await _invites.BuildJoinPlanAsync(token, settings, ct);
        var mods = token.Mods.Count > 0 ? $"，{token.Mods.Count} 个模组自动同步" : "";
        var server = string.IsNullOrWhiteSpace(token.Server) ? "" : $"\n服务器 {token.Server}（连接码 {token.Code}）";
        return new NovaReply(
            $"收到「{token.Name}」的邀请：{token.Loader} {token.Mc}{mods}.{server}\n" +
            "确认清单如下，点头我就开装，装好直接进服：",
            plan, false);
    }

    private static string InviteGuideText() =>
        "拉人即玩：在「版本」页右键你的实例 →「生成邀请」，复制 starcloud://join 链接发给好友。\n" +
        "好友把链接发给我（贴进对话即可），我自动装好同款环境并进服。\n" +
        "• 陶瓦联机（EasyTier 组网）无需公网 IP，连接码形如 U/XXXX-XXXX-XXXX-XXXX\n" +
        "• 非 Modrinth 来源的模组无法自动同步，邀请里会明确标注\n" +
        "• Forge/NeoForge 自动安装未接入，受邀方会先装好原版与模组再引导手动装加载器";

    // ── 帧数体检（阶段一 · 顾问：配置侧具体诊断，不臆造游戏内帧数） ──
    private static string FpsCheckText(LauncherSettings settings)
    {
        var physMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var max = settings.DefaultMaxMemoryMb;
        var recommended = (int)Math.Clamp((long)physMb / 2, 2048, 8192);

        var sb = new StringBuilder();
        sb.AppendLine($"配置体检（物理内存 {physMb / 1024} GB，当前最大内存 {max} MB）：");
        if (max < 2048)
            sb.AppendLine($"• 最大内存 {max} MB 偏小——现代版本带模组建议 {recommended} MB");
        else if (max > physMb / 2)
            sb.AppendLine($"• 最大内存 {max} MB 超过物理内存一半——过大反而加剧 GC 停顿，建议 {recommended} MB");
        else
            sb.AppendLine($"• 最大内存 {max} MB 合理");
        sb.AppendLine(settings.UseBuiltinJvmArgs
            ? "• G1GC 低停顿参数已启用"
            : "• 内置 G1GC 参数未启用——开启后 GC 停顿显著减少（回收停顿从秒级压到 50ms 内）");
        if (settings.VersionIsolated)
            sb.AppendLine("• 版本隔离已开启，多实例互不污染");
        sb.Append("诚实说明：我看不见你游戏内的实时帧数（视觉通路未上线），以上是配置侧诊断。\n" +
                  "带光影时先试关光影对比；回复「开箱即玩」我给你确认清单一键调优。");
        return sb.ToString();
    }

    // ── 存档搬家（阶段二 · 动手：先备份 zip，再移动；全局 saves ↔ 版本隔离 saves） ──
    private sealed record SaveLocation(string FolderName, string LevelName, string Location, string FullPath);

    private async Task<NovaReply> PlanSaveMoveAsync(string input, LauncherSettings settings, CancellationToken ct)
    {
        var dotMc = settings.DotMinecraft;
        var saves = CollectSaves(dotMc);
        if (saves.Count == 0)
            return new NovaReply("没有找到任何存档（全局 saves 和各版本隔离 saves 都扫过了）。", null, false);

        // 解析「搬家 <存档> 到 <目标>」
        var rest = input.Replace("搬家", " ").Replace("移动存档", " ").Replace("移动", " ").Trim();
        var toIdx = rest.IndexOf(" 到 ", StringComparison.Ordinal);
        if (toIdx < 0) toIdx = rest.IndexOf("到", StringComparison.Ordinal);
        if (toIdx < 0) toIdx = rest.IndexOf("→", StringComparison.Ordinal);
        var name = (toIdx >= 0 ? rest[..toIdx] : rest).Trim('「', '」', '"', ' ');
        var target = toIdx >= 0 ? rest[(toIdx + 1)..].Trim('「', '」', '"', ' ') : "";

        if (name.Length == 0)
        {
            var sb = new StringBuilder($"共 {saves.Count} 个存档：");
            foreach (var s in saves.Take(8))
                sb.Append($"\n• {s.LevelName}（{FormatSize(s)}，位于 {s.Location}）");
            sb.Append("\n回复「搬家 存档名 到 实例名」（或「到 全局」），我先备份再搬。");
            return new NovaReply(sb.ToString(), null, false);
        }

        var hit = saves.FirstOrDefault(s => s.FolderName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                            s.LevelName.Contains(name, StringComparison.OrdinalIgnoreCase))
                  ?? saves.FirstOrDefault(s => name.Contains(s.LevelName, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return new NovaReply($"没有找到存档「{name}」。可用：{string.Join("、", saves.Take(6).Select(s => s.LevelName))}。",
                null, false);

        // 解析目标：空=列出可选目标；「全局」；实例 id 前缀匹配
        if (target.Length == 0)
        {
            var versions = Directory.Exists(Path.Combine(dotMc, "versions"))
                ? Directory.EnumerateDirectories(Path.Combine(dotMc, "versions")).Select(Path.GetFileName).ToList()
                : [];
            return new NovaReply(
                $"把「{hit.LevelName}」搬到哪？回复「搬家 {name} 到 实例名」，或「到 全局」。\n" +
                $"可用实例：{string.Join("、", versions.Take(8))}", null, false);
        }

        string dstDir;
        string dstText;
        if (target.Contains("全局") || target.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            dstDir = Path.Combine(dotMc, "saves");
            dstText = "全局 saves";
        }
        else
        {
            var verDir = Path.Combine(dotMc, "versions");
            var matched = Directory.Exists(verDir)
                ? Directory.EnumerateDirectories(verDir)
                    .FirstOrDefault(d => Path.GetFileName(d).StartsWith(target, StringComparison.OrdinalIgnoreCase))
                : null;
            if (matched is null)
                return new NovaReply($"没有找到实例「{target}」。先确认版本页里的实例名。", null, false);
            dstDir = Path.Combine(matched, "saves");
            dstText = $"实例 {Path.GetFileName(matched)} 的隔离存档";
        }

        var dstPath = Path.Combine(dstDir, hit.FolderName);
        if (string.Equals(hit.FullPath, dstPath, StringComparison.OrdinalIgnoreCase))
            return new NovaReply($"「{hit.LevelName}」已经在 {dstText} 里了，不用搬。", null, false);

        var targetDir = dstDir;
        var finalPath = dstPath;
        var locText = dstText;
        var plan = new NovaPlan(Guid.NewGuid().ToString("N")[..12],
            $"存档搬家：{hit.LevelName} → {locText}",
            new List<NovaActionItem>
            {
                new($"备份 {hit.LevelName}", $"压缩到 backups/{hit.FolderName}-<时间>.zip（出问题随时还原）"),
                new("移动存档目录", $"{hit.Location} → {locText}（目标已存在同名目录则中止）"),
            },
            _ => ExecuteSaveMoveAsync(hit.FullPath, targetDir, finalPath, dotMc, hit.FolderName));
        await Task.CompletedTask;
        return new NovaReply($"「{hit.LevelName}」（{FormatSize(hit)}）要搬到 {locText}，确认后我动手：", plan, false);
    }

    private async Task<string> ExecuteSaveMoveAsync(
        string srcPath, string dstDir, string dstPath, string dotMc, string folderName)
    {
        // 1. 备份 zip（防翻车，移动前必做）
        var backupDir = Path.Combine(dotMc, "backups");
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var safeName = string.Concat(folderName.Split(Path.GetInvalidFileNameChars()));
        var zipPath = Path.Combine(backupDir, $"{safeName}-{stamp}.zip");
        await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(srcPath, zipPath));

        // 2. 移动（同名目录存在则中止——不覆盖用户数据）
        Directory.CreateDirectory(dstDir);
        if (Directory.Exists(dstPath))
            throw new InvalidOperationException($"目标已存在同名存档目录：{dstPath}（未移动，备份在 {zipPath}）");
        Directory.Move(srcPath, dstPath);
        return $"已搬家：{folderName} → {dstPath}\n备份：{zipPath}";
    }

    private List<SaveLocation> CollectSaves(string dotMc)
    {
        var result = new List<SaveLocation>();
        var global = Path.Combine(dotMc, "saves");
        if (Directory.Exists(global))
            result.AddRange(ListSaveDirs(global, "全局 saves"));
        var versionsDir = Path.Combine(dotMc, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var ver in Directory.EnumerateDirectories(versionsDir))
            {
                var saves = Path.Combine(ver, "saves");
                if (Directory.Exists(saves))
                    result.AddRange(ListSaveDirs(saves, $"实例 {Path.GetFileName(ver)}"));
            }
        }
        return result;
    }

    private static List<SaveLocation> ListSaveDirs(string savesDir, string location) =>
        Directory.EnumerateDirectories(savesDir)
            .Where(d => File.Exists(Path.Combine(d, "level.dat")))
            .Select(d => new SaveLocation(Path.GetFileName(d), Path.GetFileName(d), location, d))
            .ToList();

    private static string FormatSize(SaveLocation s)
    {
        try
        {
            long size = 0;
            foreach (var f in Directory.EnumerateFiles(s.FullPath, "*", SearchOption.AllDirectories))
                size += new FileInfo(f).Length;
            return size > 1024 * 1024 * 1024 ? $"{size / 1024.0 / 1024 / 1024:F1} GB" : $"{size / 1024.0 / 1024:F0} MB";
        }
        catch
        {
            return "大小未知";
        }
    }

    private static string HelpText() =>
        "我是 Nova——被允许动手的 AI 助手。你说一句人话，我打理启动器里的脏活累活；所有落盘动作先给确认清单，点头才执行。\n" +
        "现在就能用的：\n" +
        "1. 崩溃自愈 —— 「崩了」「装了光影一直闪退」，我读崩溃报告定位问题\n" +
        "2. 游戏体检 —— 「游戏体检」，发现损坏文件后回复「修复 版本号」，我列清单你点头\n" +
        "3. 兼容侦探 —— 「检查模组冲突」，回复「处理冲突」我把停用清单列给你确认\n" +
        "4. 开箱即玩 —— 「开箱即玩」，我按你的机器生成傻瓜配置，确认后一键应用\n" +
        "5. 找整合包 —— 「找个 1.20.1 的优化整合包」\n" +
        "第三阶段「Nova 时刻」（高光剪辑）与第四阶段「游戏内在场」在开发中（规划中）。";

    private static string LocalFallback(string message) =>
        $"这句我没听懂（「{TrimForEcho(message)}」）。我能动手的范围：崩了帮我看看 / 游戏体检 / 检查模组冲突 / 开箱即玩 / 找整合包。" +
        "接了云端模型后，闲聊我也能接住。";

    private static string TrimForEcho(string s) => s.Length <= 24 ? s : s[..24] + "…";

    // ── LLM 降级链 · 第 1 级：用户自填 Key（OpenAI 兼容协议，零成本，多模型档案取当前） ──
    private static async Task<string?> UserLlmChatAsync(
        LauncherSettings settings, string message,
        IReadOnlyList<NovaChatTurn>? history, CancellationToken ct, int? maxTokens = null)
    {
        var profile = settings.ActiveLlmProfile();
        var baseUrl = profile?.BaseUrl?.Trim().TrimEnd('/');
        var apiKey = profile?.ApiKey?.Trim();
        var model = profile?.Model?.Trim();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model))
            return null; // 未配置 → 静默降级到下一级

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        if (history is { Count: > 0 })
            messages.AddRange(history.Select(t => new { t.Role, t.Content }));
        messages.Add(new { role = "user", content = message });

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { model, messages, max_tokens = maxTokens }, JsonOpts),
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
    }

    /// <summary>Nova 人格：少折腾多玩会；具体不空谈；确认后执行；不臆造事实；简短回答。</summary>
    private const string SystemPrompt =
        "你是 Nova，Minecraft 启动器 StarCloud 内置的 AI 助手，被允许动手但克制：" +
        "给方案要具体到数值和步骤，不泛泛而谈；涉及改文件的方案先给确认清单等用户点头；" +
        "不编造价格、日期和性能数据；回答尽量简短，能一句话说清的不写长篇；" +
        "语气简短、接地气，用简体中文。";

    // ── LLM 降级链 · 第 2 级：StarCloud 云端（会话 API Key；服务未部署自动回退） ──

    /// <summary>
    /// 探活：<c>GET /v1/health</c> 的 <c>llm_active</c> 非空才认为云端聊天可用（规格 §4）。
    /// 上游模型未接入时聊天必返 <c>501</c>，这里先拦掉，省一次无谓请求。
    /// </summary>
    private static async Task<bool> CloudLlmReadyAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(StarcloudIdService.CloudBaseUrl) };
            var json = await http.GetStringAsync("/v1/health", cts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("llm_active", out var active)
                   && active.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(active.GetString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 探活超时 → 视为云端聊天不可用
        }
    }

    private static async Task<string> CloudChatAsync(
        StarcloudSession session, string message,
        IReadOnlyList<NovaChatTurn>? history, CancellationToken ct,
        bool fullLog = false, int? maxTokens = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StarcloudIdService.CloudBaseUrl) };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.ApiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new CloudChatRequest(message, CloudMessages(history), fullLog, maxTokens), JsonOpts),
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NovaCloudException(DescribeCloudError(resp.StatusCode, json));

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("text").GetString()
                   ?? throw new InvalidOperationException("云端 AI 响应缺少 text");
        return text;
    }

    /// <summary>
    /// 组装 <c>messages</c>：服务端不注入任何 system prompt（规格 §3.2），人格必须由启动器放在第一条；
    /// 历史轮次依次在后，当前输入仍走 <c>message</c> 字段，由服务端追加为最后一条 user 消息。
    /// </summary>
    private static IReadOnlyList<NovaChatTurn> CloudMessages(IReadOnlyList<NovaChatTurn>? history)
    {
        var messages = new List<NovaChatTurn>(1 + (history?.Count ?? 0))
        {
            new("system", SystemPrompt),
        };
        if (history is { Count: > 0 }) messages.AddRange(history);
        return messages;
    }

    /// <summary>
    /// 向 StarCloud 云端 /v1/chat 发送流式请求，按 SSE 解析逐字增量。
    /// 若服务端未返回 event-stream，则按原 JSON 路径一次性 yield text。
    /// </summary>
    private static async IAsyncEnumerable<string> CloudChatStreamAsync(
        StarcloudSession session, string message,
        IReadOnlyList<NovaChatTurn>? history, [EnumeratorCancellation] CancellationToken ct,
        bool fullLog = false, int? maxTokens = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StarcloudIdService.CloudBaseUrl) };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.ApiKey);
        var body = JsonSerializer.Serialize(new
        {
            message,
            messages = CloudMessages(history),
            full_log = fullLog,
            max_tokens = maxTokens,
            stream = true,
        }, JsonOpts);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            throw new NovaCloudException(DescribeCloudError(resp.StatusCode, json));
        }

        // 解析统一走 OpenAiSseClient（兼容 OpenAI chunk 与云端旧格式顶层 delta/text）
        await foreach (var evt in OpenAiSseClient.ParseSseResponseAsync(resp, ct))
        {
            if (evt.ContentDelta is { Length: > 0 } d) yield return d;
        }
    }

    /// <summary>
    /// 向用户自填 Key 的 /chat/completions 发送流式请求，按 SSE 解析 OpenAI 兼容增量。
    /// 若服务端未返回 event-stream，则按原 JSON 路径一次性 yield content。
    /// </summary>
    private static async IAsyncEnumerable<string> UserLlmChatStreamAsync(
        LauncherSettings settings, string message,
        IReadOnlyList<NovaChatTurn>? history, [EnumeratorCancellation] CancellationToken ct,
        int? maxTokens = null)
    {
        var profile = settings.ActiveLlmProfile();
        var baseUrl = profile?.BaseUrl?.Trim().TrimEnd('/');
        var apiKey = profile?.ApiKey?.Trim();
        var model = profile?.Model?.Trim();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model))
            yield break;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        if (history is { Count: > 0 })
            messages.AddRange(history.Select(t => new { t.Role, t.Content }));
        messages.Add(new { role = "user", content = message });

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { model, messages, max_tokens = maxTokens, stream = true }, JsonOpts),
            Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        await foreach (var evt in OpenAiSseClient.ParseSseResponseAsync(resp, cts.Token))
        {
            if (evt.ContentDelta is { Length: > 0 } d) yield return d;
        }
    }

    /// <summary>云端返回的业务错误：Message 已是用户可读中文（规格 §3.4，不暴露原始 code）。</summary>
    private sealed class NovaCloudException(string message) : Exception(message);

    /// <summary>把服务端错误信封 <c>{ error: { code, message } }</c> 翻译成一句人话。</summary>
    private static string DescribeCloudError(HttpStatusCode status, string json)
    {
        var code = string.Empty;
        var serverMessage = string.Empty;
        int? retryAfter = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    code = c.GetString() ?? string.Empty;
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    serverMessage = m.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("retry_after", out var ra) && ra.TryGetInt32(out var seconds))
                retryAfter = seconds;
        }
        catch { /* 响应体不是 JSON → 走状态码兜底 */ }

        // 429 排队超时：尊重服务端给出的 retry_after，按它给出等待秒数
        if (status == HttpStatusCode.TooManyRequests && retryAfter is int wait and > 0)
            return $"当前排队较多，{wait} 秒后再试一次。";

        return (status, code) switch
        {
            (HttpStatusCode.BadRequest, _) or (_, "empty_message") => "请先粘贴日志或描述问题。",
            (HttpStatusCode.Unauthorized, _) or (_, "invalid_api_key") or (_, "invalid_token")
                => "登录已过期，请在侧栏 Nova 页重新登录 Starcloud ID。",
            (HttpStatusCode.Forbidden, _) or (_, "account_banned") => "账号状态异常，请联系客服。",
            (HttpStatusCode.PaymentRequired, _) or (_, "insufficient_credits")
                => "积分不足，去「支持」页购买或订阅后再试。",
            (HttpStatusCode.TooManyRequests, _) or (_, "queue_timeout") => "当前排队较多，稍等片刻再试一次。",
            (_, "model_not_configured") => "Nova 服务未就绪，请稍后再试。",
            (_, "provider_error") or (_, "empty_response") => "模型暂时不可用，请稍后重试。",
            _ => serverMessage.Length > 0
                ? serverMessage
                : $"云端暂时不可用（{(int)status}），请稍后重试。",
        };
    }

    private sealed record CloudChatRequest(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("messages")] IReadOnlyList<NovaChatTurn> Messages,
        [property: JsonPropertyName("full_log")] bool FullLog,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens);
}
