// 联机房间服务：/v1/rooms/* 的后端适配层。
//
// 为什么单独抽一层（RoomService + IRoomBackend）：
//   · 服务端接口由用户后补，字段名可能调整 —— 改动只落在这一个文件；
//   · 接口未就绪时用 MockRoomBackend 把 UI 全链路跑通、截图验收；
//   · 单测可以换桩（baseUrl + handler），与 FriendsService 同一套做法。
//
// 鉴权与好友服务同源：Bearer <StarcloudId.ApiKey>。密码只在服务端校验，
// 大厅列表不下发密码；陶瓦联机码仅在验密通过后的 join 响应里出现。

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Starcloud.Core.Services.Rooms;

/// <summary>整合包上传结果（服务端存对象存储，url 是公开下载地址）。</summary>
public sealed record RoomPackUpload(
    string FileId,
    string? Url,
    long Size,
    string? Sha256,
    string? Name);

/// <summary>
/// 读计数包装流：StreamContent 拷贝上传体时逐块回调已读字节数，给慢上行的进度条用。
/// 节流：每跨过 1 MB（或到达流尾）才回调一次 —— StreamContent 按 4KB 拷贝，逐块报会把
/// UI 派发刷爆。
/// </summary>
file sealed class CountingStream(Stream inner, IProgress<long> uploaded) : Stream
{
    private long _sent;
    private long _nextMark = 1024 * 1024;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        if (read > 0) Report();
        return read;
    }

    private void Report()
    {
        var sent = Interlocked.Add(ref _sent, 0);
        if (sent < _nextMark && sent < inner.Length) return;
        _nextMark = sent + 1024 * 1024;
        uploaded.Report(sent);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0) Report();
        return read;
    }

    public override void Flush() => inner.Flush();
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int ReadByte() => inner.ReadByte();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    protected override void Dispose(bool disposing)
    {
        // 不落 inner：包装流由 StreamContent 收尾，真正的文件流归调用方的 await using 管
        base.Dispose(disposing);
    }
}

/// <summary>房间后端（HTTP 实现与 Mock 实现共用这一套语义）。</summary>
public interface IRoomBackend
{
    Task<IReadOnlyList<RoomSummary>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoomSummary>> MineAsync(CancellationToken ct = default);
    Task<RoomSummary> CreateAsync(CreateRoomRequest request, CancellationToken ct = default);
    Task<RoomDetail> GetAsync(string code, CancellationToken ct = default);
    Task<RoomJoinResult> JoinAsync(string code, string? password, CancellationToken ct = default);
    Task LeaveAsync(string code, CancellationToken ct = default);
    Task KickAsync(string code, string userId, CancellationToken ct = default);
    Task DissolveAsync(string code, CancellationToken ct = default);
    Task ReportTerracottaAsync(string code, string terracottaCode, CancellationToken ct = default);
    Task<IReadOnlyList<RoomMessage>> GetMessagesAsync(string code, long? beforeId, int limit,
        CancellationToken ct = default);
    Task<RoomMessage> SendMessageAsync(string code, string content, CancellationToken ct = default);
    Task StreamEventsAsync(string code, Func<RoomEvent, Task> onEvent, CancellationToken ct = default);
    /// <summary>保活：不开 SSE 的界面用它刷新活跃时间（房主 60 秒无连接会被自动关房）。</summary>
    Task HeartbeatAsync(string code, CancellationToken ct = default);
    /// <summary>上传整合包（原始字节流，200MB 上限）。返回的 file_id 建房时引用。
    /// <paramref name="uploaded"/>：已上传字节数回调（慢上行时给进度条用，可为 null）。</summary>
    Task<RoomPackUpload> UploadPackAsync(string filePath, string fileName,
        IProgress<long>? uploaded = null, CancellationToken ct = default);
}

