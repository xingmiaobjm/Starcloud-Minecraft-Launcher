// 并发下载引擎：多文件批量下载 + 多源分片竞速 + 重试 + 进度/速度/ETA 回调（对应旧 Rust download 模块）

using System.Diagnostics;
using System.Buffers;
using System.Net;
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
    /// <summary>整文件下载的读缓冲：512 KiB（原 80 KB 时每次系统调用只搬 80KB，满速时每秒上千次等待）</summary>
    private const int WholeFileBufferSize = 512 * 1024;

    /// <summary>字节遥测聚合窗口（与分片路径同一口径：10 Hz）</summary>
    private const int TelemetryWindowMs = 100;

    /// <summary>
    /// 元数据请求（版本清单 / 版本 JSON / 各类 API JSON）的单源超时。
    /// <para>为什么必须有：BMCLAPI 会把请求 302 给 P2P 节点，实测约六成节点「TCP 连上了但永不应答」
    /// （2026-09-22 复现：同一 URL 连发 5 次，3 次 25 秒零响应、2 次 1.3–1.9 秒成功）。
    /// 而 <c>_http.Timeout</c> 是无限（大文件需要），元数据若没有这层超时 + 换源，
    /// 撞上死节点就会永远转圈：无字节、无进度、无报错——用户看到的就是「点了下载一直转」。</para>
    /// </summary>
    internal static TimeSpan MetadataTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _http;
    /// <summary>直连客户端（绕开系统代理）：CurseForge 的 Forge CDN 在部分代理链路上会被回 403，见 FetchWholeAsync</summary>
    private readonly HttpClient _directHttp;
    private readonly DownloadSourceRegistry _sources;
    private readonly ChunkedFileDownloader _chunked;
    /// <summary>设置来源：本地复用要按当前 .minecraft 扫「同版本文件」</summary>
    private readonly Func<LauncherSettings> _settings;

    public DownloadService(Func<LauncherSettings> settings)
    {
        _settings = settings;
        var sockets = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 128, // 承载多源分片并发
            ConnectTimeout = TimeSpan.FromSeconds(10),   // 连不上就快换源（原来要等 20s 响应头超时）
            EnableMultipleHttp2Connections = true,       // HTTP/2 服务端会限单连接速率，多开连接才能并行提速
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        };
        // 总时长超时会误杀慢速大文件（速度 × 60s ≈ 24MB 处必然被掐死）→ 禁用，
        // 连接健康改由各读循环的 15 秒空闲超时负责（无数据即判死换源）
        // CurseForge CDN 自 2026-07-16 起要求带 x-api-key（否则 401）：挂在 handler 上，
        // 整文件 / 分片 / 探测所有路径一并覆盖，调用方一行不用改。
        _http = new HttpClient(new CurseForgeCdnAuthHandler(settings, sockets)) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Launcher");
        var directSockets = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 128,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            UseProxy = false,
        };
        _directHttp = new HttpClient(new CurseForgeCdnAuthHandler(settings, directSockets)) { Timeout = Timeout.InfiniteTimeSpan };
        _directHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Launcher");
        _sources = new DownloadSourceRegistry(settings);
        _chunked = new ChunkedFileDownloader(_http, _sources) { Log = msg => Log?.Invoke(msg) };
    }

    /// <summary>下载日志出口（「文件 → 源 → 字节数」）；默认 null = 零开销</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// P2P 对等文件源（设计：2026-10-14-p2p-download-design.md ③）：局域网同账号设备已有同
    /// SHA1 文件时网内拉取。App 层构造完成后注入（避免服务相互构造依赖）；null 或 PeerCount=0
    /// 时完全跳过，行为与从前逐字节一致。
    /// </summary>
    public Func<IPeerFileSource?>? PeerSource { get; set; }

    /// <summary>
    /// 互联网 P2P 对等源（设计：2026-10-22-cross-internet-p2p-rendezvous.md）：跨账号、跨网络，
    /// 走服务器发现 + UDP 打洞。排在局域网 P2P 之后、公网多源之前；null / PeerCount=0 时零开销。
    /// </summary>
    public Func<IPeerFileSource?>? InternetPeerSource { get; set; }

    /// <summary>
    /// 文件到手后的记账回调（路径, SHA1, 大小）：写进本地索引，供互联网 P2P 的 announce 使用。
    /// 回调异常不影响下载。
    /// </summary>
    public Action<string, string?, long>? FileObtained { get; set; }

    /// <summary>单文件下载（含重试），可选 SHA1 校验</summary>
    public Task DownloadFileAsync(
        string url, string destFile, string? sha1 = null, int maxRetries = 3,
        CancellationToken ct = default)
        => DownloadOneAsync(url, destFile, sha1, maxRetries, ct, 0, null);

    /// <summary>单文件下载（带字节进度回调；回调值是**本次增量**字节，需由调用方累加）</summary>
    public Task DownloadFileAsync(
        string url, string destFile, string? sha1, int maxRetries,
        Action<long> onBytes, CancellationToken ct = default)
        => DownloadOneAsync(url, destFile, sha1, maxRetries, ct, 0, onBytes);

    /// <summary>
    /// 单文件下载入口。CurseForge CDN 直链失败时，最后再试一次 MCIM 文件镜像
    /// （镜像只换主机、路径不变）：CDN 在国内可能被限速或不可达，而官方自 2026-07-16 起
    /// 又强制要求密钥，失败面比过去大。文件字节仍以官方为准——这里只是通道兜底，
    /// 且不给镜像发密钥（见 <see cref="CurseForgeCdn"/>）。
    /// </summary>
    private async Task DownloadOneAsync(
        string url, string destFile, string? sha1, int maxRetries, CancellationToken ct,
        long size, Action<long>? onBytes, SourceKind? preferred = null)
    {
        if (CurseForgeCdn.IsCdnUrl(url))
        {
            try
            {
                await DownloadFromAsync(url, destFile, sha1, maxRetries, ct, size, onBytes, preferred);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var mirror = CurseForgeCdn.Mirror(url);
                Log?.Invoke($"CurseForge CDN 下载失败（{ex.Message}）→ 改用 MCIM 文件镜像：{ShortName(url)}");
                await DownloadFromAsync(mirror, destFile, sha1, maxRetries, ct, size, onBytes, preferred);
                return;
            }
        }
        await DownloadFromAsync(url, destFile, sha1, maxRetries, ct, size, onBytes, preferred);
    }

    /// <summary>
    /// 单文件下载主路径：体积 ≥ 4 MiB 且该主机支持 Range → 多连接分片；
    /// 其余（小文件、不支持 Range 的源）→ 整文件下载（镜像源优先，官方兜底）。
    /// <para>2026-09-21 起分片不再只给 Mojang 域名：实测 CDN 单连接常被限到 1–3 MB/s，
    /// 而 4 条并行能拿到 3.1×，所以 Modrinth / CurseForge 这些同样支持 Range 的源也该并行。</para>
    /// </summary>
    private async Task DownloadFromAsync(
        string url, string destFile, string? sha1, int maxRetries, CancellationToken ct,
        long size, Action<long>? onBytes, SourceKind? preferred = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

        var mojang = DownloadSourceRegistry.IsMojangUrl(url);
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var range = mojang ? true : _sources.RangeSupport(host);

        // 体积未知：Mojang 走 HEAD；其它域名探测一次（顺带拿到真实大小与 Range 能力）。
        // 注意「能力已知支持分片、但调用方没给体积」也必须补探一次：否则 size 恒为 0，
        // 分片门槛（≥4MiB）判不出来，同一个主机的第二次下载就会静默退化成单连接整文件。
        // 已确认不支持 Range 的主机不再探测：它无论如何都走整文件，用不到体积。
        if (mojang)
        {
            if (size <= 0) size = await ProbeSizeAsync(url, ct);
        }
        else if (range is null)
        {
            var (probeSize, ranged) = await ProbeRangeAsync(url, ct);
            if (size <= 0) size = probeSize;
            range = ranged;
        }
        else if (range == true && size <= 0)
        {
            size = (await ProbeRangeAsync(url, ct)).Size; // 只补体积，不覆盖已记忆的能力
        }

        if (size >= ChunkedFileDownloader.Threshold && range == true)
        {
            try
            {
                // 字节增量同时送调度中心聚合（onBytes 是调用方的原始回调，保持兼容）
                await _chunked.DownloadAsync(url, destFile, sha1, size, maxRetries, b =>
                {
                    onBytes?.Invoke(b);
                    TaskCenterService.Instance.ReportDownloadBytes(b);
                }, ct, preferred);
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

        await DownloadWholeFileAsync(url, destFile, sha1, maxRetries, ct, size, onBytes, preferred);
    }

    /// <summary>
    /// 一次 Range 探测拿到两件事：真实文件大小（Content-Range 的 total）与主机是否支持分片。
    /// 只有明确的 206/200 才写记忆（网络故障不写，免得把好源记成不支持分片）。
    /// </summary>
    private async Task<(long Size, bool Range)> ProbeRangeAsync(string url, CancellationToken ct)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            conn.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                _sources.MarkRangeSupport(host, true);
                return (resp.Content.Headers.ContentRange?.Length ?? 0, true);
            }
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                _sources.MarkRangeSupport(host, false); // 无视 Range 直接给整文件
                return (resp.Content.Headers.ContentLength ?? 0, false);
            }
            return (0, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (0, false); // 探测失败不影响下载本身：按整文件路径走
        }
    }

    /// <summary>整文件下载（不分片）：按竞速池顺序换源重试，官方恒为兜底</summary>
    private async Task DownloadWholeFileAsync(
        string url, string destFile, string? sha1, int maxRetries, CancellationToken ct, long size,
        Action<long>? onBytes, SourceKind? preferred = null)
    {
        // 非 Mojang 域名没有任何镜像能改写它 → 池里只留官方一项（多连接并行由分片 worker 负责）
        var pool = _sources.RacingPoolFor(url).ToList();
        // 批量下载的文件分摊：整文件路径也先走分配到的源，失败再按池顺序兜底
        if (preferred is { } pin && pool.Remove(pin)) pool.Insert(0, pin);
        // 与分片路径同一口径：重试额度至少覆盖池里全部源，保证官方兜底在预算内被轮到
        var attempts = Math.Max(Math.Max(1, maxRetries), pool.Count);
        for (int attempt = 1; ; attempt++)
        {
            var kind = pool[(attempt - 1) % pool.Count];
            // 该主机刚被限流过 → 先等冷却，否则这次请求也只是再撞一次 429
            var cooldown = ParallelGovernor.CooldownFor(
                DownloadSourceRegistry.HostOf(_sources.Rewrite(url, kind)));
            if (cooldown > TimeSpan.Zero)
            {
                Log?.Invoke($"{DownloadSourceRegistry.Name(kind)} 正在限流冷却，等待 {cooldown.TotalSeconds:F0}s");
                await Task.Delay(cooldown, ct);
            }
            try
            {
                await FetchWholeAsync(_sources.Rewrite(url, kind), destFile, sha1, ct, onBytes);
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

    /// <summary>
    /// 整文件请求入口：CurseForge 的 Forge CDN 先直连、失败再走系统代理。
    /// <para>实测（2026-09-21）：同一 URL 经 Clash 系统代理取回 <b>403</b>、直连取回 200，
    /// 而国内不少用户默认开着代理；反过来纯直连在个别网络下又可能不通。
    /// 两个方向都试，才不会因为"用户开着代理"或"用户没开代理"各挂一半。</para>
    /// </summary>
    private async Task FetchWholeAsync(string url, string destFile, string? sha1, CancellationToken ct,
        Action<long>? onBytes)
    {
        if (IsForgeCdn(url))
        {
            try
            {
                await FetchWholeOnceAsync(_directHttp, url, destFile, sha1, ct, onBytes);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Forge CDN 直连失败，改走系统代理重试：{Path.GetFileName(destFile)}（{ex.Message}）");
            }
        }
        await FetchWholeOnceAsync(_http, url, destFile, sha1, ct, onBytes);
    }

    /// <summary>CurseForge 的文件分发域名（edge / mediafilez 等 *.forgecdn.net）</summary>
    private static bool IsForgeCdn(string url) =>
        url.Contains("forgecdn.net", StringComparison.OrdinalIgnoreCase);

    /// <summary>单次整文件请求：流式写 .sc-tmp（字节增量同时送调度中心与调用方回调）→ SHA1 校验 → 落盘。
    /// 空闲 15 秒无数据即判定连接卡死抛异常（外层换源重试）；重试时对 .sc-tmp 做 Range 断点续传，
    /// 避免「下载到一半卡 60 秒超时后进度归零重来」。</summary>
    private async Task FetchWholeOnceAsync(
        HttpClient client, string url, string destFile, string? sha1, CancellationToken ct,
        Action<long>? onBytes)
    {
        var tmp = destFile + ".sc-tmp";
        var resume = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (resume > 0)
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resume, null);
        HttpResponseMessage resp;
        try
        {
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            conn.CancelAfter(TimeSpan.FromSeconds(20)); // 连接 + 响应头超时
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("连接 20 秒无响应");
        }

        if (resume > 0 && resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 416 = 本地已下满整个文件（上次恰好在收尾时中断）→ 直接进入校验
            resp.Dispose();
            FinishWhole(destFile, tmp, sha1);
            NoteObtained(destFile, sha1);
            return;
        }
        if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            // 整文件请求也会被限流：按主机记下来，后续请求先等冷却，别再撞
            var host = DownloadSourceRegistry.HostOf(url);
            ParallelGovernor.ReportThrottled(host);
            resp.Dispose();
            throw new IOException($"{host} 限流（HTTP {(int)resp.StatusCode}）");
        }
        resp.EnsureSuccessStatusCode();

        var partial = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!partial) resume = 0; // 服务器不支持 Range → 从头下
        await using (var fs = new FileStream(tmp, resume > 0 ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buf = ArrayPool<byte>.Shared.Rent(WholeFileBufferSize);
            try
            {
                // 空闲看门狗一条流一个（原来每次读都新建 CTS，见 IdleTimeout.cs）
                using var idle = new IdleTimeout(ct, 15_000);
                long pending = 0;
                var lastFlush = Environment.TickCount64;
                while (true)
                {
                    int read;
                    try
                    {
                        read = await src.ReadAsync(buf.AsMemory(0, buf.Length), idle.Token);
                        if (read == 0) break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException("连接 15 秒无数据，已保留断点（重试将从续传开始）");
                    }
                    idle.Touch();

                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    pending += read;
                    // 10 Hz 聚合上报：调度中心不需要每条 512KB 都算一次速度
                    if (Environment.TickCount64 - lastFlush >= TelemetryWindowMs)
                    {
                        TaskCenterService.Instance.ReportDownloadBytes(pending);
                        onBytes?.Invoke(pending); // 进度条的速度靠它，缺了这条单连接下载全程停在 0
                        pending = 0;
                        lastFlush = Environment.TickCount64;
                    }
                }
                if (pending > 0)
                {
                    TaskCenterService.Instance.ReportDownloadBytes(pending);
                    onBytes?.Invoke(pending);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        FinishWhole(destFile, tmp, sha1);
        NoteObtained(destFile, sha1);
        ParallelGovernor.ReportSuccess(DownloadSourceRegistry.HostOf(url)); // 稳定可用 → 该主机下次可以多开几条
    }

    /// <summary>把到手的文件记进互联网 P2P 的本地索引；回调异常绝不影响下载。</summary>
    private void NoteObtained(string destFile, string? sha1)
    {
        if (FileObtained is null || string.IsNullOrEmpty(sha1)) return;
        try
        {
            var size = File.Exists(destFile) ? new FileInfo(destFile).Length : 0;
            FileObtained(destFile, sha1, size);
        }
        catch
        {
            // 记账失败只是少一个可分享文件
        }
    }

    /// <summary>重载：显式给出大小（本地复用等场景已经知道期望大小）。</summary>
    private void NoteObtained(string destFile, string? sha1, long size)
    {
        if (FileObtained is null || string.IsNullOrEmpty(sha1)) return;
        try { FileObtained(destFile, sha1, size); }
        catch { }
    }

    /// <summary>SHA1 校验（如提供）后把 .sc-tmp 落盘为最终文件</summary>
    private static void FinishWhole(string destFile, string tmp, string? sha1)
    {
        if (!string.IsNullOrEmpty(sha1))
        {
            using var read = File.OpenRead(tmp);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(read));
            if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SHA1 校验失败: {Path.GetFileName(destFile)}");
        }
        File.Move(tmp, destFile, overwrite: true);
    }

    /// <summary>探测远端文件大小（HEAD，逐源尝试）；失败返回 0 = 按未知处理</summary>
    private async Task<long> ProbeSizeAsync(string url, CancellationToken ct)
    {
        foreach (var kind in _sources.RacingPoolFor(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, _sources.Rewrite(url, kind));
                using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
                conn.CancelAfter(MetadataTimeout);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
                if (resp.IsSuccessStatusCode)
                {
                    _sources.RecordSuccess(kind);
                    return resp.Content.Headers.ContentLength ?? 0;
                }
                // 4xx（如不支持 HEAD）不代表源不健康：换下一个源，不记失败
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 超时 / 网络故障：记为一次失败（连续两次即降级），换下一个源
                _sources.RecordFailure(kind);
            }
        }
        return 0; // 探测失败不影响下载本身：按未知大小走整文件路径
    }

    /// <summary>字符串内容下载（版本清单 / 版本 JSON / 各类 API JSON）</summary>
    public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
        GetStringAsync(url, null, ct);

    /// <summary>
    /// 字符串内容下载（可带附加请求头，如 CurseForge 官方 API 的 `x-api-key`）。
    /// <para>逐源尝试：镜像优先、官方兜底，单个源 <see cref="MetadataTimeout"/> 内拿不到响应就换下一个。
    /// 非 Mojang 域名不会被镜像改写（竞速池只有一项），仍然是这一条有界请求。</para>
    /// <param name="trackSourceHealth">是否把成败记进源健康度。非 Mojang 的第三方接口
    /// （CurseForge 等）应传 false：它们的超时与 Mojang 镜像健康度无关，
    /// 记进去会把原版下载的源排序带偏。</param>
    /// </summary>
    public async Task<string> GetStringAsync(string url,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct = default,
        bool trackSourceHealth = true)
    {
        var pool = _sources.RacingPoolFor(url);
        var mojang = DownloadSourceRegistry.IsMojangUrl(url);
        // 第三方接口（Fabric/Modrinth/CurseForge…）与 Mojang 镜像健康度无关，不能记进去，
        // 否则一次第三方超时会把原版下载的源排序带偏。
        var recordHealth = trackSourceHealth && mojang;
        Exception? last = null;
        // 池里只有一项 = 第三方元数据（Fabric/Quilt/Modrinth…），跨境链路偶发超时/526 很常见，
        // 多给一次机会；Mojang 镜像池本身有多个源，保持一次即换源，避免拖慢失败切换。
        var perSourceAttempts = pool.Count == 1 ? 2 : 1;
        for (var i = 0; i < pool.Count; i++)
        {
            var kind = pool[i];
            for (var attempt = 1; attempt <= perSourceAttempts; attempt++)
            {
                try
                {
                    var text = await FetchStringAsync(_http, _sources.Rewrite(url, kind), headers,
                        MetadataTimeout, ct);
                    // 镜像的"姿势不对"要当源故障处理：RainYun 之类会把站点页面（HTML）当成内容返回。
                    if (kind != SourceKind.Official && LooksLikeHtml(text))
                        throw new IOException("返回的是站点页面（HTML），不是数据");
                    if (recordHealth) _sources.RecordSuccess(kind);
                    return text;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex is OperationCanceledException
                        ? new TimeoutException($"{MetadataTimeout.TotalSeconds:F0} 秒无响应")
                        : ex;
                    if (recordHealth) _sources.RecordFailure(kind);

                    if (attempt < perSourceAttempts)
                    {
                        Log?.Invoke($"{DownloadSourceRegistry.Name(kind)} 元数据请求失败" +
                                    $"（{last.Message}），重试 {attempt + 1}/{perSourceAttempts}：{ShortName(url)}");
                        try { await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct); }
                        catch (OperationCanceledException) { throw; }
                        continue;
                    }
                    if (i < pool.Count - 1)
                        Log?.Invoke($"{DownloadSourceRegistry.Name(kind)} 元数据请求失败" +
                                    $"（{last.Message}）→ 换下一个源：{ShortName(url)}");
                }
            }
        }

        // 第三方元数据只剩一条路时，系统代理/加速器常把小请求掐掉：最后绕开代理直连再试一次。
        if (!mojang)
        {
            try
            {
                var text = await FetchStringAsync(_directHttp, url, headers, MetadataTimeout, ct);
                if (LooksLikeHtml(text))
                    throw new IOException("返回的是站点页面（HTML），不是数据");
                Log?.Invoke($"直连兜底成功：{ShortName(url)}");
                return text;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                Log?.Invoke($"直连兜底失败（{ex.Message}）：{ShortName(url)}");
            }
        }

        throw new IOException(
            $"元数据下载失败（{Math.Max(pool.Count, 1)} 个源都没拿到响应）：{DisplayUrl(url)}" +
            (last?.Message is { Length: > 0 } why ? $"（{why}）" : ""), last);
    }

    /// <summary>单次 GET 文本；超时用连接级 CTS（大文件下载不走这里）。</summary>
    private static async Task<string> FetchStringAsync(HttpClient http, string url,
        IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
            foreach (var (key, value) in headers)
                req.Headers.TryAddWithoutValidation(key, value);
        using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        conn.CancelAfter(timeout);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(conn.Token);
    }

    /// <summary>报错用：带主机与路径，比只有最后一段（json）有用得多。</summary>
    private static string DisplayUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath : url;

    /// <summary>POST JSON 并返回响应文本（Starcloud ID 接口，不做源改写）</summary>
    public Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken ct = default) =>
        PostJsonAsync(url, jsonBody, null, ct);

    /// <summary>POST JSON 并返回响应文本（可带自定义请求头；30 秒超时保护）</summary>
    public async Task<string> PostJsonAsync(
        string url, string jsonBody, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);

        // 同元数据口径：接口卡住也要有界，别把调用方（Nova / 登录 / 整合包导出）挂死
        using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        conn.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await _http.SendAsync(request, conn.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(conn.Token);
    }

    /// <summary>URL 末段（日志里认得出是哪个文件即可）</summary>
    private static string ShortName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var name = Path.GetFileName(uri.AbsolutePath);
        return string.IsNullOrEmpty(name) ? uri.Host : name;
    }

    /// <summary>内容是不是 HTML 页面（首个非空白字符是 '&lt;'；合法 JSON 永远不会这样开头）</summary>
    private static bool LooksLikeHtml(string text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            return c == '<';
        }
        return false;
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
            {
                // 已经装好的文件同样进互联网 P2P 索引：manifest 给了 SHA1 与大小，供之前还会整文件复算，
                // 所以这里只按大小记账是安全的——哈希不符会拒发，不会把别人的文件当成这份。
                NoteObtained(f.Dest, f.Sha1, f.Size);
                continue;
            }
            pending.Add(f);
        }

        // 本地复用：同一份文件别的实例里已经有了（同版本 client jar / 同一个 mod），
        // 直接复制，省掉整份下载。同版本 jar 是十几 MB 量级，这一项最省时间。
        var reused = 0;
        try
        {
            var reuse = LocalFileReuse.Scan(_settings().DotMinecraft);
            reuse.AddLibraryDirectory(LauncherPaths.LibraryDir); // 资源库里留着的文件同样能复用
            foreach (var f in pending.ToList())
            {
                if (!reuse.TryProvide(f.Dest, f.Sha1, f.Size, out var from)) continue;
                pending.Remove(f);
                reused++;
                NoteObtained(f.Dest, f.Sha1, f.Size);
                LauncherLog.Write("download",
                    $"本地复用 {Path.GetFileName(f.Dest)} ← {from}");
            }
        }
        catch (Exception ex)
        {
            // 复用只是加速手段：出错就当没有，照常下载
            LauncherLog.Write("download", $"本地复用不可用，按原计划下载：{ex.Message}");
        }
        if (reused > 0) stage = $"{stage}（本地复用 {reused} 个）";

        // P2P 对等复用（第三个源）：局域网同账号设备已有同 SHA1 文件 → 网内拉取。
        // 只认 SHA1（与本地复用同一条安全线）；无哈希的条目不参与；失败逐条静默回退公网。
        var p2pHits = 0;
        var peer = PeerSource?.Invoke();
        if (peer is { PeerCount: > 0 })
        {
            foreach (var f in pending.ToList())
            {
                if (string.IsNullOrEmpty(f.Sha1)) continue; // 没哈希可比：不冒险
                try
                {
                    // 走异步版：同步版会在当前线程上阻塞等待对端（最坏 20 多秒），
                    // 而这里跑在 UI 线程上 —— 阻塞就是界面冻结。
                    if (!await peer.TryProvideAsync(f.Dest, f.Sha1, f.Size, ct).ConfigureAwait(false)) continue;
                    pending.Remove(f);
                    p2pHits++;
                    NoteObtained(f.Dest, f.Sha1, f.Size);
                    LauncherLog.Write("download",
                        $"传递优化复用 {Path.GetFileName(f.Dest)} ← 局域网对端");
                }
                catch
                {
                    // 单条 P2P 失败不影响下载：照常走公网
                }
            }
            if (p2pHits > 0) stage = $"{stage}（传递优化 {p2pHits} 个）";
        }

        // 互联网 P2P（第四个源）：跨账号、跨网络，走发现服务 + UDP 打洞。
        // 单文件预算 9 秒，任何失败都静默回退公网；只对有 SHA1 的条目尝试。
        var internetHits = 0;
        var internet = InternetPeerSource?.Invoke();
        if (internet is { PeerCount: > 0 })
        {
            (internet as P2P.InternetP2PService)?.BeginPass();
            foreach (var f in pending.ToList())
            {
                if (string.IsNullOrEmpty(f.Sha1)) continue;
                try
                {
                    if (!await internet.TryProvideAsync(f.Dest, f.Sha1, f.Size, ct).ConfigureAwait(false)) continue;
                    pending.Remove(f);
                    internetHits++;
                    NoteObtained(f.Dest, f.Sha1, f.Size);
                    LauncherLog.Write("download",
                        $"高级传递优化复用 {Path.GetFileName(f.Dest)} ← 互联网对端");
                }
                catch
                {
                    // 单条失败不影响下载：照常走公网
                }
            }
            if (internetHits > 0) stage = $"{stage}（高级传递优化 {internetHits} 个）";
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
            var raw = Interlocked.Read(ref doneBytes) + lastReported;
            // 换源重试 / 断点续传可能把同一段字节再报一遍；总量已知就夹住，免得进度冲过 100%
            var doneNow = totalBytes > 0 ? Math.Min(raw, totalBytes) : raw;
            var speed = elapsed > 0.5 ? doneNow / elapsed : 0;
            double? eta = speed > 1 && totalBytes > doneNow ? (totalBytes - doneNow) / speed : null;
            progress.Report(new BatchProgress(stage, done + doneFiles, files.Count, doneNow,
                Math.Max(totalBytes, 1), speed, eta));
        }

        if (pending.Count == 0)
        {
            doneBytes = totalBytes;
            // files.Count 已经在 done 里计过（= 已完成 + 复用），这里不能再加一遍，否则进度显示 7300/3650
            Report(force: true);
            return;
        }

        // 多文件并发时把不同文件分摊到不同源：单文件靠分片并行，多文件再叠一层"源级并行"，
        // 避免所有文件都挤在同一个最快源上（其余镜像闲着）。只有一个文件 / 一个源时不指定。
        var assignPool = new List<SourceKind>();
        if (pending.Count > 1)
        {
            foreach (var kind in _sources.RacingPool())
                if (!_sources.IsDegraded(kind)) assignPool.Add(kind);
            if (assignPool.Count == 0) assignPool.Add(SourceKind.Official);
        }
        var assignCursor = 0;
        if (assignPool.Count > 1)
        {
            LauncherLog.Write("download",
                $"批量分摊下载源：{string.Join(" / ", assignPool.Select(DownloadSourceRegistry.Name))}" +
                $"（{pending.Count} 个文件）");
        }

        // 大文件优先：先下 client.jar / 资源包这类大件，免得它们被排在队尾、
        // 晚拿到并发额度；小文件随后补位。
        var ordered = pending
            .OrderByDescending(f => f.Size)
            .ToList();

        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = ordered.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // 分摊时按文件大小错开：同一源不要连着接两个大文件，
                // 否则那个源被两个大文件压满，其余源闲着 —— 整体速度取决于最慢的那个源。
                var preferred = assignPool.Count > 1 && DownloadSourceRegistry.IsMojangUrl(f.Url)
                    ? assignPool[(int)((uint)Interlocked.Increment(ref assignCursor) %
                                        (uint)assignPool.Count)]
                    : (SourceKind?)null;
                long reported = 0;
                await DownloadOneAsync(f.Url, f.Dest, f.Sha1, maxRetries, ct, f.Size,
                    n =>
                    {
                        Interlocked.Add(ref doneBytes, n);
                        Interlocked.Add(ref reported, n);
                        Report();
                    }, preferred);
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

    /// <summary>获取 Java 主版本要求的 Adoptium 下载地址（os/arch 取当前平台；记忆约束：解析 binary 字段）</summary>
    public Task<string> GetAdoptiumDownloadUrlAsync(int majorVersion, CancellationToken ct = default) =>
        GetAdoptiumDownloadUrlAsync(majorVersion, PlatformInfo.AdoptiumOs, PlatformInfo.AdoptiumArch, ct);

    /// <summary>os/arch 显式传入的重载（跨平台单测用）；Windows 只发 .zip，unix 只发 .tar.gz</summary>
    public async Task<string> GetAdoptiumDownloadUrlAsync(int majorVersion, string os, string architecture,
        CancellationToken ct = default)
    {
        var url = $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot?architecture={architecture}&image_type=jdk&os={os}&vendor=eclipse";
        var json = await GetStringAsync(url, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var expectedExt = os == "windows" ? ".zip" : ".tar.gz";
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("binary", out var binary)) continue;
            if (!binary.TryGetProperty("package", out var package)) continue;
            if (!package.TryGetProperty("link", out var link)) continue;
            var linkStr = link.GetString();
            if (!string.IsNullOrEmpty(linkStr) &&
                linkStr.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
                return linkStr;
        }
        throw new InvalidOperationException($"Adoptium 未返回可用的 Java {majorVersion} 下载地址");
    }
}
