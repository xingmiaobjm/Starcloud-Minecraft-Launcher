// TCP 链路服务端：监听 :47778，挑战-应答握手（HMAC-SHA256(apiKey)，设计 §4）后进入命令循环。
// 命令处理委托给 IPeerLinkHandler（由 DeviceLinkService 注入），本类只管连接与协议。

using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>命令处理器（业务语义由 DeviceLinkService 提供）</summary>
public interface IPeerLinkHandler
{
    Task<PeerReply> HandleAsync(PeerRequest request, CancellationToken ct);
}

public sealed class PeerLinkServer : IDisposable
{
    /// <summary>本机 uid（sha256(userId) 前 16 hex）：uid 不符的连接直接拒绝</summary>
    private readonly string _uid;
    private readonly Func<string> _apiKey;
    private readonly Func<string> _deviceName;
    private readonly IPeerLinkHandler _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>实际监听端口（Start 后有效；0 = 绑定失败未运行）</summary>
    public int Port { get; private set; }

    private readonly Func<bool> _allowCrossAccountP2p;

    public PeerLinkServer(string uid, Func<string> apiKey, Func<string> deviceName,
        IPeerLinkHandler handler, Func<bool>? allowCrossAccountP2p = null)
    {
        _uid = uid;
        _apiKey = apiKey;
        _deviceName = deviceName;
        _handler = handler;
        _allowCrossAccountP2p = allowCrossAccountP2p ?? (() => true);
    }

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _listener = new TcpListener(IPAddress.Any, BeaconPayload.LinkPort);
        try
        {
            _listener.Start();
        }
        catch
        {
            try
            {
                // 固定端口被占（另一实例已在跑）：退让一个临时端口，beacon 会把实际端口广播出去
                _listener = new TcpListener(IPAddress.Any, 0);
                _listener.Start();
            }
            catch
            {
                _listener = null;
                throw;
            }
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* 静默 */ }
        try { _acceptLoop?.Wait(TimeSpan.FromMilliseconds(800)); } catch { /* 静默 */ }
        _acceptLoop = null;
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Port = 0;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                continue; // 单个 accept 失败不影响监听
            }
            _ = ServeClientAsync(client, ct);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);

            // ① hello
            var hello = await PeerLinkIO.ReadRequestAsync(stream, linked.Token);
            if (hello is null || hello.Cmd != "hello") return;
            var sameAccount = hello.Str("uid") == _uid;
            var crossAccount = false;
            if (!sameAccount)
            {
                // 跨账号：只开「按 SHA1 取游戏文件」的 P2P 通道（内容由请求方按哈希校验）。
                // 不接受存档同步等其它命令，也无需签名——对端拿不到本机 apiKey。
                if (!_allowCrossAccountP2p()) return;
                crossAccount = true;
                await PeerLinkIO.WriteLineAsync(stream, new { cross = true }, linked.Token);
            }
            else
            {
                var c1 = hello.Str("challenge");
                if (c1.Length is < 8 or > 128) return;

                // ② 下发己方挑战
                var c2 = Guid.NewGuid().ToString("N");
                await PeerLinkIO.WriteLineAsync(stream, new { challenge = c2 }, linked.Token);

                // ③ 验证对方签名
                var their = await PeerLinkIO.ReadRequestAsync(stream, linked.Token);
                if (their is null || their.Str("sig") != LinkAuth.Sign(_apiKey(), c1 + c2)) return;
                // ④ 回己方签名
                await PeerLinkIO.WriteLineAsync(stream, new { sig = LinkAuth.Sign(_apiKey(), c2 + c1) }, linked.Token);
            }

            // 命令循环（对端每条命令一问一答；对端断开即结束）
            while (!ct.IsCancellationRequested)
            {
                var req = await PeerLinkIO.ReadRequestAsync(stream, ct);
                if (req is null) break;
                if (crossAccount && req.Cmd is not ("p2p.has" or "p2p.fetch"))
                {
                    await PeerLinkIO.WriteLineAsync(stream, PeerReply.Fail("跨账号连接只支持 P2P 文件共享"), ct);
                    continue;
                }
                string? bodyFile = null;
                try
                {
                    // 命令声明带文件体（saves.put 的 zip）：读下来落到临时文件再交给 handler
                    if (req.HasBody)
                    {
                        bodyFile = Path.Combine(Path.GetTempPath(), "starcloud-sync",
                            $"body-{Guid.NewGuid():N}.bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(bodyFile)!);
                        await PeerLinkIO.ReadBlockToFileAsync(stream, 0, bodyFile, ct);
                        req.BodyFile = bodyFile;
                    }
                    PeerReply reply;
                    try
                    {
                        reply = await _handler.HandleAsync(req, ct);
                    }
                    catch (Exception ex)
                    {
                        reply = PeerReply.Fail(ex.Message);
                    }
                    await PeerLinkIO.WriteLineAsync(stream,
                        new { ok = reply.Ok, err = reply.Error, payload = reply.Payload }, ct);
                    if (reply.SourceFile is not null)
                    {
                        await PeerLinkIO.WriteFileBlockAsync(stream, reply.SourceFile, ct);
                        if (reply.DeleteAfterSend)
                        {
                            try { File.Delete(reply.SourceFile); } catch { /* 静默：临时文件留给系统清理 */ }
                        }
                    }
                }
                finally
                {
                    if (bodyFile is not null)
                    {
                        try { File.Delete(bodyFile); } catch { /* 静默 */ }
                    }
                }
            }
        }
        catch
        {
            // 单个客户端会话的任何异常都只断开该连接
        }
    }

    public void Dispose() => Stop();
}