/// <summary>房间接口的错误（带 HTTP 状态与稳定错误码，UI 据此出中文提示）。</summary>
public sealed class RoomApiException(string message, string? code = null, int? status = null)
    : Exception(message)
{
    public string? Code { get; } = code;
    public int? Status { get; } = status;

    /// <summary>密码错误（服务端约定 403 + code=bad_password）。</summary>
    public bool IsBadPassword =>
        Status == 403 || string.Equals(Code, "bad_password", StringComparison.OrdinalIgnoreCase);

    /// <summary>房间不存在 / 已关闭。</summary>
    public bool IsGone =>
        Status == 404 || Status == 410 ||
        string.Equals(Code, "room_not_found", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Code, "room_closed", StringComparison.OrdinalIgnoreCase);

    /// <summary>人满。</summary>
    public bool IsFull => string.Equals(Code, "room_full", StringComparison.OrdinalIgnoreCase);

    /// <summary>已有一间开放中的房间（建房 429 room_limit）——要提示「先解散」，不是普通限流。</summary>
    public bool IsRoomLimit => string.Equals(Code, "room_limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>房主不能退出（服务端要求走解散）。</summary>
    public bool IsOwnerMustDissolve =>
        string.Equals(Code, "owner_must_dissolve", StringComparison.OrdinalIgnoreCase);

    /// <summary>整合包体积超服务端单文件上限。</summary>
    public bool IsPackTooLarge =>
        string.Equals(Code, "pack_too_large", StringComparison.OrdinalIgnoreCase);
}

/// <summary>房间服务的门面：已登录走云端，未登录或强制 Mock 时走本地演示实现。</summary>
public sealed class RoomService
{
    // 联机服务器（已上线）：标准 443，独立 vhost。旧地址 :54088（183.66.27.20）已下线。
    // 注意 SSE 必须走这个域名 —— 事件流只存在于 rooms 进程内存里，主站 /v1 收不到事件。
    // 契约见 docs/api-rooms-live.md。
    public const string BaseUrl = "https://rooms.starcloudfur.cn";

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<string?> _apiKey;
    private readonly string _me;
    private IRoomBackend _backend;

    /// <summary>baseUrl / handler 只给单测换桩；mock 强制本地演示后端。</summary>
    public RoomService(Func<string?> apiKey, string baseUrl = BaseUrl,
        HttpMessageHandler? handler = null, bool mock = false, string me = "我")
    {
        _apiKey = apiKey;
        _me = me;
        _backend = mock
            ? new MockRoomBackend(me)
            : new HttpRoomBackend(apiKey, baseUrl, handler);
    }

    /// <summary>
    /// 切到本地演示后端。服务端接口还没上线时（任意 /v1/rooms 请求返回「未找到接口」）
    /// 由 UI 调用一次，大厅/房间功能立刻可验收；接口上线后不会触发。
    /// </summary>
    public void UseMock()
    {
        if (_backend is MockRoomBackend) return;
        _backend = new MockRoomBackend(_me);
    }

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_apiKey());

    /// <summary>当前后端是否本地演示实现（UI 角落标注「演示数据」）。</summary>
    public bool IsMock => _backend is MockRoomBackend;

    public Task<IReadOnlyList<RoomSummary>> ListAsync(CancellationToken ct = default) =>
        _backend.ListAsync(ct);

    public Task<IReadOnlyList<RoomSummary>> MineAsync(CancellationToken ct = default) =>
        _backend.MineAsync(ct);

    public Task<RoomSummary> CreateAsync(CreateRoomRequest request, CancellationToken ct = default) =>
        _backend.CreateAsync(request, ct);

    public Task<RoomDetail> GetAsync(string code, CancellationToken ct = default) =>
        _backend.GetAsync(Normalize(code), ct);

    public Task<RoomJoinResult> JoinAsync(string code, string? password,
        CancellationToken ct = default) =>
        _backend.JoinAsync(Normalize(code), password, ct);

    public Task LeaveAsync(string code, CancellationToken ct = default) =>
        _backend.LeaveAsync(Normalize(code), ct);

    public Task KickAsync(string code, string userId, CancellationToken ct = default) =>
        _backend.KickAsync(Normalize(code), userId, ct);

    public Task DissolveAsync(string code, CancellationToken ct = default) =>
        _backend.DissolveAsync(Normalize(code), ct);

    public Task ReportTerracottaAsync(string code, string terracottaCode,
        CancellationToken ct = default) =>
        _backend.ReportTerracottaAsync(Normalize(code), terracottaCode, ct);

    public Task<IReadOnlyList<RoomMessage>> GetMessagesAsync(string code, long? beforeId,
        int limit = 50, CancellationToken ct = default) =>
        _backend.GetMessagesAsync(Normalize(code), beforeId, Math.Clamp(limit, 1, 100), ct);

    public Task<RoomMessage> SendMessageAsync(string code, string content,
        CancellationToken ct = default) =>
        _backend.SendMessageAsync(Normalize(code), content, ct);

    public Task StreamEventsAsync(string code, Func<RoomEvent, Task> onEvent,
        CancellationToken ct = default) =>
        _backend.StreamEventsAsync(Normalize(code), onEvent, ct);

    public Task HeartbeatAsync(string code, CancellationToken ct = default) =>
        _backend.HeartbeatAsync(Normalize(code), ct);

    /// <summary>上传整合包：原始字节流 → 对象存储，返回 file_id 与公开下载 url。</summary>
    public Task<RoomPackUpload> UploadPackAsync(string filePath, string fileName,
        IProgress<long>? uploaded = null, CancellationToken ct = default) =>
        _backend.UploadPackAsync(filePath, fileName, uploaded, ct);

    /// <summary>进房前的本地校验：6 位数字码，不合法直接抛（省一次请求）。</summary>
    private static string Normalize(string code)
    {
        var digits = RoomCodeRules.Normalize(code);
        if (!RoomCodeRules.IsValid(digits))
            throw new RoomApiException("联机码应为 6 位数字", "bad_code", 400);
        return digits;
    }
}

