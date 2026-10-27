// 高级传递优化：跨网络 P2P 的客户端编排（设计：2026-10-22-cross-internet-p2p-rendezvous.md）。
//
// 三个职责：
//   ① 发现：3 个 UDP socket 各做一次 STUN，定期把候选端点与文件索引 announce 给发现服务；
//   ② 拉取：下载管线里按 sha1 查在线对端 → 申请会话 → 打洞 → 分块收文件 → 整文件 SHA1 校验；
//   ③ 供文件：收到 SSE 的 p2p.request → 打洞 → 只发自己索引里、哈希核对过的文件。
//
// 服务器不参与数据面；任何一步失败（没有对端、对称 NAT、UDP 被封）都只是返回 false，
// 下载照常走公网多源——P2P 永远是加速项。

using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services.P2P;

/// <summary>对端通过 SSE 发来的会话请求</summary>
public sealed record P2PRequest(
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("sha1")] string Sha1,
    [property: JsonPropertyName("from")] P2PRequestFrom? From);

public sealed record P2PRequestFrom(
    [property: JsonPropertyName("candidates")] IReadOnlyList<P2PCandidate>? Candidates);

/// <summary>设置页展示的高级传递优化状态</summary>
public enum InternetP2PState
{
    /// <summary>开关关闭 / 未登录 / 已停止</summary>
    Stopped = 0,

    /// <summary>已启动，正在做 STUN 探测</summary>
    Probing = 1,

    /// <summary>已上报索引，可以参与互助</summary>
    Ready = 2,

    /// <summary>公网端点探测失败（UDP/STUN 被网络拦截 / 对称 NAT 之外的环境问题）</summary>
    Unavailable = 3,
}

public sealed class InternetP2PService : IPeerFileSource, IDisposable
{
    private const int SocketCount = 3;
    private const int MaxBatchFiles = 500;
    private const int MaxIndexFiles = 2000;
    private const int FetchBudgetSeconds = 9;
    private const int PunchBudgetSeconds = 8;
    private const int MaxServeConcurrency = 3;

    private readonly Func<LauncherSettings> _settings;
    private readonly StarcloudIdService _identity;
    private readonly RendezvousClient _rendezvous;
    private readonly P2PFileLedger _ledger;
    private readonly SemaphoreSlim _serveGate = new(MaxServeConcurrency, MaxServeConcurrency);
    private readonly SemaphoreSlim _announceSignal = new(0, 1);
    private readonly object _gate = new();

    private List<UdpSocketHost> _hosts = [];
    private CancellationTokenSource? _cts;
    private CancellationToken _rootToken = CancellationToken.None;
    private Task? _loop;
    private int _snapshot;
    private long _probedAt;
    private long _lastAnnounceAt;
    private volatile bool _discoverable;
    private int _stunWarned;
    private int _backfilled;

    /// <summary>发现服务刚回过「没人有这文件」/查询失败的静默期（批量下载会逐文件来问，别每条都打一次 HTTP）</summary>
    private long _emptyPeersUntil;

    /// <summary>本轮下载批次允许花在高级传递优化上的总预算（tick 截止点）；超时整体让路给公网</summary>
    private long _passDeadline;

    public InternetP2PService(Func<LauncherSettings> settings, StarcloudIdService identity,
        P2PFileLedger? ledger = null)
    {
        _settings = settings;
        _identity = identity;
        _ledger = ledger ?? new P2PFileLedger();
        _rendezvous = new RendezvousClient(() => identity.Current?.ApiKey);
        _identity.SessionChanged += OnSessionChanged;
    }

    /// <summary>日志出口（启动器日志 + 设置页可查）</summary>
    public Action<string>? Log { get; set; }

    public bool Running { get; private set; }

    public bool CanStart => _settings().P2pInternetEnabled && _rendezvous.IsSignedIn;

    /// <summary>当前状态（设置页展示：未开启 / 探测中 / 已参与 / 网络不可用）</summary>
    public InternetP2PState State => _state;

    /// <summary>已探测到的公网候选端点数（多 socket × v4/v6）</summary>
    public int CandidateCount => Volatile.Read(ref _candidateCount);

