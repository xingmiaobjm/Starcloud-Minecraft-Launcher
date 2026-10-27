// 模组简介中文翻译：MCIM 的 /translate 接口（不涉及核心分发逻辑的辅助服务）。
//
// 2026-10 起接口只收批量请求（旧的空 body 单查直接 400——这正是此前简介一直不翻译的原因）：
//   POST https://mod.mcimirror.top/translate/curseforge   body {"modids":[238222,306612]}
//   POST https://mod.mcimirror.top/translate/modrinth     body {"project_ids":["P7dR8mSH"]}
//   → [ { "modid"|"project_id": …, "translated": "…", "original": "…", "translated_at": "…" } ]
// 响应是数组：没出现的条目 = 社区还没有译稿（本会话负缓存）；网络失败仍不写负缓存。
//
// 原文分别是 CurseForge 的 summary 与 Modrinth 的 description（一句话简介），
// 也就是搜索结果行、详情面板里显示的那行字。MCIM 侧的译稿是人工/半自动维护的社区译文，
// 比机翻贴切，故直接取用，不做本地机翻兜底。
//
// ── 三条边界 ────────────────────────────────────────────────────────────────
//   1. 只在 UI 语言为中文时启用（英文界面照原样显示英文简介）；
//   2. 原文本身已含中文 → 不翻译（社区上传的中文简介无需再翻）；
//   3. 尽力而为：单次 4 秒超时、失败**不**写缓存也不报错，静默保留原文；
//      成功结果落磁盘（mod_summary_cache.json），命中即零网络。
//
// 用户可见行为：先显示英文原文，译文异步到达后就地替换（列表不会闪、不重排）。

using System.Text.Json;

namespace Starcloud.Core.Services;

public sealed class ModSummaryTranslationService
{
    private const string TranslateRoot = "https://mod.mcimirror.top/translate";

    /// <summary>单次翻译的超时（旁路能力，卡住不能拖慢列表）</summary>
    private static readonly TimeSpan NetTimeout = TimeSpan.FromSeconds(4);

    /// <summary>同时在途的翻译请求上限（一页 20 条结果，别一次全发出去）</summary>
    private const int MaxConcurrency = 4;

    private readonly DownloadService _downloader;
    private readonly Func<LauncherSettings> _settings;
    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    /// <summary>本次会话查过但没有译文的键（避免每次滚动都重问一遍）</summary>
    private readonly HashSet<string> _misses = new(StringComparer.Ordinal);
    private bool _diskLoaded;

    public ModSummaryTranslationService(DownloadService downloader, Func<LauncherSettings> settings)
    {
        _downloader = downloader;
        _settings = settings;
    }

    private static string CachePath =>
        Path.Combine(LauncherPaths.DataDir, "mod_summary_cache.json");

