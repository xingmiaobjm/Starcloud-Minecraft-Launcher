using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Starcloud.Multiplayer.Core.Protocol;

namespace Starcloud.Multiplayer.Client;

/// <summary>联机服务端返回的业务错误（带契约错误码，界面可据此给出可操作提示）。</summary>
public sealed class MpServiceException : Exception
{
    public MpServiceException(string errorCode, string message) : base(message) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}

/// <summary>
/// 联机服务端 HTTP 客户端。所有请求带 <c>Authorization: Bearer &lt;api_key&gt;</c>。
/// <para>
/// 回环地址强制绕开系统代理：Clash/mihomo 一类会把 127.0.0.1 也截走并回 404
/// （启动器在陶瓦联机上已经栽过一次）。
/// </para>
/// </summary>
public sealed class MpServiceClient : IDisposable
{
    private readonly HttpClient _http;

    public MpServiceClient(string baseAddress, string apiKey)
    {
        BaseAddress = baseAddress.TrimEnd('/');

        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        if (IsLoopback(BaseAddress)) handler.UseProxy = false;

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add(MpProtocol.ClientHeader, MpProtocol.ClientHeaderValue);
    }

    public string BaseAddress { get; }

    public Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), ct);

    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body, options: MpJson.Options),
        }, ct);

    public Task<T> DeleteAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Delete, path), ct);

    /// <summary>发一个不关心结果的请求（例如仅 mock 存在的 api_key 登记）。</summary>
    public async Task<bool> TryPostAsync(string path, object? body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsync(
                path, body is null ? null : JsonContent.Create(body, options: MpJson.Options), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            using var response = await _http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            var envelope = TryParse<T>(json);
            if (envelope is not null)
            {
                if (envelope.Ok && envelope.Data is not null) return envelope.Data;
                throw new MpServiceException(
                    envelope.Error ?? MpProtocol.ErrServerError,
                    envelope.Message ?? "服务端返回失败");
            }

            throw new MpServiceException(
                response.StatusCode == HttpStatusCode.Unauthorized ? MpProtocol.ErrUnauthorized : MpProtocol.ErrServerError,
                $"服务端返回 {(int)response.StatusCode}，响应体无法解析：{Truncate(json)}");
        }
    }

    private static MpEnvelope<T>? TryParse<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return MpJson.Deserialize<MpEnvelope<T>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private static bool IsLoopback(string baseAddress)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback
               || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}
