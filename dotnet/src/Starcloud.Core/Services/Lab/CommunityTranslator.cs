// SC 实验室 · 社区文本翻译：把英文的投影描述转成中文（卡片"先原文、异步换译文"模式）。
//
// 端点用 MyMemory（免费、免 key，本机实测可达；谷歌端点在本网络环境不可达）。
// 免费额度按字符计：只翻"看起来是英文且不太长"的短描述，超长截断；失败返回 null（保留原文）。
// 内存 + 磁盘两级缓存（同一条描述不重复花额度）。

using System.Text.Json;

namespace Starcloud.Core.Services.Lab;

public sealed class CommunityTranslator(DownloadService download)
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    /// <summary>免费额度按字符计：超过这个长度只翻前段（描述开头已能说明是什么）</summary>
    private const int MaxChars = 320;

    private const int NetTimeoutMs = 9000;

    private readonly Dictionary<string, string?> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(2); // 同时最多两条翻译在飞
    private Dictionary<string, string>? _disk;
    private readonly object _diskLock = new();

    private static string CacheFile => Path.Combine(LauncherPaths.DataDir, "community_translation_cache.json");

    /// <summary>英文（或非中文）文本 → 中文；已有中文 / 译文取不到时返回 null（界面保留原文）</summary>
    public async Task<string?> TranslateToChineseAsync(string? text, CancellationToken ct = default)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (SearchTranslationService.ContainsCjk(trimmed!)) return null; // 已是中文

        var key = trimmed.Length <= MaxChars ? trimmed! : trimmed[..MaxChars];
        lock (_diskLock)
        {
            if (_memory.TryGetValue(key, out var cached)) return cached;
        }
        LoadDisk();
        lock (_diskLock)
        {
            if (_disk!.TryGetValue(key, out var hit)) return _memory[key] = hit;
        }

        await _gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetTimeoutMs);
            var url = $"{Endpoint}?q={Uri.EscapeDataString(key)}&langpair=en|zh-CN";
            var json = await download.GetStringAsync(url, null, timeout.Token, trackSourceHealth: false);
            var translated = ParseTranslated(json);

            lock (_diskLock)
            {
                _memory[key] = translated;
                if (translated is not null)
                {
                    // 译文里的"Litematica/Minecraft"这类专名保留原样，只当作一段普通译文缓存
                    _disk![key] = translated;
                    SaveDisk();
                }
            }
            return translated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 镜像不可达 / 超时：保留原文，下次再试（不写负缓存）
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>解析 MyMemory 响应里的译文（公开以便单测）</summary>
    public static string? ParseTranslated(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("responseData", out var data)) return null;
            if (!data.TryGetProperty("translatedText", out var text) ||
                text.ValueKind != JsonValueKind.String) return null;
            var value = text.GetString()?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            // 免费端点有时把错误塞进 translatedText（如配额提示），这类不是译文
            if (value.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase))
                return null;
            return value;
        }
        catch
        {
            return null;
        }
    }

    private void LoadDisk()
    {
        lock (_diskLock)
        {
            if (_disk is not null) return;
            try
            {
                _disk = File.Exists(CacheFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile)) ?? new()
                    : new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                _disk = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }

    private void SaveDisk()
    {
        try
        {
            Directory.CreateDirectory(LauncherPaths.DataDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(_disk));
        }
        catch
        {
            // 缓存写不进去不影响功能
        }
    }
}
