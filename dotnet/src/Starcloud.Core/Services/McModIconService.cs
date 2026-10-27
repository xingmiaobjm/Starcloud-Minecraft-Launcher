// MC 百科（mcmod.cn）模组图标：按名字搜索 → 条目类页面 → 封面图 URL。
//
// 只解析 URL 不下载：图片统一走 RemoteIconHelper / IconCache（与下载页社区图标共用一份缓存）。
// 「名字 → URL」与「查过但没有」的结果都落盘，避免重复请求百科。
// 每个名字最多两次请求（搜索 + 类页），调用方自己控制并发。

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

public static class McModIconService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly object Gate = new();
    private static Dictionary<string, string?> _urlMap = [];
    private static bool _loaded;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Starcloud Launcher)");
        return http;
    }

    private static string MapPath => Path.Combine(LauncherPaths.CacheDir, "mod-icons", "mcmod-urls.json");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(MapPath)) return;
            _urlMap = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(MapPath)) ?? [];
        }
        catch { /* 缓存损坏从零开始 */ }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapPath)!);
            File.WriteAllText(MapPath, JsonSerializer.Serialize(_urlMap));
        }
        catch { /* 缓存写失败不影响功能 */ }
    }

    /// <summary>按名字取百科封面图 URL；查不到返回 null（结果落盘，不再重复请求）。</summary>
    public static async Task<string?> ResolveUrlAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (Gate)
        {
            EnsureLoaded();
            if (_urlMap.TryGetValue(name.Trim(), out var known)) return known;
        }

        try
        {
            // 1) 搜索页：第一个条目链接就是 /class/<id>.html
            var search = await Http.GetStringAsync(
                $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(name)}", ct);
            var hit = Regex.Match(search, @"href=""https://www\.mcmod\.cn/class/(\d+)\.html""");
            if (!hit.Success) { Remember(name, null); return null; }

            // 2) 类页面：封面图是 <img data-original-title=… src="//i.mcmod.cn/class/cover/…">
            var page = await Http.GetStringAsync(
                $"https://www.mcmod.cn/class/{hit.Groups[1].Value}.html", ct);
            var img = Regex.Match(page, @"src=""(//i\.mcmod\.cn/class/cover/[^""]+)""");
            if (!img.Success) { Remember(name, null); return null; }

            // CDN 裁切后缀（@480x300.jpg）换方图；URL 交给 IconCache 统一缓存
            var url = "https:" + Regex.Replace(img.Groups[1].Value,
                @"@\d+x\d+\.[a-z]+$", "@128x128.jpg");
            Remember(name, url);
            return url;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }   // 网络失败不缓存，下次进来重试
    }

    private static void Remember(string name, string? url)
    {
        lock (Gate)
        {
            EnsureLoaded();
            _urlMap[name.Trim()] = url;
            Save();
        }
    }
}
