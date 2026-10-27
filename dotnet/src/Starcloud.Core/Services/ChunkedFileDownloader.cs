// 多源分片竞速下载器：同一文件切分片 → 多源并行拉取 → 按偏移写入 → 整文件 SHA1 校验
// 设计见 docs/designs/2026-09-12-multi-source-download-design.md §3.2 / §4.1
//
// 提速要点（2026-09-21 实测：CDN 单连接常被限到 1–3 MB/s，4 条并行能拿到 3.1×）：
//   1. 单文件默认 8 条并发连接（原 4 条），片大小按「每 worker 约 2 片」自适应（1–8 MiB）；
//   2. 进程级 worker 预算 32：批量下载时不会把连接数打爆，单文件仍独占最多 8 条；
//   3. 读缓冲 512 KiB（原 256 KiB），文件句柄带 FileOptions.Asynchronous + RandomAccess 定位写；
//   4. 空闲看门狗一条流一个（原来每次读都新建 CTS，满速时每秒数百个定时器，见 IdleTimeout.cs）；
//   5. 字节遥测 10 Hz 聚合上报（原来每 256 KiB 就 lock + 排序 + 事件三连）。

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

    /// <summary>片大小下限：每片一条请求，片太小会让首字节/握手开销吃掉并行收益</summary>
    public const long MinChunkSize = 1024 * 1024;

    /// <summary>片大小上限：再大也不会更快，只会让尾片拖长</summary>
    public const long MaxChunkSize = 8 * 1024 * 1024;

    /// <summary>单片文件的片数上限</summary>
    public const int MaxChunks = 64;

    /// <summary>单文件并发分片 worker 数（并行连接是主要提速手段）</summary>
    public const int MaxWorkersPerFile = 8;

    /// <summary>进程级 worker 预算：多文件同时下载时的总并发连接上限</summary>
    public const int GlobalWorkerBudget = 32;

    /// <summary>分片读缓冲：512 KiB（ArrayPool 租借，无 GC 压力）</summary>
    private const int BufferSize = 512 * 1024;

    /// <summary>字节遥测聚合窗口：窗口内字节合并成一次上报</summary>
    private const int TelemetryWindowMs = 100;

    /// <summary>空闲判死：连续这么久读不到字节就换源/重试</summary>
    private const int IdleTimeoutMs = 15_000;

    /// <summary>连接 + 响应头超时</summary>
    private const int ConnectTimeoutSec = 20;

    /// <summary>源质量预检：每个候选源试 1 字节的上限（并行做，总开销 ≤ 这个数）。
    /// 实测不可达镜像的连接超时约 3.7 秒，取 4 秒足够；原来 10 秒会让首个文件白等一轮。</summary>
    private const int WarmupTimeoutSec = 4;

    private static readonly SemaphoreSlim Budget = new(GlobalWorkerBudget, GlobalWorkerBudget);

    private readonly HttpClient _http;
    private readonly DownloadSourceRegistry _sources;

    public ChunkedFileDownloader(HttpClient http, DownloadSourceRegistry sources)
    {
        _http = http;
        _sources = sources;
    }

    /// <summary>日志出口（由 DownloadService 注入，默认 null = 零开销）</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// 分片计划：区间精确覆盖 [0, size)、无重叠无空洞，片数 ≤ MaxChunks。
    /// 目标 = 每个 worker 约 2 片（一片在跑、一片在等），片大小夹在 1–8 MiB 且按 1 MiB 对齐。
    /// </summary>
    internal static IReadOnlyList<(long Start, long Length)> PlanChunks(long size, int workers = MaxWorkersPerFile)
    {
        if (size <= 0) return [];

        var w = Math.Clamp(workers, 1, MaxWorkersPerFile);
        var target = (size + w * 2L - 1) / (w * 2L);
        var chunk = AlignMiB(Math.Clamp(target, MinChunkSize, MaxChunkSize));
        if ((size + chunk - 1) / chunk > MaxChunks)
            chunk = AlignMiB((size + MaxChunks - 1) / MaxChunks); // 超大文件：按片数上限均分

        var plan = new List<(long Start, long Length)>();
        for (long start = 0; start < size; start += chunk)
            plan.Add((start, Math.Min(chunk, size - start)));
        return plan;
    }

    // ── 分片级断点续传 ──

    /// <summary>
    /// 已完成分片的记录文件（与 .sc-tmp 同名加 .parts 后缀）。
    /// <para>为什么需要：原来分片失败就把 .sc-tmp 整个删掉，下一次从 0 开始重下；
    /// 大文件失败一次 = 前面几 MB 白下。整文件路径本来就有 Range 续传，
    /// 分片路径（≥ 4 MB 的文件全部走这条）却是唯一没有续传能力的。</para>
    /// <para>格式：每行一个已完成分片的起始偏移（十进制，纯文本）。解析失败就当没有记录。</para>
    /// </summary>
    private static string PartsFile(string tmp) => tmp + ".parts";

    /// <summary>读取已完成的分片起始偏移；文件损坏或读不出来时返回空（整份重下）。</summary>
    private static HashSet<long> LoadCompletedParts(string tmp)
    {
        var done = new HashSet<long>();
        try
        {
            var file = PartsFile(tmp);
            if (!File.Exists(file)) return done;
            foreach (var line in File.ReadAllLines(file))
            {
                var text = line.Trim();
                if (text.Length == 0) continue;
                if (!long.TryParse(text, out var start)) continue;
                done.Add(start);
            }
        }
        catch
        {
            // 记录文件坏了就整份重下
        }
        return done;
    }

    /// <summary>追加一条「已完成分片」的记录（每片成功即写一次）。</summary>
    private static void SaveCompletedPart(string tmp, long start)
    {
        try
        {
            var file = PartsFile(tmp);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"{start}\n");
        }
        catch
        {
            // 记录失败不影响本次下载（最坏情况是下次重下这一片）
        }
    }

    private static long AlignMiB(long bytes) =>
        (bytes + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024);

    /// <summary>
    /// 本文件的并行连接数 = 候选源里**最宽松**的历史容忍度（再夹在 1..每文件上限内）。
    /// <para>为什么不能只看池里第一个源：池首常是被 429 限过的镜像（限到 1 条），
    /// 而这片文件实际是官方 CDN 在扛 —— 按镜像的限流值收并发等于把整文件压成单连接
    /// （实测 0.29 MB/s，而官方多连接应有 1 MB/s 以上）。</para>
    /// <para>但也不能盲目取池内最大值：那个被限到 1 条的镜像未必会被用到，
    /// 按 8 条去打它就是连续 429 → 减半 → 冷却 → 再撞。
    /// 所以这里按「实际会用的那个源」取（preferred 有效时用它），取不到再退回池内最大值。</para>
    /// </summary>
    internal int ConcurrencyFor(string url, IReadOnlyList<SourceKind> pool, SourceKind? preferred = null)
    {
        var widest = pool
            .Select(k => ParallelGovernor.LimitFor(HostOf(_sources.Rewrite(url, k))))
            .DefaultIfEmpty(MaxWorkersPerFile)
            .Max();

        if (preferred is { } p && pool.Contains(p))
        {
            var limit = ParallelGovernor.LimitFor(HostOf(_sources.Rewrite(url, p)));
            return Math.Max(1, Math.Min(MaxWorkersPerFile, limit));
        }

        return Math.Max(1, Math.Min(MaxWorkersPerFile, widest));
    }

    /// <summary>
    /// 分片竞速下载。成功 = 全部片写入 .sc-tmp 且整文件 SHA1 匹配后 Move 到 destFile；
    /// 任何失败都会清理 .sc-tmp 并抛错，由调用方决定是否退化为整文件下载。
    /// </summary>
    public async Task DownloadAsync(string url, string destFile, string? sha1, long size,
        int maxRetries, Action<long>? onBytes, CancellationToken ct, SourceKind? preferred = null)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), "分片下载需要已知文件大小");

        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        var tmp = destFile + ".sc-tmp";
        var pool = new CandidateSet(_sources.RacingPoolFor(url), _sources.IsDegraded, preferred);
        if (pool.Count == 0) throw new IOException($"没有可用的下载源: {Path.GetFileName(destFile)}");

        // 首次分片前做一次源质量预检（进程内一次）
        if (_sources.WarmupNeeded) await WarmupAsync(url, pool, ct);
        if (pool.Count == 0) throw new IOException($"没有支持分片的下载源: {Path.GetFileName(destFile)}");

        var plan = PlanChunks(size, MaxWorkersPerFile);
        var allowed = Math.Min(MaxWorkersPerFile, plan.Count);
        var wanted = Math.Min(allowed, ConcurrencyFor(url, pool.Snapshot(), preferred));
        var usage = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        // 每个分片的重试额度必须至少覆盖池里全部源：否则重试次数用完了还没轮到官方兜底，
        // 整文件就会因为「假死镜像先被轮询到」而失败，退化到单连接整文件下载（实测慢十倍）。
        var attempts = Math.Max(Math.Max(1, maxRetries), pool.Count);
        var token = new object();

        // 分片级断点续传：上次已经下完的片直接跳过，只补没下完的那些。
        // 续传的前提是「片计划完全一致」——计划由 (size, MaxWorkersPerFile) 决定，
        // 两者不变就一致；记录里出现计划外偏移（换了并发度或改了文件）就整份重下。
        var completed = LoadCompletedParts(tmp);
        var resumed = false;
        if (completed.Count > 0 && File.Exists(tmp))
        {
            var tmpInfo = new FileInfo(tmp);
            var planStarts = plan.Select(c => c.Start).ToHashSet();
            resumed = tmpInfo.Length == size && completed.All(planStarts.Contains);
        }
        if (!resumed) completed.Clear();
        var resumedBytes = resumed
            ? plan.Where(c => completed.Contains(c.Start)).Sum(c => c.Length)
            : 0;

        var queue = new ConcurrentQueue<(long Start, long Length)>(
            resumed ? plan.Where(c => !completed.Contains(c.Start)) : plan);
        if (resumed && resumedBytes > 0)
            Log?.Invoke($"分片续传 {Path.GetFileName(destFile)}：跳过 {completed.Count} 片"
                        + $"（{resumedBytes / 1024.0 / 1024.0:F1}MB 已下）");

        // 进程级预算：能租几条算几条（至少 1 条），批量下载时自动让路给别的文件
        var leased = 0;
        while (leased < wanted && await Budget.WaitAsync(0, ct)) leased++;
        if (leased == 0)
        {
            await Budget.WaitAsync(ct);
            leased = 1;
        }

        TaskCenterService.Instance.ReportRacingBegin(token, Path.GetFileName(destFile), size);
        try
        {
            // FileOptions.Asynchronous：RandomAccess 的定位写在异步句柄上才是真异步（否则走线程池模拟）
            // OpenOrCreate + 预分配：续传时保留已有内容，只把没下完的片写进去。
            // 不续传时文件可能存在但内容是旧的（上次失败留下的），必须先清零 ——
            // 否则旧内容会被当成新片写进去，最终 SHA1 校验必然失败。
            // 续传成立 → OpenOrCreate（保留已下内容）；不成立 → Create（截断重建，
            // 否则上次失败留下的旧内容会被当成新片写进去，SHA1 必然对不上）。
            // 注意不能用 OpenOrCreate 一刀切：它不会截断、也不会按 size 预分配，
            // 实测会让整个分片下载退化成单连接（吞吐掉到 1/4）。
            using (var handle = File.OpenHandle(tmp, resumed ? FileMode.OpenOrCreate : FileMode.Create,
                       FileAccess.Write, FileShare.None, FileOptions.Asynchronous, size))
            {
                var running = Enumerable.Range(0, leased).Select(_ => Task.Run(async () =>
                {
                    while (queue.TryDequeue(out var chunk))
                        await FetchChunkAsync(handle, tmp, url, chunk, pool, usage, onBytes, attempts, token, ct);
                }, ct));
                await Task.WhenAll(running);
            }

            // 完整性闸门：分片重试只会计多不会计少，成功字节 < 文件大小 = 有分片静默缺失，
            // 绝不能当成功落盘 → 抛错走调用方的整文件退化
            // 续传时已下完的片不重下，usage 里没有它们，要按「已完成字节」补进来
            var got = usage.Values.Sum() + resumedBytes;
            if (got < size)
                throw new IOException($"分片不完整：成功 {got / 1048576.0:F1} MB / 应为 {size / 1048576.0:F1} MB");

            VerifyAndCommit(tmp, destFile, sha1);
            TryDelete(PartsFile(tmp));
        }
        catch
        {
            // 保留 .sc-tmp 与分片记录：下次重试（或调用方退回整文件路径时）能接着下，
            // 而不是从头再来（原来这里直接删掉临时文件，大文件失败一次就白下几十 MB）。
            // 记录文件不存在时说明这一轮没成功过任何一片，删掉临时文件即可。
            if (!File.Exists(PartsFile(tmp))) TryDelete(tmp);
            TaskCenterService.Instance.ReportRacingEnd(token, false, usage.Values.Sum());
            throw;
        }
        finally
        {
            Budget.Release(leased);
        }
        TaskCenterService.Instance.ReportRacingEnd(token, true, size);

        Log?.Invoke($"分片竞速 {Path.GetFileName(destFile)}"
                    + $"（{size / 1024.0 / 1024.0:F1}MB / {plan.Count} 片 / {leased} 并发）："
                    + string.Join(" / ", usage.OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{kv.Key} {kv.Value / 1024.0 / 1024.0:F1}MB")));
    }

    /// <summary>
    /// 源质量预检：第一次分片前给每个候选镜像发一个 1 字节 Range 请求（并行、每源 10 秒封顶）。
    /// <para>为什么值得：源质量在单机网络上很稳定——实测 FastMirror 的 TLS 一直是坏的、RainYun 返回站点页面；
    /// 不预检的话它们和可用源同样靠前，每个大文件都要先浪费一轮重试才轮到官方兜底，
    /// 严重时整个文件退化成单连接整文件下载（0.1–0.3 MB/s）。官方源不预检（它是兜底，没有更后面的源可换）。</para>
    /// </summary>
    private async Task WarmupAsync(string url, CandidateSet pool, CancellationToken ct)
    {
        _sources.MarkWarmupDone();   // 无论结果如何都只做一次，预检本身也发请求
        var probes = pool.Snapshot().Where(k => k != SourceKind.Official).ToList();
        if (probes.Count == 0) return;
        await Task.WhenAll(probes.Select(kind => ProbeSourceAsync(url, kind, pool, ct)));
    }

    private async Task ProbeSourceAsync(string url, SourceKind kind, CandidateSet pool, CancellationToken ct)
    {
        var target = _sources.Rewrite(url, kind);
        var host = HostOf(target);
        var label = Label(kind, host);
        var channel = DownloadSourceRegistry.ChannelOf(url);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            conn.CancelAfter(TimeSpan.FromSeconds(WarmupTimeoutSec));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);

            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                _sources.MarkRangeSupport(host, true);
                _sources.RecordRangeHonored(kind, channel);
                _sources.RecordSuccess(kind);      // 可用 → 排到其它镜像前面
                Log?.Invoke($"源预检：{label} 可用（支持分片）");
                return;
            }
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                // 无视 Range 直接给整文件。镜像会 302 到随机节点，个别节点不认 Range 属正常，
                // 所以只计数；连续够多（本通道）才认定它不能分片，届时分片选择处会跳过它。
                _sources.RecordRangeIgnored(kind, channel);
                if (_sources.NoRangeSupport(kind, channel))
                {
                    pool.Remove(kind);
                    Log?.Invoke($"源预检：{label} 在{ChannelText(channel)}通道上不支持分片，退出本次竞速池");
                }
                else
                {
                    Log?.Invoke($"源预检：{label} 回了整文件（未采纳 Range），本次仍参与但排后");
                }
                return;
            }
            _sources.MarkDegraded(kind);
            Log?.Invoke($"源预检：{label} 返回 {(int)resp.StatusCode}，本进程内靠后使用");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消：什么都不记
        }
        catch (Exception ex)
        {
            _sources.MarkDegraded(kind);
            // 官方兜底永远在池里；预检失败的镜像本次直接不参与，避免每片都白等一次超时
            if (pool.Count > 1) pool.Remove(kind);
            Log?.Invoke($"源预检：{label} 不可用（{ex.Message}），本次不再参与分片");
        }
    }

    /// <summary>单个分片的结果：是否支持 Range + 实测样本（用于按速度选源）</summary>
    private readonly record struct ChunkFetchResult(bool Ranged, long Bytes, long ElapsedMs);

    /// <summary>取下一片，失败换源重试（同一片累计重试上限 = maxRetries）</summary>
    private async Task FetchChunkAsync(SafeFileHandle handle, string tmp, string url,
        (long Start, long Length) chunk,
        CandidateSet pool, ConcurrentDictionary<string, long> usage, Action<long>? onBytes,
        int attempts, object token, CancellationToken ct)
    {
        var failed = 0;
        var channel = DownloadSourceRegistry.ChannelOf(url);
        while (true)
        {
            var kind = pool.Next();
            var target = _sources.Rewrite(url, kind);
            var host = HostOf(target);
            var label = Label(kind, host);

            // 已判定该源在**这条通道**上不能分片 → 换下一个源；池空则抛错，由调用方退化整文件。
            // 注意粒度是「源 + 通道」：BMCLAPI 的 /maven 节点不认 Range，不代表它给客户端 jar 分片也不行。
            if (_sources.NoRangeSupport(kind, channel))
            {
                pool.Remove(kind);
                continue;
            }

            ChunkFetchResult result;
            try
            {
                result = await FetchRangeAsync(handle, target, kind, host, label, channel, chunk, usage,
                    onBytes, token, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                _sources.RecordFailure(kind);
                // 本文件内直接摘掉这个坏源：否则 8 个 worker 会轮流撞它，每片都先超时再换源
                pool.ReleaseProbe(kind);
                if (pool.Count > 1) pool.Remove(kind);
                Log?.Invoke($"{label} 分片失败（{ex.Message}）→ 本文件内换源重试");
                if (++failed >= attempts) throw;
                // 退避：平方增长；被限流时按主机的冷却时间等（限流不会因为立刻重试就消失）
                var backoff = TimeSpan.FromMilliseconds(400 * failed * failed);
                var cooldown = ParallelGovernor.CooldownFor(host);
                await Task.Delay(cooldown > backoff ? cooldown : backoff, ct);
                continue;
            }

            if (result.Ranged)
            {
                _sources.RecordSuccess(kind);
                ParallelGovernor.ReportSuccess(host);
                pool.ReportSpeed(kind, result.Bytes, result.ElapsedMs);
                // 记下这一片已完成：下次失败重试时它不用再下（分片级断点续传）
                SaveCompletedPart(tmp, chunk.Start);
                return;
            }
            // 源忽略 Range 返回整文件：已退出分片池，立即换源且不消耗重试额度
            pool.Remove(kind);
        }
    }

    /// <summary>拉取单个分片并按偏移写入；返回 false 表示该源不支持 Range（已被踢出分片池）</summary>
    private async Task<ChunkFetchResult> FetchRangeAsync(SafeFileHandle handle, string target,
        SourceKind kind, string host, string label, SourceChannel channel, (long Start, long Length) chunk,
        ConcurrentDictionary<string, long> usage,
        Action<long>? onBytes, object token, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        req.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.Start + chunk.Length - 1);
        // 分片必须走 HTTP/1.1：HTTP/2 会把 8 条"并行"分片多路复用到一条 TCP 连接上，
        // 结果是流量被单连接拥塞窗口掐住（实测：同样 8 分片，HTTP/2 只有单连接速度，
        // 且慢速 CDN 上会因流控停顿触发空闲超时反复重试）。HTTP/1.1 = 每片一条连接，才真的有 8 倍并行。
        req.Version = HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        HttpResponseMessage resp;
        try
        {
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            conn.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSec));
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"连接 {label} {ConnectTimeoutSec} 秒无响应");
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                // 该源忽略 Range 回了整文件：按「源 + 通道」计数（换源，但不消耗重试额度）。
                // 不在这里直接判死：镜像会 302 到随机节点，个别节点不认 Range 很常见。
                _sources.RecordRangeIgnored(kind, channel);
                return new ChunkFetchResult(false, 0, 0);
            }
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                ParallelGovernor.ReportThrottled(host); // 按主机记住：下次这个域名少开几条连接
                throw new IOException($"{label} 限流（HTTP {(int)resp.StatusCode}）");
            }

            resp.EnsureSuccessStatusCode();
            if (resp.StatusCode != HttpStatusCode.PartialContent)
                throw new IOException($"下载源 {label} 返回 {(int)resp.StatusCode}");

            Log?.Invoke($"分片 {chunk.Start / 1048576.0:F0}MB/{chunk.Length / 1048576.0:F1}MB ← {label} HTTP/{resp.Version}");
            _sources.MarkRangeSupport(host, true);  // 实测支持分片 → 同主机后续文件直接走分片
            _sources.RecordRangeHonored(kind, channel); // 这条通道上确实能分片：清掉之前的忽略计数

            var declared = resp.Content.Headers.ContentLength;
            if (declared is > 0 && declared != chunk.Length)
                throw new IOException($"分片长度不符：期望 {chunk.Length}，实际 {declared}");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using var idle = new IdleTimeout(ct, IdleTimeoutMs);
                long offset = chunk.Start;
                long remaining = chunk.Length;
                long pending = 0;                          // 聚合中的未上报字节
                var lastFlush = Environment.TickCount64;

                void FlushTelemetry()
                {
                    if (pending <= 0) return;
                    var n = pending;
                    pending = 0;
                    usage.AddOrUpdate(label, n, (_, v) => v + n);
                    onBytes?.Invoke(n);
                    TaskCenterService.Instance.ReportSourceBytes(label, n);
                    lastFlush = Environment.TickCount64;
                }

                while (remaining > 0)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(
                            buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token);
                        if (read <= 0) throw new IOException($"分片响应提前结束（{label}）");
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"分片 {IdleTimeoutMs / 1000} 秒无数据（{label}）");
                    }
                    idle.Touch();

                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct);
                    offset += read;
                    remaining -= read;
                    pending += read;
                    // 10 Hz 聚合：进度条要平顺，但不必每 512 KiB 就锁一次调度中心
                    if (Environment.TickCount64 - lastFlush >= TelemetryWindowMs) FlushTelemetry();
                }
                FlushTelemetry();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // 分片整体成功 → 按分片长度计入进度（去重，不受重试重复字节影响）
        TaskCenterService.Instance.ReportChunkDone(token, chunk.Length);
        return new ChunkFetchResult(true, chunk.Length, Environment.TickCount64 - started);
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

    private static string HostOf(string url) =>
        DownloadSourceRegistry.HostOf(url);

    /// <summary>展示用源名：Mojang 镜像报镜像名，非 Mojang 域名（不会被改写）直接报主机名</summary>
    private static string Label(SourceKind kind, string host) =>
        DownloadSourceRegistry.IsMojangHost(host) ? DownloadSourceRegistry.Name(kind) : host;

    /// <summary>展示用通道名（仅用于日志）</summary>
    private static string ChannelText(SourceChannel channel) => channel switch
    {
        SourceChannel.Meta => "版本清单",
        SourceChannel.Client => "客户端",
        SourceChannel.Assets => "资源",
        SourceChannel.Maven => "库",
        _ => "普通",
    };

    /// <summary>分片阶段的候选源集合（可并发剔除不支持 Range 的源）</summary>
    /// <summary>
    /// 分片阶段的候选源集合（可并发剔除不支持 Range / 预检失败的源）。
    /// 选源策略：先让每个健康源各跑一片拿到实测速度，之后一直用最快的那一个；
    /// 被降级（预检失败/连续失败）的源不参与 —— 旧实现每片轮询所有源，
    /// 坏源/慢源也均摊 1/N 的片，慢片会把整个文件拖住。
    /// </summary>
    internal sealed class CandidateSet
    {
        private SourceKind[] _items;
        private int _cursor = -1;
        private readonly Func<SourceKind, bool>? _isDegraded;
        private SourceKind? _preferred;
        private readonly ConcurrentDictionary<SourceKind, SpeedSample> _speeds = new();
        private readonly ConcurrentDictionary<SourceKind, byte> _probing = new();

        private sealed class SpeedSample
        {
            public double Ema; // bytes / ms
            public int Samples;
        }

        public CandidateSet(IEnumerable<SourceKind> items, Func<SourceKind, bool>? isDegraded = null,
            SourceKind? preferred = null)
        {
            _items = items.ToArray();
            _isDegraded = isDegraded;
            _preferred = preferred;
        }

        public int Count => _items.Length;

        public SourceKind First => _items.Length > 0 ? _items[0] : SourceKind.Official;

        /// <summary>当前池快照（质量预检用；数组引用读取本身是原子的）</summary>
        public IReadOnlyList<SourceKind> Snapshot() => _items;

        public SourceKind Next()
        {
            var items = _items;
            if (items.Length == 0) throw new IOException("没有支持分片下载的下载源");

            // 批量下载时文件被分摊到指定源：整个文件都用它，换源只在它失败/降级后发生。
            // 这条短路是「源级并行」策略的前提：批量时把不同文件分摊到不同源，
            // 目的正是让各镜像同时出力（见 DownloadService.DownloadBatchAsync）。
            // 若在这里放开择优，每个文件的分片都会去抢最快源，其余镜像就闲着了 ——
            // 整体吞吐反而下降。慢源的处置交给上层：分摊时按健康度挑源 + 源失败即换。
            if (_preferred is { } preferred && items.Contains(preferred) &&
                !(_isDegraded?.Invoke(preferred) ?? false))
            {
                return preferred;
            }

            var healthy = _isDegraded is null ? items : items.Where(k => !_isDegraded(k)).ToArray();
            var candidates = healthy.Length > 0 ? healthy : items;

            // 还没测过速度的源：每个源只放一条探测请求（多 worker 同时压同一个慢源会更慢）
            if (candidates.Any(k => SpeedOf(k) <= 0))
            {
                foreach (var kind in candidates)
                {
                    if (SpeedOf(kind) > 0) continue;
                    if (_probing.TryAdd(kind, 1)) return kind;
                }

                // 未测速的源都在探测中：先用已测过的；实在没有就随便挑一个
                var sampled = candidates.Where(k => SpeedOf(k) > 0).ToArray();
                if (sampled.Length > 0)
                    return sampled[(int)((uint)Interlocked.Increment(ref _cursor) % (uint)sampled.Length)];
                return candidates[(int)((uint)Interlocked.Increment(ref _cursor) % (uint)candidates.Length)];
            }

            // 都有速度样本：选实测吞吐最高的；相同速度按池内顺序（顺序已体现镜像→官方的健康度）
            var best = candidates[0];
            var bestSpeed = SpeedOf(best);
            for (var i = 1; i < candidates.Length; i++)
            {
                var speed = SpeedOf(candidates[i]);
                if (speed > bestSpeed)
                {
                    best = candidates[i];
                    bestSpeed = speed;
                }
            }
            return best;
        }

        /// <summary>记录一次分片实测吞吐（bytes/ms 的 EMA）；首片直接作为初值。</summary>
        public void ReportSpeed(SourceKind kind, long bytes, long elapsedMs)
        {
            _probing.TryRemove(kind, out _);
            if (bytes <= 0 || elapsedMs <= 0) return;
            var instant = bytes / (double)elapsedMs;
            var sample = _speeds.GetOrAdd(kind, _ => new SpeedSample());
            lock (sample)
            {
                sample.Ema = sample.Samples == 0 ? instant : sample.Ema * 0.6 + instant * 0.4;
                sample.Samples++;
            }
        }

        /// <summary>探测失败 / 源被剔除：释放探测占位。</summary>
        public void ReleaseProbe(SourceKind kind) => _probing.TryRemove(kind, out _);

        private double SpeedOf(SourceKind kind) =>
            _speeds.TryGetValue(kind, out var sample) && sample.Samples > 0 ? sample.Ema : 0;

        public void Remove(SourceKind kind)
        {
            if (_preferred == kind) _preferred = null;
            ReleaseProbe(kind);
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