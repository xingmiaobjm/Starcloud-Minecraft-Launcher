// 跨互联网 P2P 客户端测试：帧编解码、会话密钥与握手、STUN 应答解析、本地索引，
// 以及一条回环 UDP 集成用例（打洞 → 握手 → 分块传文件 → SHA1 一致）。

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using Starcloud.Core.Services;
using Starcloud.Core.Services.P2P;
using Xunit;

namespace Starcloud.Core.Tests;

public class P2PClientTests
{
    // ── 帧 ──

    [Fact]
    public void 帧编码解码保留全部字段与载荷()
    {
        var payload = RandomNumberGenerator.GetBytes(1100);
        var encoded = new P2PFrame(P2PFrameType.Data, 0x11223344, 7, 3, 0b101, payload).Encode();

        Assert.True(P2PFrame.TryParse(encoded, out var frame));
        Assert.Equal(P2PFrameType.Data, frame.Type);
        Assert.Equal(0x11223344u, frame.SessionTag);
        Assert.Equal(7u, frame.Seq);
        Assert.Equal(3u, frame.AckSeq);
        Assert.Equal(0b101u, frame.AckBits);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void 帧头非法或超长时拒绝()
    {
        var good = new P2PFrame(P2PFrameType.Ack, 1, 2).Encode();
        var badMagic = (byte[])good.Clone();
        badMagic[0] = 0;
        var badVersion = (byte[])good.Clone();
        badVersion[2] = 99;
        var tooLong = new byte[P2PFrame.HeaderSize + P2PFrame.MaxPayload + 1];
        new P2PFrame(P2PFrameType.Ack, 1).Encode().CopyTo(tooLong, 0);

        Assert.False(P2PFrame.TryParse(badMagic, out _));
        Assert.False(P2PFrame.TryParse(badVersion, out _));
        Assert.False(P2PFrame.TryParse(tooLong, out _));
        Assert.False(P2PFrame.TryParse(good.AsSpan(0, 10), out _));
    }

    // ── 会话密钥与握手 ──

    [Fact]
    public void 相同令牌派生出相同密钥与标签()
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var a = new P2PSessionKey(token);
        var b = new P2PSessionKey(token);

        Assert.Equal(a.Tag, b.Tag);
        var mac = a.Mac("hello"u8);
        Assert.True(b.Verify("hello"u8, mac));
        Assert.False(b.Verify("hell0"u8, mac));
        Assert.False(b.Verify("hello"u8, mac[..15]));
    }

    [Fact]
    public void 不同令牌无法互相验证()
    {
        var a = new P2PSessionKey(RandomNumberGenerator.GetBytes(32));
        var b = new P2PSessionKey(RandomNumberGenerator.GetBytes(32));
        var mac = a.Mac("data"u8);
        Assert.False(b.Verify("data"u8, mac));
    }

    [Fact]
    public void 握手载荷可以往返且篡改会被拒()
    {
        var key = new P2PSessionKey(RandomNumberGenerator.GetBytes(32));
        var sha1 = Convert.ToHexString(SHA1.HashData("file"u8)).ToLowerInvariant();
        var hello = P2PHandshake.BuildHello(sha1, 12345, 0, 42, key);

        Assert.True(P2PHandshake.TryParseHello(hello, key, out var parsedSha1, out var size, out var offset, out var nonce));
        Assert.Equal(sha1, parsedSha1);
        Assert.Equal(12345, size);
        Assert.Equal(0, offset);
        Assert.Equal(42ul, nonce);

        hello[25] ^= 0xFF; // 改大小字段 → MAC 不再匹配
        Assert.False(P2PHandshake.TryParseHello(hello, key, out _, out _, out _, out _));

        var ack = P2PHandshake.BuildHelloAck(nonce, key);
        Assert.True(P2PHandshake.VerifyHelloAck(ack, nonce, key));
        Assert.False(P2PHandshake.VerifyHelloAck(ack, nonce + 1, key));
    }

    // ── STUN ──

