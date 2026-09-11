// 并发下载引擎：多文件批量下载 + 多源分片竞速 + 重试 + 进度/速度/ETA 回调（对应旧 Rust download 模块）

using System.Diagnostics;
using System.Text;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

/// <summary>批量下载进度快照</summary>
public sealed record BatchProgress(
    string Stage,
    int DoneFiles,
    int TotalFiles,
    long DoneBytes,
    long TotalBytes,
    double SpeedBytesPerSec,
    double? EtaSecs)
{
    public int Percentage => TotalBytes > 0 ? (int)(DoneBytes * 100 / TotalBytes) : 0;
}

public sealed class DownloadService
{
    private readonly HttpClient _http;
    private readonly DownloadSourceRegistry _sources;
    private readonly ChunkedFileDownloader _chunked;

    public DownloadService(Func<LauncherSettings> settings)
    {
        var sockets = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 128, // 承载多源分片并发
        };
        _http = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Launcher");
        _sources = new DownloadSourceRegistry(settings);
        _chunked = new ChunkedFileDownloader(_http, _sources) { Log = msg => Log?.Invoke(msg) };
    }

    /// <summary>下载日志出口（「文件 → 源 → 字节数」）；默认 null = 零开销</summary>
    public Action<string>? Log { get; set; }

    /// <summary>单文件下载（含重试），可选 SHA1 校验</summary>
    public Task DownloadFileAsync(
        string url, string destFile, string? sha1 = null, int maxRetries = 3,
        CancellationToken ct = default)
        => DownloadOneAsync(url, destFile, sha1, maxRetries, ct, 0, null);

    /// <summary>
    /// 单文件下载主路径：Mojang 域名且体积 ≥ 4 MiB → 多源分片竞速；
    /// 其余（小文件、非 Mojang 域名）→ 整文件下载（镜像源优先，官方兜底）。
    /// </summary>
    private async Task DownloadOneAsync(
        string url, string destFile, string? sha1, int maxRetries, CancellationToken ct,
        long size, Action<long>? onBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

        // 只有 Mojang 官方域名（原版安装链路）参与分片竞速；体积未知时探测一次
        if (size < ChunkedFileDownloader.Threshold && DownloadSourceRegistry.IsMojangUrl(url))
            size = Math.Max(size, await ProbeSizeAsync(url, ct));

        if (size >= ChunkedFileDownloader.Threshold)
        {
            try
            {
                await _chunked.DownloadAsync(url, destFile, sha1, size, maxRetries, onBytes, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 分片失败（校验不过 / 无源支持 Range / 重试耗尽）→ 退化为不分片的整文件下载
                Log?.Invoke($"分片竞速失败 {Path.GetFileName(destFile)}（{ex.Message}）→ 退化为整文件下载");
            }
        }

        await DownloadWholeFileAsync(url, destFile, sha1, maxRetries, ct, size);
    }

    /// <summary>整文件下载（不分片）：按竞速池顺序换源重试，官方恒为兜底</summary>
    private async Task DownloadWholeFileAsync(
        string url, string destFile, string? sha1, int maxRetries, CancellationToken ct, long size)
    {
        var pool = _sources.RacingPool();
        var attempts = Math.Max(1, maxRetries);
        for (int attempt = 1; ; attempt++)
        {
            var kind = pool[(attempt - 1) % pool.Count];
            try
            {
                await FetchWholeAsync(_sources.Rewrite(url, kind), destFile, sha1, ct);
                _sources.RecordSuccess(kind);
                if (size >= ChunkedFileDownloader.Threshold)
                    Log?.Invoke($"整文件下载 {Path.GetFileName(destFile)}（{DownloadSourceRegistry.Name(kind)}）");
                return;
            }
            catch (Exception) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                _sources.RecordFailure(kind);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), ct);
            }
        }
    }

    /// <summary>单次整文件请求：流式写 .sc-tmp → SHA1 校验 → 落盘</summary>
    private async Task FetchWholeAsync(string url, string destFile, string? sha1, CancellationToken ct)
    {
        var tmp = destFile + ".sc-tmp";
        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await resp.Content.CopyToAsync(fs, ct);
        }
        if (!string.IsNullOrEmpty(sha1))
        {
            await using var read = File.OpenRead(tmp);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(read));
            if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SHA1 校验失败: {Path.GetFileName(destFile)}");
        }
        File.Move(tmp, destFile, overwrite: true);
    }

    /// <summary>探测远端文件大小（HEAD，经首选源改写）；失败返回 0 = 按未知处理</summary>
    private async Task<long> ProbeSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, _sources.RewritePreferred(url));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode) return resp.Content.Headers.ContentLength ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            // 探测失败不影响下载本身：按未知大小走整文件路径
        }
        return 0;
    }

    /// <summary>字符串内容下载（经首选源改写，故版本清单自动走镜像）</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(_sources.RewritePreferred(url), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>POST JSON 并返回响应文本（Starcloud ID 接口，不做源改写）</summary>
    public async Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken ct = default)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>并发批量下载文件（相同 URL 集合去重），汇报整体进度</summary>
    public async Task DownloadBatchAsync(
        IReadOnlyList<(string Url, string Dest, string? Sha1, long Size)> files,
        string stage,
        int concurrency,
        int maxRetries,
        IProgress<BatchProgress>? progress,
        CancellationToken ct = default)
    {
        // 去重（同 hash 已存在则跳过）
        var pending = new List<(string Url, string Dest, string? Sha1, long Size)>();
        foreach (var f in files.DistinctBy(f => f.Dest))
        {
            if (File.Exists(f.Dest) && f.Size > 0 && new FileInfo(f.Dest).Length == f.Size)
                continue;
            pending.Add(f);
        }

        long totalBytes = pending.Sum(f => Math.Max(f.Size, 0));
        long doneBytes = 0;
        int doneFiles = 0;
        var done = files.Count - pending.Count;
        var sw = Stopwatch.StartNew();
        var lastReported = 0L;
        var lastReportMs = 0L;

        // 大文件由分片 worker 增量上报字节，故节流刷新（每 100ms 一次），文件完成时强制刷新
        void Report(bool force = false)
        {
            if (progress is null) return;
            var now = sw.ElapsedMilliseconds;
            if (force)
            {
                Interlocked.Exchange(ref lastReportMs, now);
            }
            else
            {
                var prev = Interlocked.Read(ref lastReportMs);
                if (now - prev < 100) return;
                if (Interlocked.CompareExchange(ref lastReportMs, now, prev) != prev) return;
            }
            var elapsed = sw.Elapsed.TotalSeconds;
            var doneNow = Interlocked.Read(ref doneBytes) + lastReported;
            var speed = elapsed > 0.5 ? doneNow / elapsed : 0;
            double? eta = speed > 1 && totalBytes > doneNow ? (totalBytes - doneNow) / speed : null;
            progress.Report(new BatchProgress(stage, done + doneFiles, files.Count, doneNow,
                Math.Max(totalBytes, 1), speed, eta));
        }

        if (pending.Count == 0)
        {
            doneBytes = totalBytes;
            doneFiles = files.Count;
            Report(force: true);
            return;
        }

        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = pending.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try
            {
                long reported = 0;
                await DownloadOneAsync(f.Url, f.Dest, f.Sha1, maxRetries, ct, f.Size,
                    n =>
                    {
                        Interlocked.Add(ref doneBytes, n);
                        Interlocked.Add(ref reported, n);
                        Report();
                    });
                // 整文件兜底路径无增量上报，补齐差额保证进度最终到位
                Interlocked.Add(ref doneBytes, Math.Max(0, Math.Max(f.Size, 0) - Interlocked.Read(ref reported)));
                Interlocked.Increment(ref doneFiles);
                Report(force: true);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>获取 Java 主版本要求的 Adoptium 下载地址（记忆约束：解析 binary 字段）</summary>
    public async Task<string> GetAdoptiumDownloadUrlAsync(int majorVersion, CancellationToken ct = default)
    {
        var url = $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";
        var json = await GetStringAsync(url, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("binary", out var binary)) continue;
            if (!binary.TryGetProperty("package", out var package)) continue;
            if (!package.TryGetProperty("link", out var link)) continue;
            var linkStr = link.GetString();
            if (!string.IsNullOrEmpty(linkStr) && linkStr.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return linkStr;
        }
        throw new InvalidOperationException($"Adoptium 未返回可用的 Java {majorVersion} 下载地址");
    }
}
