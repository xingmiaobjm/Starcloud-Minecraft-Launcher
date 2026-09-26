// 联机服务：提供方注册表 + 会话编排 + 状态轮询。
//
// 界面只跟这一层打交道：拿提供方列表、切提供方、装核心、建房/进房/停联机、看状态。
// 底下是哪一个 SDK（内置陶瓦联机 / 第三方清单）由 <see cref="IMultiplayerProvider"/> 吸收掉。

namespace Starcloud.Core.Services.Multiplayer;

public sealed class MultiplayerService : IDisposable
{
    private readonly DownloadService _download;
    private readonly Func<LauncherSettings> _settings;
    private readonly Action? _saveSettings;
    private readonly Func<string?> _defaultNickname;

    private readonly Dictionary<string, IMultiplayerProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MultiplayerProviderInfo> _infos = [];
    private readonly object _gate = new();

    private Timer? _poller;
    private int _polling;   // Interlocked 哨兵：轮询回调不重入（查询比间隔慢时）
    private bool _disposed;

    /// <summary>轮询到新状态 / 提供方切换时触发（可能来自后台线程，界面自行切回 UI 线程）</summary>
    public event Action<MultiplayerSnapshot>? Changed;

    public MultiplayerService(
        DownloadService download,
        Func<LauncherSettings> settings,
        Func<string?> defaultNickname,
        Action? saveSettings = null)
    {
        _download = download;
        _settings = settings;
        _defaultNickname = defaultNickname;
        _saveSettings = saveSettings;

        // 内置提供方：陶瓦联机
        Register(new ProtocolMultiplayerProvider(MultiplayerSdkProtocol.Terracotta, download));
        // 第三方联机 SDK：清单目录里的每一份 JSON 一个提供方
        foreach (var protocol in MultiplayerSdkManifests.LoadAll(LauncherPaths.MultiplayerSdkDir))
            Register(new ProtocolMultiplayerProvider(protocol, download));
    }

    /// <summary>
    /// 登记提供方（代码式 SDK 集成的入口：实现 <see cref="IMultiplayerProvider"/> 后调这里）。
    /// 同 id 重复登记时首个生效——内置陶瓦联机不会被第三方清单顶掉。
    /// </summary>
    public void Register(IMultiplayerProvider provider)
    {
        lock (_gate)
        {
            if (!_providers.TryAdd(provider.Id, provider)) return;
            _infos.Add(new MultiplayerProviderInfo(
                provider.Id, provider.DisplayName, provider.Vendor, provider.VendorUrl,
                provider.Description, BuiltIn: provider.Id == "terracotta"));
        }
    }

    public IReadOnlyList<MultiplayerProviderInfo> Providers
    {
        get { lock (_gate) return _infos.ToList(); }
    }

    /// <summary>当前提供方（设置里持久化的 id；失效时退回第一个）</summary>
    public IMultiplayerProvider Active
    {
        get
        {
            var wanted = _settings().MultiplayerProvider;
            lock (_gate)
            {
                if (wanted is { Length: > 0 } && _providers.TryGetValue(wanted, out var p)) return p;
                return _providers.Values.First();
            }
        }
    }

    public MultiplayerSnapshot? Snapshot { get; private set; }

    /// <summary>切换提供方（持久化；切换会重置会话跟踪，下一次操作重新拉起对应核心）</summary>
    public void SelectProvider(string id)
    {
        if (Active.Id == id) return;
        _settings().MultiplayerProvider = id;
        _saveSettings?.Invoke();
        Snapshot = null;
    }

    /// <summary>联机用的玩家名（空则跟随当前账户名）</summary>
    public string Nickname
    {
        get
        {
            var configured = _settings().MultiplayerNickname;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            return _defaultNickname() is { Length: > 0 } fallback ? fallback : "Player";
        }
        set
        {
            _settings().MultiplayerNickname = value.Trim();
            _saveSettings?.Invoke();
        }
    }

    /// <summary>节点地区偏好：auto / cn / global（同房间玩家接同一地区节点最稳）</summary>
    public string NodeRegion
    {
        get
        {
            var r = _settings().MultiplayerNodeRegion;
            return r is "cn" or "global" ? r : "auto";
        }
        set
        {
            _settings().MultiplayerNodeRegion = value is "cn" or "global" ? value : "auto";
            _saveSettings?.Invoke();
        }
    }