    /// <summary>是否启用（UI 语言为中文才翻）</summary>
    public bool Enabled =>
        (_settings().UiLanguage ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 取中文简介（单条，详情弹窗用）。返回 null 表示「保持原文」。
    /// 内部复用批量接口（单元素批），语义与 <see cref="TranslateManyAsync"/> 一致。
    /// </summary>
    public async Task<string?> TranslateAsync(
        string? source, string? projectId, string? original, CancellationToken ct = default)
    {
        var id = (projectId ?? "").Trim();
        if (id.Length == 0 || string.IsNullOrEmpty(original?.Trim())) return null;
        var map = await TranslateManyAsync(source!, [(id, original)], ct);
        return map.TryGetValue(id, out var zh) ? zh : null;
    }

    /// <summary>一次批量请求的条目上限</summary>
    private const int BatchSize = 50;

    /// <summary>
    /// 批量取中文简介（搜索列表一页一次请求）。返回 { projectId → 译文 }，没出现的键 = 保持原文。
    /// 缓存命中不出网；本会话已知没译稿的不重问。
    /// </summary>
    public async Task<Dictionary<string, string>> TranslateManyAsync(
        string source, IReadOnlyList<(string Id, string? Original)> items, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Enabled || items.Count == 0) return result;

        var isCurseForge = string.Equals(source, ContentSourceIds.CurseForge, StringComparison.OrdinalIgnoreCase);
        var prefix = isCurseForge ? ContentSourceIds.CurseForge : ContentSourceIds.Modrinth;
        LoadDiskCache();

        var pending = new List<string>();
        foreach (var (rawId, original) in items)
        {
            var id = rawId.Trim();
            if (id.Length == 0) continue;
            // CF 的批量接口只收数字 id：个别来源给了脏值就跳过，别让一条毁了整批
            if (isCurseForge && !long.TryParse(id, out _)) continue;
            var text = original?.Trim();
            // 原文已是中文，或没有原文：没什么可翻的
            if (string.IsNullOrEmpty(text) || SearchTranslationService.ContainsCjk(text!)) continue;

            var key = $"{prefix}:{id}";
            string? cached = null;
            var knownMiss = false;
            lock (_memory)
            {
                _memory.TryGetValue(key, out cached);
                knownMiss = _misses.Contains(key);
            }
            if (cached is not null) result[id] = cached;
            else if (!knownMiss) pending.Add(id);
        }
        if (pending.Count == 0) return result;

        await _gate.WaitAsync(ct);
        try
        {
            var url = isCurseForge ? $"{TranslateRoot}/curseforge" : $"{TranslateRoot}/modrinth";
            foreach (var chunk in pending.Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                // CF 的 id 一律是数字、走 modids；Modrinth 的 id 是 slug 走 project_ids
                var body = isCurseForge
                    ? JsonSerializer.Serialize(new { modids = chunk.Select(long.Parse).ToArray() })
                    : JsonSerializer.Serialize(new { project_ids = chunk.ToArray() });

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(NetTimeout);
                var json = await _downloader.PostJsonAsync(url, body, timeout.Token);

                var answered = ParseBatch(json);
                foreach (var (id, translated) in answered)
                {
                    result[id] = translated;
                    Remember($"{prefix}:{id}", translated);
                }
                // 接口正常返回但没给译稿的条目：明确没译文，本会话不再重问
                lock (_memory)
                    foreach (var id in chunk)
                        if (!answered.ContainsKey(id))
                            _misses.Add($"{prefix}:{id}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 镜像不可达：已到手的照用，其余保留原文（网络失败不写负缓存）
        }
        finally
        {
            _gate.Release();
        }
        return result;
    }

    /// <summary>
    /// 解析批量响应：CF 的 id 键是 <c>modid</c>（数字），Modrinth 是 <c>project_id</c>（字符串）。
    /// 旧单对象格式已随接口改版退役；坏响应按无译文处理。
    /// </summary>
    public static Dictionary<string, string> ParseBatch(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? id = null;
                if (item.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String)
                    id = pid.GetString();
                if (id is null && item.TryGetProperty("modid", out var mid))
                {
                    if (mid.ValueKind == JsonValueKind.Number) id = mid.GetRawText();
                    else if (mid.ValueKind == JsonValueKind.String) id = mid.GetString();
                }
                if (string.IsNullOrEmpty(id)) continue;
                if (!item.TryGetProperty("translated", out var t) || t.ValueKind != JsonValueKind.String)
                    continue;
                var text = t.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text)) result[id!] = text!;
            }
        }
        catch
        {
            // 坏响应按无译文处理
        }
        return result;
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
                    if (!string.IsNullOrWhiteSpace(v))
                        _memory[k] = v;
        }
        catch
        {
            // 缓存损坏从零开始（翻译是尽力而为）
        }
    }

    private void Remember(string key, string translated)
    {
        Dictionary<string, string> snapshot;
        lock (_memory)
        {
            _memory[key] = translated;
            snapshot = new Dictionary<string, string>(_memory, StringComparer.Ordinal);
        }
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // 写缓存失败不影响本次显示
        }
    }
}
