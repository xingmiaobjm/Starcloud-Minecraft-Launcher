// 云端公告：拉取 Markdown 文本并在启动器里只读展示。
//
// 安全边界（用户明确要求「仅显示 MD，不能执行任何代码」）：
//   · 只做 GET 拉取，响应只取 title / body_md 等纯文本字段；
//   · 渲染走 MarkdownDocument + NovaMarkdown（解析成段落/标题/列表/代码块 → RichTextBlock 内联），
//     不解析 HTML、不加载远程资源、不执行脚本；
//   · 链接只允许 http/https，点击时才交给系统浏览器打开；
//   · 任何异常静默（公告是旁路能力，绝不能影响启动）。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>一条云端公告（只读文本 + Markdown 正文）</summary>
public sealed record Announcement(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body_md")] string Body,
    [property: JsonPropertyName("published_at")] string? PublishedAt = null,
    [property: JsonPropertyName("pinned")] bool Pinned = false);

public sealed class AnnouncementService
{
    /// <summary>与好友/账号服务同一个后端</summary>
    public const string BaseUrl = FriendsService.BaseUrl;

    private readonly Func<LauncherSettings> _settings;
    private readonly string _baseUrl;

    /// <summary>baseUrl 仅给本地联调/测试注入，正式运行走默认云端地址。</summary>
    public AnnouncementService(Func<LauncherSettings> settings, string? baseUrl = null)
    {
        _settings = settings;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? BaseUrl : baseUrl!;
    }

    /// <summary>
    /// 拉取公告列表：匿名 GET，10 秒超时；失败返回空表（界面不提示，避免打扰）。
    /// </summary>
    public async Task<IReadOnlyList<Announcement>> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(8),
            })
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromSeconds(12),
            };
            using var resp = await http.GetAsync("/v1/announcements", ct);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct);
            return Parse(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>解析响应：兼容 {items:[...]} 与直接数组两种形态（公开以便单测）</summary>
    public static IReadOnlyList<Announcement> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                array = items;
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                array = data;
            else return [];

            var list = new List<Announcement>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Str(item, "id");
                var title = Str(item, "title");
                var body = Str(item, "body_md") ?? Str(item, "body");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || body is null) continue;
                // 正文是纯文本展示用：Web 端不该塞脚本进来，这里也不解释任何标记以外的东西
                list.Add(new Announcement(id!, title!, body,
                    Str(item, "published_at"), Bool(item, "pinned")));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>挑出「还没看过」的最新一条（置顶优先，其次按列表顺序即发布时间倒序）</summary>
    public static Announcement? PickUnread(IReadOnlyList<Announcement> items, IReadOnlyList<string> seenIds)
    {
        var seen = new HashSet<string>(seenIds ?? [], StringComparer.Ordinal);
        var unread = items.Where(a => !seen.Contains(a.Id)).ToList();
        if (unread.Count == 0) return null;
        return unread.FirstOrDefault(a => a.Pinned) ?? unread[0];
    }

    /// <summary>记下已看过的公告 id（上限 50，避免设置文件无限增长）</summary>
    public void MarkSeen(Announcement announcement)
    {
        try
        {
            var settings = _settings();
            var seen = (settings.SeenAnnouncementIds ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
            if (!seen.Contains(announcement.Id)) seen.Add(announcement.Id);
            if (seen.Count > 50) seen = seen.Skip(seen.Count - 50).ToList();
            settings.SeenAnnouncementIds = seen;
            settings.Save();
        }
        catch
        {
            // 记不住只会重复弹一次，不值得打断用户
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