    /// <summary>第三方 SDK 清单目录（界面上的「打开目录」用）</summary>
    public static string SdkManifestDir => LauncherPaths.MultiplayerSdkDir;

    // ── 状态 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询一次状态。**不会**因为查询而拉起联机核心：只有确认本地已有活着的会话才会去问状态。
    /// </summary>
    public async Task<MultiplayerSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        var p = Active;
        var running = false;
        var state = MultiplayerState.Idle;
        string? error = null;

        try
        {
            running = await p.ProbeSessionAsync(ct);
            if (running) state = await p.FetchStateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var snap = new MultiplayerSnapshot(
            p.Id, p.DisplayName, p.Vendor, p.VendorUrl,
            new MultiplayerCoreStatus(p.Installed, p.InstalledVersion, running),
            state, error);
        Snapshot = snap;
        return snap;
    }

    /// <summary>开始轮询（页面加载时调用）；间隔 1.5 秒，查询期间不重入</summary>
    public void StartPolling()
    {
        lock (_gate)
        {
            if (_disposed || _poller is not null) return;
            _poller = new Timer(_ => _ = PollAsync(), null,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1.5));
        }
    }

    /// <summary>停止轮询（页面卸载时调用）</summary>
    public void StopPolling()
    {
        lock (_gate)
        {
            _poller?.Dispose();
            _poller = null;
        }
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var snap = await RefreshAsync();
            Changed?.Invoke(snap);
        }
        catch
        {
            // 轮询失败静默：下一轮再试，别把界面刷成错误态
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    // ── 操作 ─────────────────────────────────────────────────────────────

    /// <summary>安装当前提供方的核心；同时登记到调度中心（可在任务中心看到进度/取消）</summary>
    public async Task InstallCoreAsync(IProgress<MultiplayerProgress>? progress, CancellationToken ct)
    {
        var task = TaskCenterService.Instance.Begin($"安装{Active.DisplayName}核心");
        try
        {
            await Active.InstallCoreAsync(progress, ct);
            TaskCenterService.Instance.Complete(task, "安装完成");
        }
        catch (OperationCanceledException)
        {
            TaskCenterService.Instance.Cancel(task);
            throw;
        }
        catch (Exception ex)
        {
            TaskCenterService.Instance.Fail(task, ex.Message);
            throw;
        }
    }

    /// <summary>创建房间：先确保会话、再拉节点，最后让核心开始扫描本机局域网世界</summary>
    public async Task HostAsync(CancellationToken ct)
    {
        var p = Active;
        await p.EnsureSessionAsync(ct);
        var nodes = await p.FetchNodesAsync(NodeRegion, ct);
        await p.HostAsync(Nickname, nodes, ct);
        LauncherLog.Write("multiplayer", $"{p.Id} 创建房间（玩家名 {Nickname}，节点 {nodes.Count} 个）");
    }

    /// <summary>加入房间</summary>
    public async Task JoinAsync(string room, CancellationToken ct)
    {
        var p = Active;
        await p.EnsureSessionAsync(ct);
        var nodes = await p.FetchNodesAsync(NodeRegion, ct);
        await p.JoinAsync(room.Trim(), Nickname, nodes, ct);
        LauncherLog.Write("multiplayer", $"{p.Id} 加入房间 {room.Trim()}（玩家名 {Nickname}）");
    }

    /// <summary>回到空闲（不断开会话进程）</summary>
    public Task IdleAsync(CancellationToken ct) => Active.IdleAsync(ct);

    /// <summary>停止联机：先回空闲，再结束本启动器拉起的核心进程</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await Active.StopSessionAsync(ct);
        await RefreshAsync(ct);
    }

    public Task<string> FetchLogsAsync(CancellationToken ct) => Active.FetchLogsAsync(ct);

    /// <summary>拉取公共节点（给界面显示"本次用了几个节点"）</summary>
    public Task<IReadOnlyList<string>> FetchNodesAsync(CancellationToken ct) =>
        Active.FetchNodesAsync(NodeRegion, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        lock (_gate)
        {
            foreach (var p in _providers.Values) p.Dispose();
            _providers.Clear();
            _infos.Clear();
        }
    }
}
