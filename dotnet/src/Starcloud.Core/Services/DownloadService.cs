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

    // 保留 handler 引用：代理是 handler 级配置，改设置后要能就地重建连接池
    private readonly SocketsHttpHandler _httpHandler;
    private readonly SocketsHttpHandler _directHandler;

    public DownloadService(Func<LauncherSettings> settings)
    {
        _settings = settings;
        var sockets = _httpHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 128, // 承载多源分片并发
            ConnectTimeout = TimeSpan.FromSeconds(10),   // 连不上就快换源（原来要等 20s 响应头超时）
            EnableMultipleHttp2Connections = true,       // HTTP/2 服务端会限单连接速率，多开连接才能并行提速
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        };
        ApplyProxy(sockets, settings());
        // 总时长超时会误杀慢速大文件（速度 × 60s ≈ 24MB 处必然被掐死）→ 禁用，
        // 连接健康改由各读循环的 15 秒空闲超时负责（无数据即判死换源）
        // CurseForge CDN 自 2026-07-16 起要求带 x-api-key（否则 401）：挂在 handler 上，
        // 整文件 / 分片 / 探测所有路径一并覆盖，调用方一行不用改。
        _http = new HttpClient(new CurseForgeCdnAuthHandler(settings, sockets)) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Launcher");
        var directSockets = _directHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 128,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            // 局域网 / P2P 通道本来就是内网通信，走代理反而连不上：保持直连
            UseProxy = false,
        };
        _directHttp = new HttpClient(new CurseForgeCdnAuthHandler(settings, directSockets)) { Timeout = Timeout.InfiniteTimeSpan };
        _directHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Launcher");
        _sources = new DownloadSourceRegistry(settings);
        _chunked = new ChunkedFileDownloader(_http, _sources) { Log = msg => Log?.Invoke(msg) };
    }

    /// <summary>
    /// 按设置给 handler 装代理策略：
    /// system（默认）= 跟随系统代理（SocketsHttpHandler 默认行为）；
    /// direct = 忽略系统代理直连；custom = 用用户填的代理地址。
    /// 代理地址非法时退回跟随系统，不因为一个填错的值把下载整死。
    /// </summary>
    private static void ApplyProxy(SocketsHttpHandler handler, LauncherSettings settings)
    {
        switch (settings.DownloadProxyMode?.Trim().ToLowerInvariant())
        {
            case "direct":
                handler.UseProxy = false;
                break;
            case "custom":
                if (Uri.TryCreate(settings.DownloadProxyUrl?.Trim(), UriKind.Absolute, out var proxyUri) &&
                    (proxyUri.Scheme == Uri.UriSchemeHttp || proxyUri.Scheme == Uri.UriSchemeHttps))
                {
                    handler.Proxy = new System.Net.WebProxy(proxyUri);
                    handler.UseProxy = true;
                }
                else
                {
                    LauncherLog.Write("download",
                        $"自定义代理地址不可用（{settings.DownloadProxyUrl}），已退回跟随系统代理");
                }
                break;
            // system / 未设置：保持默认（UseProxy=true + Proxy=null → HttpClient.DefaultProxy）
        }
    }

    /// <summary>
    /// 设置改动后立即生效：代理是 handler 级配置，改完不重建连接池就得重启启动器。
    /// 只作用于公网下载通道——直连通道（局域网 / P2P）恒不走代理。
    /// </summary>
    public void ApplyProxySettings() => ApplyProxy(_httpHandler, _settings());

    /// <summary>下载日志出口（「文件 → 源 → 字节数」）；默认 null = 零开销</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// P2P 对等文件源（设计：2026-10-14-p2p-download-design.md ③）：局域网同账号设备已有同
    /// SHA1 文件时网内拉取。App 层构造完成后注入（避免服务相互构造依赖）；null 或 PeerCount=0
    /// 时完全跳过，行为与从前逐字节一致。
    /// </summary>
    /// <summary>源注册表（竞速池 / 健康度 / 远程开关）：给启动链设置 S3 镜像参与用。</summary>
    public DownloadSourceRegistry Sources => _sources;

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

        LauncherLog.Debug("download",
            $"决策 {Path.GetFileName(destFile)}：{size / 1024.0:F0} KB，"
            + (size >= ChunkedFileDownloader.Threshold && range == true ? "分片竞速" : "整文件")
            + $"（主机 {host}，Range={range}）");

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

    /// <summary>
    /// 批量下载开始前，对全部镜像做一次并行探速（读 256 KB 计时），结果直接进健康度：
    /// 连不上的直接降级，能用的按实测吞吐排序。这样「把文件分摊到多个源」才有依据——
    /// 否则会按声明顺序把文件分给不通的镜像，失败后又全部涌到最快的那个源上。
    /// 每个进程只做一次（分片路径的预检共用同一个标记）。
    /// </summary>
    private async Task<HashSet<SourceKind>> WarmupSourcesAsync(string sampleUrl, CancellationToken ct)
    {
        var ranged = new HashSet<SourceKind> { SourceKind.Official };   // 官方一直支持 Range
        if (!_sources.WarmupNeeded) return ranged;
        _sources.MarkWarmupDone();

        // 官方也要探：分摊时靠「实测速度」决定给谁分文件，官方没有样本会被当成 0 分，
        // 结果整批文件全分给了某个看起来有样本的慢镜像（实测 auto 反而比仅官方慢 44%）。
        var probes = _sources.RacingPool().ToList();
        if (probes.Count == 0) return ranged;

        const int sampleBytes = 256 * 1024;
        await Task.WhenAll(probes.Select(async kind =>
        {
            string target;
            if (kind == SourceKind.Official)
            {
                target = sampleUrl;   // 官方不需要改写
            }
            else
            {
                target = _sources.Rewrite(sampleUrl, kind);
                if (string.Equals(target, sampleUrl, StringComparison.OrdinalIgnoreCase)) return;
            }
            var host = DownloadSourceRegistry.HostOf(target);
            try
            {
                // 先用「版本清单」探活：每个镜像都该有这个文件，拿它区分两件事——
                // ① 源本身失效（整站 404，如上海交大镜像已下线）→ 直接降级，别让它占着分摊名额；
                // ② 只是这个文件没缓存 → 不降级（正常回落）。
                // 少了这一步，失效源会永远留在池里：每个被分给它的文件都要 404 一次再换源。
                if (!await ProbeSourceAliveAsync(kind, ct)) return;
                // 认 Range 的源才进「大文件分摊」名单（探活已把能力写进 registry）
                if (_sources.RangeSupport(DownloadSourceRegistry.HostOf(target)) == true)
                    lock (ranged) ranged.Add(kind);
                using var req = new HttpRequestMessage(HttpMethod.Get, target);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, sampleBytes - 1);
                req.Version = HttpVersion.Version11;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
                conn.CancelAfter(TimeSpan.FromSeconds(4));
                var sw = Stopwatch.StartNew();
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode is 404 or 410)
                    {
                        // 这个文件它没有，不代表源坏：只记 404，不降级
                        _sources.RecordNotFound(kind);
                    }
                    else
                    {
                        _sources.MarkDegraded(kind);
                    }
                    LauncherLog.Debug("download", $"源探速：{DownloadSourceRegistry.Name(kind)} 返回 {(int)resp.StatusCode}");
                    return;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(conn.Token);
                var buffer = new byte[32 * 1024];
                long read = 0;
                while (read < sampleBytes)
                {
                    var n = await stream.ReadAsync(buffer, conn.Token);
                    if (n <= 0) break;
                    read += n;
                }
                sw.Stop();
                if (read > 0 && sw.ElapsedMilliseconds > 0)
                {
                    _sources.RecordSpeed(kind, read, sw.ElapsedMilliseconds);
                    ParallelGovernor.ReportSuccess(host);
                    if (resp.StatusCode == HttpStatusCode.PartialContent)
                        lock (ranged) ranged.Add(kind);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // 连不上/DNS/SSL：这个源在本机网络里用不了，直接降级（参与分摊只会拖累）
                _sources.MarkDegraded(kind);
                LauncherLog.Debug("download",
                    $"源探速：{DownloadSourceRegistry.Name(kind)} 不可用（{ex.Message}）");
            }
        }));
        return ranged;
    }

    /// <summary>
    /// 源探活 + Range 能力：用版本清单这种「每个镜像都该有」的文件判断源是否还在服务。
    /// 整站 404 / 连接失败 → 降级（不再参与分摊）；
    /// 另外看它认不认 Range —— 不认的源只能整文件下载，不该被分摊到大文件上
    /// （分片必然失败，然后再退回最快的那个源，等于所有流量又挤到一个源）。
    /// </summary>
    private async Task<bool> ProbeSourceAliveAsync(SourceKind kind, CancellationToken ct)
    {
        const string metaUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        var target = _sources.Rewrite(metaUrl, kind);
        if (string.Equals(target, metaUrl, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            conn.CancelAfter(TimeSpan.FromSeconds(2.5));   // 不通就快失败：预检是纯开销
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, conn.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _sources.MarkDegraded(kind);
                LauncherLog.Write("download",
                    $"源探活：{DownloadSourceRegistry.Name(kind)} 清单返回 {(int)resp.StatusCode}（整站不可用），本次不参与分摊");
                return false;
            }
            // 206 = 认 Range（可参与分片分摊）；200 = 只能整文件
            var ranged = resp.StatusCode == HttpStatusCode.PartialContent;
            _sources.MarkRangeSupport(DownloadSourceRegistry.HostOf(target), ranged);
            if (!ranged)
                LauncherLog.Write("download",
                    $"源探活：{DownloadSourceRegistry.Name(kind)} 不回 Range，只用于整文件下载");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _sources.MarkDegraded(kind);
            LauncherLog.Write("download",
                $"源探活：{DownloadSourceRegistry.Name(kind)} 不可达（{ex.Message}），本次不参与分摊");
            return false;
        }
    }

    /// <summary>
    /// 是否属于「这个源在本机网络里根本连不上」的硬故障：
    /// SSL 握手失败 / DNS 解析失败 / 连接被拒 / 连接超时。
    /// 与「请求发出去了但失败」区分开——前者一次就该把源降权，后者才需要累计两次。
    /// </summary>
    private static bool IsHardConnectFailure(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("handshake", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("拒绝", StringComparison.Ordinal)
            || message.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("无法连接", StringComparison.Ordinal)
            || ex is System.Net.Http.HttpRequestException;
    }

    /// <summary>
    /// 主源全部失败后的最后一张牌：把 Starcloud 加速（S3）追加进这个文件的源池。
    /// <para>用户定调（2026-11-02）：S3 只做**极端情况**的辅助 —— 常规下载由镜像与官方扛，
    /// 只有它们在这个文件上全试过还失败，才请它出来。返回 false 表示已经没牌可打。</para>
    /// </summary>
    private bool AppendAuxiliary(List<SourceKind> pool)
    {
        if (!_sources.StarcloudMirrorEnabled) return false;
        if (pool.Contains(SourceKind.Starcloud)) return false;
        if (_sources.IsDegraded(SourceKind.Starcloud)) return false;
        pool.Add(SourceKind.Starcloud);
        _sources.UnlockAuxiliary();
        LauncherLog.Write("download", "主源全部失败，本文件改用 Starcloud 加速兜底");
        Log?.Invoke("主源全部失败，改用 Starcloud 加速兜底");
        return true;
    }

    /// <summary>
    /// 整文件下载（不分片）：按竞速池顺序换源重试，官方恒为兜底</summary>
    private async Task DownloadWholeFileAsync(
        string url, string destFile, string? sha1, int maxRetries, CancellationToken ct, long size,
        Action<long>? onBytes, SourceKind? preferred = null)
    {
        // 非 Mojang 域名没有任何镜像能改写它 → 池里只留官方一项（多连接并行由分片 worker 负责）
        var pool = _sources.RacingPoolFor(url).ToList();
        // 批量下载的文件分摊：整文件路径也先走分配到的源，失败再按池顺序兜底
        if (preferred is { } pin && pool.Remove(pin)) pool.Insert(0, pin);
        // 官方不再无条件垫底：预热之后官方往往就是实测最快的源，按声明顺序让它垫底
        // 意味着每个小文件都要先白撞一轮 404 镜像。按实测分数重排（官方参与排序），
        // 分数并列 / 全部未知时保持原顺序 —— 那时官方仍是最后的兜底。
        if (preferred is null && pool.Count > 1)
        {
            pool = pool.Select((k, i) => (k, i))
                .OrderBy(x => _sources.IsDegraded(x.k) ? 1 : 0)
                .ThenBy(x => _sources.InNotFoundCooldown(x.k) ? 1 : 0)
                .ThenByDescending(x => _sources.ScoreOf(x.k))
                .ThenBy(x => x.i)
                .Select(x => x.k)
                .ToList();
        }

        // 选源规则：**每个源最多试一次**，全试过才算山穷水尽。
        // 不能按固定次数轮询：404（文件未缓存）不算失败也不摘源，按次数轮询会
        // 反复撞同一个 404 镜像、把官方挤出击额度之外——实测 8 个官方能下的文件
        // 被误判成「主源全部失败」，白白烧了兜底源的带宽。
        var tried = new HashSet<SourceKind>();
        var attempt = 0;
        while (true)
        {
            attempt++;
            var untouched = pool.Where(k => !tried.Contains(k)).ToList();
            if (untouched.Count == 0)
            {
                // 真·极端情况：主源（含官方）在这份文件上全试过仍失败 → 请兜底源
                if (ct.IsCancellationRequested || !AppendAuxiliary(pool))
                    throw new IOException($"全部下载源均不可用: {Path.GetFileName(destFile)}");
                continue;   // untouched 现在只剩刚追加的兜底源
            }

            // 该主机刚被限流过 → 有没在冷却的源就直接换它，**绝不干等**
            //（等冷却把整批拖住是分片路径修过的老毛病：一个文件撞上 429，整批 6 秒变 248 秒）。
            // 只有还没试过的源全都在冷却时，才等最短的那个。
            var kind = untouched[0];
            var cooldown = ParallelGovernor.CooldownFor(
                DownloadSourceRegistry.HostOf(_sources.Rewrite(url, kind)));
            if (cooldown > TimeSpan.Zero)
            {
                var ready = untouched.Where(k => ParallelGovernor.CooldownFor(
                    DownloadSourceRegistry.HostOf(_sources.Rewrite(url, k))) == TimeSpan.Zero).ToList();
                if (ready.Count > 0)
                {
                    kind = ready[0];
                }
                else
                {
                    var shortest = untouched.Min(k => ParallelGovernor.CooldownFor(
                        DownloadSourceRegistry.HostOf(_sources.Rewrite(url, k))));
                    Log?.Invoke($"{DownloadSourceRegistry.Name(kind)} 正在限流冷却，" +
                                $"未试过的源都在冷却，等待 {shortest.TotalSeconds:F0}s");
                    await Task.Delay(shortest, ct);
                }
            }
            tried.Add(kind);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await FetchWholeAsync(_sources.Rewrite(url, kind), destFile, sha1, ct, onBytes);
                _sources.RecordSuccess(kind);
                // 实测吞吐必须回填：分摊权重逐文件按分数现算，整文件不回填的话，
                // 镜像的预热样本（256KB，虚高）会一路有效到批次结束——
                // 实测 10 个小文件全被一个实际慢 5 倍的镜像拖走（自动比仅官方慢 50%）。
                sw.Stop();
                if (size > 0 && sw.ElapsedMilliseconds > 0)
                    _sources.RecordSpeed(kind, size, sw.ElapsedMilliseconds);
                LauncherLog.Debug("download",
                    $"完成 {Path.GetFileName(destFile)} ← {DownloadSourceRegistry.Name(kind)}"
                    + $"，第 {attempt} 次尝试，{size / 1024.0:F0} KB，{sw.ElapsedMilliseconds} ms"
                    + (size > 0 && sw.ElapsedMilliseconds > 0
                        ? $"，{size / 1024.0 / Math.Max(1, sw.ElapsedMilliseconds):F1} MB/s"
                        : ""));
                if (size >= ChunkedFileDownloader.Threshold)
                    Log?.Invoke($"整文件下载 {Path.GetFileName(destFile)}（{DownloadSourceRegistry.Name(kind)}）");
                return;
            }
            catch (PermanentHttpException)
            {
                // 404 / 410：**这个源**没有这个文件（镜像未缓存 / SJTU 不代理 libraries 路径）。
                // 不能直接放弃整个文件——别的源很可能有。记录后换下一个源。
                // 另外这里必须排在下面的通用 catch **之前**：PermanentHttpException 是
                // Exception 的子类，放到后面就永远是死代码（实测过：整文件路径遇到镜像 404
                // 直接抛错，5 个库文件整批失败）。
                _sources.RecordNotFound(kind);
                Log?.Invoke($"{DownloadSourceRegistry.Name(kind)} 返回 404/410，换下一个源");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 失败也留痕（含来源与已耗时）：排查「这个文件为什么下不下来」全靠它
                sw.Stop();
                LauncherLog.Debug("download",
                    $"失败 {Path.GetFileName(destFile)} ← {DownloadSourceRegistry.Name(kind)}"
                    + $"，第 {attempt} 次尝试，{sw.ElapsedMilliseconds} ms：{ex.Message}");
                // 连接层面的硬故障（SSL / DNS / 拒绝连接）：这个源在本机网络里根本用不了，
                // 一次就降权。等第二次失败意味着每个小文件都先白撞它一轮——
                // 实测 45 个文件就是这样比官方源慢 14%。
                if (IsHardConnectFailure(ex))
                    _sources.MarkDegraded(kind);
                else
                    _sources.RecordFailure(kind);
                // 限流：换个源就行，等冷却只会拖慢整批（详细理由见分片路径）
                var throttled = ex.Message.Contains("限流")
                                || ex.Message.Contains("429")
                                || ex.Message.Contains("503");
                if (!throttled)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(3000, 400 * attempt * attempt)), ct);
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
            _sources.RecordThrottled(SourceKind.Official);   // 整文件路径的限流也算源健康度
            // 服务端给了 Retry-After 就按它说的等（比我们自己猜的准）
            if (resp.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
                ParallelGovernor.ReportRetryAfter(host, delta);
            resp.Dispose();
            throw new IOException($"{host} 限流（HTTP {(int)resp.StatusCode}）");
        }
        // 404 / 410：文件在这个源上就是不存在，重试也没用 —— 换源或放弃
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            resp.Dispose();
            throw new PermanentHttpException($"下载源返回 {(int)resp.StatusCode}（文件不存在）");
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

    /// <summary>
    /// 校验已落盘文件的 sha512（新版 Modrinth 索引给的是 sha512，不是 sha1）。
    /// <para>下载层只认 sha1，这里在落盘后补一道校验 —— 校验不过就删掉，
    /// 宁可不装也别装个坏的。参照 PrismLauncher 的 parseManifest 用 sha512。</para>
    /// </summary>
    internal static bool VerifySha512(string path, string expectedHex)
    {
        try
        {
            using var read = File.OpenRead(path);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(read));
            return actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>探测远端文件大小（HEAD，逐源尝试）；失败返回 0 = 按未知处理</summary>
    private async Task<long> ProbeSizeAsync(string url, CancellationToken ct)    {
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
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? sha512ByDest = null)
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

        // 每个批次重新判定「要不要请 Starcloud 兜底」：上一批因为它救场而被放行，
        // 不代表这一批还要它（S3 只做极端情况辅助，不进常规竞速）
        _sources.ResetAuxiliary();

        // 多文件并发时把不同文件分摊到不同源：单文件靠分片并行，多文件再叠一层"源级并行"，
        // 避免所有文件都挤在同一个最快源上（其余镜像闲着）。只有一个文件 / 一个源时不指定。
        HashSet<SourceKind> ranged = [SourceKind.Official];
        var assignPool = new List<SourceKind>();
        if (pending.Count > 1)
        {
            // **分摊前先探一次**：没有实测数据时按声明顺序分，会把文件分给不通/未缓存的镜像，
            // 失败后它们又全部退到最快的那个源 —— 实测结果是 35 片全在 S3、官方只分到 2 片，
            // S3 被自己打限流，整批耗时从 10 秒涨到 164 秒。
            ranged = await WarmupSourcesAsync(pending[0].Url, ct);

            foreach (var kind in _sources.RacingPool())
                if (!_sources.IsDegraded(kind)) assignPool.Add(kind);
            if (assignPool.Count == 0) assignPool.Add(SourceKind.Official);
        }

        // 分摊只给「实测速度接近最快源」的那些源。
        // 关键教训一：平均分摊会把文件分给慢一个数量级的源，整批时间就由它决定——
        // 实测本机 S3 有 23 MB/s、官方只有 0.48 MB/s（差 48 倍），把 1/4 的文件
        // 分给官方，整批就从 2 秒变成 52 秒。
        // 关键教训二：预热只有 256KB 样本，会高估镜像（BMCLAPI 边缘节点应答快，
        // 持续吞吐只有样本的一半不到）——门槛放到 0.75，样本明显虚的源不参赛。
        // 关键教训三：权重不能整批固定。RecordSpeed 会在批次过程中持续回填真实吞吐，
        // 每个文件分摊时现算一遍，慢源自己掉出名单；固定快照会把错误放大到整批
        // （实测：镜像样本虚高时一半文件被它拖住，自动模式反而比仅官方慢 80%）。
        const double KeepRatio = 0.75;
        List<SourceKind> Weight(IReadOnlyList<SourceKind> pool)
        {
            if (pool.Count <= 1) return pool.ToList();
            var maxScore = pool.Max(k => _sources.ScoreOf(k));
            if (maxScore <= 0) return pool.ToList();
            var fast = pool.Where(k => _sources.ScoreOf(k) >= maxScore * KeepRatio).ToList();
            if (fast.Count == 0) return pool.ToList();
            // 快源内部再按速度加权（快的多分），权重 = speed/maxSpeed × 4，最少 1
            var list = new List<SourceKind>();
            foreach (var kind in fast)
            {
                var weight = Math.Clamp((int)Math.Round(_sources.ScoreOf(kind) / maxScore * 4), 1, 4);
                list.AddRange(Enumerable.Repeat(kind, weight));
            }
            return list;
        }

        // 逐文件取分摊名单：永远用「当下」的存活源与实测分数（含兜底解锁后的变化）
        List<SourceKind> PoolForFile(bool bigFile)
        {
            var live = _sources.RacingPool().Where(k => !_sources.IsDegraded(k)).ToList();
            if (live.Count == 0) live.Add(SourceKind.Official);
            if (bigFile)
            {
                // 大文件只能分给认 Range 的源：分给不认 Range 的，分片必然失败再退回最快源，
                // 等于所有流量又挤到一处（实测 BMCL 有速度样本但只回整文件，坑在这里）
                var rangeOnly = Weight(live.Where(ranged.Contains).ToList());
                if (rangeOnly.Count > 0) return rangeOnly;
            }
            return Weight(live);
        }

        var assignCursor = 0;
        var initialPool = PoolForFile(bigFile: false);
        if (initialPool.Count > 1)
        {
            LauncherLog.Write("download",
                $"批量分摊下载源：{string.Join(" / ", initialPool.Distinct().Select(DownloadSourceRegistry.Name))}" +
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
                // 大文件走分片：只在认 Range 的源里分摊；小文件整文件下载，任意存活源都行。
                // 名单只剩一个源时也要钉住它：不然分片器自己的加权轮询会把份额派回慢源，
                // 一个 1 MB/s 的镜像就能给整批拖出长尾（实测 22MB 的文件被分走一半）。
                var poolForFile = PoolForFile(f.Size >= ChunkedFileDownloader.Threshold);
                var preferred = poolForFile.Count > 0 && DownloadSourceRegistry.IsMojangUrl(f.Url)
                    ? poolForFile[(int)((uint)Interlocked.Increment(ref assignCursor) %
                                        (uint)poolForFile.Count)]
                    : (SourceKind?)null;
                long reported = 0;
                await DownloadOneAsync(f.Url, f.Dest, f.Sha1, maxRetries, ct, f.Size,
                    n =>
                    {
                        Interlocked.Add(ref doneBytes, n);
                        Interlocked.Add(ref reported, n);
                        Report();
                    }, preferred);
                // sha512 二次校验：整合包索引给的 sha512（新版 Modrinth 常见），
                // 下载层只认 sha1，这里按落盘后的文件补一道校验，
                // 校验不过就把文件删掉（宁可不装，也别装个坏的）。
                if (sha512ByDest is not null &&
                    sha512ByDest.TryGetValue(f.Dest, out var expected512) &&
                    !string.IsNullOrWhiteSpace(expected512))
                {
                    if (!VerifySha512(f.Dest, expected512))
                    {
                        try { File.Delete(f.Dest); } catch { }
                        throw new IOException($"SHA512 校验失败: {Path.GetFileName(f.Dest)}");
                    }
                }
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
