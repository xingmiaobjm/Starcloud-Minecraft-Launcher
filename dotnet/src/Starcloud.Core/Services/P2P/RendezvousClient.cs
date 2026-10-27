// 发现服务客户端：announce / peers / session / close（设计文档 §4）。
//
// 全部是 HTTPS 上的小 JSON，不碰文件字节；服务器只负责告诉两端「谁在线、地址是什么」。
// 与 FriendsService 共用 baseUrl 与 Bearer apiKey 口径，错误体也复用同一套解析。

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services.P2P;

/// <summary>对端候选地址（公网映射，来自 STUN）</summary>
public sealed record P2PCandidate(
    [property: JsonPropertyName("addr")] string Addr,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("family")] int Family);

/// <summary>peers 查询返回的一个在线对端</summary>
public sealed record P2PPeer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("candidates")] IReadOnlyList<P2PCandidate>? Candidates,
    [property: JsonPropertyName("nat")] string? Nat,
    [property: JsonPropertyName("age")] int Age);

/// <summary>一次传输会话的授权：session_id + 一次性令牌 + 对端候选</summary>
public sealed record P2PSessionGrant(
    [property: JsonPropertyName("session")] string SessionId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("peer")] P2PSessionPeer? Peer);

public sealed record P2PSessionPeer(
    [property: JsonPropertyName("candidates")] IReadOnlyList<P2PCandidate>? Candidates);

internal sealed record P2PAnnounceBody(
    [property: JsonPropertyName("nat")] string Nat,
    [property: JsonPropertyName("ttl")] int Ttl,
    [property: JsonPropertyName("candidates")] IReadOnlyList<P2PCandidate> Candidates,
    [property: JsonPropertyName("files")] IReadOnlyList<P2PIndexEntry> Files,
    [property: JsonPropertyName("snapshot")] int Snapshot,
    [property: JsonPropertyName("batch")] int Batch,
    [property: JsonPropertyName("batches")] int Batches);

/// <summary>announce 的服务端回执：accepted = 服务器真正收下并写入索引的条数</summary>
public sealed record P2PAnnounceResult(int Accepted, int NextAnnounceIn);

public sealed class RendezvousClient
{
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        BaseAddress = new Uri(FriendsService.BaseUrl),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;

    public RendezvousClient(Func<string?> apiKey, string baseUrl = FriendsService.BaseUrl,
        HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _http = baseUrl == FriendsService.BaseUrl && handler is null
            ? DefaultHttp
            : new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: false)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(20),
            };
    }

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_apiKey());

    public async Task<P2PAnnounceResult> AnnounceAsync(IReadOnlyList<P2PCandidate> candidates,
        string nat, IReadOnlyList<P2PIndexEntry> files, int snapshot, int batch, int batches,
        CancellationToken ct = default)
    {
        using var doc = await SendForDocumentAsync(HttpMethod.Post, "/v1/p2p/announce",
            new P2PAnnounceBody(nat, 300, candidates, files, snapshot, batch, batches), ct);
        var accepted = 0;
        var next = 180;
        if (doc.RootElement.TryGetProperty("accepted", out var a) && a.TryGetInt32(out var av)) accepted = av;
        if (doc.RootElement.TryGetProperty("next_announce_in", out var n) && n.TryGetInt32(out var nv)) next = nv;
        return new P2PAnnounceResult(accepted, next);
    }

    public Task OfflineAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "/v1/p2p/announce", null, ct);

    public async Task<IReadOnlyList<P2PPeer>> PeersAsync(string sha1, long size,
        CancellationToken ct = default)
    {
        using var doc = await SendForDocumentAsync(HttpMethod.Get,
            $"/v1/p2p/peers?sha1={Uri.EscapeDataString(sha1)}&size={size}", null, ct);
        if (!doc.RootElement.TryGetProperty("peers", out var peers) ||
            peers.ValueKind != JsonValueKind.Array)
            return [];
        return peers.Deserialize<List<P2PPeer>>(FriendsService.JsonOpts) ?? [];
    }

    public async Task<P2PSessionGrant?> CreateSessionAsync(string sha1, string peerId,
        CancellationToken ct = default)
    {
        using var doc = await SendForDocumentAsync(HttpMethod.Post, "/v1/p2p/session",
            new { sha1, peer = peerId }, ct);
        return doc.RootElement.Deserialize<P2PSessionGrant>(FriendsService.JsonOpts);
    }

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/v1/p2p/session/{Uri.EscapeDataString(sessionId)}/close", null, ct);

    /// <summary>
    /// 上报会话结果（ok / failed / canceled）：服务端据此做失败降权与成功率统计。
    /// 必须在 close 之前调用（close 会删掉会话行）。
    /// </summary>
    public Task ReportSessionAsync(string sessionId, string outcome, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/v1/p2p/session/{Uri.EscapeDataString(sessionId)}/report",
            new { outcome }, ct);

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var _ = await SendForDocumentAsync(method, path, body, ct);
    }

    private async Task<JsonDocument> SendForDocumentAsync(HttpMethod method, string path,
        object? body, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new FriendApiException("unauthorized", "请先登录 Starcloud ID");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static async Task<FriendApiException> ErrorAsync(HttpResponseMessage response,
        CancellationToken ct)
    {
        var code = "http_error";
        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    code = c.GetString() ?? code;
                if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = m.GetString();
            }
        }
        catch
        {
            // 非 JSON 错误体：按状态码兜底
        }
        return new FriendApiException(code, message ?? $"发现服务返回 {(int)response.StatusCode}",
            response.StatusCode);
    }
}
