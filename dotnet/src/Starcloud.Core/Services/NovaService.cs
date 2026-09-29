// Nova — 被允许动手的 AI 助手（按 NOVA_DESIGN.md v1.0 重建）。
//
// 能力阶段（信任递进，每一步只在用户点头之后）：
//   阶段一 顾问：崩溃自愈 / 兼容侦探 / 游戏体检 / 整合包推荐 —— 只给建议，不落盘
//   阶段二 执行：版本修复 / 模组停用 / 开箱即玩配置 —— 先给确认清单，点头才执行，执行写操作日志
//   （阶段三「主动」Nova 时刻、阶段四「在场」游戏内浮窗：视觉通路未上线，仅提供许可层开关与说明）
//
// 产品基石（不可跳过）：任何落盘动作先出 NovaPlan 确认清单，用户确认后执行并记入可审计的操作日志。
//
// LLM 降级链（严禁请求因无额度而失败卡死；三级依次尝试）：
//   1. 用户自填 Key（OpenAI 兼容 /chat/completions，零成本；配了就用它）
//   2. Starcloud 云端中转（Starcloud ID 下发的 API Key → /v1/ai/*，每用户每周 100 万 token，服务端记账）
//   3. 本地规则引擎兜底（能力指令不走 LLM，永不失败）
// 合规说明：模型凭据不下发到客户端，上游合约由 Starcloud ID 服务端持有，客户端只拿本用户的 Key。
// 服务端实现要求见 docs/designs/2026-09-21-nova-gateway-server-spec.md。
// 额度：客户端只做「显示 + 讲清楚 + 降级」，不本地计数（多端不一致，也挡不住刷）。

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

/// <summary>一轮对话（随请求携带上下文）</summary>
public sealed record NovaChatTurn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed class NovaService
{
    private readonly ModrinthService _mods;
    private readonly GameDiagnosticsService _diag;
    private readonly ModConflictService _conflicts;
    private readonly NovaPermissionStore _permissions;
    private readonly NovaActionLog _log;
    private readonly NovaMomentsService _moments;
    private readonly NovaInviteService _invites;
    private readonly SaveService _saves;
    private readonly NovaAgentLoop? _agent;

    /// <summary>中文搜索词 → 英文（可选）：本地推荐/搜索要用英文才搜得到东西</summary>
    private readonly SearchTranslationService? _translation;

    /// <summary>Starcloud ID 会话来源：云端中转的 API Key 从这里取（未接入时为 null，只走自填 Key / 本地兜底）</summary>
    private readonly StarcloudIdService? _starcloudId;

    /// <summary>最近一次体检结果（用于「修复 <版本号>」的指代解析）</summary>
    private DiagReport? _lastDiag;

    /// <summary>当前实例上下文（VersionsPage/HomePage 选中时同步；回退 settings.LastVersion）</summary>
    private string? _currentInstance;