    /// <summary>累计从对端取到的文件数与字节数（设置页展示用）</summary>
    public long BytesFetched => Interlocked.Read(ref _bytesFetched);

    private long _bytesFetched;
    private volatile InternetP2PState _state = InternetP2PState.Stopped;
    private int _candidateCount;

    /// <summary>下载管线的对等源：0 = 完全不参与（未登录 / 开关关闭 / STUN 全失败）</summary>
    public int PeerCount => Running && _discoverable && _settings().P2pDownloadEnabled ? 1 : 0;

    // ── 生命周期 ──

    public bool Start()
    {
        lock (_gate)
        {
            if (Running) return true;
            if (!CanStart) return false;
            try
            {
                _hosts = Enumerable.Range(0, SocketCount).Select(_ => new UdpSocketHost()).ToList();
                foreach (var host in _hosts) host.Start();
                _cts = new CancellationTokenSource();
                _rootToken = _cts.Token;
                _snapshot++;
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token), token);
                Running = true;
                _state = InternetP2PState.Probing;
                Log?.Invoke("传输优化已启动：正在探测公网端点");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"传输优化启动失败：{ex.Message}");
                // Running 还没置真，StopLocked 会直接返回：这里自己回收半成品 socket
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                _rootToken = CancellationToken.None;
                foreach (var host in _hosts) host.Dispose();
                _hosts = [];
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (!Running) return;
        Running = false;
        _discoverable = false;
        _state = InternetP2PState.Stopped;
        Volatile.Write(ref _candidateCount, 0);
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _loop = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _rootToken = CancellationToken.None;
        foreach (var host in _hosts) host.Dispose();
        _hosts = [];
        // 主动下线：让对端立刻查不到自己（失败也无所谓，TTL 300s 会兜底）
        _ = Task.Run(async () =>
        {
            try { await _rendezvous.OfflineAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
        });
        Log?.Invoke("传输优化已停止");
    }

    private void OnSessionChanged()
    {
        if (_identity.IsLoggedIn) Start();
        else Stop();
    }

