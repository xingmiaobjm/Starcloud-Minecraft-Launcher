// Nova 时刻后端（NOVA_DESIGN.md 第 6 节「Nova 时刻」+ NOVA_INTEGRATION.md 第 6 节时刻卡片规格）：
//   事件流：增量解析 .minecraft/logs/latest.log（死亡 / 进度 / 挑战，中英文日志兼容，聊天行排除）
//   高光判定：本地规则引擎挑值得纪念的瞬间（挑战达成 / 高威胁翻车 / 惨案时刻），克制提案不刷屏
//   提案：命中后生成候选卡（含 Nova 解说语），用户点头才转正为时刻卡片（默认克制、确认后执行）
//   战报卡：下机后生成「这盘干了啥」总结（时长 / 进度 / 挑战 / 死亡 / 高光统计）
//   存储：%APPDATA%/starcloud-launcher/nova.moments.json（时刻 + 战报 + 待处理候选池）
//
//   视觉通路说明（不臆造事实）：帧采集与画面剪辑依赖游戏内通路（规划中）。候选片段先按
//   「事件前 25 秒 ~ 事件后 5 秒」规则标注范围，卡片预览区以占位呈现——不伪造画面数据；
//   坐标 / 装备信息仅在日志可解析时填充，解析不到就留空。

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

/// <summary>日志事件流中的一条游戏事件（高光判定的原料；Summary 保留日志原文，可审计）</summary>
public sealed record NovaGameEvent(
    [property: JsonPropertyName("time")] DateTime Time,
    [property: JsonPropertyName("kind")] string Kind,          // death / advancement / challenge / goal
    [property: JsonPropertyName("summary")] string Summary,    // 日志原文（不加工）
    [property: JsonPropertyName("actor")] string? Actor = null,   // 玩家名（能解析时）
    [property: JsonPropertyName("title")] string? Title = null,   // 进度名（进度类事件）
    [property: JsonPropertyName("threat")] bool HighThreat = false); // 涉及高威胁实体

/// <summary>高光候选提案：用户点头（Accept）才转正为时刻卡片</summary>
public sealed record NovaMomentCandidate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("event_time")] DateTime EventTime,
    [property: JsonPropertyName("kind")] string Kind,      // challenge / death
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("caption")] string Caption,
    [property: JsonPropertyName("state")] string State)    // proposed / accepted / dismissed
{
    [JsonIgnore] public string TimeText => EventTime.ToString("HH:mm:ss");

    [JsonIgnore] public string KindText => Kind == "challenge" ? "🏆 挑战达成" : "💀 惨案时刻";
}

/// <summary>
/// Nova 时刻卡片（NOVA_INTEGRATION.md 第 6 节：解说语 / 事件标签 / 坐标 / 时间戳 / 片段范围）。
/// Coordinate 为空 = 日志不可解析，UI 不显示坐标行；画面剪辑待视觉通路就绪后接入。
/// </summary>
public sealed record NovaMoment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("event_time")] DateTime EventTime,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,          // 事件摘要（日志原文）
    [property: JsonPropertyName("caption")] string Caption,      // Nova 解说语
    [property: JsonPropertyName("coordinate")] string? Coordinate,
    [property: JsonPropertyName("clip_range")] string ClipRangeText)
{
    [JsonIgnore] public string TimeText => EventTime.ToString("HH:mm:ss");

    [JsonIgnore] public string KindText => Kind == "challenge" ? "🏆 挑战达成" : "💀 惨案时刻";

    [JsonIgnore] public bool HasCoordinate => !string.IsNullOrEmpty(Coordinate);
}

/// <summary>下机战报卡（NOVA_DESIGN.md：这盘干了啥——时长 / 进度 / 挑战 / 死亡 / 高光）</summary>
public sealed record NovaSessionReport(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("ended_at")] DateTime EndedAt,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("advancements")] int Advancements,
    [property: JsonPropertyName("challenges")] int Challenges,
    [property: JsonPropertyName("deaths")] int Deaths,
    [property: JsonPropertyName("worst_death")] string? WorstDeath,
    [property: JsonPropertyName("moments_saved")] int MomentsSaved)
{
    [JsonIgnore] public string DurationText
    {
        get
        {
            var span = EndedAt - StartedAt;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))} 分钟";
        }
    }
}