    // 共享 HttpClient：每条消息 new 一个会烧端口 + 每次重做 TLS 握手（性能修复）
    private static readonly HttpClient SharedHttp = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // max_tokens 未指定时不发该字段
        // 兜底：匿名对象里的 PascalCase 也会被压成 snake_case，服务端不会再收到 "Content" 这类键
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public NovaService(
        ModrinthService mods,
        GameDiagnosticsService diag, ModConflictService conflicts,
        NovaPermissionStore permissions, NovaActionLog log,
        NovaMomentsService moments, NovaInviteService invites, SaveService saves,
        NovaToolRegistry? tools = null, StarcloudIdService? starcloudId = null,
        SearchTranslationService? translation = null)
    {
        _mods = mods;
        _diag = diag;
        _conflicts = conflicts;
        _permissions = permissions;
        _log = log;
        _moments = moments;
        _invites = invites;
        _saves = saves;
        _starcloudId = starcloudId;
        _translation = translation;
        if (tools is not null) _agent = new NovaAgentLoop(this, tools);
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
            return new NovaReply("请描述要处理的问题，例如「启动崩溃，帮我看看」「找一个 1.20.1 的优化整合包」。", null, false);

        var local = await SafeRouteLocalAsync(message, settings, ct);
        if (local is not null) return local;

        // 崩溃日志裁剪：只把因果链（栈顶 / Caused by: / System Details / Suspected Mods）发给模型；
        // fullLog=true 时原样发送（用户已知会更贵）。非崩溃文本裁剪函数会逐字节原样返回。
        var body = message.Trim();
        var trim = fullLog ? CrashLogTrimmer.Passthrough(body) : CrashLogTrimmer.TrimCrashLog(body);
        var sendHistory = TrimHistory(history, fullLog);

        // 未命中能力指令 → LLM 降级链：用户自接模型 → 云端中转 → 本地兜底
        LlmStreamException? llmFailure = null;
        try
        {
            var text = await UserLlmChatAsync(settings, trim.Text, sendHistory, ct, maxTokens);
            if (text is not null) return new NovaReply(text, null, true, trim);
        }
        catch (OperationCanceledException) { throw; }
        catch (LlmStreamException ex) { llmFailure = ex; } // 记下原因：下面两通道都没成时讲给用户
        catch { /* 用户 Key 失效/欠费 → 降级本地兜底，不卡死 */ }

        // 第 2 级：Starcloud 云端中转（Starcloud ID 登录时下发的 Key，服务端按周记账）
        NovaGatewayException? gatewayNotice = null;
        if (GatewayKey(settings) is { } gatewayKey)
        {
            try
            {
                var text = await NovaGatewayClient.ChatAsync(
                    gatewayKey, BuildMessages(trim.Text, sendHistory), NovaGatewayClient.DefaultModel, maxTokens, ct);
                if (!string.IsNullOrWhiteSpace(text)) return new NovaReply(text, null, true, trim);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 网关超时 → 本地兜底 */ }
            catch (OperationCanceledException) { throw; }
            catch (NovaGatewayException ex) when (ex.StatusCode > 0)
            {
                // 记下来，但先不返回：用户自己接了模型时，他自己的报错更接近真相
                LauncherLog.Write("nova", $"云端中转失败：{Describe(ex)}");
                gatewayNotice = ex;
            }
            catch (Exception ex) { LauncherLog.Write("nova", $"云端中转失败：{Describe(ex)}"); }
        }

        // 自接模型的报错优先于云端：用户配的是哪个通道，就先说哪个通道的问题
        if (llmFailure is not null && settings.ActiveLlmProfile() is { } badProfile)
            return new NovaReply(LlmNotice(llmFailure, badProfile), null, false, trim);

        // 额度用尽 / 未授权是「用户需要知道」的状态：讲清楚并给出下一步，不静默冒充兜底
        if (gatewayNotice is not null)
            return new NovaReply(GatewayNotice(gatewayNotice), null, false, trim);

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
        Action<NovaReply>? onFullReply = null,
        Action<string>? onToolStarted = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            yield return "请描述要处理的问题，例如「启动崩溃，帮我看看」「找一个 1.20.1 的优化整合包」。";
            yield break;
        }

        var local = await SafeRouteLocalAsync(message, settings, ct);
        if (local is not null)
        {
            onFullReply?.Invoke(local);
            yield return local.Text;
            yield break;
        }

        var body = message.Trim();
        var trim = fullLog ? CrashLogTrimmer.Passthrough(body) : CrashLogTrimmer.TrimCrashLog(body);
        var sendHistory = TrimHistory(history, fullLog);

        // Agent 循环优先（本地能力路由之后）：LLM 具备工具调用能力时让 Nova 真正动手——
        // 只读工具立即执行、写操作出 NovaPlan 确认清单。零事件（无可用通道）→ 回退纯文本链。
        if (_agent is not null)
        {
            var agentStarted = false;
            await foreach (var evt in _agent.RunAsync(trim.Text, settings, sendHistory, maxTokens, ct))
            {
                agentStarted = true;
                switch (evt)
                {
                    case AgentTextDelta d:
                        yield return d.Delta;
                        break;
                    case AgentToolStarted t:
                        // UI 动作提示：只给工具名（界面自己翻成「正在搜索 Modrinth 资源…」）。
                        // 参数预览属于内部细节，不进对话，也不该出现在气泡里。
                        onToolStarted?.Invoke(t.Tool);
                        break;
                    case AgentPlanProposed p:
                        // 文本增量已进气泡，这里只把确认清单挂到消息上（UI 渲染确认卡）
                        onFullReply?.Invoke(new NovaReply(p.Text, p.Plan, true, trim));
                        break;
                }
            }
            if (agentStarted) yield break;
        }

