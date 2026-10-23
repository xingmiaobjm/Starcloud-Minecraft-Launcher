// TCP 链路客户端：连接对端 → 同一套挑战-应答握手 → 一问一答（命令 + 可选文件块）。
// 每个客户端实例对应一次完整会话（连接 - N 条命令 - 关闭）；用完即弃，无连接池（M1 频率低）。

using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;

namespace Starcloud.Core.Services;

/// <summary>命令响应：Payload 为对端 JSON 行里的 payload 对象；HasFile 表示其后跟了一个文件块。</summary>
public sealed record PeerResponse(
    bool Ok,
    JsonElement? Payload,
    string? Error,
    string? ReceivedFile = null)
{
    /// <summary>取 payload 里某字段（文件类响应的 size / sha256 在这里）</summary>
    public T? Value<T>(string key, T? fallback = default)
    {
        if (Payload is not { } p || p.ValueKind != JsonValueKind.Object) return fallback;
        if (!p.TryGetProperty(key, out var v)) return fallback;
        try
        {
            return v.ValueKind == JsonValueKind.Number && typeof(T) == typeof(long) ? (T)(object)v.GetInt64()
                 : v.ValueKind == JsonValueKind.Number && typeof(T) == typeof(int) ? (T)(object)v.GetInt32()
                 : v.ValueKind == JsonValueKind.String ? (T)(object)v.GetString()!
                 : v.ValueKind == JsonValueKind.True ? (T)(object)true
                 : v.ValueKind == JsonValueKind.False ? (T)(object)false
                 : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}

public sealed class PeerLinkClient : IAsyncDisposable
{
    private readonly string _uid;
    private readonly string _apiKey;
    private readonly string _deviceName;

    /// <summary>跨账号连接（只允许 p2p.has / p2p.fetch，无签名认证）</summary>
    public bool CrossAccount { get; private set; }
    private TcpClient? _client;
    private NetworkStream? _stream;

    private PeerLinkClient(string uid, string apiKey, string deviceName)
    {
        _uid = uid;
        _apiKey = apiKey;
        _deviceName = deviceName;
    }

    /// <summary>连接并对端握手；任何一步失败抛 IOException（调用方提示"连不上 / 校验失败"）</summary>
    public static async Task<PeerLinkClient> ConnectAsync(
        string uid, string apiKey, string deviceName, string address, int port,
        CancellationToken ct = default)
    {
        var client = new PeerLinkClient(uid, apiKey, deviceName);
        try
        {
            client._client = new TcpClient();
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            conn.CancelAfter(TimeSpan.FromSeconds(8));
            await client._client.ConnectAsync(address, port, conn.Token);
            client._stream = client._client.GetStream();

            var c1 = Guid.NewGuid().ToString("N");
            await PeerLinkIO.WriteLineAsync(client._stream,
                new { cmd = "hello", uid, name = deviceName, challenge = c1 }, ct);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(20));

            var hello = await PeerLinkIO.ReadRequestAsync(client._stream, idle.Token);
            if (hello is null) throw new IOException("对端拒绝握手");
            if (hello.Bool("cross"))
            {
                // 跨账号 P2P：对端只接受「按 SHA1 取游戏文件」，不做签名（双方拿不到彼此 apiKey）
                client.CrossAccount = true;
                return client;
            }
            var c2 = hello.Str("challenge");
            if (c2.Length == 0) throw new IOException("对端拒绝握手");
            await PeerLinkIO.WriteLineAsync(client._stream,
                new { sig = LinkAuth.Sign(apiKey, c1 + c2) }, ct);
            var done = await PeerLinkIO.ReadRequestAsync(client._stream, idle.Token);
            if (done?.Str("sig") != LinkAuth.Sign(apiKey, c2 + c1))
                throw new IOException("对端身份校验失败（两台设备不是同一 Starcloud ID？）");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>发一条命令并收响应；bodyFile 为随命令上传的文件块（命令 JSON 需自带 has_body=true
    /// 标记，对端才知道要读块）；destFile 为要接收的文件块落点（expectSha256 非空时校验，失败删文件并抛异常）。</summary>
    public async Task<PeerResponse> CommandAsync(
        object command, string? destFile = null, string? expectSha256 = null,
        string? bodyFile = null, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("会话未连接");
        using var perCmd = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCmd.CancelAfter(TimeSpan.FromMinutes(30)); // 大 zip 的安全上限
        await PeerLinkIO.WriteLineAsync(_stream, command, perCmd.Token);
        if (bodyFile is not null)
            await PeerLinkIO.WriteFileBlockAsync(_stream, bodyFile, perCmd.Token);

        var resp = await PeerLinkIO.ReadRequestAsync(_stream, perCmd.Token)
                   ?? throw new IOException("对端断开");
        var ok = resp.Args.TryGetValue("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        JsonElement? payload = resp.Args.TryGetValue("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
        var err = resp.Str("err");
        string? received = null;

        if (destFile is not null)
        {
            if (!ok) throw new IOException(err is null ? "对端拒绝" : err);
            var size = payload is { } pp && pp.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            await PeerLinkIO.ReadBlockToFileAsync(_stream, size, destFile, perCmd.Token);
            if (expectSha256 is { } want)
            {
                var actual = await Task.Run(() => HashHex(destFile, want), perCmd.Token);
                if (!actual.Equals(want, StringComparison.OrdinalIgnoreCase))
                {
                    // 保留实得摘要前缀与长度：现场排查"传坏了"还是"拿错文件"就靠这两条
                    throw new IOException($"文件校验失败（期望 {want[..Math.Min(12, want.Length)]}…，实得 {actual[..12]}…，{new FileInfo(destFile).Length} 字节）");
                }
            }
            received = destFile;
        }
        return new PeerResponse(ok, payload, ok ? null : (err ?? "对端返回失败"), received);
    }

    /// <summary>按期望摘要的长度选算法：64 hex = SHA-256，40 hex = SHA-1（下载管线复用 SHA1 口径）</summary>
    private static string HashHex(string file, string want)
    {
        using var fs = File.OpenRead(file);
        var hex = want.Length switch
        {
            64 => Convert.ToHexString(SHA256.HashData(fs)),
            40 => Convert.ToHexString(SHA1.HashData(fs)),
            _ => throw new IOException("未知摘要长度"),
        };
        return hex.ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        await Task.CompletedTask;
    }
}
