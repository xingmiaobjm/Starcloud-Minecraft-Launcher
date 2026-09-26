// 中文搜索词 → 英文搜索词：让「在搜索框里打中文」也能搜到 Modrinth / CurseForge 的英文项目。
//
// 三条路，按「准 → 快 → 万能」排序（实测于 2026-09-21，见 docs/designs/2026-09-21-search-translation-design.md）：
//
//   1. 内置词典：常见中文译名（钠 / 物品管理器 / 机械动力…）零网络精确命中，不会翻错；
//   2. MC 百科：`search.mcmod.cn` 搜索页第一条模组条目的标题形如「钠 (Sodium)」，
//      括号里的英文就是社区通用英文名——这正是「通过 MC 百科翻译」的含义；
//   3. 机器翻译：Google gtx（`translate.googleapis.com`）→ MyMemory 兜底，
//      覆盖词典与百科都没收录的长尾词、描述性短语。
//
// 只有**含中文**的查询才走这条路；纯英文查询原样返回，一次网络都不发。
// 中英混输（「Sodium 钠」）不送机翻，只取英文那部分——机翻会叠成「Sodium Sodium」反而搜不到。
// 结果带内存 + 磁盘缓存（search_translation_cache.json），同一个词只查一次。

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

/// <summary>搜索词翻译结果；<paramref name="Effective"/> 是真正拿去搜索的词</summary>
public sealed record TranslatedQuery(string Original, string Effective, string? Source)
{
    /// <summary>是否真的发生了翻译（有效词与原文不同）</summary>
    public bool Translated => !string.Equals(Effective, Original, StringComparison.Ordinal);

    /// <summary>来源标签：dict / mcmod / machine；未翻译为 null</summary>
    public string SourceLabel => Source switch
    {
        "dict" => "内置词典",
        "mcmod" => "MC 百科",
        "machine" => "机器翻译",
        _ => "",
    };
}

public sealed class SearchTranslationService
{
    /// <summary>MC 百科搜索（条目标题形如「钠 (Sodium)」）</summary>
    private const string McModSearchUrl = "https://search.mcmod.cn/s?key={0}";

    /// <summary>Google gtx 免密钥接口（返回 JSON 数组；国内通常需要代理，故失败不阻断）</summary>
    private const string GoogleGtxUrl =
        "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q={0}";

    /// <summary>MyMemory 兜底（免费额度有限，仅作最后一道）</summary>
    private const string MyMemoryUrl =
        "https://api.mymemory.translated.net/get?q={0}&langpair=zh-CN|en";

    /// <summary>每步联网的超时（翻译是旁路能力，卡住不该拖慢搜索）</summary>
    private static readonly TimeSpan NetTimeout = TimeSpan.FromSeconds(6);

    /// <summary>常见中文搜索词 → 英文模组名（精确匹配；移植自旧版并补了常搜项）</summary>
    private static readonly Dictionary<string, string> Dict = new(StringComparer.Ordinal)
    {
        ["钠"] = "Sodium",
        ["锂"] = "Lithium",
        ["光影"] = "Shaders",
        ["着色器"] = "Shaders",
        ["高清修复"] = "OptiFine",
        ["小地图"] = "Xaero's Minimap",
        ["旅行地图"] = "Journey Map",
        ["物品管理器"] = "Just Enough Items",
        ["合成表"] = "Just Enough Items",
        ["机械动力"] = "Create",
        ["通用机械"] = "Mekanism",
        ["植物魔法"] = "Botania",
        ["血魔法"] = "Blood Magic",
        ["星辉魔法"] = "Astral Sorcery",
        ["神秘时代"] = "Thaumcraft",
        ["暮色森林"] = "The Twilight Forest",
        ["天境"] = "Aether",
        ["虚无世界"] = "Advent of Ascension",
        ["应用能源"] = "Applied Energistics 2",
        ["精致存储"] = "Refined Storage",
        ["匠魂"] = "Tinkers' Construct",
        ["冰与火"] = "Ice and Fire",
        ["农夫乐事"] = "Farmer's Delight",
        ["更多生物群系"] = "Biomes O' Plenty",
        ["路标"] = "Waystones",
        ["苹果皮"] = "AppleSkin",
        ["旅行者背包"] = "Traveler's Backpack",
        ["幸运方块"] = "Lucky Block",
        ["回放"] = "Replay Mod",
        ["语音"] = "Simple Voice Chat",
        ["表情"] = "Emotecraft",
        ["铁箱子"] = "Iron Chests",
        ["储物抽屉"] = "Storage Drawers",
        ["车万女仆"] = "Touhou Little Maid",
        ["机器人"] = "Create",
        ["自动合成"] = "CraftTweaker",
        ["性能优化"] = "Performance",
        ["内存优化"] = "FerriteCore",
        ["光影包"] = "Shaders",
        ["资源包"] = "Resource Pack",
        ["整合包"] = "Modpack",
        ["家具"] = "Furniture",
        ["枪械"] = "Guns",
        ["拔刀剑"] = "SlashBlade",
        ["血条"] = "Health Bar",
        ["传送"] = "Waystones",
        ["背包"] = "Backpack",
        ["任务书"] = "FTB Quests",
        ["烹饪"] = "Cooking",
        ["食物"] = "Food",
        ["装饰"] = "Decorative",
        ["建筑"] = "Building",
    };

