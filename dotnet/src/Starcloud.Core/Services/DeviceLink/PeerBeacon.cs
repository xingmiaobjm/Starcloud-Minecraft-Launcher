// 设备发现：UDP 广播 beacon（设计 §3）。
//
// 发送端每 3 秒向 255.255.255.255:47777 广播一条 JSON（uid / 设备名 / TCP 端口）；
// 监听端只收「uid 与本机相同」的包——不同 Starcloud ID 的设备互相不可见。
// peer 表带 lastSeen，超时（15s）剔除；线程安全：表操作全部锁内。

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Starcloud.Core.Services;

/// <summary>UDP beacon：广播本机 + 汇聚对端。Start/Stop 可重入。</summary>
public sealed class PeerBeacon : IDisposable
{
    /// <summary>本实例唯一标识：广播会从本机网卡回环收到自己（真实网卡地址，非 Loopback），
    /// 只认 iid 才能把"自己"从对端表里挡住。</summary>
    private readonly string _iid = Guid.NewGuid().ToString("N");
    private readonly string _uid;
    private readonly Func<string> _deviceName;
    private readonly Func<int> _linkPort;
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private UdpClient? _listener;
    private UdpClient? _sender;
    private CancellationTokenSource? _cts;
    private Task? _sendLoop;
    private Task? _recvLoop;

    /// <summary>设备上线（name, address）：UI 层借此弹「已连接」横幅。事件在接收循环线程触发，订阅方自行调度。</summary>
    public event Action<string, string>? PeerUp;

    /// <summary>设备离线（name）：剔除过期项时触发。</summary>
    public event Action<string>? PeerDown;

    public PeerBeacon(string uid, Func<string> deviceName, Func<int> linkPort)
    {
        _uid = uid;
        _deviceName = deviceName;
        _linkPort = linkPort;
    }

    /// <summary>当前在线对端快照（剔除超时项后按名称排序）；剔除时对每个离线设备触发 <see cref="PeerDown"/></summary>
    public List<PeerInfo> Snapshot()
    {
        var cutoff = DateTime.Now - BeaconPayload.OfflineAfter;
        List<PeerInfo> result = [];
        foreach (var kv in _peers)
        {
            if (kv.Value.LastSeen < cutoff)
            {
                if (_peers.TryRemove(kv.Key, out var gone))
                {
                    try { PeerDown?.Invoke(gone.Name); } catch { /* 订阅者异常不阻断 */ }
                }
            }
            else result.Add(kv.Value);
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>按地址:端口查一个对端（同步发起时用）；找不到返回 null</summary>
    public PeerInfo? Find(string address, int port) =>
        _peers.TryGetValue($"{address}:{port}", out var p) ? p : null;

    public void Start()
    {
        if (_sendLoop is not null) return;
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new UdpClient(AddressFamily.InterNetwork);
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPayload.BeaconPort));
            _sender = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
            };
        }
        catch
        {
            DisposeClientObjects();
            _cts.Dispose();
            _cts = null;
            throw; // 端口被占（另一实例已在跑）由调用方决定降级
        }
        _sendLoop = SendLoopAsync(_cts.Token);
        _recvLoop = RecvLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        // 超时给得短：两个循环体都以 ct 取消为退出条件，正常应在毫秒级退出；
        // 这里只是兜底（Stop 由调用方放在后台线程，见 DeviceLinkService 注释）
        try { _sendLoop?.Wait(TimeSpan.FromMilliseconds(800)); } catch { /* 静默 */ }
        try { _recvLoop?.Wait(TimeSpan.FromMilliseconds(800)); } catch { /* 静默 */ }
        _sendLoop = null;
        _recvLoop = null;
        DisposeClientObjects();
        _cts?.Dispose();
        _cts = null;
        _peers.Clear();
    }

    private void DisposeClientObjects()
    {
        _listener?.Dispose();
        _sender?.Dispose();
        _listener = null;
        _sender = null;
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var any = new IPEndPoint(IPAddress.Broadcast, BeaconPayload.BeaconPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var payload = new BeaconPayload(BeaconPayload.CurrentVersion, _uid,
                    _deviceName(), _linkPort(), BeaconPayload.AppId, _iid).Encode();
                await _sender!.SendAsync((ReadOnlyMemory<byte>)payload, any, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 广播失败（无网络 / 防火墙）不终止循环：下个周期再试
            }
            try { await Task.Delay(BeaconPayload.Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RecvLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _listener!.ReceiveAsync(ct);
                var payload = BeaconPayload.Decode(res.Buffer);
                if (payload is null) continue; // 非法包：丢弃
                // 不同 Starcloud ID 的设备也保留：只用于「按 SHA1 提供游戏文件」的 P2P 加速，
                // 存档同步与其余命令仍由服务端握手挡在同账号范围内
                if (payload.Iid == _iid) continue;                    // 自己网卡的广播回环：丢弃
                var key = $"{res.RemoteEndPoint.Address}:{payload.Port}";
                var isNew = !_peers.ContainsKey(key);
                _peers[key] = new PeerInfo
                {
                    Uid = payload.Uid,
                    Name = payload.Name,
                    Port = payload.Port,
                    Address = res.RemoteEndPoint.Address.ToString(),
                    LastSeen = DateTime.Now,
                };
                if (isNew)
                {
                    try { PeerUp?.Invoke(payload.Name, res.RemoteEndPoint.Address.ToString()); }
                    catch { /* 订阅者异常不阻断 */ }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 单包解析 / socket 瞬时错误不影响监听
            }
        }
    }

    public void Dispose() => Stop();
}
