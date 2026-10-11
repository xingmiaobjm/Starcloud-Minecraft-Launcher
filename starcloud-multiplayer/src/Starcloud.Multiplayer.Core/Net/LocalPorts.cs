using System.Net;
using System.Net.Sockets;

namespace Starcloud.Multiplayer.Core.Net;

/// <summary>本机端口相关的小工具。</summary>
public static class LocalPorts
{
    /// <summary>要一个空闲 TCP 端口：优先用 <paramref name="preferred"/>，被占就退到系统分配。</summary>
    public static int FindFreeTcpPort(int preferred = 0)
    {
        if (preferred > 0 && IsFree(preferred)) return preferred;
        return AllocateEphemeral();
    }

    public static bool IsFree(int port)
    {
        // 必须绑 IPAddress.Any（0.0.0.0）而不是 Loopback：Windows 允许在 0.0.0.0:port
        // 已被占用时仍绑定 127.0.0.1:port，用 Loopback 试探会误判「空闲」；
        // 而 EasyTier 的端口转发随后绑 0.0.0.0:port 就会 os error 10048 直接退出。
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int AllocateEphemeral()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>本机某个 TCP 端口是否有人监听（房主开房前用它确认「游戏里已对局域网开放」）。</summary>
    public static async Task<bool> IsTcpPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
