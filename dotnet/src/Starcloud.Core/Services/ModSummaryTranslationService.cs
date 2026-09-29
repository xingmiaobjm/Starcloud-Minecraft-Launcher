// 模组简介中文翻译：MCIM 的 /translate 接口（不涉及核心分发逻辑的辅助服务）。
//
//   POST https://mod.mcimirror.top/translate/curseforge?modId=238222
//   POST https://mod.mcimirror.top/translate/modrinth?project_id=P7dR8mSH
//   → { "translated": "查看物品和配方", "original": "View Items and Recipes", "translated_at": "…" }
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
    /// 取中文简介。返回 null 表示「保持原文」（未启用 / 原文已是中文 / 无译文 / 请求失败）。
    /// </summary>
    public async Task<string?> TranslateAsync(
        string? source, string? projectId, string? original, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        var text = original?.Trim();
        // 原文已是中文，或没有原文：没什么可翻的
        if (string.IsNullOrEmpty(text) || SearchTranslationService.ContainsCjk(text!)) return null;
        var id = (projectId ?? "").Trim();
        if (id.Length == 0) return null;

        var isCurseForge = string.Equals(source, ContentSourceIds.CurseForge, StringComparison.OrdinalIgnoreCase);
        var key = $"{(isCurseForge ? ContentSourceIds.CurseForge : ContentSourceIds.Modrinth)}:{id}";

        LoadDiskCache();
        lock (_memory)
        {
            if (_memory.TryGetValue(key, out var cached)) return cached;
            if (_misses.Contains(key)) return null;
        }

        var url = isCurseForge
            ? $"{TranslateRoot}/curseforge?modId={Uri.EscapeDataString(id)}"
            : $"{TranslateRoot}/modrinth?project_id={Uri.EscapeDataString(id)}";

        await _gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetTimeout);
            var json = await _downloader.PostJsonAsync(url, "{}", timeout.Token);
            var translated = ParseTranslated(json);
            if (string.IsNullOrWhiteSpace(translated))
            {
                lock (_memory) _misses.Add(key);
                return null;
            }
            Remember(key, translated!);
            return translated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 镜像不可达 / 该条目没译稿：保留原文，下次进列表再试（不写负缓存）
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>解析 /translate 响应里的 translated 字段（公开以便单测）</summary>
    public static string? ParseTranslated(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("translated", out var value) ||
                value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
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
