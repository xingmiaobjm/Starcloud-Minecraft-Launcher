// 分片级断点续传回归：中途失败后重试，已下完的片不应重下。
//
// 背景：原来分片失败就把 .sc-tmp 整个删掉，下一次从 0 开始重下 —— 大文件失败一次
// 就白下几十 MB。整文件路径本来就有 Range 续传，分片路径（≥ 4 MiB 的文件全走这条）
// 却是唯一没有续传能力的。

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class ChunkResumeTests
{
    private const int ChunkBytes = 32 * 1024;

    /// <summary>可控的本地 Range 服务器：可指定「第 N 个请求就断开」。</summary>
    private sealed class FlakyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly int _perConnBytesPerSec;
        private readonly int _failAfterRequests;
        private readonly CancellationTokenSource _cts = new();
        private int _requests;

        public FlakyServer(byte[] payload, int perConnBytesPerSec, int failAfterRequests)
        {
            _payload = payload;
            _perConnBytesPerSec = perConnBytesPerSec;
            _failAfterRequests = failAfterRequests;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
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
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
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
                        var spec = line[6..].Trim();
                        var dash = spec.IndexOf('-');
                        if (dash < 0) continue;
                        long.TryParse(spec[6..dash], out start);
                        var tail = spec[(dash + 1)..];
                        end = long.TryParse(tail, out var parsed) && tail.Length > 0
                            ? parsed
                            : _payload.Length - 1;
                    }

                    var seq = Interlocked.Increment(ref _requests);
                    // 第 failAfterRequests 条请求直接掐断：模拟中途失败
                    if (seq > _failAfterRequests)
                    {
                        client.Client.Close();
                        return;
                    }

                    if (end >= _payload.Length) end = _payload.Length - 1;
                    if (end < start) end = start;
                    var length = end - start + 1;
                    var head = $"HTTP/1.1 206 Partial Content\r\nContent-Length: {length}\r\n" +
                               $"Content-Range: bytes {start}-{end}/{_payload.Length}\r\nAccept-Ranges: bytes\r\n" +
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
                        var wantMs = sent * 1000.0 / _perConnBytesPerSec;
                        var waitMs = wantMs - sw.Elapsed.TotalMilliseconds;
                        if (waitMs > 1) await Task.Delay((int)waitMs);
                    }
                    await stream.FlushAsync();
                }
                catch
                {
                    // 断连是预期行为
                }
            }
        }
    }

    [Fact]
    public async Task 分片中途失败后重试_已完成的片不重复下载()
    {
        const int size = 8 * 1024 * 1024;
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        var saved = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), $"sc-resume-{Guid.NewGuid():N}.bin");
            var tmp = dest + ".sc-tmp";

            // 第一轮：服务器在第 3 条请求后掐断 → 分片失败
            using (var flaky = new FlakyServer(payload, 4 * 1024 * 1024, 3))
            {
                var settings = new LauncherSettings { DownloadSource = "auto", DownloadCustomMirrorUrl = "" };
                var service = new DownloadService(() => settings);
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    service.DownloadFileAsync($"http://127.0.0.1:{flaky.Port}/big.bin", dest, sha1));
            }

            // 失败后应留下续传现场：临时文件 + 已完成分片记录
            Assert.True(File.Exists(tmp), "失败后没有保留 .sc-tmp，续传无从谈起");
            var partsFile = tmp + ".parts";
            Assert.True(File.Exists(partsFile), "失败后没有留下分片完成记录");
            var recorded = File.ReadAllLines(partsFile)
                .Where(l => l.Trim().Length > 0)
                .Select(l => long.Parse(l.Trim()))
                .ToList();
            Assert.NotEmpty(recorded);

            // 第二轮：服务器正常 → 应跳过已完成的片，只补剩下的
            var before = recorded.Count;
            using (var good = new FlakyServer(payload, 4 * 1024 * 1024, int.MaxValue))
            {
                var settings = new LauncherSettings { DownloadSource = "auto", DownloadCustomMirrorUrl = "" };
                var service = new DownloadService(() => settings);
                var sw = Stopwatch.StartNew();
                await service.DownloadFileAsync($"http://127.0.0.1:{good.Port}/big.bin", dest, sha1);
                sw.Stop();

                Assert.True(File.Exists(dest));
                Assert.Equal(size, new FileInfo(dest).Length);
                Assert.Equal(sha1, await ComputeSha1Async(dest));

                // 8 MiB 分 8 片：跳过的片越多，重试时新请求越少。
                // （请求数会略多于片数：每轮先有一次体积 / Range 探测。）
                var totalRequests = before + good.Requests;
                Assert.True(good.Requests < 8 + 2,
                    $"重试时又发了 {good.Requests} 条请求（已跳过 {before} 片）：续传没生效");
                Assert.True(sw.Elapsed.TotalSeconds < 10,
                    $"续传重试耗时 {sw.Elapsed.TotalSeconds:F1}s：像是整个文件重下了");
                Assert.True(totalRequests >= before,
                    "请求数统计异常");
            }

            File.Delete(tmp);
            File.Delete(partsFile);
            File.Delete(dest);
        }
        finally
        {
            HttpClient.DefaultProxy = saved;
        }
    }

    private static async Task<string> ComputeSha1Async(string path)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