    // ── ① 发现：STUN + announce ──

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hosts.Count == 0) return;
                // 首次启动先回填存量文件：老用户装好的库/资源无需重新下载就能分享
                if (Interlocked.Exchange(ref _backfilled, 1) == 0)
                {
                    var added = P2PIndexBackfill.Run(_settings().DotMinecraft, _ledger);
                    if (added > 0) Log?.Invoke($"扫描本机已安装文件：新增 {added} 条可分享索引");
                }
                await ProbeAsync(ct).ConfigureAwait(false);
                if (!_discoverable)
                {
                    if (Interlocked.Exchange(ref _stunWarned, 1) == 0)
                        Log?.Invoke("公网端点探测失败（UDP/STUN 不可用），传输优化暂不参与");
                    await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    continue;
                }

                await AnnounceAsync(ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastAnnounceAt, Environment.TickCount64);
                // 有新增文件时会被 RequestAnnounce 提前唤醒（最多 30 秒一次），否则 3 分钟一轮
                await _announceSignal.WaitAsync(TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"传输优化发现流程异常：{ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        // NAT 映射会随网络切换/路由超时变化：每 15 分钟重探一次，别拿着过期端点对外宣布
        var now = Environment.TickCount64;
        if (_probedAt > 0 && now - _probedAt > 15 * 60 * 1000)
            foreach (var host in _hosts) host.Endpoints.Clear();
        _probedAt = now;

        // 各 socket 的探测并行：3 socket × 2 地址族 × 2 服务器 = 最多 12 次探测，
        // 串行做最坏要 24 秒才判得出「不可用」；并行后就是单次的 2 秒超时。
        await Task.WhenAll(_hosts.Select(async host =>
        {
            if (host.Endpoints.Count > 0) return;
            // 同一 socket 的 v4 / v6 映射是两回事，两个地址族都要探
            foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
            {
                foreach (var (name, port) in StunClient.Servers)
                {
                    var mapped = await StunClient.DiscoverAsync(host, name, port, family,
                        TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    if (mapped is null) continue;
                    var endpoint = UdpSocketHost.Normalize(mapped);
                    if (!host.Endpoints.Any(e => e.Equals(endpoint))) host.Endpoints.Add(endpoint);
                    break;
                }
            }
        })).ConfigureAwait(false);
        _discoverable = _hosts.Any(h => h.Endpoints.Count > 0);
        Volatile.Write(ref _candidateCount, CurrentCandidates().Count);
        _state = _discoverable ? InternetP2PState.Ready : InternetP2PState.Unavailable;
    }

    /// <summary>NAT 类型：同一个 socket 问两个 STUN，映射不一致 = 对称 NAT（仍上报，排序靠后）。</summary>
    private async Task<string> DetectNatAsync(CancellationToken ct)
    {
        var first = _hosts.FirstOrDefault(h => h.Endpoints.Count > 0);
        if (first is null) return "unknown";
        var family = first.Endpoints[0].AddressFamily;
        var (name0, port0) = StunClient.Servers[0];
        var (name1, port1) = StunClient.Servers[1];
        var a = await StunClient.DiscoverAsync(first, name0, port0, family,
            TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var b = await StunClient.DiscoverAsync(first, name1, port1, family,
            TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (a is null || b is null) return "unknown";
        return UdpSocketHost.Normalize(a).Equals(UdpSocketHost.Normalize(b)) ? "cone" : "symmetric";
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        var candidates = CurrentCandidates();
        if (candidates.Count == 0) return;
        var nat = await DetectNatAsync(ct).ConfigureAwait(false);
        var files = _ledger.Snapshot(MaxIndexFiles);
        var batches = Math.Max(1, (files.Count + MaxBatchFiles - 1) / MaxBatchFiles);
        var snapshot = Interlocked.Increment(ref _snapshot);

        var accepted = 0;
        for (var i = 0; i < batches; i++)
        {
            var slice = files.Skip(i * MaxBatchFiles).Take(MaxBatchFiles).ToList();
            var result = await _rendezvous.AnnounceAsync(candidates, nat, slice, snapshot, i + 1, batches, ct)
                .ConfigureAwait(false);
            accepted += result.Accepted;
        }
        Log?.Invoke($"已上报 {files.Count} 个文件索引（服务器接收 {accepted} 条）、" +
                    $"{candidates.Count} 个候选端点（NAT {nat}）");
    }

    /// <summary>
    /// 上报给发现服务的候选端点。后端上限：总数 4、v4 / v6 各最多 2、端口 ≥1024，
    /// 超了整条 announce 会被拒（bad_candidates），所以这里自己先按家族裁剪。
    /// IPv6 优先（有全球地址时通常可直连），IPv4 作为并行兜底。
    /// </summary>
    private List<P2PCandidate> CurrentCandidates()
    {
        var all = new List<P2PCandidate>();
        foreach (var host in _hosts)
        {
            foreach (var endpoint in host.Endpoints)
            {
                if (endpoint.Port is < 1024 or > 65535) continue;
                var candidate = new P2PCandidate(endpoint.Address.ToString(), endpoint.Port,
                    endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4);
                if (all.Any(c => c.Addr == candidate.Addr && c.Port == candidate.Port)) continue;
                all.Add(candidate);
            }
        }

        var result = new List<P2PCandidate>(4);
        var v4 = 0;
        var v6 = 0;
        foreach (var candidate in all.OrderBy(c => c.Family == 6 ? 0 : 1))
        {
            if (candidate.Family == 6)
            {
                if (v6 >= 2) continue;
                v6++;
            }
            else
            {
                if (v4 >= 2) continue;
                v4++;
            }
            result.Add(candidate);
            if (result.Count >= 4) break;
        }
        return result;
    }

    /// <summary>
    /// 一轮批量下载开始：给高级传递优化发放 60 秒总预算。下载管线逐文件同步调用
    /// TryProvide，没有批次边界的话，最坏情况是几千个文件 × 每条 9 秒的建链预算。
    /// </summary>
    public void BeginPass() =>
        Interlocked.Exchange(ref _passDeadline, Environment.TickCount64 + 60_000);

    /// <summary>下载管线记账：文件到手后写进本地索引，并尽快让对端查得到（30 秒节流）。</summary>
    public void RecordObtained(string destFile, string? sha1, long size)
    {
        var before = _ledger.Count;
        _ledger.Record(destFile, sha1, size);
        if (_ledger.Count > before) RequestAnnounce();
    }

    /// <summary>提前唤醒 announce 循环（新文件刚下完，不必等满 3 分钟）。</summary>
    public void RequestAnnounce()
    {
        if (!Running) return;
        if (Environment.TickCount64 - Interlocked.Read(ref _lastAnnounceAt) < 30_000) return;
        try
        {
            if (_announceSignal.CurrentCount == 0) _announceSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有一个待处理的唤醒
        }
    }

    // ── ② 拉取（IPeerFileSource） ──

    public bool TryProvide(string destFile, string sha1, long size, out string viaPeer)
    {
        viaPeer = "";
        // 同步外壳：实际流程在后台线程跑，调用线程只等结果（老接口保留给脚本 / 插件用）
        var winner = "";
        var done = new ManualResetEventSlim(false);
        _ = Task.Run(async () =>
        {
            try { winner = await FetchAsync(destFile, sha1, size).ConfigureAwait(false) ?? ""; }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(FetchBudgetSeconds + 5));
        viaPeer = winner;
        return winner.Length > 0;
    }

    /// <summary>异步版（下载管线用这一条：不阻塞调用线程）。</summary>
    public async Task<bool> TryProvideAsync(string destFile, string sha1, long size,
        CancellationToken ct = default)
    {
        if (PeerCount == 0) return false;
        if (string.IsNullOrEmpty(sha1) || size < P2PFileLedger.MinIndexedSize) return false;
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _emptyPeersUntil)) return false;
        if (now > Interlocked.Read(ref _passDeadline)) return false;
        try
        {
            var winner = await FetchAsync(destFile, sha1, size, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(winner)) return false;
            Interlocked.Add(ref _bytesFetched, size);
            Log?.Invoke($"传输优化命中 {Path.GetFileName(destFile)} ← {winner}");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"传输优化拉取失败（回退公网）：{ex.Message}");
            return false;
        }
    }

    private async Task<string?> FetchAsync(string destFile, string sha1, long size,
        CancellationToken outerCt = default)
    {
        // 预算分两段：建链（查对端 → 申请会话 → 打洞 → 握手）9 秒；
        // 传输按体积另算（最低按 2 MB/s），否则稍大的 jar 会在传一半时被预算掐断。
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(_rootToken, outerCt);
        setup.CancelAfter(TimeSpan.FromSeconds(FetchBudgetSeconds));
        var ct = setup.Token;

        IReadOnlyList<P2PPeer> peers;
        try
        {
            peers = await _rendezvous.PeersAsync(sha1, size, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"查询对端失败：{ex.Message}");
            // 查询失败同样静默一会儿：服务不可用时别让批量下载逐文件撞超时
            Interlocked.Exchange(ref _emptyPeersUntil, Environment.TickCount64 + 5_000);
            return null;
        }
        if (peers.Count == 0)
        {
            // 没人有这文件：10 秒内不再逐文件问服务（批量下载一个批次几千条）
            Interlocked.Exchange(ref _emptyPeersUntil, Environment.TickCount64 + 10_000);
            return null;
        }

        var candidates = peers
            .Where(p => p.Candidates is { Count: > 0 })
            .OrderBy(p => p.Nat == "symmetric" ? 1 : 0)
            .Take(2)
            .ToList();
        if (candidates.Count == 0) return null;

        var hosts = _hosts.ToList();
        if (hosts.Count == 0) return null;
        var destDir = Path.GetDirectoryName(destFile);
        if (string.IsNullOrEmpty(destDir)) return null;
        // 目标目录可能还不存在（libraries/ 下的库文件就是这种情况）：
        // 目录不存在时创建临时文件会失败，P2P 永远命中不了（还会被静默吞掉）。
        try { Directory.CreateDirectory(destDir); } catch { }

        var winner = "";
        var gate = new object();
        using var winnerCts = new CancellationTokenSource();
        var tasks = candidates.Select(peer => Task.Run(async () =>
        {
            var tmp = Path.Combine(destDir, $".p2p-{Guid.NewGuid():N}.part");
            string? sessionId = null;
            var delivered = false;
            try
            {
                var grant = await _rendezvous.CreateSessionAsync(sha1, peer.Id, ct).ConfigureAwait(false);
                if (grant?.Peer?.Candidates is not { Count: > 0 } remote) return;
                sessionId = grant.SessionId;
                var token = ParseToken(grant.Token);
                if (token is null) return;
                var key = new P2PSessionKey(token);
                var endpoints = ToEndPoints(remote);
                if (endpoints.Count == 0) return;

                var punched = await P2PHolePunch.RunAsync(hosts, endpoints, key.Tag,
                    TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                if (punched is not { } path) return;

                using var session = new P2PSession(hosts, path.Host, path.Remote, key);
                if (!await HelloAsync(session, sha1, size, key, ct).ConfigureAwait(false)) return;

                // 传输阶段：换一条按体积计算的预算，并与其他对端共享「已有人成功」的取消信号
                using var transferCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _rootToken, winnerCts.Token);
                transferCts.CancelAfter(TimeSpan.FromSeconds(TransferSeconds(size)));
                var transfer = transferCts.Token;

                var handle = File.OpenHandle(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    FileOptions.Asynchronous, size);
                try
                {
                    // 进度上报：P2P 原来完全不报，用户看到进度条卡在 0% 直到传输结束才瞬间跳满
                    var reported = 0L;
                    await session.ReceiveFileAsync(handle, size, 0, n =>
                    {
                        Interlocked.Add(ref reported, n);
                        TaskCenterService.Instance.ReportDownloadBytes(n);
                    }, transfer).ConfigureAwait(false);
                }
                finally
                {
                    handle.Dispose();
                }

                await using (var verify = File.OpenRead(tmp))
                {
                    var actual = Convert.ToHexString(await SHA1.HashDataAsync(verify, transfer).ConfigureAwait(false));
                    if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase)) return;
                }

                lock (gate)
                {
                    if (winner.Length > 0) return;
                    try
                    {
                        File.Move(tmp, destFile, overwrite: true);
                        winner = string.IsNullOrEmpty(peer.Id) ? "在线用户" : $"在线用户 {peer.Id}";
                        delivered = true;
                        winnerCts.Cancel(); // 其他对端不用再传了
                    }
                    catch
                    {
                        // 落盘失败：当没命中，回退公网
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 预算用完：交给其他对端 / 公网
            }
            catch (Exception ex)
            {
                Log?.Invoke($"对端 {peer.Id} 拉取失败：{ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (sessionId is not null)
                {
                    // 先结算再关闭：close 会删会话行，report 之后就没得报了
                    var outcome = delivered ? "ok" : winnerCts.IsCancellationRequested ? "canceled" : "failed";
                    try { await _rendezvous.ReportSessionAsync(sessionId, outcome).ConfigureAwait(false); }
                    catch { }
                    await CloseAsync(sessionId).ConfigureAwait(false);
                }
            }
        })).ToArray();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        return winner.Length > 0 ? winner : null;
    }

    /// <summary>传输预算：按 2 MB/s 的保守速度估算，最低 15 秒、最高 120 秒。</summary>
    private static double TransferSeconds(long size) =>
        Math.Clamp(size / (2.0 * 1024 * 1024), 15, 120);

    private static async Task<bool> HelloAsync(P2PSession session, string sha1, long size,
        P2PSessionKey key, CancellationToken ct)
    {
        var nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var payload = P2PHandshake.BuildHello(sha1, size, 0, nonce, key);
        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            await session.SendAsync(P2PFrameType.Hello, payload: payload).ConfigureAwait(false);
            var frame = await session.ReadAsync(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            if (frame is { Type: P2PFrameType.HelloAck } ack &&
                P2PHandshake.VerifyHelloAck(ack.Payload.Span, nonce, key))
                return true;
        }
        return false;
    }

    // ── ③ 供文件（SSE p2p.request） ──

    /// <summary>由 App 层把好友 SSE 上的 p2p.request 转进来（后台线程）。</summary>
    public Task HandleEventAsync(FriendEvent ev)
    {
        if (ev.Type != "p2p.request" || !Running) return Task.CompletedTask;
        var request = ParseRequest(ev.Data);
        if (request is null) return Task.CompletedTask;
        _ = Task.Run(() => ServeAsync(request));
        return Task.CompletedTask;
    }

    private async Task ServeAsync(P2PRequest request)
    {
        if (!_serveGate.Wait(0))
        {
            // 并发供文件已满：直接谢绝，请求方会走公网
            await CloseAsync(request.Session).ConfigureAwait(false);
            return;
        }

        var served = false;
        try
        {
            var token = ParseToken(request.Token);
            if (token is null) return;
            var key = new P2PSessionKey(token);
            var endpoints = ToEndPoints(request.From!.Candidates!);
            if (endpoints.Count == 0) return;

            var hosts = _hosts.ToList();
            if (hosts.Count == 0) return;

            // 建链预算：打洞 + 等握手
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(_rootToken);
            setup.CancelAfter(TimeSpan.FromSeconds(30));
            var punched = await P2PHolePunch.RunAsync(hosts, endpoints, key.Tag,
                TimeSpan.FromSeconds(PunchBudgetSeconds), setup.Token).ConfigureAwait(false);
            if (punched is not { } path)
            {
                Log?.Invoke("供文件：打洞未打通，已放弃（请求方会回退公网）");
                return;
            }

            using var session = new P2PSession(hosts, path.Host, path.Remote, key);
            var hello = await WaitHelloAsync(session, key, setup.Token).ConfigureAwait(false);
            if (hello is null) return;

            var (sha1, size, offset, nonce) = hello.Value;
            // 会话令牌只授权了请求里的那个文件：握手声明的 sha1 必须与之一致
            if (!string.Equals(sha1, request.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                await session.AbortAsync(3).ConfigureAwait(false);
                return;
            }
            if (!_ledger.TryResolve(sha1, size, out var file, out var actual) || actual != size)
            {
                await session.AbortAsync(1).ConfigureAwait(false);
                return;
            }

            // 传输预算按体积与用户限速估算（限速越低给得越宽），上限 10 分钟
            var rate = Math.Clamp(_settings().P2pInternetUploadMbps, 1, 32) * 1024.0 * 1024.0;
            using var transferCts = CancellationTokenSource.CreateLinkedTokenSource(_rootToken);
            transferCts.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(size / Math.Max(rate * 0.5, 1), 20, 600)));
            var cts = transferCts;

            // 供之前整文件核对：文件被换过就绝不发出去
            if (!await _ledger.VerifyAsync(file, sha1, cts.Token).ConfigureAwait(false))
            {
                await session.AbortAsync(2).ConfigureAwait(false);
                return;
            }
            if (!_settings().P2pInternetEnabled)
            {
                await session.AbortAsync(4).ConfigureAwait(false);
                return;
            }

            await session.SendAsync(P2PFrameType.HelloAck,
                payload: P2PHandshake.BuildHelloAck(nonce, key)).ConfigureAwait(false);

            using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.Asynchronous);
            var limiter = new P2PRateLimiter(Math.Clamp(_settings().P2pInternetUploadMbps, 1, 32)
                                             * 1024.0 * 1024.0);
            await session.SendFileAsync(handle, size, offset, null, limiter, cts.Token).ConfigureAwait(false);
            served = true;
            Log?.Invoke($"已向对端发送 {Path.GetFileName(file)}");
        }
        catch (OperationCanceledException)
        {
            // 会话超时：正常收尾
        }
        catch (Exception ex)
        {
            Log?.Invoke($"供文件失败：{ex.Message}");
        }
        finally
        {
            _serveGate.Release();
            try { await _rendezvous.ReportSessionAsync(request.Session, served ? "ok" : "failed").ConfigureAwait(false); }
            catch { }
            await CloseAsync(request.Session).ConfigureAwait(false);
        }
    }

    private static async Task<(string Sha1, long Size, long Offset, ulong Nonce)?> WaitHelloAsync(
        P2PSession session, P2PSessionKey key, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            var frame = await session.ReadAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            if (frame is not { Type: P2PFrameType.Hello }) continue;
            if (P2PHandshake.TryParseHello(frame.Value.Payload.Span, key,
                    out var sha1, out var size, out var offset, out var nonce))
                return (sha1, size, offset, nonce);
        }
        return null;
    }

    private async Task CloseAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        try { await _rendezvous.CloseSessionAsync(sessionId).ConfigureAwait(false); }
        catch { }
    }

    // ── 工具 ──

    private static List<System.Net.IPEndPoint> ToEndPoints(IReadOnlyList<P2PCandidate> candidates)
    {
        var result = new List<System.Net.IPEndPoint>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.Port is <= 0 or > 65535) continue;
            if (!System.Net.IPAddress.TryParse(candidate.Addr, out var address)) continue;
            result.Add(new System.Net.IPEndPoint(address, candidate.Port));
        }
        return result;
    }

    /// <summary>
    /// 解析 SSE 的 p2p.request：字段名按设计文档，同时容忍后端常见变体
    /// （session_id / candidates 在顶层 / 候选是 "ip:port" 字符串），避免后端换一种写法就整条断掉。
    /// </summary>
    internal static P2PRequest? ParseRequest(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        var session = ReadString(data, "session") ?? ReadString(data, "session_id");
        var token = ReadString(data, "token") ?? ReadString(data, "session_token");
        var sha1 = ReadString(data, "sha1");
        if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(token) ||
            string.IsNullOrEmpty(sha1) || sha1.Length != 40)
            return null;

        var candidates = new List<P2PCandidate>();
        if (data.TryGetProperty("from", out var from))
        {
            if (from.ValueKind == JsonValueKind.Object &&
                from.TryGetProperty("candidates", out var nested))
                ReadCandidates(nested, candidates);
            else
                ReadCandidates(from, candidates);
        }
        if (candidates.Count == 0 && data.TryGetProperty("candidates", out var flat))
            ReadCandidates(flat, candidates);
        return candidates.Count == 0
            ? null
            : new P2PRequest(session, token, sha1.ToLowerInvariant(), new P2PRequestFrom(candidates));
    }

    private static string? ReadString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static void ReadCandidates(JsonElement node, List<P2PCandidate> into)
    {
        if (node.ValueKind != JsonValueKind.Array) return;
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                if (!item.TryGetProperty("addr", out var addr) && !item.TryGetProperty("ip", out addr))
                    continue;
                if (addr.ValueKind != JsonValueKind.String ||
                    addr.GetString() is not { Length: > 0 } host ||
                    !item.TryGetProperty("port", out var port) || !port.TryGetInt32(out var number) ||
                    number is <= 0 or > 65535)
                    continue;
                var family = item.TryGetProperty("family", out var f) && f.TryGetInt32(out var fam) ? fam : 0;
                into.Add(new P2PCandidate(host, number, family));
            }
            else if (item.ValueKind == JsonValueKind.String &&
                     item.GetString() is { Length: > 0 } text)
            {
                var colon = text.LastIndexOf(':');
                if (colon <= 0) continue;
                if (!int.TryParse(text[(colon + 1)..], out var number) || number is <= 0 or > 65535)
                    continue;
                into.Add(new P2PCandidate(text[..colon].Trim('[', ']'), number, 0));
            }
        }
    }

    /// <summary>
    /// 令牌编码：后端可能给标准 base64、URL-safe base64（无 padding）或十六进制。
    /// 三种都接受，后端实现二选一时不用改客户端。
    /// </summary>
    internal static byte[]? ParseToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var text = token.Trim();

        // 纯十六进制要先认：64 位 hex 同样“是合法的 base64”，先按 base64 解会得到错误字节
        if (text.Length >= 32 && text.Length % 2 == 0 && text.All(Uri.IsHexDigit))
        {
            try
            {
                var bytes = Convert.FromHexString(text);
                if (bytes.Length >= 16) return bytes;
            }
            catch
            {
                // 落到 base64
            }
        }

        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };
        try
        {
            var bytes = Convert.FromBase64String(normalized);
            if (bytes.Length >= 16) return bytes;
        }
        catch
        {
            // 不是 base64：试十六进制
        }
        try
        {
            var bytes = Convert.FromHexString(text);
            return bytes.Length >= 16 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _identity.SessionChanged -= OnSessionChanged;
        Stop();
        _serveGate.Dispose();
        _announceSignal.Dispose();
    }
}
