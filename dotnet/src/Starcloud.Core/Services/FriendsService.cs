// Starcloud 好友：好友关系 / 申请 / 搜索 / 联机邀请，以及对端事件的 SSE 长连接。
//
// 协议（后端 starcloudfur.cn /v1/friends/*，鉴权 Bearer <api_key>）：
//   GET  /v1/friends                      {friends:[{user_id,id,display_name,avatar_url,since,unread}], total}
//   GET  /v1/friends/search?q=             {users:[{user_id,id,display_name,avatar_url,relation}]}
//   GET  /v1/friends/requests              {incoming:[...], outgoing:[...]}
//   POST /v1/friends/requests              {target,message?} → 201 {request}
//   POST /v1/friends/requests/{id}/accept|reject
//   DELETE /v1/friends/requests/{id}       撤回自己发出的申请
//   DELETE /v1/friends/{userId}            删除好友
//   GET  /v1/friends/invites               {incoming:[...], outgoing:[...]}
//   POST /v1/friends/{userId}/invites      {address?,port?,room_code?,message?} → 201 {invite}
//   POST /v1/friends/invites/{id}/accept|decline
//   DELETE /v1/friends/invites/{id}        撤回自己发出的邀请
//   GET  /v1/friends/events                SSE：friend_request / friend_request_accepted /
//                                          friend_message / invite / invite_accepted /
//                                          invite_declined / invite_canceled
// 错误体统一 {"error":{"code","message"}}，message 已中文化，直接展示。

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>用户在好友语境下的公开资料</summary>
public sealed record PublicProfile(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

/// <summary>好友列表项（unread = 对方发来的未读消息数）</summary>
public sealed record FriendProfile(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("since")] string Since,
    [property: JsonPropertyName("unread")] int Unread)
{
    public PublicProfile ToProfile() => new(UserId, Id, DisplayName, AvatarUrl);
}

/// <summary>好友申请（incoming 的 User = 申请人，outgoing 的 User = 被申请人）</summary>
public sealed record FriendRequestView(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("user")] PublicProfile User);

public sealed record FriendRequests(
    [property: JsonPropertyName("incoming")] IReadOnlyList<FriendRequestView> Incoming,
    [property: JsonPropertyName("outgoing")] IReadOnlyList<FriendRequestView> Outgoing);

/// <summary>搜索结果；relation: friend / outgoing / incoming / none</summary>
public sealed record FriendSearchResult(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("relation")] string Relation);

/// <summary>联机邀请；user = 收到的邀请里的邀请人 / 发出的邀请里的被邀请人</summary>
public sealed record FriendInvite(
    [property: JsonPropertyName("invite_id")] string InviteId,
    [property: JsonPropertyName("from_user_id")] string FromUserId,
    [property: JsonPropertyName("to_user_id")] string ToUserId,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("port")] int? Port,
    [property: JsonPropertyName("room_code")] string? RoomCode,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("expires_at")] string ExpiresAt,
    [property: JsonPropertyName("responded_at")] string? RespondedAt,
    [property: JsonPropertyName("user")] PublicProfile User)
{
    /// <summary>地址或房间码——邀请里至少有一个，UI 直接拿它展示/连接</summary>
    public string ConnectionText => RoomCode is { Length: > 0 }
        ? RoomCode
        : Port is { } p ? $"{Address}:{p}" : Address ?? "";
}

public sealed record FriendInvites(
    [property: JsonPropertyName("incoming")] IReadOnlyList<FriendInvite> Incoming,
    [property: JsonPropertyName("outgoing")] IReadOnlyList<FriendInvite> Outgoing);

/// <summary>一条好友私聊消息</summary>
public sealed record FriendMessage(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("from_user_id")] string FromUserId,
    [property: JsonPropertyName("to_user_id")] string ToUserId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("read_at")] string? ReadAt);

/// <summary>聊天附件：消息文本 = 前缀 + 这个结构的 JSON（走现有消息通道，后端只管文件存取）</summary>
public sealed record FileAttachment(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id);

/// <summary>群摘要（会话列表行）</summary>
public sealed record GroupSummary(
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("member_count")] int MemberCount,
    [property: JsonPropertyName("unread")] int Unread);

/// <summary>群成员</summary>
public sealed record GroupMember(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role);