    private readonly DownloadService _downloader;
    private readonly Dictionary<string, TranslatedQuery> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _diskLoaded;

    public SearchTranslationService(DownloadService downloader) => _downloader = downloader;

    private static string CachePath =>
        Path.Combine(LauncherPaths.DataDir, "search_translation_cache.json");

    /// <summary>
    /// 把用户输入的搜索词处理成「实际用于搜索的词」。
    /// 纯英文（或纯符号）原样返回；含中文才去做词典 / MC 百科 / 机器翻译。
    /// </summary>
    public async Task<TranslatedQuery> TranslateAsync(string query, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0 || !ContainsCjk(q)) return new TranslatedQuery(q, q, null);

        // 中英混输（如「Sodium 钠」「create 机械动力」）：只取拉丁字母那部分。
        // 送机翻会把两种写法叠起来（「Sodium 钠」→「Sodium Sodium」），反而搜不到。
        if (LatinOnlySegment(q) is { } latin) return new TranslatedQuery(q, latin, "latin");

        if (Dict.TryGetValue(q, out var hit)) return new TranslatedQuery(q, hit, "dict");

        LoadDiskCache();
        lock (_memory)
        {
            if (_memory.TryGetValue(q, out var cached)) return cached;
        }

        var translated = await TranslateCoreAsync(q, ct);
        Remember(translated);
        return translated;
    }

    private async Task<TranslatedQuery> TranslateCoreAsync(string q, CancellationToken ct)
    {
        // ① MC 百科：条目标题「中文 (English)」里的英文名最贴合社区叫法
        if (await FromMcModAsync(q, ct) is { } en && IsUsableEnglish(en))
            return new TranslatedQuery(q, en, "mcmod");

        // ② 机器翻译：长尾词 / 描述性短语（词典与百科都收不到的）
        if (await MachineTranslateAsync(q, ct) is { } machine && IsUsableEnglish(machine))
            return new TranslatedQuery(q, machine, "machine");

        return new TranslatedQuery(q, q, null);
    }

    /// <summary>MC 百科搜索页取第一条模组条目标题里的英文名（「钠 (Sodium)」→ Sodium）</summary>
    private async Task<string?> FromMcModAsync(string query, CancellationToken ct)
    {
        try
        {
            var html = await GetAsync(string.Format(McModSearchUrl, Uri.EscapeDataString(query)), ct);
            foreach (Match m in Regex.Matches(html,
                         @"href=""[^""]*/class/\d+\.html""[^>]*>(.*?)</a>", RegexOptions.Singleline))
            {
                var title = System.Net.WebUtility.HtmlDecode(
                    Regex.Replace(Regex.Replace(m.Groups[1].Value, @"</em>\s*<em>", " "), "</?em>", "")).Trim();
                // 标题里的括号分组：只要「含英文且不含中文」的那个（排除 (1.20.1) 之类的干扰）
                foreach (Match g in Regex.Matches(title, @"[（(]([^）)]*)[）)]"))
                {
                    var candidate = g.Groups[1].Value.Trim();
                    if (IsUsableEnglish(candidate)) return candidate;
                }
            }
        }
        catch
        {
            // 百科不可达就当没收录：继续走机器翻译
        }
        return null;
    }

    /// <summary>Google gtx → MyMemory；都失败返回 null</summary>
    private async Task<string?> MachineTranslateAsync(string query, CancellationToken ct)
    {
        try
        {
            var json = await GetAsync(string.Format(GoogleGtxUrl, Uri.EscapeDataString(query)), ct);
            using var doc = JsonDocument.Parse(json);
            // 响应形如 [[["sodium","钠",null,null,2]],...]：把各分段的首元素拼起来
            if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                doc.RootElement.GetArrayLength() > 0 &&
                doc.RootElement[0].ValueKind == JsonValueKind.Array)
            {
                var parts = doc.RootElement[0].EnumerateArray()
                    .Where(seg => seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                    .Select(seg => seg[0].ValueKind == JsonValueKind.String ? seg[0].GetString() : null)
                    .Where(s => !string.IsNullOrEmpty(s));
                var text = string.Concat(parts).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch
        {
            // gtx 在无代理网络下常常超时：换 MyMemory
        }

        try
        {
            var json = await GetAsync(string.Format(MyMemoryUrl, Uri.EscapeDataString(query)), ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("responseData", out var data) &&
                data.TryGetProperty("translatedText", out var text) &&
                text.ValueKind == JsonValueKind.String)
                return text.GetString()?.Trim();
        }
        catch
        {
            // 兜底也失败：保持原文搜索
        }
        return null;
    }

    /// <summary>带超时的字符串请求：翻译是旁路能力，卡住不能拖慢搜索</summary>
    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NetTimeout);
        return await _downloader.GetStringAsync(url, timeout.Token);
    }

    /// <summary>是否是可用的英文搜索结果：含拉丁字母、不含中文、长度合理</summary>
    public static bool IsUsableEnglish(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Length is 0 or > 80) return false;
        if (ContainsCjk(t)) return false;
        return t.Any(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
    }

    /// <summary>是否含 CJK 汉字（决定要不要翻译）</summary>
    public static bool ContainsCjk(string text) =>
        text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    /// <summary>
    /// 中英混输时挑出含拉丁字母的词元（「Sodium 钠」→「Sodium」）；
    /// 压根没有连续两个拉丁字母（「钠 1.20」）时返回 null，交给百科 / 机翻处理。
    /// </summary>
    public static string? LatinOnlySegment(string query)
    {
        if (!Regex.IsMatch(query, "[A-Za-z]{2,}")) return null;
        var tokens = query
            .Split([' ', '\t', ',', '，', '、', '/', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Any(char.IsAsciiLetter))
            .ToArray();
        if (tokens.Length == 0) return null;
        var joined = string.Join(' ', tokens);
        return IsUsableEnglish(joined) ? joined : null;
    }

    // ── 缓存 ─────────────────────────────────────────────────────────────

    private void LoadDiskCache()
    {
        if (_diskLoaded) return;
        _diskLoaded = true;
        try
        {
            if (!File.Exists(CachePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
            if (map is null) return;
            lock (_memory)
                foreach (var (k, v) in map)
                    _memory[k] = new TranslatedQuery(k, v, "cache");
        }
        catch
        {
            // 缓存损坏从零开始（翻译是尽力而为）
        }
    }

    private void Remember(TranslatedQuery translated)
    {
        lock (_memory) _memory[translated.Original] = translated;
        try
        {
            _gate.Wait();
            try
            {
                Dictionary<string, string> map;
                lock (_memory)
                    map = _memory.Where(kv => kv.Value.Translated)
                        .ToDictionary(kv => kv.Key, kv => kv.Value.Effective);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(map));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // 写缓存失败不影响本次搜索
        }
    }
}
