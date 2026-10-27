// 设备链路编排：生命周期（beacon + TCP 服务）、在线设备表、通道状态、命令语义（IPeerLinkHandler）、
// P2P 文件复用（IPeerFileSource，供下载管线插桩）。
//
// 启动前置条件：已登录 Starcloud ID 且设置里打开了设备链路——未登录时 Start 是空操作，
// UI 层负责提示「同步需要登录」；登录 / 切换设置后由 UI 再次调用 Start。

using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;

namespace Starcloud.Core.Services;

/// <summary>下载管线的对等文件源：命中即把对端文件校验后落到目标路径（只认 SHA1）。</summary>
public interface IPeerFileSource
{
    /// <summary>当前在线对端数（0 时下载管线完全跳过，零开销）</summary>
    int PeerCount { get; }

    /// <summary>尝试从对端获取文件并落到 destFile；失败 / 未命中返回 false（静默回退公网）</summary>
    bool TryProvide(string destFile, string sha1, long size, out string viaPeer);

    /// <summary>
    /// 异步版的 <see cref="TryProvide"/>。下载管线走这一条：
    /// 同步版会在调用线程上阻塞等待（建链最坏 9 秒、局域网最坏 22 秒），
    /// 而下载管线跑在 UI 线程上 —— 阻塞就是界面冻结。
    /// </summary>
    Task<bool> TryProvideAsync(string destFile, string sha1, long size, CancellationToken ct = default);
}

public sealed class DeviceLinkService : IAsyncDisposable, IPeerLinkHandler, IPeerFileSource
{
    private readonly Func<LauncherSettings> _settings;
    private readonly StarcloudIdService _identity;
    private readonly SaveLibraryService _saveLibrary;
    private readonly SaveService _saves;

    private PeerBeacon? _beacon;
    private PeerLinkServer? _server;
    private readonly object _gate = new();
    private readonly List<Func<Task<LinkChannel>>> _probes = [];

    /// <summary>对端哈希命中缓存（p2p.has 现场算过一次的 sha1 → 源路径），进程内有效</summary>
    private readonly Dictionary<string, string> _hashIndex = new(StringComparer.OrdinalIgnoreCase);
    private LocalFileReuse? _reuseIndex;
    private long _reuseIndexAt;

    public bool Running { get; private set; }

    /// <summary>启动 / 停止日志出口（设置页展示与启动器日志）</summary>
    public Action<string>? Log { get; set; }

    /// <summary>设备上线（名称, 地址）：beacon 事件透传，UI 弹「已连接」横幅。后台线程触发。</summary>
    public event Action<string, string>? PeerUp;

    /// <summary>设备离线（名称）。</summary>
    public event Action<string>? PeerDown;

    public DeviceLinkService(
        Func<LauncherSettings> settings,
        StarcloudIdService identity,
        SaveLibraryService saveLibrary,
        SaveService saves)
    {
        _settings = settings;
        _identity = identity;
        _saveLibrary = saveLibrary;
        _saves = saves;
    }

    /// <summary>注册通道探针（蓝牙 / 星闪由 App 层注入——WinRT 探测无法进纯 C# 的 Core）</summary>
    public void RegisterProbe(string name, Func<Task<LinkChannel>> probe)
    {
        lock (_gate) _probes.Add(probe);
        _probeNames.Add(name);
    }

    private readonly List<string> _probeNames = [];

    /// <summary>三条通道的当前状态（LAN 恒可用；其余按探针实时询问）</summary>
    public async Task<List<LinkChannel>> ChannelsAsync()
    {
        var list = new List<LinkChannel>
        {
            new("Wi-Fi / 局域网", LinkChannelStatus.Available,
                Running ? "发现与传输就绪" : "未启用"),
        };
        for (var i = 0; i < _probes.Count; i++)
        {
            try
            {
                list.Add(await _probes[i]());
            }
            catch
            {
                list.Add(new LinkChannel(_probeNames[i], LinkChannelStatus.Unavailable, "探测失败"));
            }
        }
        return list;
    }

    /// <summary>当前在线对端快照</summary>
    public List<PeerInfo> Peers => _beacon?.Snapshot() ?? [];

