// 多源分片竞速下载器：同一文件切分片 → 多源并行拉取 → 按偏移写入 → 整文件 SHA1 校验
// 设计见 docs/designs/2026-09-12-multi-source-download-design.md §3.2 / §4.1

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Starcloud.Core.Services;

/// <summary>把大文件切分成片、从多个下载源并行拉取（单源也可并行，用于提速）</summary>
public sealed class ChunkedFileDownloader
{
    /// <summary>≥ 该体积才分片（小文件分片开销大于收益）</summary>
    public const long Threshold = 4 * 1024 * 1024;

    /// <summary>默认片大小</summary>
    public const long ChunkSize = 4 * 1024 * 1024;

    /// <summary>单片文件的最大片数</summary>
    public const int MaxChunks = 16;

    /// <summary>单个文件的并发分片 worker 数</summary>
    public const int MaxWorkers = 4;

    /// <summary>分片读缓冲：256 KiB</summary>
    private const int BufferSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly DownloadSourceRegistry _sources;

    public ChunkedFileDownloader(HttpClient http, DownloadSourceRegistry sources)
    {
        _http = http;
        _sources = sources;
    }

    /// <summary>日志出口（由 DownloadService 注入，默认 null = 零开销）</summary>
    public Action<string>? Log { get; set; }

    /// <summary>分片计划：区间精确覆盖 [0, size)，无重叠无空洞，片数 ≤ MaxChunks</summary>
    internal static IReadOnlyList<(long Start, long Length)> PlanChunks(long size)
    {
        if (size <= 0) return [];

        var chunk = ChunkSize;
        if ((size + chunk - 1) / chunk > MaxChunks)
        {
            // 片数超上限 → 扩大片大小并向上对齐到 1 MiB
            const long align = 1024 * 1024;
            var need = (size + MaxChunks - 1) / MaxChunks;
            chunk = (need + align - 1) / align * align;
        }

        var plan = new List<(long Start, long Length)>();
        for (long start = 0; start < size; start += chunk)
            plan.Add((start, Math.Min(chunk, size - start)));
        return plan;
    }