/// <summary>/v1/rooms/* 的 HTTP 实现。</summary>
internal sealed class HttpRoomBackend : IRoomBackend
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        BaseAddress = new Uri(RoomService.BaseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };

    // 事件流：30 秒超时会掐断长连接
    private static readonly HttpClient StreamHttp = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        BaseAddress = new Uri(RoomService.BaseUrl),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private readonly HttpClient _streamHttp;

    public HttpRoomBackend(Func<string?> apiKey, string baseUrl, HttpMessageHandler? handler)
    {
        _apiKey = apiKey;
        var custom = baseUrl != RoomService.BaseUrl || handler is not null;
        _http = custom
            ? new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: false)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            }
            : Http;
        _streamHttp = custom
            ? new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: false)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = Timeout.InfiniteTimeSpan,
            }
            : StreamHttp;
    }

    public async Task<IReadOnlyList<RoomSummary>> ListAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/v1/rooms", null, ct);
        return ReadList<RoomSummary>(doc.RootElement, "rooms");
    }

    public async Task<IReadOnlyList<RoomSummary>> MineAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/v1/rooms/mine", null, ct);
        return ReadList<RoomSummary>(doc.RootElement, "rooms");
    }

    public async Task<RoomSummary> CreateAsync(CreateRoomRequest request, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, "/v1/rooms", request, ct);
        return ReadOne<RoomSummary>(doc.RootElement, "room")
               ?? throw new RoomApiException("创建房间失败：响应缺少房间信息");
    }

    public async Task<RoomDetail> GetAsync(string code, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"/v1/rooms/{code}", null, ct);
        return ReadOne<RoomDetail>(doc.RootElement, "room")
               ?? throw new RoomApiException("获取房间失败：响应缺少房间信息");
    }

    public async Task<RoomJoinResult> JoinAsync(string code, string? password,
        CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"/v1/rooms/{code}/join",
            new { password }, ct);
        return ReadOne<RoomJoinResult>(doc.RootElement, "result") ?? new RoomJoinResult(
            ReadOne<RoomDetail>(doc.RootElement, "room")
                ?? throw new RoomApiException("加入房间失败：响应缺少房间信息"),
            TryGetString(doc.RootElement, "terracotta_code"));
    }

    public Task LeaveAsync(string code, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Post, $"/v1/rooms/{code}/leave", null, ct);

    public Task KickAsync(string code, string userId, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Post, $"/v1/rooms/{code}/kick", new { user_id = userId }, ct);

    public Task DissolveAsync(string code, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Delete, $"/v1/rooms/{code}", null, ct);

    public Task ReportTerracottaAsync(string code, string terracottaCode,
        CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Put, $"/v1/rooms/{code}/terracotta",
            new { terracotta_code = terracottaCode }, ct);

    public Task HeartbeatAsync(string code, CancellationToken ct = default) =>
        SendOnlyAsync(HttpMethod.Post, $"/v1/rooms/{code}/heartbeat", null, ct);

    /// <summary>
    /// 上传整合包：请求体就是文件原始字节（Content-Type: application/octet-stream）。
    /// 走 <see cref="_streamHttp"/>（无超时）：200MB 的包在慢上行下会远超普通请求的 30 秒。
    /// </summary>
    public async Task<RoomPackUpload> UploadPackAsync(string filePath, string fileName,
        IProgress<long>? uploaded = null, CancellationToken ct = default)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists) throw new FileNotFoundException(filePath);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/rooms/packs?name={Uri.EscapeDataString(fileName)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey());
        await using var fs = File.OpenRead(filePath);
        // 计数流：StreamContent 拷贝时逐块回调，慢上行也能看到进度在走
        req.Content = uploaded is null
            ? new StreamContent(fs)
            : new StreamContent(new CountingStream(fs, uploaded));
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = fi.Length;

        HttpResponseMessage resp;
        try
        {
            resp = await _streamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            throw new RoomApiException("联机服务器暂时连不上", "network", null);
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            // 响应形如 { "pack": { file_id, url, size, sha256, name } }
            var pack = doc.RootElement.TryGetProperty("pack", out var p) ? p : doc.RootElement;
            var fileId = TryGetString(pack, "file_id");
            if (string.IsNullOrWhiteSpace(fileId))
                throw new RoomApiException("上传响应缺少 file_id");
            var size = pack.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s)
                ? s : fi.Length;
            return new RoomPackUpload(fileId, TryGetString(pack, "url"), size,
                TryGetString(pack, "sha256"), TryGetString(pack, "name"));
        }
    }

    public async Task<IReadOnlyList<RoomMessage>> GetMessagesAsync(string code, long? beforeId,
        int limit, CancellationToken ct = default)
    {
        var query = beforeId is { } b ? $"?before_id={b}&limit={limit}" : $"?limit={limit}";
        using var doc = await SendAsync(HttpMethod.Get, $"/v1/rooms/{code}/messages{query}", null, ct);
        return ReadList<RoomMessage>(doc.RootElement, "messages");
    }

    public async Task<RoomMessage> SendMessageAsync(string code, string content,
        CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"/v1/rooms/{code}/messages",
            new { content }, ct);
        return ReadOne<RoomMessage>(doc.RootElement, "message")
               ?? throw new RoomApiException("发送消息失败：响应缺少消息体");
    }

    public async Task StreamEventsAsync(string code, Func<RoomEvent, Task> onEvent,
        CancellationToken ct = default)
    {
        var key = RequireKey();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/rooms/{code}/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.ParseAdd("text/event-stream");

        using var resp = await SendStreamAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return;
            if (line.Length == 0) { eventName = null; continue; }
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
                await onEvent(new RoomEvent(name, doc.RootElement.Clone()));
            }
        }
    }

    // ── HTTP 骨架（与 FriendsService 同构） ──

    /// <summary>SSE 连接：连接失败同样包成 network 错误码（外部取消照常透传）。</summary>
    private async Task<HttpResponseMessage> SendStreamAsync(HttpRequestMessage req,
        CancellationToken ct)
    {
        try
        {
            return await _streamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            throw new RoomApiException("联机服务器暂时连不上", "network", null);
        }
    }

    private string RequireKey() =>
        _apiKey() is { Length: > 0 } key
            ? key
            : throw new RoomApiException("请先登录 Starcloud ID", "no_session", 401);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey());
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, RoomService.JsonOpts),
                Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            // 服务器没开机 / 网络不通 / 超时：给稳定错误码，UI 靠它降级演示数据
            throw new RoomApiException("联机服务器暂时连不上", "network", null);
        }
        if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private async Task SendOnlyAsync(HttpMethod method, string path, object? body,
        CancellationToken ct)
    {
        using var doc = await SendAsync(method, path, body, ct);
    }

    private static async Task<Exception> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object)
            {
                return new RoomApiException(
                    err.TryGetProperty("message", out var m) ? m.GetString() ?? "请求失败" : "请求失败",
                    err.TryGetProperty("code", out var c) ? c.GetString() : null,
                    (int)resp.StatusCode);
            }
        }
        catch { /* 非 JSON 错误体：走下面的通用文案 */ }
        return new RoomApiException(
            (int)resp.StatusCode == (int)HttpStatusCode.Unauthorized
                ? "登录已失效，请重新登录 Starcloud ID"
                : $"服务器返回 {(int)resp.StatusCode}",
            null, (int)resp.StatusCode);
    }

    private static IReadOnlyList<T> ReadList<T>(JsonElement root, string prop)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.Deserialize<List<T>>(RoomService.JsonOpts) ?? [];
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            return arr.Deserialize<List<T>>(RoomService.JsonOpts) ?? [];
        return [];
    }

    private static T? ReadOne<T>(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) ? el.Deserialize<T>(RoomService.JsonOpts) : default;

    private static string? TryGetString(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

/// <summary>
/// 本地演示后端：接口未就绪时把 UI 全链路跑通。
/// 两个预置房间（一个公开、一个密码 123456），支持建房/进出/踢人/解散/推荐/聊天，
/// 事件用内存通道推送 —— 与云端实现的语义保持一致。
/// </summary>
internal sealed class MockRoomBackend : IRoomBackend
{
    private sealed class MockRoom
    {
        public required RoomSummary Summary { get; set; }
        public List<RoomMember> Members { get; } = [];
        public List<RoomMessage> Messages { get; } = [];
        public string? Password { get; set; }
        public string? TerracottaCode { get; set; }
        public bool Closed { get; set; }
        public Channel<RoomEvent> Events { get; } = Channel.CreateUnbounded<RoomEvent>();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, MockRoom> _rooms = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mine = new(StringComparer.Ordinal);
    private readonly string _me;
    private readonly string _myUserId = "me";

    public MockRoomBackend(string me)
    {
        _me = string.IsNullOrWhiteSpace(me) ? "我" : me;
        SeedDemoRooms();
    }

    private void SeedDemoRooms()
    {
        var host = new RoomUser("u-host", "小明", null);
        Add(new MockRoom
        {
            Summary = new RoomSummary("248135", "小明的建筑服", host, 2, 8, false,
                "1.20.1", "fabric",
                new RoomPackInfo("demo-pack", 42_000_000, null, "modrinth", "1.20.1", "fabric")
                { Name = "建筑整合包" },
                true, DateTimeOffset.Now.AddMinutes(-30).ToString("O")),
            TerracottaCode = "U/DEMO-BUILD-0001-AAAA",
        }, ownerName: "小明", ownerId: "u-host");
        Add(new MockRoom
        {
            Summary = new RoomSummary("731906", "周末生存 · 带密码", host, 3, 6, true,
                "1.19.2", "forge",
                new RoomPackInfo("demo-pack-2", 108_000_000, null, "modrinth", "1.19.2", "forge")
                { Name = "生存整合包" },
                true, DateTimeOffset.Now.AddMinutes(-12).ToString("O")),
            Password = "123456",
            TerracottaCode = "U/DEMO-SURV-0002-BBBB",
        }, ownerName: "小明", ownerId: "u-host");
    }

    private void Add(MockRoom room, string ownerName, string ownerId)
    {
        room.Members.Add(new RoomMember(ownerId, ownerName, null, "owner", true));
        _rooms[room.Summary.Code] = room;
    }

    public Task<IReadOnlyList<RoomSummary>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _rooms.Values
                .Where(r => !r.Closed)
                .Select(r => r.Summary with { Members = r.Members.Count })
                .OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult<IReadOnlyList<RoomSummary>>(list);
        }
    }

    public Task<IReadOnlyList<RoomSummary>> MineAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _mine
                .Where(_rooms.ContainsKey)
                .Select(code => _rooms[code])
                .Where(r => !r.Closed)
                .Select(r => r.Summary with { Members = r.Members.Count })
                .ToList();
            return Task.FromResult<IReadOnlyList<RoomSummary>>(list);
        }
    }

    public Task<RoomSummary> CreateAsync(CreateRoomRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var code = NewCode();
            var me = new RoomUser(_myUserId, _me, null);
            var room = new MockRoom
            {
                Summary = new RoomSummary(code, request.Name, me, 1,
                    Math.Clamp(request.MaxPlayers, CreateRoomRequest.MinPlayers,
                        CreateRoomRequest.MaxPlayersLimit),
                    !string.IsNullOrEmpty(request.Password),
                    request.McVersion ?? request.Pack?.McVersion,
                    request.Loader ?? request.Pack?.Loader,
                    request.Pack, false,
                    DateTimeOffset.Now.ToString("O")),
                Password = string.IsNullOrEmpty(request.Password) ? null : request.Password,
            };
            room.Members.Add(new RoomMember(_myUserId, _me, null, "owner", true));
            room.Messages.Add(new RoomMessage(1, code, "system", "系统",
                null, "房间已创建，等待玩家加入。", DateTimeOffset.Now.ToString("O")));
            _rooms[code] = room;
            _mine.Add(code);
            return Task.FromResult(room.Summary);
        }
    }

    public Task<RoomDetail> GetAsync(string code, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            return Task.FromResult(Detail(room));
        }
    }

    public Task<RoomJoinResult> JoinAsync(string code, string? password,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            if (room.Closed) throw new RoomApiException("房间已关闭", "room_closed", 410);
            if (room.Password is { Length: > 0 } pw && pw != password)
                throw new RoomApiException("密码不正确", "bad_password", 403);
            if (room.Members.Count >= room.Summary.MaxPlayers && room.Members.All(m => m.UserId != _myUserId))
                throw new RoomApiException("房间人数已满", "room_full", 409);

            if (room.Members.All(m => m.UserId != _myUserId))
            {
                room.Members.Add(new RoomMember(_myUserId, _me, null, "member", true));
                Publish(room, RoomEvent.MemberJoined,
                    new { user_id = _myUserId, display_name = _me });
            }
            _mine.Add(code);
            return Task.FromResult(new RoomJoinResult(Detail(room), room.TerracottaCode));
        }
    }

    public Task LeaveAsync(string code, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            room.Members.RemoveAll(m => m.UserId == _myUserId);
            Publish(room, RoomEvent.MemberLeft, new { user_id = _myUserId, display_name = _me });
            return Task.CompletedTask;
        }
    }

    public Task KickAsync(string code, string userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            if (room.Members.FirstOrDefault(m => m.UserId == _myUserId) is not { IsOwner: true })
                throw new RoomApiException("只有房主可以踢人", "not_owner", 403);
            room.Members.RemoveAll(m => m.UserId == userId);
            Publish(room, RoomEvent.Kicked, new { user_id = userId });
            return Task.CompletedTask;
        }
    }

    public Task DissolveAsync(string code, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            room.Closed = true;
            _mine.Remove(code);
            Publish(room, RoomEvent.RoomClosed, new { code });
            return Task.CompletedTask;
        }
    }

    public Task ReportTerracottaAsync(string code, string terracottaCode,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            room.TerracottaCode = terracottaCode;
            room.Summary = room.Summary with { HasTerracotta = true };
            Publish(room, RoomEvent.TerracottaReady, new { terracotta_code = terracottaCode });
            return Task.CompletedTask;
        }
    }

    public Task HeartbeatAsync(string code, CancellationToken ct = default)
    {
        lock (_gate) _ = Require(code);
        return Task.CompletedTask;
    }

    /// <summary>演示后端：不上传，返回一个假 file_id（演示房间本来就带预置包）。</summary>
    public Task<RoomPackUpload> UploadPackAsync(string filePath, string fileName,
        IProgress<long>? uploaded = null, CancellationToken ct = default)
    {
        var size = new FileInfo(filePath).Length;
        return Task.FromResult(new RoomPackUpload(
            "demo-pack-" + Guid.NewGuid().ToString("N")[..8], null, size, null, fileName));
    }

    public Task<IReadOnlyList<RoomMessage>> GetMessagesAsync(string code, long? beforeId,
        int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            var list = room.Messages
                .Where(m => beforeId is null || m.Id < beforeId)
                .OrderBy(m => m.Id)
                .TakeLast(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<RoomMessage>>(list);
        }
    }

    public Task<RoomMessage> SendMessageAsync(string code, string content,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var room = Require(code);
            var message = new RoomMessage(
                room.Messages.Count == 0 ? 1 : room.Messages[^1].Id + 1,
                code, _myUserId, _me, null, content, DateTimeOffset.Now.ToString("O"));
            room.Messages.Add(message);
            Publish(room, RoomEvent.Message, message);
            return Task.FromResult(message);
        }
    }

    public async Task StreamEventsAsync(string code, Func<RoomEvent, Task> onEvent,
        CancellationToken ct = default)
    {
        Channel<RoomEvent> events;
        lock (_gate) events = Require(code).Events;
        await foreach (var ev in events.Reader.ReadAllAsync(ct))
        {
            await onEvent(ev);
        }
    }

    private MockRoom Require(string code) =>
        _rooms.TryGetValue(code, out var room) && !room.Closed
            ? room
            : throw new RoomApiException("房间不存在或已关闭", "room_not_found", 404);

    private RoomDetail Detail(MockRoom room)
    {
        var me = room.Members.FirstOrDefault(m => m.UserId == _myUserId);
        return new RoomDetail(room.Summary with { Members = room.Members.Count },
            room.Members.ToList(), me is not null, me?.IsOwner == true);
    }

    private string NewCode()
    {
        var rng = Random.Shared;
        for (var i = 0; i < 200; i++)
        {
            var code = rng.Next(100000, 999999).ToString();
            if (!_rooms.ContainsKey(code)) return code;
        }
        throw new RoomApiException("联机码生成失败，请重试", "code_exhausted", 500);
    }

    private static void Publish(MockRoom room, string type, object data) =>
        room.Events.Writer.TryWrite(new RoomEvent(type,
            JsonSerializer.SerializeToElement(data, RoomService.JsonOpts)));
}
