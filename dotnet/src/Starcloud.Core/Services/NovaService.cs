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

using System.Net.Http.Headers;
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

/// <summary>Nova 一轮回复：文本 + 可选确认清单（Plan 非空 = 需要用户点头才动手）</summary>
public sealed record NovaReply(string Text, NovaPlan? Plan, bool FromCloud);

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

    /// <summary>最近一次体检结果（用于「修复 <版本号>」的指代解析）</summary>
    private DiagReport? _lastDiag;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public NovaService(
        StarcloudIdService id, ModrinthService mods,
        GameDiagnosticsService diag, ModConflictService conflicts,
        NovaPermissionStore permissions, NovaActionLog log)
    {
        _id = id;
        _mods = mods;
        _diag = diag;
        _conflicts = conflicts;
        _permissions = permissions;
        _log = log;
    }

    /// <summary>发送一句话给 Nova；能力指令走本地引擎（具体不空谈），其余走 LLM 降级链。</summary>
    public async Task<NovaReply> ChatAsync(
        string message, LauncherSettings settings,
        IReadOnlyList<NovaChatTurn>? history = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new NovaReply("说一句人话就行，比如「崩了帮我看看」「找个 1.20.1 的优化整合包」。", null, false);

        var local = await RouteLocalAsync(message, settings, ct);
        if (local is not null) return local;

        // 未命中能力指令 → LLM 降级链
        var session = _id.Current;
        if (session is { Channel: StarcloudIdService.CloudChannel })
        {
            try
            {
                return new NovaReply(await CloudChatAsync(session, message.Trim(), history, ct), null, true);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 云端不可用 → 继续降级 */ }
        }

        try
        {
            var text = await UserLlmChatAsync(settings, message.Trim(), history, ct);
            if (text is not null) return new NovaReply(text, null, true);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 用户 Key 失效/欠费 → 降级本地兜底，不卡死 */ }

        return new NovaReply(LocalFallback(message), null, false);
    }

    // ── 本地能力路由（命中返回 null 表示未命中） ──
    private async Task<NovaReply?> RouteLocalAsync(
        string input, LauncherSettings settings, CancellationToken ct)
    {
        var lower = input.ToLowerInvariant();

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

        if (lower.Contains("开箱即玩") || lower.Contains("小白") || lower.Contains("傻瓜") ||
            lower.Contains("优化配置") || lower.Contains("推荐配置") || lower.Contains("帮我调"))
            return PlanSmoothStart(settings);

        if (lower.Contains("整合包") || lower.Contains("modpack"))
            return new NovaReply(await FindModpacksTextAsync(input, settings, ct), null, false);

        if (lower.Contains("时刻") || lower.Contains("捕捉") || lower.Contains("剪辑") ||
            lower.Contains("分享") || lower.Contains("录像"))
            return new NovaReply(MomentsText(), null, false);

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

    // ── Nova 时刻 / 许可层说明（阶段三「主动」：视觉通路未上线，能力规划中） ──
    private static string MomentsText()
    {
        var (capT, capB) = NovaPermissionStore.Describe(NovaPermissionStore.Capture);
        var (clipT, clipB) = NovaPermissionStore.Describe(NovaPermissionStore.Clip);
        var (shT, shB) = NovaPermissionStore.Describe(NovaPermissionStore.Share);
        return "「Nova 时刻」是下机后的一页游戏纪念卡：我自动挑出的高光瞬间，剪成秒级片段配解说。\n" +
               "它属于第三阶段「主动」能力，视觉通路（帧采集 + 高光判定）还在开发中（规划中，非示意数据）。\n" +
               "动手许可层的三档开关你可以先授权，能力上线后立即生效：\n" +
               $"• {capT}：{capB}\n• {clipT}：{clipB}\n• {shT}：{shB}\n" +
               "默认全部关闭——没开 = 我一个字也不看你的屏幕；开启会记入可审计流水。";
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

    // ── LLM 降级链 · 第 1 级：用户自填 Key（OpenAI 兼容协议，零成本） ──
    private static async Task<string?> UserLlmChatAsync(
        LauncherSettings settings, string message,
        IReadOnlyList<NovaChatTurn>? history, CancellationToken ct)
    {
        var baseUrl = settings.NovaLlmBaseUrl?.Trim().TrimEnd('/');
        var apiKey = settings.NovaLlmApiKey?.Trim();
        var model = settings.NovaLlmModel?.Trim();
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
            JsonSerializer.Serialize(new { model, messages }, JsonOpts),
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
    }

    /// <summary>Nova 人格：少折腾多玩会；具体不空谈；确认后执行；不臆造事实。</summary>
    private const string SystemPrompt =
        "你是 Nova，Minecraft 启动器 StarCloud 内置的 AI 助手，被允许动手但克制：" +
        "给方案要具体到数值和步骤，不泛泛而谈；涉及改文件的方案先给确认清单等用户点头；" +
        "不编造价格、日期和性能数据；语气简短、接地气，用简体中文。";

    // ── LLM 降级链 · 第 2 级：StarCloud 云端（会话 API Key；服务未部署自动回退） ──
    private static async Task<string> CloudChatAsync(
        StarcloudSession session, string message,
        IReadOnlyList<NovaChatTurn>? history, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(StarcloudIdService.CloudBaseUrl) };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/ai/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.ApiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new CloudChatRequest(message, history ?? []), JsonOpts),
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("text").GetString()
                   ?? throw new InvalidOperationException("云端 AI 响应缺少 text");
        return text;
    }

    private sealed record CloudChatRequest(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("history")] IReadOnlyList<NovaChatTurn> History);
}