    [Fact]
    public void STUN应答解析XOR映射地址()
    {
        var txId = RandomNumberGenerator.GetBytes(12);
        var response = BuildStunResponse(txId, IPAddress.Parse("203.0.113.7"), 51234, xor: true);

        Assert.True(StunClient.TryParseBindingResponse(response, txId, out var mapped));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), mapped!.Address);
        Assert.Equal(51234, mapped.Port);
    }

    [Fact]
    public void STUN应答事务ID不符时拒绝()
    {
        var txId = RandomNumberGenerator.GetBytes(12);
        var response = BuildStunResponse(txId, IPAddress.Loopback, 1000, xor: true);
        var other = RandomNumberGenerator.GetBytes(12);

        Assert.False(StunClient.TryParseBindingResponse(response, other, out _));
    }

    [Fact]
    public void STUN应答解析IPv6映射地址()
    {
        var txId = RandomNumberGenerator.GetBytes(12);
        var address = IPAddress.Parse("2001:db8::1");
        var response = BuildStunResponse(txId, address, 40122, xor: true);

        Assert.True(StunClient.TryParseBindingResponse(response, txId, out var mapped));
        Assert.Equal(address, mapped!.Address);
        Assert.Equal(40122, mapped.Port);
    }

    [Fact]
    public void STUN请求是20字节绑定请求()
    {
        var txId = RandomNumberGenerator.GetBytes(12);
        var request = StunClient.BuildBindingRequest(txId);
        Assert.Equal(20, request.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request));
        Assert.Equal(0x2112A442u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4)));
        Assert.Equal(txId, request[8..]);
    }

    // ── 本地索引 ──

    [Fact]
    public void 索引只登记大文件且能按sha1找回()
    {
        var dir = TempDir();
        try
        {
            var big = Path.Combine(dir, "big.bin");
            WriteRandom(big, 3 * 1024 * 1024);
            var small = Path.Combine(dir, "small.bin");
            WriteRandom(small, 1024 * 1024);
            var bigSha1 = Sha1Of(big);
            var ledger = new P2PFileLedger(Path.Combine(dir, "index.json"));

            ledger.Record(big, bigSha1, new FileInfo(big).Length);
            ledger.Record(small, Sha1Of(small), new FileInfo(small).Length);
            ledger.Record(big, "", new FileInfo(big).Length);          // 无哈希：忽略
            ledger.Record(Path.Combine(dir, "nope.bin"), bigSha1, 4000000); // 不存在：忽略

            Assert.Equal(1, ledger.Count);
            var snapshot = ledger.Snapshot();
            Assert.Single(snapshot);
            Assert.Equal(bigSha1, snapshot[0].Sha1);

            Assert.True(ledger.TryResolve(bigSha1.ToUpperInvariant(), new FileInfo(big).Length,
                out var path, out var size));
            Assert.Equal(Path.GetFullPath(big), path);
            Assert.Equal(new FileInfo(big).Length, size);
            Assert.False(ledger.TryResolve(bigSha1, 123, out _, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 索引落盘后可以重新加载()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "lib.jar");
            WriteRandom(file, 2 * 1024 * 1024 + 5);
            var sha1 = Sha1Of(file);
            var indexPath = Path.Combine(dir, "index.json");

            var first = new P2PFileLedger(indexPath);
            first.Record(file, sha1, new FileInfo(file).Length);
            first.Flush();

            var second = new P2PFileLedger(indexPath);
            Assert.Equal(1, second.Count);
            Assert.True(second.TryResolve(sha1, null, out var path, out _));
            Assert.Equal(Path.GetFullPath(file), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 供文件前的哈希核对能识别被替换的文件()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "a.bin");
            WriteRandom(file, 2 * 1024 * 1024);
            var expected = Sha1Of(file);
            var ledger = new P2PFileLedger(Path.Combine(dir, "index.json"));

            Assert.True(await ledger.VerifyAsync(file, expected));
            Assert.False(await ledger.VerifyAsync(file, new string('0', 40)));

            WriteRandom(file, 2 * 1024 * 1024); // 换掉内容
            Assert.False(await ledger.VerifyAsync(file, expected));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── 回环集成：打洞 → 握手 → 分块传输 ──

    [Fact(Timeout = 30000)]
    public async Task 回环UDP可以打洞并完整传一个文件()
    {
        var dir = TempDir();
        try
        {
            var source = Path.Combine(dir, "source.bin");
            const int size = 300 * 1024 + 777;
            WriteRandom(source, size);
            var sha1 = Sha1Of(source);

            var token = RandomNumberGenerator.GetBytes(32);
            var keyA = new P2PSessionKey(token);
            var keyB = new P2PSessionKey(token);
            Assert.Equal(keyA.Tag, keyB.Tag);

            using var hostA = new UdpSocketHost();
            using var hostB = new UdpSocketHost();
            hostA.Start();
            hostB.Start();
            var endpointB = new IPEndPoint(IPAddress.Loopback, hostB.Port);
            var endpointA = new IPEndPoint(IPAddress.Loopback, hostA.Port);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var punchA = P2PHolePunch.RunAsync([hostA], [endpointB], keyA.Tag,
                TimeSpan.FromSeconds(3), cts.Token);
            var punchB = P2PHolePunch.RunAsync([hostB], [endpointA], keyB.Tag,
                TimeSpan.FromSeconds(3), cts.Token);
            var punchedA = await punchA;
            var punchedB = await punchB;
            Assert.NotNull(punchedA);
            Assert.NotNull(punchedB);

            using var sessionA = new P2PSession([hostA], punchedA!.Value.Host, punchedA.Value.Remote, keyA);
            using var sessionB = new P2PSession([hostB], punchedB!.Value.Host, punchedB.Value.Remote, keyB);

            const ulong nonce = 0x1234567890ABCDEF;
            var target = Path.Combine(dir, "target.bin");
            var provider = Task.Run(async () =>
            {
                P2PFrame? hello = null;
                var deadline = Environment.TickCount64 + 5000;
                while (Environment.TickCount64 < deadline && hello is null)
                    hello = await sessionB.ReadAsync(TimeSpan.FromMilliseconds(200), cts.Token);
                Assert.NotNull(hello);
                Assert.Equal(P2PFrameType.Hello, hello!.Value.Type);
                Assert.True(P2PHandshake.TryParseHello(hello.Value.Payload.Span, keyB,
                    out var sentSha1, out var sentSize, out _, out var sentNonce));
                Assert.Equal(sha1, sentSha1);
                Assert.Equal(size, sentSize);

                await sessionB.SendAsync(P2PFrameType.HelloAck,
                    payload: P2PHandshake.BuildHelloAck(sentNonce, keyB));

                using var handle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                await sessionB.SendFileAsync(handle, size, 0, null, null, cts.Token);
            });

            await sessionA.SendAsync(P2PFrameType.Hello,
                payload: P2PHandshake.BuildHello(sha1, size, 0, nonce, keyA));
            P2PFrame? ack = null;
            var ackDeadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < ackDeadline)
            {
                ack = await sessionA.ReadAsync(TimeSpan.FromMilliseconds(200), cts.Token);
                if (ack is { Type: P2PFrameType.HelloAck }) break;
            }
            Assert.NotNull(ack);
            Assert.True(P2PHandshake.VerifyHelloAck(ack!.Value.Payload.Span, nonce, keyA));

            using (var handle = File.OpenHandle(target, FileMode.Create, FileAccess.Write, FileShare.None,
                       FileOptions.Asynchronous, size))
            {
                await sessionA.ReceiveFileAsync(handle, size, 0, null, cts.Token);
            }
            await provider;

            Assert.Equal(sha1, Sha1Of(target));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── 发现服务客户端（钉住后端契约） ──

    [Fact]
    public async Task 发现服务客户端按约定解析peers()
    {
        string? path = null;
        var handler = new StubHandler(request =>
        {
            path = request.RequestUri!.PathAndQuery;
            return Json("""
                {"sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","peers":[
                  {"id":"9a1c4e7f","candidates":[{"addr":"203.0.113.7","port":51234,"family":4}],
                   "nat":"cone","age":12}],"total":1}
                """);
        });
        var client = new RendezvousClient(() => "api-key", "https://example.test", handler);

        var peers = await client.PeersAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1024);

        Assert.NotNull(path);
        Assert.Contains("sha1=", path);
        Assert.Contains("size=1024", path);
        Assert.Single(peers);
        Assert.Equal("9a1c4e7f", peers[0].Id);
        Assert.Equal("cone", peers[0].Nat);
        Assert.Equal(51234, peers[0].Candidates![0].Port);
    }

    [Fact]
    public async Task 发现服务客户端按约定解析会话令牌()
    {
        var handler = new StubHandler(_ => Json("""
            {"session":"s-1","token":"c2VjcmV0LXRva2VuLTE2LWJ5dGVzIQ==",
             "expires_in":120,"peer":{"candidates":[{"addr":"198.51.100.9","port":40122,"family":4}]}}
            """));
        var client = new RendezvousClient(() => "api-key", "https://example.test", handler);

        var grant = await client.CreateSessionAsync(new string('b', 40), "9a1c4e7f");

        Assert.NotNull(grant);
        Assert.Equal("s-1", grant!.SessionId);
        Assert.Equal(120, grant.ExpiresIn);
        Assert.Equal("198.51.100.9", grant.Peer!.Candidates![0].Addr);
        Assert.True(Convert.FromBase64String(grant.Token).Length >= 16);
    }

    [Fact]
    public async Task announce请求体包含候选端点与分批索引()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"ok":true}""");
        });
        var client = new RendezvousClient(() => "api-key", "https://example.test", handler);

        await client.AnnounceAsync(
            [new P2PCandidate("203.0.113.7", 51234, 4)],
            "cone",
            [new P2PIndexEntry(new string('c', 40), 4000000)],
            snapshot: 3, batch: 1, batches: 2);

        Assert.NotNull(body);
        Assert.Contains("\"nat\":\"cone\"", body);
        Assert.Contains("\"port\":51234", body);
        Assert.Contains("\"snapshot\":3", body);
        Assert.Contains("\"batch\":1", body);
        Assert.Contains("\"batches\":2", body);
        Assert.Contains(new string('c', 40), body);
    }

    [Fact]
    public async Task 未登录时不发请求直接报错()
    {
        var client = new RendezvousClient(() => null, "https://example.test", new StubHandler(_ => Json("{}")));
        var error = await Assert.ThrowsAsync<FriendApiException>(() =>
            client.PeersAsync(new string('d', 40), 1));
        Assert.Equal("unauthorized", error.Code);
    }

    [Fact]
    public async Task 发现服务错误体映射成带code的异常()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("""{"error":{"code":"rate_limited","message":"请求太快"}}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var client = new RendezvousClient(() => "api-key", "https://example.test", handler);

        var error = await Assert.ThrowsAsync<FriendApiException>(() =>
            client.CreateSessionAsync(new string('e', 40), "peer"));

        Assert.Equal("rate_limited", error.Code);
        Assert.Contains("请求太快", error.Message);
    }

    // ── 后端字段兼容（token 编码 / SSE 事件形态） ──

    [Fact]
    public void 会话令牌接受标准base64与URLsafe与十六进制()
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var standard = Convert.ToBase64String(token);
        var urlSafe = standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var hex = Convert.ToHexString(token);

        Assert.Equal(token, InternetP2PService.ParseToken(standard));
        Assert.Equal(token, InternetP2PService.ParseToken(urlSafe));
        Assert.Equal(token, InternetP2PService.ParseToken(hex));
        Assert.Null(InternetP2PService.ParseToken(""));
        Assert.Null(InternetP2PService.ParseToken("not-a-token"));
    }

    [Fact]
    public void p2p请求解析兼容嵌套与平铺候选()
    {
        var sha1 = new string('a', 40);
        var nestedJson = "{\"session\":\"s-1\",\"token\":\"dG9rZW4tMTYtYnl0ZXMhISE=\",\"sha1\":\"" + sha1 +
                         "\",\"from\":{\"candidates\":[{\"addr\":\"203.0.113.7\",\"port\":51234,\"family\":4}]}}";
        using var nested = JsonDocument.Parse(nestedJson);
        var request = InternetP2PService.ParseRequest(nested.RootElement);
        Assert.NotNull(request);
        Assert.Equal("s-1", request!.Session);
        Assert.Equal(sha1, request.Sha1);
        Assert.Equal(51234, request.From!.Candidates![0].Port);

        var flatJson = "{\"session_id\":\"s-2\",\"token\":\"dG9rZW4tMTYtYnl0ZXMhISE=\",\"sha1\":\"" + sha1 +
                       "\",\"candidates\":[\"198.51.100.9:40122\"]}";
        using var flat = JsonDocument.Parse(flatJson);
        var flatRequest = InternetP2PService.ParseRequest(flat.RootElement);
        Assert.NotNull(flatRequest);
        Assert.Equal("s-2", flatRequest!.Session);
        Assert.Equal("198.51.100.9", flatRequest.From!.Candidates![0].Addr);
        Assert.Equal(40122, flatRequest.From.Candidates[0].Port);
    }

    [Fact]
    public void p2p请求缺少必要字段时忽略()
    {
        var sha1 = new string('b', 40);
        using var missingToken = JsonDocument.Parse(
            "{\"session\":\"s\",\"sha1\":\"" + sha1 +
            "\",\"from\":{\"candidates\":[{\"addr\":\"1.2.3.4\",\"port\":1}]}}");
        using var noCandidates = JsonDocument.Parse(
            "{\"session\":\"s\",\"token\":\"dG9rZW4tMTYtYnl0ZXMhISE=\",\"sha1\":\"" + sha1 + "\"}");
        Assert.Null(InternetP2PService.ParseRequest(missingToken.RootElement));
        Assert.Null(InternetP2PService.ParseRequest(noCandidates.RootElement));
    }

    [Fact]
    public void 回填能按版本清单登记已装的大文件()
    {
        var dir = TempDir();
        try
        {
            const int size = 3 * 1024 * 1024;
            var mc = Path.Combine(dir, ".minecraft");
            var versionDir = Path.Combine(mc, "versions", "1.20.1");
            Directory.CreateDirectory(versionDir);

            var jar = Path.Combine(versionDir, "1.20.1.jar");
            WriteRandom(jar, size);
            var lib = Path.Combine(mc, "libraries", "org", "example", "demo", "1.0", "demo-1.0.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
            WriteRandom(lib, size);
            var libSha1 = Sha1Of(lib);
            Directory.CreateDirectory(Path.Combine(mc, "assets", "objects"));
            var asset = Path.Combine(mc, "assets", "objects", "tmp-asset.bin");
            WriteRandom(asset, size);
            var assetHash = Sha1Of(asset);
            var assetFinal = Path.Combine(mc, "assets", "objects", assetHash[..2], assetHash);
            Directory.CreateDirectory(Path.GetDirectoryName(assetFinal)!);
            File.Move(asset, assetFinal);
            Directory.CreateDirectory(Path.Combine(mc, "assets", "indexes"));
            File.WriteAllText(Path.Combine(mc, "assets", "indexes", "5.json"),
                "{\"objects\":{\"minecraft/sounds/x.ogg\":{\"hash\":\"" + assetHash +
                "\",\"size\":" + size + "}}}");

            var jarSha1 = Sha1Of(jar);
            File.WriteAllText(Path.Combine(versionDir, "1.20.1.json"),
                "{\"id\":\"1.20.1\",\"downloads\":{\"client\":{\"url\":\"https://x\",\"sha1\":\"" +
                jarSha1 + "\",\"size\":" + size + "}},\"assetIndex\":{\"id\":\"5\",\"url\":\"\"}," +
                "\"libraries\":[{\"name\":\"org.example:demo:1.0\",\"url\":\"https://libs.example\"," +
                "\"sha1\":\"" + libSha1 + "\",\"size\":" + size + "}]}");

            var ledger = new P2PFileLedger(Path.Combine(dir, "index.json"));
            var added = P2PIndexBackfill.Run(mc, ledger);

            Assert.Equal(3, added);
            Assert.True(ledger.TryResolve(jarSha1, size, out var path, out _));
            Assert.Equal(Path.GetFullPath(jar), path);

            // 大小不符的版本文件不登记（避免把改过的文件当成官方文件对外声称）
            var badDir = Path.Combine(mc, "versions", "1.20.2");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "1.20.2.json"),
                "{\"id\":\"1.20.2\",\"downloads\":{\"client\":{\"url\":\"https://x\",\"sha1\":\"" +
                new string('f', 40) + "\",\"size\":" + (size + 1) + "}}}");
            WriteRandom(Path.Combine(badDir, "1.20.2.jar"), size);
            Assert.Equal(0, P2PIndexBackfill.Run(mc, ledger));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── 工具 ──

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-p2p-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteRandom(string path, int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(path, data);
    }

    private static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>构造一条 Binding Success Response（带 XOR-MAPPED-ADDRESS）。</summary>
    private static byte[] BuildStunResponse(byte[] transactionId, IPAddress address, int port, bool xor)
    {
        var valueLength = address.AddressFamily == AddressFamily.InterNetwork ? 8 : 20;
        var attrLength = 4 + valueLength;
        var total = 20 + attrLength;
        var buffer = new byte[total];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)attrLength);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 0x2112A442);
        transactionId.CopyTo(span[8..]);

        BinaryPrimitives.WriteUInt16BigEndian(span[20..], 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], (ushort)valueLength);
        span[24] = 0;
        span[25] = (byte)(address.AddressFamily == AddressFamily.InterNetwork ? 0x01 : 0x02);
        var encodedPort = xor ? port ^ 0x2112 : port;
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], (ushort)encodedPort);

        var raw = address.GetAddressBytes();
        if (xor)
        {
            var mask = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask, 0x2112A442);
            transactionId.CopyTo(mask.AsSpan(4));
            for (var i = 0; i < raw.Length; i++) raw[i] ^= mask[i];
        }
        raw.CopyTo(span[28..]);
        return buffer;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