    /// <summary>
    /// 分片竞速下载。成功 = 全部片写入 .sc-tmp 且整文件 SHA1 匹配后 Move 到 destFile；
    /// 任何失败都会清理 .sc-tmp 并抛错，由调用方决定是否退化为整文件下载。
    /// </summary>
    public async Task DownloadAsync(string url, string destFile, string? sha1, long size,
        int maxRetries, Action<long>? onBytes, CancellationToken ct)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), "分片下载需要已知文件大小");

        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        var tmp = destFile + ".sc-tmp";
        var plan = PlanChunks(size);
        var pool = new CandidateSet(_sources.RacingPool());
        if (pool.Count == 0) throw new IOException($"没有可用的下载源: {Path.GetFileName(destFile)}");

        var usage = new ConcurrentDictionary<SourceKind, long>();
        var attempts = Math.Max(1, maxRetries);
        var queue = new ConcurrentQueue<(long Start, long Length)>(plan);

        TaskCenterService.Instance.ReportRacingBegin(size);
        try
        {
            using (var handle = File.OpenHandle(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                       FileOptions.None, size))
            {
                var workers = Enumerable.Range(0, Math.Min(MaxWorkers, plan.Count)).Select(_ => Task.Run(async () =>
                {
                    while (queue.TryDequeue(out var chunk))
                        await FetchChunkAsync(handle, url, chunk, pool, usage, onBytes, attempts, ct);
                }, ct));
                await Task.WhenAll(workers);
            }
            // 完整性闸门：分片重试只会计多不会计少，成功字节 < 文件大小 = 有分片静默缺失，
            // 绝不能当成功落盘 → 抛错走调用方的整文件退化（断点续传）
            if (usage.Values.Sum() < size)
                throw new IOException(
                    $"分片不完整：成功 {usage.Values.Sum() / 1048576.0:F1} MB / 应为 {size / 1048576.0:F1} MB");

            VerifyAndCommit(tmp, destFile, sha1);
        }
        catch
        {
            TryDelete(tmp);
            TaskCenterService.Instance.ReportRacingEnd(false);
            throw;
        }
        TaskCenterService.Instance.ReportRacingEnd(true);

        Log?.Invoke($"分片竞速 {Path.GetFileName(destFile)}（{size / 1024.0 / 1024.0:F1}MB / {plan.Count} 片）："
                    + string.Join(" / ", usage.OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{DownloadSourceRegistry.Name(kv.Key)} {kv.Value / 1024.0 / 1024.0:F1}MB")));
    }

    /// <summary>取下一片，失败换源重试（同一片累计重试上限 = maxRetries）</summary>
    private async Task FetchChunkAsync(SafeFileHandle handle, string url, (long Start, long Length) chunk,
        CandidateSet pool, ConcurrentDictionary<SourceKind, long> usage, Action<long>? onBytes,
        int attempts, CancellationToken ct)
    {
        var failed = 0;
        while (true)
        {
            var kind = pool.Next();
            bool ranged;
            try
            {
                ranged = await FetchRangeAsync(handle, _sources.Rewrite(url, kind), kind, chunk, usage, onBytes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                _sources.RecordFailure(kind);
                if (++failed >= attempts) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * failed), ct);
                continue;
            }

            if (ranged)
            {
                _sources.RecordSuccess(kind);
                return;
            }
            // 源忽略 Range 返回整文件：已退出分片池，立即换源且不消耗重试额度
            pool.Remove(kind);
        }
    }

    /// <summary>拉取单个分片并按偏移写入；返回 false 表示该源不支持 Range（已被踢出分片池）</summary>
    private async Task<bool> FetchRangeAsync(SafeFileHandle handle, string target, SourceKind kind,
        (long Start, long Length) chunk, ConcurrentDictionary<SourceKind, long> usage,
        Action<long>? onBytes, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        req.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.Start + chunk.Length - 1);
        HttpResponseMessage resp;
        try
        {
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            conn.CancelAfter(TimeSpan.FromSeconds(20)); // 连接 + 响应头超时
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"连接 {DownloadSourceRegistry.Name(kind)} 20 秒无响应");
        }

        if (resp.StatusCode == HttpStatusCode.OK)
        {
            _sources.MarkNoRangeSupport(kind);
            return false;
        }
        resp.EnsureSuccessStatusCode();
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new IOException($"下载源 {DownloadSourceRegistry.Name(kind)} 返回 {(int)resp.StatusCode}");

        var declared = resp.Content.Headers.ContentLength;
        if (declared is > 0 && declared != chunk.Length)
            throw new IOException($"分片长度不符：期望 {chunk.Length}，实际 {declared}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long offset = chunk.Start;
            long remaining = chunk.Length;
            while (remaining > 0)
            {
                // 空闲超时：15 秒读不到字节判死重试该分片（禁用总时长限制后防止连接卡死永久挂起）
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(15));
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token);
                    if (read <= 0) throw new IOException($"分片响应提前结束（{DownloadSourceRegistry.Name(kind)}）");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"分片 15 秒无数据（{DownloadSourceRegistry.Name(kind)}）");
                }
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct);
                offset += read;
                remaining -= read;
                usage.AddOrUpdate(kind, read, (_, v) => v + read);
                onBytes?.Invoke(read);
                TaskCenterService.Instance.ReportSourceBytes(DownloadSourceRegistry.Name(kind), read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        // 分片整体成功 → 按分片长度计入进度（去重，不受重试重复字节影响）
        TaskCenterService.Instance.ReportChunkDone(chunk.Length);
        return true;
    }

    /// <summary>整文件 SHA1 校验通过后落盘（校验失败抛错，由调用方清理临时文件）</summary>
    private static void VerifyAndCommit(string tmp, string destFile, string? sha1)
    {
        if (!string.IsNullOrEmpty(sha1))
        {
            using var fs = File.OpenRead(tmp);
            var actual = Convert.ToHexString(SHA1.HashData(fs));
            if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SHA1 校验失败: {Path.GetFileName(destFile)}");
        }
        File.Move(tmp, destFile, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响主流程（下次下载会覆盖）
        }
    }

    /// <summary>分片阶段的候选源集合（可并发剔除不支持 Range 的源）</summary>
    private sealed class CandidateSet
    {
        private SourceKind[] _items;
        private int _cursor = -1;

        public CandidateSet(IEnumerable<SourceKind> items) => _items = items.ToArray();

        public int Count => _items.Length;

        /// <summary>轮询取下一个源</summary>
        public SourceKind Next()
        {
            var items = _items;
            if (items.Length == 0) throw new IOException("没有支持分片下载的下载源");
            var i = Interlocked.Increment(ref _cursor);
            return items[(int)((uint)i % (uint)items.Length)];
        }

        public void Remove(SourceKind kind)
        {
            while (true)
            {
                var items = _items;
                var next = items.Where(x => x != kind).ToArray();
                if (next.Length == items.Length) return;
                if (ReferenceEquals(Interlocked.CompareExchange(ref _items, next, items), items)) return;
            }
        }
    }
}