        // 纯文本链：用户自接模型 → 云端中转。任一级产出增量即结束；
        // 已经出过字又断流时如实说明（半截回答不能冒充完整回答）。
        var channels = new List<IAsyncEnumerable<string>>();
        if (HasUserProfile(settings))
            channels.Add(UserLlmChatStreamAsync(settings, trim.Text, sendHistory, ct, maxTokens));
        if (GatewayKey(settings) is { } streamKey)
            channels.Add(NovaGatewayClient.StreamDeltasAsync(
                streamKey, BuildMessages(trim.Text, sendHistory), maxTokens, ct));

        NovaGatewayException? notice = null;
        LlmStreamException? llmNotice = null;
        foreach (var channel in channels)
        {
            var produced = false;
            var truncated = false;
            await using var en = channel.GetAsyncEnumerator(ct);
            while (true)
            {
                bool has;
                try { has = await en.MoveNextAsync(); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { truncated = produced; break; }
                catch (OperationCanceledException) { throw; }
                catch (NovaGatewayException ex) when (ex.StatusCode > 0)
                {
                    LauncherLog.Write("nova", $"云端中转通道失败：{Describe(ex)}");
                    notice ??= ex; break;
                }
                // 自接模型失败：记下状态码与上游原文，两条通道都没出字时讲给用户听
                catch (LlmStreamException ex)
                {
                    LauncherLog.Write("nova", $"自接模型通道失败：{Describe(ex)}");
                    llmNotice ??= ex; break;
                }
                catch (Exception ex) // 网络层失败 → 换下一级（没出过字就是静默降级）
                {
                    LauncherLog.Write("nova", $"对话通道失败：{Describe(ex)}");
                    truncated = produced; break;
                }
                if (!has) break;
                if (string.IsNullOrEmpty(en.Current)) continue;
                produced = true;
                yield return en.Current;
            }

            if (produced)
            {
                if (truncated) yield return "\n（连接中断，以上是已经收到的部分；可以再问一次。）";
                yield break;
            }
        }

        // 配了自接模型却打不通 → 直接说清是哪一步错了（模型名 / Key / 地址），别再退回兜底文案
        if (llmNotice is not null && settings.ActiveLlmProfile() is { } badProfile)
        {
            yield return LlmNotice(llmNotice, badProfile);
            yield break;
        }

        // 有额度/授权类失败 → 讲清楚原因与下一步；否则才是「没匹配到本地功能」
        if (notice is not null)
        {
            yield return GatewayNotice(notice);
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
    /// <summary>
    /// 本地能力路由的兜底壳：本地引擎（搜索 / 诊断 / 扫目录）出错不能让整条对话变成「出错了」。
    /// 记一条日志后返回 null，上层继续走 LLM / Agent——用户至少还能拿到回答。
    /// </summary>
    private async Task<NovaReply?> SafeRouteLocalAsync(
        string message, LauncherSettings settings, CancellationToken ct)
    {
        try { return await RouteLocalAsync(message, settings, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LauncherLog.Write("nova", $"本地能力路由失败（转交模型处理）：{Describe(ex)}");
            return null;
        }
    }

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
                    ? "暂无记录。启动一局游戏后，退出时会自动生成本局汇总。"
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
        {
            // 有可用模型时交给 Agent：它会先翻译关键词、按版本/加载器搜、还能直接装；
            // 本地引擎只会按下载量列 5 条（中文关键词在 Modrinth 上基本搜不到）。没模型时才本地兜底。
            if (HasLlmChannel(settings)) return null;
            return new NovaReply(await FindModpacksTextAsync(input, settings, ct), null, false);
        }

        if (lower.Contains("时刻") || lower.Contains("录屏") || lower.Contains("录像") ||
            lower.Contains("录制"))
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
                ("outofmemoryerror", $"内存不足。{memHint}；回复「开箱即玩」可获取按本机硬件生成的配置清单。"),
                ("heap space", $"堆内存溢出。{memHint}；回复「开箱即玩」可获取按本机硬件生成的配置清单。"),
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
            sb.Append($"回复「修复 {fixable[0]}」我会给出确认清单，确认后才会执行修复" +
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
            sb.Append("\n回复「处理冲突」我会列出停用清单，确认后才会执行。");
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
                "每个版本的存档/模组/配置单独存放，互不污染"),
        };
        var plan = new NovaPlan(Guid.NewGuid().ToString("N")[..12],
        "配置推荐：按本机硬件生成",
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
        return new NovaReply($"已读取本机配置（物理内存 {physMb / 1024} GB）。推荐配置如下，确认后写入启动器设置：", plan, false);
    }

    // ── 能力 5：查找整合包（阶段一 · 顾问，Modrinth） ──
    private async Task<string> FindModpacksTextAsync(
        string raw, LauncherSettings settings, CancellationToken ct)
    {
        var query = ExtractQuery(raw);
        // 当前实例的**原版版本号**（实例名不是版本号：直接当 facets 用会让 Modrinth 回 400）
        var gameVer = InstanceVersion.BaseGameVersion(settings.DotMinecraft, settings.LastVersion);
        // Modrinth / CurseForge 只有英文名：中文关键词先翻一道，否则搜出来永远是 0 条
        if (_translation is not null && SearchTranslationService.ContainsCjk(query))
        {
            try { query = (await _translation.TranslateAsync(query, ct)).Effective; }
            catch { /* 翻译不可用就用原词搜，别让推荐挂在这 */ }
        }

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
        "给我", "有没有", "有什么", "我",   // 别收「要」：要塞（stronghold）这类正经词会被吃掉
        "的", "了", "吗", "呢", "？", "?", "！", "!", "。", "，", ",", "、",
    ];

    private static string ExtractQuery(string raw)
    {
        var s = raw.ToLowerInvariant();
        foreach (var w in StopWords) s = s.Replace(w, " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    // ── Nova 时刻概览（阶段三「主动」：日志观察 + 硬件录屏） ──
    private string MomentsOverviewText()
    {
        var (recT, recB) = NovaPermissionStore.Describe(NovaPermissionStore.Recording);

        var sb = new StringBuilder();
        sb.AppendLine(_moments.IsSessionActive
            ? $"我正在观察 {_moments.SessionVersion} 的游戏日志（从 {_moments.SessionStart:HH:mm} 开始），" +
              "进度、挑战与异常退出都会被记录。"
            : "当前没有进行中的游戏会话；游戏启动后我自动开始观察。");
        sb.AppendLine("时刻卡记录的是文字事实（坐标、时间、事件）；画面由录屏保存成 mp4，两者对得上时间。");

        var pending = _moments.Candidates.Where(c => c.State == "proposed").ToList();
        if (pending.Count > 0)
        {
            sb.AppendLine($"有 {pending.Count} 个待确认时刻（在 Nova 页处理）：");
            foreach (var c in pending.Take(3))
                sb.Append($"• {c.TimeText} {c.Caption}\n");
        }
        else if (_moments.Moments.Count > 0)
        {
            sb.AppendLine($"已记录 {_moments.Moments.Count} 个时刻；发送「战报」查看最近一局汇总。");
        }

        sb.Append("操作许可（默认全部关闭，开启即显式授权）：\n" +
                  $"• {recT}：{recB}");
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
                "确认清单如下，确认后开始安装，装好后可直接进服：",
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
                "带光影时先关闭光影对比；回复「开箱即玩」可获取调优确认清单。");
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
        "我是 Nova，Starcloud 启动器内置的助手，可以在你确认后执行操作：所有会改动文件的操作都会先列出确认清单，确认后才执行，并记入操作记录。\n" +
        "当前可用的功能：\n" +
        "1. 崩溃排查 —— 发送「崩了」「装了光影后一直闪退」，或直接贴崩溃日志，我会读取崩溃报告定位原因\n" +
        "2. 游戏体检 —— 发送「游戏体检」检查文件完整性；发现问题后发送「修复 版本号」，确认清单后执行\n" +
        "3. 模组冲突 —— 发送「检查模组冲突」定位冲突；发送「处理冲突」列出停用清单待确认\n" +
        "4. 配置推荐 —— 发送「开箱即玩」，按本机硬件生成推荐配置，确认后应用\n" +
        "5. 资源查找 —— 发送「找一个 1.20.1 的优化整合包」\n" +
        "6. 录屏 —— 发送「开始录屏」，从显卡缓冲区采集并硬件编码存到本地（需先在 Nova 页开启录屏许可）\n" +
        "游戏内浮窗仍在开发中。";

    private static string LocalFallback(string message) =>
        $"没有匹配到本地功能（输入：「{TrimForEcho(message)}」）。可以试试：崩溃排查 / 游戏体检 / 检查模组冲突 / 开箱即玩 / 找整合包。" +
        "其他问题可以在 Nova 页「新增模型」接入你自己的模型后再问。";

    private static string TrimForEcho(string s) => s.Length <= 24 ? s : s[..24] + "…";

    // ── LLM 降级链 · 通道判定 / 消息组装 / 失败文案（三级共用） ──

    /// <summary>
    /// 用户自填模型档案：地址 + 模型名齐全就算配置好（Key 可空，本地服务不要 Key）。
    /// 规则见 <see cref="LauncherSettings.ReadyLlmProfile"/>。
    /// </summary>
    private static NovaLlmProfile? UserProfileReady(LauncherSettings settings) =>
        settings.ReadyLlmProfile();

    private static bool HasUserProfile(LauncherSettings settings) => UserProfileReady(settings) is not null;

    /// <summary>
    /// 有没有可用的模型通道（自接模型 / 云端中转）：本地引擎与 Agent 的分工线。
    /// 有模型 → 内容搜索这类「要动脑」的活交给 Agent；没模型 → 本地引擎兜底，保证离线也能答。
    /// </summary>
    private bool HasLlmChannel(LauncherSettings settings) =>
        UserProfileReady(settings) is not null || GatewayKey(settings) is not null;

    /// <summary>
    /// 云端中转的可用 Key：开关打开 + 云端通道已登录（测试通道的本地假 Key 不参与）。
    /// 不可用 → null（调用方直接跳过这一级）。
    /// </summary>
    private string? GatewayKey(LauncherSettings settings) =>
        NovaGatewayClient.CanUse(settings, _starcloudId?.Current) ? _starcloudId!.Current!.ApiKey : null;

    /// <summary>发给模型的完整消息数组：system 人格 + 已裁剪的历史 + 本轮输入。</summary>
    internal static List<object> BuildMessages(string message, IReadOnlyList<NovaChatTurn>? history)
    {
        var messages = new List<object> { new ChatMessage("system", SystemPrompt) };
        if (history is { Count: > 0 })
            // 空正文轮（计划卡轮 / 中断轮）会被服务端按 400 拒绝，发之前先剔掉
            messages.AddRange(history
                .Where(t => !string.IsNullOrWhiteSpace(t.Content))
                .Select(t => new ChatMessage(t.Role, t.Content)));
        messages.Add(new ChatMessage("user", message));
        return messages;
    }

    /// <summary>
    /// 自接模型通道失败的用户可读说明：把上游状态码与原文摊开，并给出下一步。
    /// 以前这里静默降级，用户只看到「没匹配到本地功能」，配错了也无从下手。
    /// </summary>
    private static string LlmNotice(LlmStreamException ex, NovaLlmProfile profile)
    {
        var name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Model : profile.Name;
        var status = ex.StatusCode;
        var why = status > 0
            ? $"你自己的模型「{name}」这次没能应答（{DescribeLlmFailure(status)}）。"
            : $"连不上你的模型「{name}」。";
        var detail = LlmEndpoint.Explain(status, LlmEndpoint.Summarize(ex.ResponseBody), profile.BaseUrl);
        return $"{why}\n{detail}";
    }

    private static string DescribeLlmFailure(int status) => status switch
    {
        400 => "请求被拒绝",
        401 or 403 => "Key 无效",
        404 => "地址不对",
        429 => "被限流",
        >= 500 => "上游故障",
        _ => $"HTTP {status}",
    };

    /// <summary>
    /// 云端中转失败的用户可见说明：原因（额度用尽 / 未授权 / 上游故障）+ 下一步能做什么。
    /// 「额度用尽」还额外说明本地能力不受影响——降级是产品承诺，不是道歉。
    /// </summary>
    private static string GatewayNotice(NovaGatewayException ex)
    {
        var tail = ex.IsQuotaExceeded
            ? "本地能力（崩溃排查 / 游戏体检 / 模组冲突 / 资源查找）不受影响，可以直接继续用；也可以在 Nova 页接入自己的模型接着问。"
            : "可以在 Nova 页查看云端额度与登录状态；换成自己的模型也能继续问。";
        return ex.Message + "\n" + tail;
    }

    // ── LLM 降级链 · 第 1 级：用户自填 Key（OpenAI 兼容协议，零成本，多模型档案取当前） ──
    private static async Task<string?> UserLlmChatAsync(
        LauncherSettings settings, string message,
        IReadOnlyList<NovaChatTurn>? history, CancellationToken ct, int? maxTokens = null)
    {
        if (UserProfileReady(settings) is not { } profile)
            return null; // 未配置 → 静默降级到下一级
        var url = LlmEndpoint.ChatUrl(profile.BaseUrl);
        var apiKey = profile.ApiKey.Trim();
        var model = profile.Model.Trim();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var messages = BuildMessages(message, history);

        var http = SharedHttp;
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(
            OpenAiSseClient.BuildChatRequestJson(model, messages, tools: null, maxTokens, stream: false),
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new LlmStreamException(
                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {LlmEndpoint.Summarize(json)}",
                (int)resp.StatusCode, json);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
    }

    /// <summary>Nova 人格：结论先行；短；具体不空谈；确认后执行；不臆造事实；语气平实。</summary>
    internal const string SystemPrompt =
        "你是 Nova，Minecraft 启动器 StarCloud 内置的助手，可以在用户确认后执行操作：" +
        "回答控制在 1~2 句（最多 3 条要点、120 字以内），只有用户明确要「详细步骤 / 清单」时才展开；" +
        "不复述工具或日志返回的原文，只给结论和下一步；不写「还需要什么帮助吗」这类客套；" +
        "回答结论先行，给出的方案要具体到文件、数值和步骤，不泛泛而谈；" +
        "涉及改动文件的方案先列出确认清单等用户确认，不擅自执行；" +
        "不编造价格、日期和性能数据，不确定就直说；" +
        "语气平实、专业，像产品内置功能那样说明，不寒暄、不感叹、不用夸张比喻；" +
        "用简体中文。";

    /// <summary>
    /// 向用户自填 Key 的 /chat/completions 发送流式请求，按 SSE 解析 OpenAI 兼容增量。
    /// 若服务端未返回 event-stream，则按原 JSON 路径一次性 yield content。
    /// </summary>
    private static async IAsyncEnumerable<string> UserLlmChatStreamAsync(
        LauncherSettings settings, string message,
        IReadOnlyList<NovaChatTurn>? history, [EnumeratorCancellation] CancellationToken ct,
        int? maxTokens = null)
    {
        if (UserProfileReady(settings) is not { } profile)
            yield break;
        var url = LlmEndpoint.ChatUrl(profile.BaseUrl);
        var apiKey = profile.ApiKey.Trim();
        var model = profile.Model.Trim();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var messages = BuildMessages(message, history);

        var http = SharedHttp;
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(
            OpenAiSseClient.BuildChatRequestJson(model, messages, tools: null, maxTokens, stream: true),
            Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            // 失败原因必须能到达用户：带上状态码与上游原文，交给上层翻译成人话
            var err = await resp.Content.ReadAsStringAsync(cts.Token);
            throw new LlmStreamException(
                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {LlmEndpoint.Summarize(err)}",
                (int)resp.StatusCode, err);
        }

        await foreach (var evt in OpenAiSseClient.ParseSseResponseAsync(resp, cts.Token))
        {
            if (evt.ContentDelta is { Length: > 0 } d) yield return d;
        }
    }

    // ── Agent 循环 · LLM 通道（带 tools 的流式调用） ──

    /// <summary>
    /// Agent 循环的一次流式调用：用户自填 Key（OpenAI 兼容 tools）→ Starcloud 云端中转。
    /// 通道按序尝试，前一个在产出任何事件之前失败就安静换下一个；
    /// 全部不可用时零事件返回，调用方回退纯文本链（本地兜底）。
    /// </summary>
    internal async IAsyncEnumerable<SseEvent> AgentLlmStreamAsync(
        IReadOnlyList<object> messages, string toolsJson,
        int? maxTokens, LauncherSettings settings,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(120)); // 工具轮往返更长，放宽到 2 分钟

        var tools = JsonSerializer.Deserialize<JsonElement>(toolsJson);
        var channels = new List<IAsyncEnumerable<SseEvent>>();
        if (UserProfileReady(settings) is { } profile)
            channels.Add(UserAgentChannelAsync(profile, messages, tools, maxTokens, linked.Token));
        if (GatewayKey(settings) is { } gatewayKey)
            channels.Add(NovaGatewayClient.StreamChatAsync(
                gatewayKey, messages, tools, NovaGatewayClient.DefaultModel, maxTokens, linked.Token));

        var channelIndex = 0;
        foreach (var channel in channels)
        {
            var produced = false;
            var label = channelIndex++ == 0 && UserProfileReady(settings) is not null ? "自接模型" : "云端中转";
            await using var en = channel.GetAsyncEnumerator(linked.Token);
            while (true)
            {
                bool has;
                try { has = await en.MoveNextAsync(); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // 本轮超时 → 换下一级
                catch (OperationCanceledException) { throw; }                                    // 用户取消 → 上抛
                catch (Exception ex) when (!produced) // 这个通道打不通（离线 / 无额度 / Key 失效）→ 试下一级
                {
                    LauncherLog.Write("nova", $"{label}通道失败（本轮尚无内容）：{Describe(ex)}");
                    break;
                }
                if (!has) break;
                produced = true;
                yield return en.Current;
            }
            if (produced) yield break; // 已经出过内容，本轮不再换通道
        }
    }

    /// <summary>
    /// 自接模型通道（Agent 工具轮）：先按标准 OpenAI 形状发（带 tools）；
    /// 上游用 400 拒掉请求体时（不少中转 / 本地服务会拒 tools 或直接嫌身体太大），
    /// 自动去掉 tools 再发一次——宁可这一轮没有工具，也要先让用户拿到回答。
    /// 每次失败都写进启动器日志（状态码 + 上游原文），否则用户只看到「400」无从下手。
    /// </summary>
    private async IAsyncEnumerable<SseEvent> UserAgentChannelAsync(
        NovaLlmProfile profile, IReadOnlyList<object> messages, JsonElement tools,
        int? maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = LlmEndpoint.ChatUrl(profile.BaseUrl);
        var key = profile.ApiKey.Trim();
        var model = profile.Model.Trim();

        // ① 标准形状：带 tools
        var produced = false;
        Exception? failure = null;
        await using (var en = OpenAiSseClient
            .StreamChatAsync(SharedHttp, url, key, model, messages, tools, maxTokens, ct)
            .GetAsyncEnumerator(ct))
        {
            while (true)
            {
                bool has;
                try { has = await en.MoveNextAsync(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failure = ex; break; }
                if (!has) break;
                produced = true;
                yield return en.Current;
            }
        }
        if (produced) yield break;

        // ② 400 = 上游不接受这个请求体（不认 tools / 身体太大 / 字段太新）→ 去掉 tools 再发一次
        if (failure is LlmStreamException { StatusCode: 400 })
        {
            LauncherLog.Write("nova",
                $"自接模型「{model}」带工具请求被拒（{Describe(failure)}），去掉工具重试一次");
            await using var plain = OpenAiSseClient
                .StreamChatAsync(SharedHttp, url, key, model, messages, tools: null, maxTokens, ct)
                .GetAsyncEnumerator(ct);
            while (true)
            {
                bool has;
                try { has = await plain.MoveNextAsync(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 重试也没成 → 抛第一次的 400：那才是最接近真相的原因
                    LauncherLog.Write("nova", $"自接模型「{model}」去掉工具重试仍失败：{Describe(ex)}");
                    throw failure;
                }
                if (!has) break;
                produced = true;
                yield return plain.Current;
            }
            yield break;
        }

        if (failure is not null)
        {
            LauncherLog.Write("nova", $"自接模型「{model}」调用失败：{Describe(failure)}");
            throw failure;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        LlmStreamException s => $"HTTP {s.StatusCode} {LlmEndpoint.Summarize(s.ResponseBody)}",
        _ => ex.Message,
    };
}