public sealed partial class NovaMomentsService
{
    // 候选片段范围：捕捉边界承诺「暂存最近 30 秒」，按事件前 25s + 后 5s 切分
    public const string ClipRange = "事件前 25 秒 ~ 事件后 5 秒";

    private const int MaxMoments = 100;
    private const int MaxReports = 20;
    private const int MaxCandidates = 50;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly List<NovaMoment> _moments = [];
    private readonly List<NovaSessionReport> _reports = [];
    private readonly List<NovaMomentCandidate> _candidates = [];
    private readonly List<NovaGameEvent> _sessionEvents = [];
    private readonly Dictionary<string, DateTime> _lastProposed = new();

    private long _offset;      // latest.log 增量读取位置（字节）
    private bool _sessionStarted;

    public NovaMomentsService() => Load();

    // ── 会话生命周期（App 层在游戏启动 / 退出时调用） ──

    public bool IsSessionActive { get; private set; }

    public string? SessionVersion { get; private set; }

    public DateTime SessionStart { get; private set; }

    /// <summary>对局开始：latest.log 由游戏启动重建，从头读</summary>
    public void StartSession(string versionId)
    {
        _offset = 0;
        _sessionEvents.Clear();
        _lastProposed.Clear();
        _candidates.RemoveAll(c => c.State == "proposed");
        SessionVersion = versionId;
        SessionStart = DateTime.Now;
        _sessionStarted = true;
        IsSessionActive = true;
    }

    /// <summary>
    /// 对局结束：生成战报卡并落盘。事件由 ObserveOnceAsync 持续归档，
    /// 此处只做统计收口；无事件的对局也生成（时长统计本身有意义）。
    /// </summary>
    public NovaSessionReport EndSession()
    {
        var endedAt = DateTime.Now;
        var deaths = _sessionEvents.Where(e => e.Kind == "death").ToList();
        var report = new NovaSessionReport(
            Guid.NewGuid().ToString("N")[..12],
            SessionStart, endedAt, SessionVersion,
            _sessionEvents.Count(e => e.Kind == "advancement"),
            _sessionEvents.Count(e => e.Kind is "challenge" or "goal"),
            deaths.Count,
            deaths.FirstOrDefault(e => e.HighThreat)?.Summary ?? deaths.FirstOrDefault()?.Summary,
            _moments.Count(m => m.CreatedAt >= SessionStart));
        _reports.Add(report);
        if (_reports.Count > MaxReports) _reports.RemoveRange(0, _reports.Count - MaxReports);
        IsSessionActive = false;
        Save();
        return report;
    }

    /// <summary>会话开始前收到了事件流扫描请求（不该发生）→ 静默当作已开始，避免丢提案</summary>
    public bool EnsureSession() => _sessionStarted;

    // ── 事件流：增量读取 latest.log → 解析 → 判定 → 提案（观察循环每 5 秒调一次） ──

    /// <summary>候选提案事件：App 层订阅后更新状态为「有提案」并弹候选卡</summary>
    public event Action<NovaMomentCandidate>? CandidateProposed;

    public IReadOnlyList<NovaMomentCandidate> Candidates => _candidates;

    public IReadOnlyList<NovaMoment> Moments => _moments;

    public IReadOnlyList<NovaSessionReport> Reports => _reports;

    public NovaSessionReport? LastReport =>
        _reports.OrderByDescending(r => r.EndedAt).FirstOrDefault();