/// <summary>群详情（含成员列表）</summary>
public sealed record GroupDetail(
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("members")] IReadOnlyList<GroupMember> Members);

/// <summary>一条群消息</summary>
public sealed record GroupMessage(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("from_user_id")] string FromUserId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("created_at")] string CreatedAt);

/// <summary>聊天记录一页（按 id 向前翻页）</summary>
public sealed record ConversationPage(
    [property: JsonPropertyName("items")] IReadOnlyList<FriendMessage> Items,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("next_before_id")] long? NextBeforeId);

/// <summary>事件类型见后端 realtime.ts</summary>
public sealed record FriendEvent(string Type, JsonElement Data);

/// <summary>事件数据反序列化（SSE 的 data 直接被各事件类型共用）</summary>
public static class FriendEventData
{
    public static T? Parse<T>(JsonElement data) =>
        data.Deserialize<T>(FriendsService.JsonOpts);
}

/// <summary>后端错误（code 供调用方做特殊分支，message 直接给用户看）</summary>
public sealed class FriendApiException(string code, string message, HttpStatusCode? status = null)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
}

public sealed class FriendsService
{
    public const string BaseUrl = "https://starcloudfur.cn";

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // 控制面请求不接系统代理：与 StarcloudIdService 同一口径
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };

    // 文件传输专用：默认 30 秒超时会掐死 200MB 的上传/下载（连接超时保留，快速失败）
    private static readonly HttpClient FileHttp = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;

    /// <summary>baseUrl / handler 只给单测换桩用，正式代码走默认云端地址。</summary>
    public FriendsService(
        Func<string?> apiKey, string baseUrl = BaseUrl, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _http = baseUrl == BaseUrl && handler is null
            ? DefaultHttp
            : new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: false)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
    }

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_apiKey());

    // ── 好友 ──

    public async Task<IReadOnlyList<FriendProfile>> GetFriendsAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/v1/friends", null, ct);
        return ReadList<FriendProfile>(doc.RootElement, "friends");
    }

    public async Task<FriendRequests> GetRequestsAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/v1/friends/requests", null, ct);
        return doc.RootElement.Deserialize<FriendRequests>(JsonOpts)
               ?? throw new InvalidOperationException("好友申请响应格式不正确");
    }

    public async Task<IReadOnlyList<FriendSearchResult>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        using var doc = await SendAsync(HttpMethod.Get,
            "/v1/friends/search?q=" + Uri.EscapeDataString(q), null, ct);
        return ReadList<FriendSearchResult>(doc.RootElement, "users");
    }

    public Task SendRequestAsync(string target, string? message, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, "/v1/friends/requests",
            new { target, message = string.IsNullOrWhiteSpace(message) ? null : message.Trim() }, ct);

    public Task AcceptRequestAsync(string requestId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post,
            $"/v1/friends/requests/{Uri.EscapeDataString(requestId)}/accept", new { }, ct);

    public Task RejectRequestAsync(string requestId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post,
            $"/v1/friends/requests/{Uri.EscapeDataString(requestId)}/reject", new { }, ct);

    public Task CancelRequestAsync(string requestId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete,
            $"/v1/friends/requests/{Uri.EscapeDataString(requestId)}", null, ct);

    public Task RemoveFriendAsync(string userId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete,
            $"/v1/friends/{Uri.EscapeDataString(userId)}", null, ct);

    // ── 联机邀请 ──

    public async Task<FriendInvites> GetInvitesAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/v1/friends/invites", null, ct);
        return doc.RootElement.Deserialize<FriendInvites>(JsonOpts)
               ?? throw new InvalidOperationException("联机邀请响应格式不正确");
    }

    public async Task<FriendInvite> CreateInviteAsync(
        string userId, string? address, int? port, string? roomCode, string? message,
        CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post,
            $"/v1/friends/{Uri.EscapeDataString(userId)}/invites",
            new
            {
                address = string.IsNullOrWhiteSpace(address) ? null : address.Trim(),
                port,
                room_code = string.IsNullOrWhiteSpace(roomCode) ? null : roomCode.Trim(),
                message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
            }, ct);
        return doc.RootElement.TryGetProperty("invite", out var invite)
            ? invite.Deserialize<FriendInvite>(JsonOpts)
              ?? throw new InvalidOperationException("联机邀请响应格式不正确")
            : throw new InvalidOperationException("联机邀请响应缺少 invite");
    }

    public Task AcceptInviteAsync(string inviteId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post,
            $"/v1/friends/invites/{Uri.EscapeDataString(inviteId)}/accept", new { }, ct);

    public Task DeclineInviteAsync(string inviteId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post,
            $"/v1/friends/invites/{Uri.EscapeDataString(inviteId)}/decline", new { }, ct);

    public Task CancelInviteAsync(string inviteId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete,
            $"/v1/friends/invites/{Uri.EscapeDataString(inviteId)}", null, ct);

    // ── 私聊 ──

    public async Task<ConversationPage> GetConversationAsync(
        string userId, long? beforeId = null, int limit = 50, CancellationToken ct = default)
    {
        var path = $"/v1/friends/{Uri.EscapeDataString(userId)}/messages?limit={Math.Clamp(limit, 1, 100)}";
        if (beforeId is > 0) path += $"&before_id={beforeId}";
        using var doc = await SendAsync(HttpMethod.Get, path, null, ct);
        return doc.RootElement.Deserialize<ConversationPage>(JsonOpts)
               ?? new ConversationPage([], false, null);
    }

    public async Task<FriendMessage> SendMessageAsync(
        string userId, string content, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post,
            $"/v1/friends/{Uri.EscapeDataString(userId)}/messages", new { content }, ct);
        return doc.RootElement.TryGetProperty("message", out var message)
            ? message.Deserialize<FriendMessage>(JsonOpts)
              ?? throw new InvalidOperationException("消息响应格式不正确")
            : throw new InvalidOperationException("消息响应缺少 message");
    }

    // ── 文件传输（图片 ≤30MB / 视频 ≤200MB / 文件 ≤200MB；接口见 docs/api-files.md） ──

    public const long MaxImageBytes = 30L * 1024 * 1024;
    public const long MaxFileBytes = 200L * 1024 * 1024;

    /// <summary>附件消息的内容前缀：前端据此把气泡渲染成文件卡片。</summary>
    public const string FileMessagePrefix = "[文件]";

    /// <summary>把附件编码成一条聊天消息文本（走现有消息通道，后端只管文件存取）。</summary>
    public static string EncodeFileMessage(string name, long size, string kind, string id) =>
        FileMessagePrefix + JsonSerializer.Serialize(
            new { name, size, kind, id }, JsonOpts);

    public static FileAttachment? TryParseFileMessage(string? content)
    {
        if (content is null ||
            !content.StartsWith(FileMessagePrefix, StringComparison.Ordinal)) return null;
        try
        {
            return JsonSerializer.Deserialize<FileAttachment>(
                content[FileMessagePrefix.Length..], JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>上传文件，返回 (fileId, 服务端确认的大小)。</summary>
    public async Task<(string Id, long Size)> UploadFileAsync(
        string filePath, CancellationToken ct = default)
    {
        var key = RequireKey();
        var fi = new FileInfo(filePath);
        if (!fi.Exists) throw new FileNotFoundException(filePath);

        await using var fs = File.OpenRead(filePath);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(fs), "file", fi.Name);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/files") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await FileHttp.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("上传响应缺少文件 id");
        var size = doc.RootElement.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s)
            ? s : fi.Length;
        return (id, size);
    }

    /// <summary>下载文件到指定路径。</summary>
    public async Task DownloadFileAsync(string fileId, string destPath, CancellationToken ct = default)
    {
        var key = RequireKey();
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/files/{Uri.EscapeDataString(fileId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await FileHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destPath);
        await stream.CopyToAsync(output, ct);
    }

    /// <summary>把该好友发来的消息标为已读，返回更新条数。</summary>
    public async Task<int> MarkReadAsync(string userId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post,
            $"/v1/friends/{Uri.EscapeDataString(userId)}/messages/read", new { }, ct);
        return doc.RootElement.TryGetProperty("updated", out var updated) &&
               updated.TryGetInt32(out var count) ? count : 0;
    }

    // ── 群聊（接口见服务端 groups 模块；附件协议与好友消息一致） ──

    public async Task<GroupDetail> CreateGroupAsync(
        string name, IReadOnlyList<string> memberIds, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, "/v1/groups",
            new { name, members = memberIds }, ct);
        return doc.RootElement.GetProperty("group").Deserialize<GroupDetail>(JsonOpts)
               ?? throw new InvalidOperationException("群聊响应格式不正确");
    }

    public async Task<IReadOnlyList<GroupSummary>> GetGroupsAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/v1/groups", null, ct);
        return ReadList<GroupSummary>(doc.RootElement, "groups");
    }

    public async Task<GroupDetail> GetGroupDetailAsync(string groupId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"/v1/groups/{Uri.EscapeDataString(groupId)}", null, ct);
        return doc.RootElement.Deserialize<GroupDetail>(JsonOpts)
               ?? throw new InvalidOperationException("群聊响应格式不正确");
    }

    public async Task<IReadOnlyList<GroupMessage>> GetGroupMessagesAsync(
        string groupId, long? beforeId = null, CancellationToken ct = default)
    {
        var url = $"/v1/groups/{Uri.EscapeDataString(groupId)}/messages?limit=100" +
                  (beforeId is { } b ? $"&before_id={b}" : "");
        using var doc = await SendAsync(HttpMethod.Get, url, null, ct);
        return ReadList<GroupMessage>(doc.RootElement, "items");
    }

    public async Task<GroupMessage> SendGroupMessageAsync(
        string groupId, string content, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post,
            $"/v1/groups/{Uri.EscapeDataString(groupId)}/messages", new { content }, ct);
        return doc.RootElement.TryGetProperty("message", out var message)
            ? message.Deserialize<GroupMessage>(JsonOpts)
              ?? throw new InvalidOperationException("消息响应格式不正确")
            : throw new InvalidOperationException("消息响应缺少 message");
    }

    public Task MarkGroupReadAsync(string groupId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post,
            $"/v1/groups/{Uri.EscapeDataString(groupId)}/messages/read", new { }, ct);

    public Task DissolveGroupAsync(string groupId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete,
            $"/v1/groups/{Uri.EscapeDataString(groupId)}", null, ct);

    public Task AddGroupMembersAsync(string groupId, IReadOnlyList<string> userIds, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post,
            $"/v1/groups/{Uri.EscapeDataString(groupId)}/members", new { members = userIds }, ct);

    // ── 实时事件 ──

    /// <summary>
    /// 建立一条 SSE 长连接并逐条回调；连接断开（正常结束 / 网络错误）时返回或抛出，
    /// 由调用方决定重连。服务端每 25 秒发一次心跳注释行。
    /// </summary>
    public async Task StreamEventsAsync(
        Func<FriendEvent, Task> onEvent, CancellationToken ct = default)
    {
        var key = RequireKey();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/friends/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.ParseAdd("text/event-stream");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw await ErrorAsync(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return;
            if (line.Length == 0)
            {
                eventName = null;
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal) &&
                     eventName is { Length: > 0 } name && name != "ready")
            {
                var payload = line[5..].Trim();
                if (payload.Length == 0) continue;
                using var doc = JsonDocument.Parse(payload);
                await onEvent(new FriendEvent(name, doc.RootElement.Clone()));
            }
        }
    }

    // ── HTTP 基础 ──

    private string RequireKey() =>
        _apiKey() is { Length: > 0 } key
            ? key
            : throw new FriendApiException("unauthorized", "请先登录 Starcloud ID");

    private async Task SendNoContentAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var _ = await SendAsync(method, path, body, ct);
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey());
        if (body is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json");
        }
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw await ErrorAsync(resp, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }

    /// <summary>后端错误体统一是 {"error":{"code","message"}}；解析不到就按状态码兜底。</summary>
    private static async Task<FriendApiException> ErrorAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        string code = "http_error";
        string? message = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    code = c.GetString() ?? code;
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = m.GetString();
            }
        }
        catch
        {
            // 非 JSON / 空响应：走兜底文案
        }
        return new FriendApiException(code,
            string.IsNullOrWhiteSpace(message) ? $"请求失败（{(int)resp.StatusCode}）" : message,
            resp.StatusCode);
    }

    private static IReadOnlyList<T> ReadList<T>(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.Deserialize<List<T>>(JsonOpts) ?? [];
    }
}
