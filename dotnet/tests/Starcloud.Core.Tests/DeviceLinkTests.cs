// 设备链路测试：beacon 帧编解码、鉴权签名、冲突裁决（纯函数）+ 真实 TCP 回环集成（握手 / 命令 / 文件双向）。
//
// 集成用例起真实 PeerLinkServer（随机端口由 Server 自行退让）+ PeerLinkClient 打命令，
// 验证：正确密钥握手成功、错误密钥被拒、p2p.has/fetch 命中与未命中、saves.put 上传校验落地。

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class DeviceLinkTests
{
    // ── beacon 帧编解码 ──

    [Fact]
    public void beacon帧编码后可以原样解码()
    {
        var p = new BeaconPayload(1, "abc123", "My-PC", 47778, "starcloud");
        var decoded = BeaconPayload.Decode(p.Encode());

        Assert.NotNull(decoded);
        Assert.Equal(p.Uid, decoded!.Uid);
        Assert.Equal(p.Name, decoded.Name);
        Assert.Equal(p.Port, decoded.Port);
    }

    [Fact]
    public void beacon帧版本不符或应用不符时拒绝()
    {
        var wrongVersion = new BeaconPayload(99, "u", "n", 1, "starcloud").Encode();
        var wrongApp = new BeaconPayload(1, "u", "n", 1, "other-app").Encode();
        var garbage = "not json"u8.ToArray();

        Assert.Null(BeaconPayload.Decode(wrongVersion));
        Assert.Null(BeaconPayload.Decode(wrongApp));
        Assert.Null(BeaconPayload.Decode(garbage));
    }

    // ── 鉴权原语 ──

    [Fact]
    public void uid哈希是16位hex且同输入同输出()
    {
        var a = LinkAuth.UidHash("user-1");
        var b = LinkAuth.UidHash("user-1");
        var c = LinkAuth.UidHash("user-2");

        Assert.Equal(16, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void hmac签名与SHA256手工值一致()
    {
        var expected = Convert.ToHexString(
            new System.Security.Cryptography.HMACSHA256("key"u8.ToArray())
                .ComputeHash("msg"u8.ToArray())).ToLowerInvariant();
        Assert.Equal(expected, LinkAuth.Sign("key", "msg"));
    }

    // ── 冲突裁决（新者胜、旧者保留、两边都不丢） ──

    [Fact]
    public void 冲突裁决_本地没有世界时直接接收()
    {
        Assert.Equal(SyncDecision.Accept, SyncConflict.Resolve(null, DateTime.Now));
        Assert.Equal(SyncDecision.Accept, SyncConflict.Resolve(null, null));
    }

    [Fact]
    public void 冲突裁决_来的更新则保留本地旧世界()
    {
        var local = DateTime.Now;
        Assert.Equal(SyncDecision.AcceptWithBackup, SyncConflict.Resolve(local, local.AddMinutes(5)));
        // 拿不准（incoming 无时间戳）：保留两边，不覆盖
        Assert.Equal(SyncDecision.AcceptWithBackup, SyncConflict.Resolve(local, null));
    }

    [Fact]
    public void 冲突裁决_本地更新则拒收()
    {
        var local = DateTime.Now;
        Assert.Equal(SyncDecision.Reject, SyncConflict.Resolve(local, local.AddMinutes(-5)));
        Assert.Equal(SyncDecision.Reject, SyncConflict.Resolve(local, local));
    }

    // ── 真实 TCP 回环集成 ──

    private static string Sha1Hex(string content) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed class FakeHandler : IPeerLinkHandler
    {
        public Func<PeerRequest, PeerReply>? OnRequest;

        public Task<PeerReply> HandleAsync(PeerRequest request, CancellationToken ct) =>
            Task.FromResult(OnRequest!(request));
    }

    [Fact]
    public async Task 回环_正确密钥握手并完成p2p命中与未命中()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sc-devlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var content = "peer-file-content-123";
            var peerFile = Path.Combine(temp, "client.jar");
            await File.WriteAllTextAsync(peerFile, content);
            var sha1 = Sha1Hex(content);

            var handler = new FakeHandler
            {
                OnRequest = req => req.Cmd switch
                {
                    "p2p.has" when req.Str("sha1") == sha1 =>
                        new PeerReply(true, new { size = new FileInfo(peerFile).Length }),
                    "p2p.fetch" when req.Str("sha1") == sha1 =>
                        new PeerReply(true, new { size = new FileInfo(peerFile).Length }, SourceFile: peerFile),
                    _ => PeerReply.Fail("本机没有该文件"),
                },
            };
            using var server = new PeerLinkServer(LinkAuth.UidHash("user-1"), () => "key-1", () => "PC-A", handler);
            server.Start();
            Assert.True(server.Port > 0);

            await using var client = await PeerLinkClient.ConnectAsync(
                LinkAuth.UidHash("user-1"), "key-1", "PC-B", "127.0.0.1", server.Port);

            var dest = Path.Combine(temp, "fetched-client.jar");
            var has = await client.CommandAsync(new { cmd = "p2p.has", sha1, size = (long)content.Length, destName = "client.jar" });
            Assert.True(has.Ok);

            var fetch = await client.CommandAsync(
                new { cmd = "p2p.fetch", sha1, size = (long)content.Length, destName = "client.jar" },
                destFile: dest, expectSha256: sha1);
            Assert.True(File.Exists(dest), "落地文件不存在");
            var got = await File.ReadAllTextAsync(dest);
            Assert.True(got == content, $"内容不符：期望长度 {content.Length}，实际长度 {got.Length}，前 32 字节 {string.Join(' ', got.Take(32).Select(c => (int)c))}");
            Assert.True(fetch.Ok);
            Assert.Equal(content, await File.ReadAllTextAsync(dest));

            // 未命中：对端拒绝，客户端收到 Ok=false
            var miss = await client.CommandAsync(new { cmd = "p2p.has", sha1 = "deadbeef", size = 1L, destName = "x" });
            Assert.False(miss.Ok);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 静默 */ }
        }
    }

    [Fact]
    public async Task 回环_错误密钥无法完成握手()
    {
        var handler = new FakeHandler { OnRequest = _ => PeerReply.Fail("never") };
        using var server = new PeerLinkServer(LinkAuth.UidHash("user-1"), () => "real-key", () => "PC-A", handler);
        server.Start();

        await Assert.ThrowsAsync<IOException>(() =>
            PeerLinkClient.ConnectAsync(LinkAuth.UidHash("user-1"), "wrong-key", "PC-B", "127.0.0.1", server.Port));
    }

    [Fact]
    public async Task 回环_关闭跨账号时不同账号连接被拒绝()
    {
        var handler = new FakeHandler { OnRequest = _ => PeerReply.Fail("never") };
        using var server = new PeerLinkServer(LinkAuth.UidHash("user-1"), () => "key-1", () => "PC-A", handler,
            allowCrossAccountP2p: () => false);
        server.Start();

        // 开关关闭：uid 不同 → hello 后直接断开（读不到 challenge）
        await Assert.ThrowsAnyAsync<Exception>(() =>
            PeerLinkClient.ConnectAsync(LinkAuth.UidHash("user-2"), "key-1", "PC-B", "127.0.0.1", server.Port));
    }

    [Fact]
    public async Task 回环_跨账号只允许P2P命令()
    {
        var seen = new List<string>();
        var handler = new FakeHandler
        {
            OnRequest = req =>
            {
                seen.Add(req.Cmd);
                return req.Cmd.StartsWith("p2p.", StringComparison.Ordinal)
                    ? new PeerReply(true, new { ok = true })
                    : new PeerReply(true, new { leaked = true });
            },
        };
        using var server = new PeerLinkServer(LinkAuth.UidHash("user-1"), () => "key-1", () => "PC-A", handler,
            allowCrossAccountP2p: () => true);
        server.Start();

        // 不同账号：握手成功（无签名），但服务端只放行 p2p.*
        await using var client = await PeerLinkClient.ConnectAsync(
            LinkAuth.UidHash("user-2"), "any-key", "PC-B", "127.0.0.1", server.Port);
        Assert.True(client.CrossAccount);

        var p2p = await client.CommandAsync(new { cmd = "p2p.has", sha1 = "abc", size = 1L, destName = "a.jar" });
        Assert.True(p2p.Ok);

        // 存档同步等命令必须被挡掉（handler 根本收不到）
        var other = await client.CommandAsync(new { cmd = "saves.list" });
        Assert.False(other.Ok);
        Assert.DoesNotContain("saves.list", seen);
    }

    [Fact]
    public async Task 回环_saves上传带文件体并完成校验()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sc-devlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string? receivedBody = null;
        try
        {
            var handler = new FakeHandler
            {
                OnRequest = req =>
                {
                    if (req.Cmd == "saves.put" && req.BodyFile is not null)
                    {
                        receivedBody = File.ReadAllText(req.BodyFile);
                        return new PeerReply(true, new { folder = req.Str("folder") });
                    }
                    return PeerReply.Fail("unsupported");
                },
            };
            using var server = new PeerLinkServer(LinkAuth.UidHash("user-1"), () => "key-1", () => "PC-A", handler);
            server.Start();

            var zip = Path.Combine(temp, "world.zip");
            await File.WriteAllTextAsync(zip, "fake-zip-bytes");

            await using var client = await PeerLinkClient.ConnectAsync(
                LinkAuth.UidHash("user-1"), "key-1", "PC-B", "127.0.0.1", server.Port);
            var resp = await client.CommandAsync(new
            {
                cmd = "saves.put",
                folder = "New World",
                has_body = true,
            }, bodyFile: zip);
            Assert.True(resp.Ok);
            Assert.Equal("fake-zip-bytes", receivedBody);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 静默 */ }
        }
    }
}
