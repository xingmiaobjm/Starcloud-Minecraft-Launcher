// 本机局域网世界探测：监听 Minecraft 的 LAN 广播（UDP 4445 → 224.0.2.60）。
//
// 为什么需要它：陶瓦核心的「开房」是一次性扫描 —— 扫一次没找到局域网世界就停在那里。
// 房主建房时游戏往往还没启动（或还没点「对局域网开放」），那次扫描必然落空，
// 之后就算开了 LAN 也没人再触发扫描（表现就是「等了好久才好」）。
//
// MC 在游戏内点「对局域网开放」后会每秒往 224.0.2.60:4445 发一次广播，内容是
//     [MOTD]世界名[/MOTD][AD]端口[/AD]
// 收到广播就说明局域网世界已经开好 —— 这时再让陶瓦去扫描，能立刻命中。
//
// 纯 UDP、不依赖游戏进程：游戏重启、换端口都能重新发现。

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Starcloud.Core.Services.Rooms;

public sealed class LanProbeService : IDisposable
{
    public const int LanPort = 4445;
    private const string MulticastGroup = "224.0.2.60";

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();

    /// <summary>发现局域网世界（世界名, 端口）。同一世界每秒会触发多次，调用方自行节流。</summary>
    public event Action<string, int>? WorldFound;

    public bool Running => _udp is not null;

    public void Start()
    {
        lock (_gate)
        {
            if (_udp is not null) return;
            try
            {
                var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress, true);
                // 绑任意地址的 4445：广播是发给 224.0.2.60 的组播，绑到具体网卡会漏（多网卡机器）
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanPort));
                try { udp.JoinMulticastGroup(IPAddress.Parse(MulticastGroup)); }
                catch { /* 组播加入失败仍可能收到广播包，不致命 */ }

                _udp = udp;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));
            }
            catch (Exception ex)
            {
                LauncherLog.Write("lanprobe", $"监听 {LanPort} 失败：{ex.Message}");
                _udp = null;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _cts?.Dispose();
            _cts = null;
            _udp = null;
        }
    }

    public void Dispose() => Stop();

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (ParsePort(text) is not { } port || port <= 0) continue;
                WorldFound?.Invoke(ParseMotd(text), port);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // 瞬时错误（ICMP 端口不可达等）不停循环
                try { await Task.Delay(500, ct); } catch { return; }
            }
        }
    }

    /// <summary>从广播包里取端口：[AD]25565[/AD]。</summary>
    public static int? ParsePort(string payload)
    {
        const string open = "[AD]";
        const string close = "[/AD]";
        var start = payload.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = payload.IndexOf(close, start, StringComparison.Ordinal);
        if (end <= start) return null;
        return int.TryParse(payload[start..end], out var port) ? port : null;
    }

    /// <summary>从广播包里取世界名：[MOTD]xxx[/MOTD]。</summary>
    public static string ParseMotd(string payload)
    {
        const string open = "[MOTD]";
        const string close = "[/MOTD]";
        var start = payload.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return "";
        start += open.Length;
        var end = payload.IndexOf(close, start, StringComparison.Ordinal);
        return end <= start ? "" : payload[start..end];
    }
}
