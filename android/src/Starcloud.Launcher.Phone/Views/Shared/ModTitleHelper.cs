// Mod 名称中文翻译：内存 → 磁盘缓存 → 联网解析（MC 百科搜索页第一条模组条目标题）。
// 移植自桌面 Starcloud.App.Views.Shared.ModTitleHelper.cs（仅改命名空间，逻辑 1:1）。

using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Launcher.Phone.Views.Shared;

/// <summary>
/// Mod 名称中文翻译（来源：MC 百科搜索页第一条模组条目的标题）。缓存优先：内存 → 磁盘
/// （mod_title_cache.json，英文原名 → 中文名）→ 联网解析；未命中先显示原名并后台解析，
/// 下一次列表刷新即显示中文；解析失败（无网络 / 页面结构变化）静默保留原名。
/// </summary>
public static class ModTitleHelper
{
    /// <summary>英文名 → 中文名；null = 已查过但无翻译（占位防重复请求）。</summary>
    private static readonly Dictionary<string, string?> Cache = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static bool _loaded;

    static ModTitleHelper() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Starcloud Launcher)");

    private static string CachePath => Path.Combine(
        Starcloud.Core.Services.LauncherPaths.DataDir, "mod_title_cache.json");

    /// <summary>显示标题：命中翻译显示中文，否则原名（未命中的顺手发起后台解析）。</summary>
    public static string Display(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        EnsureLoaded();
        if (Cache.TryGetValue(title, out var zh)) return zh ?? title;
        Cache[title] = null;   // 占位，避免同名重复请求
        _ = ResolveAsync(title);
        return title;
    }

    /// <summary>异步取翻译：命中直接返回；未命中解析后返回（无翻译返回 null，供详情窗标题即时更新）。</summary>
    public static async Task<string?> GetAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        EnsureLoaded();
        if (Cache.TryGetValue(title, out var zh)) return zh;
        Cache[title] = null;   // 占位，避免同名重复请求
        return await ResolveAsync(title);
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(CachePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
            if (map is null) return;
            foreach (var (k, v) in map) Cache[k] = v;
        }
        catch { /* 缓存损坏从零开始 */ }
    }

    private static async Task<string?> ResolveAsync(string title)
    {
        try
        {
            var url = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(title)}";
            var html = await Http.GetStringAsync(url);
            // 条目结构：<a ... href="https://www.mcmod.cn/class/3124.html"><em>Fabric</em><em>API</em></a>
            // 页面 URL 为 /class/<id>.html，关键词以 <em> 高亮分词包裹；相邻 <em> 之间补空格还原词距
            var m = Regex.Match(html, @"href=""[^""]*/class/\d+\.html""[^>]*>(.*?)</a>",
                RegexOptions.Singleline);
            if (!m.Success) return null;
            var zh = System.Net.WebUtility.HtmlDecode(
                Regex.Replace(Regex.Replace(m.Groups[1].Value, @"</em>\s*<em>", " "), "</?em>", "")).Trim();
            if (string.IsNullOrWhiteSpace(zh)) return null;
            await Gate.WaitAsync();
            try
            {
                Cache[title] = zh;
                File.WriteAllText(CachePath, JsonSerializer.Serialize(
                    Cache.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!)));
            }
            finally { Gate.Release(); }
            return zh;
        }
        catch { return null; /* 翻译是尽力而为 */ }
    }
}