    /// <summary>观察一轮：增量解析新日志行，命中高光规则即生成候选提案</summary>
    public async Task<IReadOnlyList<NovaMomentCandidate>> ObserveOnceAsync(
        string dotMinecraft, CancellationToken ct = default)
    {
        if (!IsSessionActive) return [];
        var events = await ReadNewEventsAsync(dotMinecraft, ct);
        _sessionEvents.AddRange(events);

        var proposals = new List<NovaMomentCandidate>();
        foreach (var e in events)
        {
            if (!ShouldPropose(e)) continue;
            var candidate = new NovaMomentCandidate(
                Guid.NewGuid().ToString("N")[..12], e.Time, e.Kind, e.Summary,
                CaptionFor(e), "proposed");
            _candidates.Add(candidate);
            if (_candidates.Count > MaxCandidates)
                _candidates.RemoveAt(0);
            proposals.Add(candidate);
            CandidateProposed?.Invoke(candidate);
        }
        if (proposals.Count > 0) Save();
        return proposals;
    }

    /// <summary>用户点头：候选 → 时刻卡片（转正落盘）</summary>
    public NovaMoment Accept(string candidateId)
    {
        var idx = _candidates.FindIndex(c => c.Id == candidateId && c.State == "proposed");
        if (idx < 0) throw new InvalidOperationException("候选不存在或已处理过");
        var c = _candidates[idx];
        var moment = new NovaMoment(
            Guid.NewGuid().ToString("N")[..12], DateTime.Now, c.EventTime,
            c.Kind, c.Summary, c.Caption, null, ClipRange);
        _moments.Add(moment);
        if (_moments.Count > MaxMoments) _moments.RemoveAt(0);
        _candidates[idx] = c with { State = "accepted" };
        Save();
        return moment;
    }

    /// <summary>用户不要：候选划掉，不产生任何卡片</summary>
    public void Dismiss(string candidateId)
    {
        var idx = _candidates.FindIndex(c => c.Id == candidateId && c.State == "proposed");
        if (idx < 0) return;
        _candidates[idx] = _candidates[idx] with { State = "dismissed" };
        Save();
    }