    /// <summary>链路是否真的在跑（登录 + 开关都满足）</summary>
    public bool CanStart => _identity.IsLoggedIn && _settings().DeviceLinkEnabled;

    /// <summary>启动链路（幂等；前置条件不满足时保持停止并返回 false）</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (Running) return true;
            if (!CanStart) return false;
            var session = _identity.Current!;
            var uid = LinkAuth.UidHash(session.UserId);
            try
            {
                _server = new PeerLinkServer(uid, () => _identity.Current?.ApiKey ?? "",
                    () => DeviceName, this, () => _settings().DeviceP2PCrossAccount);
                _server.Start();
                _beacon = new PeerBeacon(uid, () => DeviceName, () => _server?.Port ?? 0);
                _beacon.PeerUp += (name, addr) => PeerUp?.Invoke(name, addr);
                _beacon.PeerDown += name => PeerDown?.Invoke(name);
                _beacon.Start();
                Running = true;
                Log?.Invoke($"设备链路已启动（TCP :{_server.Port}，设备名 {DeviceName}）");
                return true;
            }
            catch (Exception ex)
            {
                Stop();
                Log?.Invoke($"设备链路启动失败：{ex.Message}");
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _beacon?.Stop();
            _server?.Stop();
            _beacon = null;
            _server = null;
            _hashIndex.Clear();
            Running = false;
        }
    }

    /// <summary>对端展示名：设置里的设备名，没设就用机器名</summary>
    public string DeviceName
    {
        get
        {
            var name = _settings().DeviceName;
            return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        }
    }

    // ── 命令语义（IPeerLinkHandler）：对端发来的请求都在这里落地 ──

    public async Task<PeerReply> HandleAsync(PeerRequest request, CancellationToken ct)
    {
        var s = _settings();
        return request.Cmd switch
        {
            "saves.list" => ListSaves(s),
            "saves.check" => CheckSave(s, request),
            "saves.put" => await ReceiveSaveAsync(s, request, ct),
            "saves.get" => await SendSaveAsync(s, request, ct),
            "cfg.get" => GetInstanceConfig(s, request),
            "cfg.put" => ReceiveConfig(s, request),
            "p2p.has" => PeerHasFile(s, request),
            "p2p.fetch" => PeerFetchFile(s, request),
            _ => PeerReply.Fail($"未知命令 {request.Cmd}"),
        };
    }

    /// <summary>把对端要的世界现场打包（临时 zip）→ 带哈希回传，发完即删临时文件。</summary>
    private async Task<PeerReply> SendSaveAsync(LauncherSettings s, PeerRequest req, CancellationToken ct)
    {
        var folder = SafeFolderName(req.Str("folder"));
        if (folder.Length == 0) return PeerReply.Fail("世界目录名非法");
        var target = SaveRootOf(s, req.Str("versionId"), req.Bool("isolated"));
        var worldDir = Path.Combine(target, "saves", folder);
        if (!Directory.Exists(worldDir)) return PeerReply.Fail("对端没有这个世界（可能已被删除）");

        var tempZip = Path.Combine(Path.GetTempPath(), "starcloud-sync", $"out-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tempZip)!);
        await Task.Run(() => ZipFile.CreateFromDirectory(worldDir, tempZip,
            CompressionLevel.Optimal, includeBaseDirectory: false), ct);
        // 流式算哈希：存档 zip 可能有几百 MB，File.ReadAllBytes 会把整份读进内存
        var sha256 = await Task.Run(async () =>
        {
            await using var fs = new FileStream(tempZip, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1 << 16, useAsync: true);
            var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }, ct).ConfigureAwait(false);
        return new PeerReply(true, new
        {
            size = new FileInfo(tempZip).Length,
            sha256,
        }, SourceFile: tempZip, DeleteAfterSend: true);
    }

    private PeerReply ListSaves(LauncherSettings s)
    {
        var saves = _saveLibrary.ListAll(s.DotMinecraft, s);
        var payload = new
        {
            saves = saves.Select(x => new
            {
                folder = x.FolderName,
                name = x.LevelName,
                lastPlayedMs = x.LastPlayed is { } t ? ToUnixMs(t) : 0,
                size = x.SizeBytes,
                versionId = x.VersionId,
                isolated = x.Isolated,
            }).ToArray(),
        };
        return new PeerReply(true, payload);
    }

    /// <summary>接收前裁决：对端同名世界更新 → 拒收；否则接受（旧世界改名保留由 put 落地时执行）</summary>
    private PeerReply CheckSave(LauncherSettings s, PeerRequest req)
    {
        var incoming = FromUnixMs(req.Long("lastPlayedMs"));
        var target = SaveRootOf(s, req.Str("versionId"), req.Bool("isolated"));
        var local = LocalLastPlayed(Path.Combine(target, "saves", req.Str("folder")));
        return new PeerReply(true, new
        {
            action = SyncConflict.Resolve(local, incoming).ToString(),
            localLastPlayedMs = local is { } t ? ToUnixMs(t) : 0,
        });
    }

    private async Task<PeerReply> ReceiveSaveAsync(LauncherSettings s, PeerRequest req, CancellationToken ct)
    {
        var folder = SafeFolderName(req.Str("folder"));
        if (folder.Length == 0) return PeerReply.Fail("世界目录名非法");
        if (req.BodyFile is null) return PeerReply.Fail("缺少存档数据");
        var incoming = FromUnixMs(req.Long("lastPlayedMs"));
        var target = SaveRootOf(s, req.Str("versionId"), req.Bool("isolated"));
        var savesDir = Path.Combine(target, "saves");
        var destDir = Path.Combine(savesDir, folder);

        var decision = SyncConflict.Resolve(LocalLastPlayed(destDir), incoming);
        if (decision == SyncDecision.Reject)
            return PeerReply.Fail("对端已有更新的同名世界，已保留对端版本（未覆盖）");

        // 校验请求体 zip → 冲突改名 → 解压落地
        var tempZip = req.BodyFile;
        // 流式算哈希：存档 zip 可能有几百 MB，File.ReadAllBytes 会把整份读进内存
        string actual;
        await using (var fs = new FileStream(tempZip, FileMode.Open, FileAccess.Read,
                     FileShare.Read, 1 << 16, useAsync: true))
        {
            var hash = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }
        if (!actual.Equals(req.Str("sha256"), StringComparison.OrdinalIgnoreCase))
            return PeerReply.Fail("zip 校验失败（传输不完整）");

        if (Directory.Exists(destDir) && decision == SyncDecision.AcceptWithBackup)
        {
            var backup = destDir + $".conflict-{DateTime.Now:yyyyMMdd-HHmmss}";
            Directory.Move(destDir, backup);
            Log?.Invoke($"同步冲突：旧世界已保留为 {Path.GetFileName(backup)}");
        }
        Directory.CreateDirectory(savesDir);
        ZipFile.ExtractToDirectory(tempZip, destDir, overwriteFiles: true);
        Log?.Invoke($"已接收世界 {folder}（来自设备同步）");

        // 版本数据随存档走：请求体里带版本配置 JSON 时一并落地
        var cfgJson = req.Str("cfgJson");
        if (cfgJson.Length > 0)
        {
            var pendingDir = Path.Combine(LauncherPaths.GameStateDir(s.DotMinecraft), "pending-config");
            Directory.CreateDirectory(pendingDir);
            File.WriteAllText(Path.Combine(pendingDir, req.Str("versionId") + ".json"), cfgJson);
        }
        return new PeerReply(true, new { folder });
    }

    private PeerReply GetInstanceConfig(LauncherSettings s, PeerRequest req)
    {
        var versionId = req.Str("versionId");
        var store = new InstanceSettingsStore(s.DotMinecraft);
        var cfg = store.Get(versionId);
        return new PeerReply(true, new
        {
            versionId,
            json = JsonSerializer.Serialize(cfg),
        });
    }

    private PeerReply ReceiveConfig(LauncherSettings s, PeerRequest req)
    {
        var versionId = req.Str("versionId");
        if (versionId.Length is 0 or > 128 || versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return PeerReply.Fail("版本 Id 非法");
        var json = req.Str("json");
        // 目标版本已装 → 配置缓存后由安装流程应用；这里如实报告安装态
        var pendingDir = Path.Combine(LauncherPaths.GameStateDir(s.DotMinecraft), "pending-config");
        Directory.CreateDirectory(pendingDir);
        File.WriteAllText(Path.Combine(pendingDir, versionId + ".json"), json);
        var installed = Directory.Exists(Path.Combine(s.DotMinecraft, "versions", versionId));
        return new PeerReply(true, new { installed, pending = !installed });
    }

    private PeerReply PeerHasFile(LauncherSettings s, PeerRequest req)
    {
        var sha1 = req.Str("sha1");
        var size = req.Long("size");
        var path = FindLocalFile(s, req.Str("destName"), sha1, size);
        return path is null
            ? PeerReply.Fail("本机没有该文件")
            : new PeerReply(true, new { size = new FileInfo(path).Length });
    }

    private PeerReply PeerFetchFile(LauncherSettings s, PeerRequest req)
    {
        var sha1 = req.Str("sha1");
        var size = req.Long("size");
        var path = FindLocalFile(s, req.Str("destName"), sha1, size);
        return path is null
            ? PeerReply.Fail("本机没有该文件")
            : new PeerReply(true, new { size = new FileInfo(path).Length }, SourceFile: path);
    }

    /// <summary>本机文件索引（P2P）：LocalFileReuse 同源索引 + 命中缓存，10 分钟重建一次</summary>
    private string? FindLocalFile(LauncherSettings s, string destName, string sha1, long size)
    {
        if (_hashIndex.TryGetValue(sha1, out var cached) && File.Exists(cached)) return cached;
        if (_reuseIndex is null || Environment.TickCount64 - _reuseIndexAt > TimeSpan.FromMinutes(10).TotalMilliseconds)
        {
            _reuseIndex = LocalFileReuse.Scan(s.DotMinecraft);
            _reuseIndex.AddLibraryDirectory(LauncherPaths.LibraryDir);
            _reuseIndexAt = Environment.TickCount64;
        }
        var hit = _reuseIndex.FindByHash(destName is { Length: > 0 } ? destName : "unknown-" + sha1[..8], sha1, size);
        if (hit is not null) _hashIndex[sha1] = hit;
        return hit;
    }

    /// <summary>存档所在实例根：隔离版本 → versions/<id>/；否则共享 .minecraft 根</summary>
    private static string SaveRootOf(LauncherSettings s, string versionId, bool isolated) =>
        isolated && versionId.Length > 0
            ? InstanceLayout.GameRoot(s.DotMinecraft, versionId, true)
            : s.DotMinecraft;

    private static DateTime? LocalLastPlayed(string worldDir)
    {
        var levelDat = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(levelDat)) return null;
        try
        {
            using var gz = new GZipStream(File.OpenRead(levelDat), CompressionMode.Decompress);
            if (LevelNbtReader.Read(gz)?.Get("Data") is not LevelNbtCompound data) return null;
            return data.Get("LastPlayed") is LevelNbtLong lp
                ? DateTimeOffset.FromUnixTimeMilliseconds(lp.Value).LocalDateTime
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeFolderName(string name) =>
        string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();

    private static long ToUnixMs(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private static DateTime? FromUnixMs(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null;

    // ── P2P 下载复用（IPeerFileSource）：下载管线的第三个源 ──

    public int PeerCount => _settings().P2pDownloadEnabled && Running ? _beacon?.Snapshot().Count ?? 0 : 0;

    /// <summary>
    /// 从对等设备整文件取一次（命中即返回）。多个对端并发尝试，谁先完成用谁；
    /// 慢设备 20 秒后放弃——旧实现是串行遍历且无超时，一个掉线的对端能把整批下载拖住。
    /// </summary>
    public bool TryProvide(string destFile, string sha1, long size, out string viaPeer)
    {
        viaPeer = "";
        // 同步外壳：把异步实现搬到一个后台线程上等，调用方（老接口）不受影响
        var winner = "";
        var done = new ManualResetEventSlim(false);
        _ = Task.Run(async () =>
        {
            try { winner = await TryProvideCoreAsync(destFile, sha1, size).ConfigureAwait(false); }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(24));
        viaPeer = winner;
        return winner.Length > 0;
    }

    /// <summary>异步版（下载管线用这一条：不阻塞调用线程）。</summary>
    public async Task<bool> TryProvideAsync(string destFile, string sha1, long size,
        CancellationToken ct = default)
    {
        var via = await TryProvideCoreAsync(destFile, sha1, size, ct).ConfigureAwait(false);
        if (via.Length == 0) return false;
        Log?.Invoke($"P2P 复用 {Path.GetFileName(destFile)} ← {via}");
        return true;
    }

    /// <summary>实际的对端取文件流程（同步与异步两条路共用）。</summary>
    private async Task<string> TryProvideCoreAsync(string destFile, string sha1, long size,
        CancellationToken ct = default)
    {
        var me = _identity.Current;
        if (me is null || PeerCount == 0) return "";
        var peers = _beacon!.Snapshot();
        if (peers.Count == 0) return "";

        var destName = Path.GetFileName(destFile);
        var destDir = Path.GetDirectoryName(destFile)!;
        // 目标目录可能还不存在（libraries/ 下的库文件就是这种情况）：
        // 目录不存在时创建临时文件会直接失败，P2P 永远命中不了。
        try { Directory.CreateDirectory(destDir); } catch { }
        var uid = LinkAuth.UidHash(me.UserId);
        var winner = "";
        var gate = new object();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(20));

        var tasks = peers.Select(peer => Task.Run(async () =>
        {
            if (stop.IsCancellationRequested) return;
            // 每个对端写自己的临时文件：并发完成时不会互相覆盖，胜者再原子替换
            var tmp = Path.Combine(destDir, $".p2p-{Guid.NewGuid():N}.part");
            PeerLinkClient? client = null;
            try
            {
                client = await PeerLinkClient.ConnectAsync(
                    uid, me.ApiKey, DeviceName, peer.Address, peer.Port, stop.Token)
                    .ConfigureAwait(false);
                var has = await client.CommandAsync(
                    new { cmd = "p2p.has", sha1, size, destName }, ct: stop.Token).ConfigureAwait(false);
                if (!has.Ok) return;
                var result = await client.CommandAsync(
                    new { cmd = "p2p.fetch", sha1, size, destName },
                    tmp, expectSha256: sha1, ct: stop.Token).ConfigureAwait(false);
                if (!result.Ok) return;
                lock (gate)
                {
                    if (winner.Length > 0) return;   // 已有更快的对端完成
                    try
                    {
                        File.Move(tmp, destFile, overwrite: true);
                        winner = peer.Name;
                    }
                    catch { /* 落盘失败：当没命中，回退公网 */ }
                }
            }
            catch
            {
                // 单个对端失败（离线 / 跨账号被拒 / 超时）不影响其余对端与公网回退
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (client is not null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        })).ToArray();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* 单个对端失败不影响整体 */ }
        stop.Cancel();
        stop.Dispose();

        return winner;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}

/// <summary>存档同步冲突裁决（纯函数，单测覆盖）</summary>
public enum SyncDecision
{
    /// <summary>本地没有同名世界：直接接收</summary>
    Accept,
    /// <summary>本地同名世界较旧（或未知新旧）：旧世界改名保留后接收</summary>
    AcceptWithBackup,
    /// <summary>本地同名世界更新：拒收（保护对端新进度）</summary>
    Reject,
}

public static class SyncConflict
{
    /// <summary>按 level.dat 的 LastPlayed 裁决：两边都不丢——新者胜、旧者改名保留、拿不准也保留。</summary>
    public static SyncDecision Resolve(DateTime? localLast, DateTime? incomingLast)
    {
        if (localLast is null) return SyncDecision.Accept;
        if (incomingLast is null) return SyncDecision.AcceptWithBackup; // 拿不准：保留两边
        return incomingLast.Value > localLast.Value
            ? SyncDecision.AcceptWithBackup
            : SyncDecision.Reject;
    }
}
