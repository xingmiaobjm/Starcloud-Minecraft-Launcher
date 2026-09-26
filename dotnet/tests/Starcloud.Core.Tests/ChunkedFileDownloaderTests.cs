// 下载引擎测试：
//   ① PlanChunks（纯逻辑）：区间覆盖性、片数上限、片大小随 worker 数自适应；
//   ② 端到端吞吐（本地限速服务器）：**单连接被限到 1 MB/s 时，整文件必须靠并行分片拉到 ≥2.5 MB/s**
//      —— 这是「下载速度不低于 5 MB/s」这条产品要求的回归护栏：5MB/s 靠的是并行连接，
//      一旦哪天有人把分片或并发调回单连接，这条测试会先红。
//   ③ 不支持 Range 的源：自动退回整文件下载，且结果正确（含 SHA1 校验）。

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class ChunkedFileDownloaderTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>断言：首片从 0 开始、逐片紧邻、各片长度之和 == 文件大小、片数不超过上限</summary>
    private static void AssertCoverage(IReadOnlyList<(long Start, long Length)> plan, long size)
    {
        Assert.Equal(0L, plan[0].Start);

        var chunkSize = plan[0].Length;
        var cursor = 0L;
        foreach (var (start, length) in plan)
        {
            Assert.Equal(cursor, start);                                   // 与上一片紧邻，无重叠无空洞
            Assert.True(length > 0, "分片长度必须为正");
            Assert.True(length <= chunkSize, "单片不得超过首片大小");
            cursor += length;
        }

        Assert.Equal(size, cursor);                                        // 末片正好补齐文件末尾
        Assert.True(plan.Count <= ChunkedFileDownloader.MaxChunks);
    }

    // ── ① 分片计划 ─────────────────────────────────────────────

    [Fact]
    public void 小文件_单片()
    {
        var plan = ChunkedFileDownloader.PlanChunks(1024);

        var chunk = Assert.Single(plan);
        Assert.Equal((0L, 1024L), chunk);
    }

    [Fact]
    public void 中等文件_按每worker约两片切分()
    {
        // 12 MiB / (8 worker × 2 片) = 0.75 MiB 目标 → 落到 1 MiB 下限 → 12 片
        var plan = ChunkedFileDownloader.PlanChunks(12 * MiB);

        Assert.Equal(12, plan.Count);
        Assert.All(plan, c => Assert.Equal(1 * MiB, c.Length));
        AssertCoverage(plan, 12 * MiB);
    }

    [Fact]
    public void 两端文件_首片不小于下限_末片补齐()
    {
        var plan = ChunkedFileDownloader.PlanChunks(10 * MiB + 123);

        Assert.True(plan[0].Length >= ChunkedFileDownloader.MinChunkSize);
        Assert.Equal(123L, plan[^1].Length);
        AssertCoverage(plan, 10 * MiB + 123);
    }

    [Fact]
    public void 超大文件_片数被上限压住()
    {
        var size = 1024 * MiB;

        var plan = ChunkedFileDownloader.PlanChunks(size);

        Assert.True(plan.Count <= ChunkedFileDownloader.MaxChunks);
        Assert.True(plan.Count > ChunkedFileDownloader.MaxWorkersPerFile, "大文件片数要多于 worker，才能持续喂满并发");
        AssertCoverage(plan, size);
    }

    [Fact]
    public void 片大小为整MiB对齐()
    {
        var size = 100 * MiB + 1;

        var plan = ChunkedFileDownloader.PlanChunks(size);

        // 除末片外，片大小都是 1 MiB 的整数倍（Range 请求对偏移更友好）
        for (var i = 0; i < plan.Count - 1; i++)
            Assert.Equal(0, plan[i].Length % MiB);
        AssertCoverage(plan, size);
    }

    [Fact]
    public void worker数被夹在1到上限之间()
    {
        var many = ChunkedFileDownloader.PlanChunks(64 * MiB, 1000);
        var capped = ChunkedFileDownloader.PlanChunks(64 * MiB, ChunkedFileDownloader.MaxWorkersPerFile);
        Assert.Equal(capped, many);

        var single = ChunkedFileDownloader.PlanChunks(64 * MiB, 1);
        AssertCoverage(single, 64 * MiB);
        Assert.True(single.Count <= capped.Count, "worker 越少，片应越少（更粗的分片）");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非法大小_返回空计划(long size) =>
        Assert.Empty(ChunkedFileDownloader.PlanChunks(size));

    [Theory]
    [InlineData(1)]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(4 * 1024 * 1024 + 1)]
    [InlineData(64 * 1024 * 1024)]
    [InlineData(70 * 1024 * 1024 + 7)]
    [InlineData(1024 * 1024 * 1024 + 3)]
    public void 各种大小_均满足覆盖性与片上限制(long size)
    {
        var plan = ChunkedFileDownloader.PlanChunks(size);

        Assert.NotEmpty(plan);
        AssertCoverage(plan, size);
    }

    // ── ② 端到端吞吐：单连接限速 → 并行分片必须快得多 ───────────

    [Fact]
    public void 池首被限流时并行度按最宽松的源算()
    {
        ParallelGovernor.ResetForTests();
        var registry = new DownloadSourceRegistry(() => new LauncherSettings());
        var downloader = new ChunkedFileDownloader(new HttpClient(), registry);
        var url = "https://libraries.minecraft.net/com/example/lib/1.0/lib-1.0.jar";
        var pool = registry.RacingPoolFor(url);

        // 池首是 BMCLAPI：把它按 429 的路径一路减半到 1 条
        for (var i = 0; i < 4; i++) ParallelGovernor.ReportThrottled("bmclapi2.bangbang93.com");
        Assert.Equal(1, ParallelGovernor.LimitFor("bmclapi2.bangbang93.com"));

        // 但这个文件实际由别的源（官方/其它镜像）在扛 —— 不该被池首的限流压成单连接
        Assert.True(downloader.ConcurrencyFor(url, pool) > 1,
            "并行度被池首的限流值吃掉了：文件会被压成单连接下载");
        ParallelGovernor.ResetForTests();
    }

    /// <summary>
    /// 本机系统代理（如 127.0.0.1:26561 的本地代理）会把连回环的请求也截走，导致测试服务器收不到请求。
    /// 这两个端到端用例先在进程内强制直连，跑完恢复原设置。
    /// </summary>
    private static async Task WithDirectHttpAsync(Func<Task> body)
    {
        var saved = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();
        try
        {
            await body();
        }
        finally
        {
            HttpClient.DefaultProxy = saved;
        }
    }

    [Fact]
    public Task 单连接被限速时_并行分片把整文件吞吐拉起来() => WithDirectHttpAsync(async () =>
    {
        const int size = 8 * 1024 * 1024;      // 8 MiB
        const int perConn = 1024 * 1024;       // 每条连接 1 MB/s：串行要 8 秒
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var server = new ThrottledServer(payload, perConn);
        var settings = new LauncherSettings { DownloadSource = "auto", DownloadCustomMirrorUrl = "" };
        var service = new DownloadService(() => settings);
        var dest = Path.Combine(Path.GetTempPath(), $"sc-dl-{Guid.NewGuid():N}.bin");

        try
        {
            // 吞吐是墙钟量测：构建/其它测试抢 CPU 时单次结果可能被压低。
            // 取两次里最好的一次（真出问题时两次都过不了），避免把偶发抖动当成并行失效。
            var best = 0.0;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                TryDelete(dest);
                var sw = Stopwatch.StartNew();
                await service.DownloadFileAsync($"http://127.0.0.1:{server.Port}/big.bin", dest, sha1);
                sw.Stop();

                Assert.True(File.Exists(dest));
                Assert.Equal(size, new FileInfo(dest).Length);

                var mbps = size / 1048576.0 / sw.Elapsed.TotalSeconds;
                best = Math.Max(best, mbps);
                if (best > 2.5) break;
            }
            Assert.True(server.Requests > 1, $"只发了 {server.Requests} 条请求：没有走并行分片");
            Assert.True(best > 2.5,
                $"吞吐 {best:F2} MB/s（单连接上限 {perConn / 1048576.0:F1} MB/s，请求 {server.Requests} 条）：并行分片没生效");
        }
        finally
        {
            TryDelete(dest);
        }
    });

    [Fact]
    public Task 不支持Range的源_退回整文件下载且结果正确() => WithDirectHttpAsync(async () =>
    {
        const int size = 5 * 1024 * 1024;
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var server = new ThrottledServer(payload, perConnBytesPerSec: 0, supportsRange: false);
        var settings = new LauncherSettings { DownloadSource = "auto", DownloadCustomMirrorUrl = "" };
        var service = new DownloadService(() => settings);
        var dest = Path.Combine(Path.GetTempPath(), $"sc-dl-{Guid.NewGuid():N}.bin");

        try
        {
            await service.DownloadFileAsync($"http://127.0.0.1:{server.Port}/no-range.bin", dest, sha1);
            Assert.Equal(size, new FileInfo(dest).Length);

            // 第二次下载同一主机：Range 能力已记忆，不再探测（也不会再白试分片）
            await service.DownloadFileAsync($"http://127.0.0.1:{server.Port}/no-range.bin", dest, sha1);
            Assert.Equal(size, new FileInfo(dest).Length);
            Assert.True(server.Requests <= 4,
                $"同主机第二次下载仍在反复探测（请求 {server.Requests} 条）");
        }
        finally
        {
            TryDelete(dest);
        }
    });

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响测试结论
        }
    }

    /// <summary>
    /// 本地限速 HTTP 服务器：支持 Range（可选），每条连接按字节速率限速。
    /// 用裸 TcpListener 而不是 HttpListener：后者在非管理员下需要 URL ACL，测试不应依赖提权。
    /// </summary>
    private sealed class ThrottledServer : IDisposable
    {
        private const int ChunkBytes = 32 * 1024;

        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly int _perConnBytesPerSec;
        private readonly bool _supportsRange;
        private readonly CancellationTokenSource _cts = new();
        private int _requests;

        public ThrottledServer(byte[] payload, int perConnBytesPerSec, bool supportsRange = true)
        {
            _payload = payload;
            _perConnBytesPerSec = perConnBytesPerSec;
            _supportsRange = supportsRange;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        /// <summary>累计请求数（用来证明真的开了多条连接 / 没有反复探测）</summary>
        public int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync();
                    if (requestLine is null) return;

                    long start = 0;
                    long end = _payload.Length - 1;
                    for (var line = await reader.ReadLineAsync();
                         !string.IsNullOrEmpty(line);
                         line = await reader.ReadLineAsync())
                    {
                        if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                        var spec = line[6..].Trim();                       // bytes=0-1023
                        var dash = spec.IndexOf('-');
                        if (dash < 0) continue;
                        long.TryParse(spec[6..dash], out start);
                        var tail = spec[(dash + 1)..];
                        end = long.TryParse(tail, out var parsed) && tail.Length > 0
                            ? parsed
                            : _payload.Length - 1;
                    }

                    Interlocked.Increment(ref _requests);
                    var ranged = _supportsRange;
                    if (!_supportsRange)
                    {
                        start = 0;
                        end = _payload.Length - 1;
                    }
                    if (end >= _payload.Length) end = _payload.Length - 1;
                    if (end < start) end = start;

                    var length = end - start + 1;
                    var head = ranged
                        ? $"HTTP/1.1 206 Partial Content\r\nContent-Length: {length}\r\n" +
                          $"Content-Range: bytes {start}-{end}/{_payload.Length}\r\nAccept-Ranges: bytes\r\n" +
                          "Content-Type: application/octet-stream\r\nConnection: keep-alive\r\n\r\n"
                        : $"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\n" +
                          "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));

                    var sw = Stopwatch.StartNew();
                    long sent = 0;
                    while (sent < length && !_cts.IsCancellationRequested)
                    {
                        var n = (int)Math.Min(ChunkBytes, length - sent);
                        await stream.WriteAsync(_payload.AsMemory((int)(start + sent), n));
                        sent += n;
                        if (_perConnBytesPerSec <= 0) continue;
                        // 追加延迟把这条连接压到目标速率（落后就不等，避免补偿式爆发）
                        var wantMs = sent * 1000.0 / _perConnBytesPerSec;
                        var waitMs = wantMs - sw.Elapsed.TotalMilliseconds;
                        if (waitMs > 1) await Task.Delay((int)waitMs);
                    }
                    await stream.FlushAsync();
                }
                catch
                {
                    // 客户端取消 / 连接重置：测试服务器不关心
                }
            }
        }
    }
}