    public bool DeleteMoment(string id)
    {
        var removed = _moments.RemoveAll(m => m.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    public void ClearMoments()
    {
        _moments.Clear();
        Save();
    }

    /// <summary>战报文本（NOVA_DESIGN.md 文案规范：具体不空谈，只写事实）</summary>
    public string BuildReportText(NovaSessionReport r)
    {
        var sb = new StringBuilder("【本局战报】");
        sb.Append($"\n• 对局：{r.StartedAt:HH:mm} ~ {r.EndedAt:HH:mm}（{r.DurationText}）");
        if (!string.IsNullOrEmpty(r.Version)) sb.Append($"\n• 版本：{r.Version}");
        sb.Append($"\n• 进度：解锁 {r.Advancements} 个，其中挑战 {r.Challenges} 个");
        sb.Append($"\n• 死亡：{r.Deaths} 次{(string.IsNullOrEmpty(r.WorstDeath) ? "" : $"（印象最深：{TrimForEcho(r.WorstDeath)}）")}");
        sb.Append($"\n• 高光：已收藏 {r.MomentsSaved} 个 Nova 时刻");
        if (r.Advancements + r.Challenges + r.Deaths == 0)
            sb.Append("\n这局没在日志里留下事件——挂机了？还是日志通路没接上。");
        return sb.ToString();
    }

    // ── 高光判定规则 ──

    private bool ShouldPropose(NovaGameEvent e)
    {
        // 克制提案：挑战必提；高威胁翻车必提；普通死亡 15 分钟内最多 1 个；同事件 60 秒去重
        if (!_lastProposed.TryGetValue(e.Kind, out var last) || DateTime.Now - last > TimeSpan.FromSeconds(60))
        {
            if (e.Kind == "challenge" || (e.Kind == "death" && e.HighThreat))
            {
                _lastProposed[e.Kind] = e.Time;
                return true;
            }
        }

        if (e.Kind == "death" && !e.HighThreat &&
            (!_lastProposed.TryGetValue("death-normal", out var lastNormal) ||
             e.Time - lastNormal > TimeSpan.FromMinutes(15)))
        {
            _lastProposed["death-normal"] = e.Time;
            return true;
        }
        return false;
    }

    private static string CaptionFor(NovaGameEvent e) => e.Kind switch
    {
        "challenge" => e.Title is null
            ? "挑战达成，这波值得留个纪念。"
            : $"挑战「{e.Title}」达成，这波值得留个纪念。",
        "death" when e.HighThreat => "在高威胁目标手里翻了车，当反面教材收藏一下？",
        "death" => "这段有点惨烈，做成「惨案时刻」乐一下？",
        _ => "这个瞬间值得收藏。",
    };

    private static string TrimForEcho(string s) => s.Length <= 24 ? s : s[..24] + "…";

    // ── 日志解析（latest.log；兼容英文原版与中文本地化日志） ──

    /// <summary>增量读取 latest.log 新增内容，解析为游戏事件（只消费完整行，尾部半行留给下一轮）</summary>
    private async Task<List<NovaGameEvent>> ReadNewEventsAsync(string dotMinecraft, CancellationToken ct)
    {
        var events = new List<NovaGameEvent>();
        var logPath = Path.Combine(dotMinecraft, "logs", "latest.log");
        if (!File.Exists(logPath)) return events;

        try
        {
            await using var fs = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (_offset > fs.Length) _offset = 0; // 日志被轮转/重建，从头读

            fs.Seek(_offset, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms, ct);
            var bytes = ms.ToArray(); // C# 12 async 方法内不能用 Span，用数组承载

            // 最后一个 \n 之前的才是完整行（\n 字节不会出现在 UTF-8 多字节序列内，切分安全）
            var lastNl = Array.LastIndexOf(bytes, (byte)'\n');
            var consumed = lastNl >= 0 ? lastNl + 1 : 0;
            _offset += consumed;

            foreach (var raw in Encoding.UTF8.GetString(bytes, 0, consumed).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0 && ParseEvent(line) is { } e) events.Add(e);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 日志读取失败不阻断观察循环（文件被锁 / 磁盘问题）
        }
        return events;
    }

    [GeneratedRegex(@"^\[(\d{1,2}):(\d{2}):(\d{2})\]\s*\[[^\]]+\]:\s*(.+)$")]
    private static partial Regex LogLineRegex();

    /// <summary>英文进度/挑战/目标行：「Steve has completed the challenge [Two by Two]」</summary>
    [GeneratedRegex(@"^(?<who>.+?)\s+(?:has\s+)?(?:made the advancement|completed the challenge|reached the goal)\s+\[(?<title>.+)\]$")]
    private static partial Regex AdvancementEnRegex();

    /// <summary>中文进度/挑战/目标行：「Steve 完成了挑战 [裹足不前]」</summary>
    [GeneratedRegex(@"^(?<who>.+?)\s+(?:达成了进度|完成了挑战|达成了目标)\s*[【\[](?<title>.+)[】\]]$")]
    private static partial Regex AdvancementZhRegex();

    /// <summary>中文死亡行（聊天行已先按 &lt;玩家&gt; 前缀排除）</summary>
    [GeneratedRegex(@"(被.{1,24}?(杀死了|干掉了|射杀了|炸死了|烧死了|戳死了|捅死了|碾死了|蜇死了|捏死了))|摔死|从高处掉落|落地过猛|淹死|窒息而死|试图在熔岩里游泳|饿死|被压扁|感受到了动能|被炸飞|凋零而死|发现地面是熔岩|被冻死|触电而死")]
    private static partial Regex DeathZhRegex();

    private static readonly string[] DeathKeywords =
    [
        "was slain by", "was shot by", "was killed by", "got finished off by",
        "blown up by", "blew up", "was fireballed by", "was stung to death",
        "was poked to death", "was skewered", "was impaled", "was obliterated",
        "was hammered", "fell from a high place", "fell off", "was doomed to fall",
        "hit the ground too hard", "drowned", "suffocated in a wall",
        "tried to swim in lava", "went up in flames", "burned to death",
        "walked into a cactus", "was pricked to death", "starved to death",
        "was squashed by", "experienced kinetic energy", "went off with a bang",
        "withered away", "died by", "was frozen", "was killed by magic",
    ];

    private static readonly string[] HighThreatKeywords =
    [
        "ender dragon", "末影龙", "wither", "凋灵", "warden", "监守者",
        "ravager", "劫掠兽", "evoker", "唤魔者", "piglin brute", "猪灵蛮兵",
        "charged creeper", "闪电苦力怕", "elder guardian", "远古守卫者",
    ];

    private NovaGameEvent? ParseEvent(string line)
    {
        var m = LogLineRegex().Match(line);
        if (!m.Success) return null;

        var hour = int.Parse(m.Groups[1].Value);
        var minute = int.Parse(m.Groups[2].Value);
        var second = int.Parse(m.Groups[3].Value);
        var message = m.Groups[4].Value.Trim();

        if (message.StartsWith('<')) return null; // 聊天行：<玩家名> 消息——聊天不是事件

        var time = ResolveTimestamp(hour, minute, second);

        // 进度三连（advancement 不提案只计数；challenge/goal 进战报）
        var adv = AdvancementEnRegex().Match(message) is { Success: true } en ? en :
                  AdvancementZhRegex().Match(message) is { Success: true } zh ? zh : null;
        if (adv is not null)
        {
            var kind = message.Contains("challenge") || message.Contains("挑战") ? "challenge"
                     : message.Contains("goal") || message.Contains("目标") ? "goal"
                     : "advancement";
            return new NovaGameEvent(time, kind, message,
                adv.Groups["who"].Value.Trim(), adv.Groups["title"].Value.Trim());
        }

        // 死亡（英文关键词 + 中文正则）
        var isDeath = DeathKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase))
                      || DeathZhRegex().IsMatch(message);
        if (isDeath)
        {
            var threat = HighThreatKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
            return new NovaGameEvent(time, "death", message, HighThreat: threat);
        }
        return null;
    }

    /// <summary>日志只有时分秒：按今天解析；若时间在未来（跨午夜旧日志）回退到昨天</summary>
    private static DateTime ResolveTimestamp(int hour, int minute, int second)
    {
        var today = DateTime.Today.AddHours(hour).AddMinutes(minute).AddSeconds(second);
        return today > DateTime.Now.AddMinutes(1) ? today.AddDays(-1) : today;
    }

    // ── 持久化 ──

    private sealed record MomentsStore(
        [property: JsonPropertyName("moments")] List<NovaMoment> Moments,
        [property: JsonPropertyName("reports")] List<NovaSessionReport> Reports,
        [property: JsonPropertyName("candidates")] List<NovaMomentCandidate> Candidates);

    private void Save()
    {
        try
        {
            LauncherPaths.EnsureDataDir();
            File.WriteAllText(LauncherPaths.NovaMomentsFile,
                JsonSerializer.Serialize(new MomentsStore(_moments, _reports, _candidates), JsonOpts));
        }
        catch { /* 存储失败不阻断观察 */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(LauncherPaths.NovaMomentsFile)) return;
            var store = JsonSerializer.Deserialize<MomentsStore>(
                File.ReadAllText(LauncherPaths.NovaMomentsFile), JsonOpts);
            if (store is null) return;
            // readonly 字段不能整体重赋值，改清空后填充
            _moments.Clear();
            if (store.Moments is not null) _moments.AddRange(store.Moments);
            _reports.Clear();
            if (store.Reports is not null) _reports.AddRange(store.Reports);
            _candidates.Clear();
            if (store.Candidates is not null) _candidates.AddRange(store.Candidates);
        }
        catch
        {
            _moments.Clear();
            _reports.Clear();
            _candidates.Clear();
        }
    }
}
