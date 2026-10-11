using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Starcloud.Multiplayer.Core.Net;

/// <summary>
/// 一个只回答「服务器列表探测」的假 Minecraft 服务端。房主用 <c>--selftest</c> 开房时，
/// 它顶替真实游戏占住 <c>mc_port</c>——于是不用真的开游戏就能验证隧道。
/// <para>同时实现现代（Handshake + Status）与老式（0xFE → 0xFF）两套应答。</para>
/// </summary>
public sealed class FakeMinecraftServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly string _motd;
    private readonly string _version;

    public int Port { get; }

    private FakeMinecraftServer(TcpListener listener, string motd, string version)
    {
        _listener = listener;
        _motd = motd;
        _version = version;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <param name="port">想要占住的端口；传 0 表示随便给一个空闲端口。</param>
    public static FakeMinecraftServer Start(int port, string motd = "Starcloud 联机自检", string version = "starcloud/selftest")
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        return new FakeMinecraftServer(listener, motd, version);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var stream = client.GetStream();
                    await HandleAsync(stream, ct);
                }
                catch (Exception)
                {
                    // 探测方随时可能断开，这里静默收场即可。
                }
                finally
                {
                    client.Dispose();
                }
            }, ct);
        }
    }

    private async Task HandleAsync(NetworkStream stream, CancellationToken ct)
    {
        var first = new byte[1];
        await McWire.ReadExactlyAsync(stream, first, ct);

        if (first[0] == 0xFE)
        {
            await RespondLegacyAsync(stream, ct);
            return;
        }

        // 现代协议：首字节是包长的 VarInt 开端。
        var length = first[0] & 0x7F;
        if ((first[0] & 0x80) != 0)
        {
            var shift = 7;
            while (true)
            {
                var next = new byte[1];
                await McWire.ReadExactlyAsync(stream, next, ct);
                length |= (next[0] & 0x7F) << shift;
                if ((next[0] & 0x80) == 0) break;
                shift += 7;
            }
        }

        var packet = new byte[length];
        await McWire.ReadExactlyAsync(stream, packet, ct);
        // Handshake 包里带了协议号/地址/端口/next state；这里只关心「有没有握手」，
        // 于是直接等下一个包（Status Request）并回一份状态。
        _ = await McWire.ReadVarIntAsync(stream, ct);   // Status Request 的包长
        _ = await McWire.ReadVarIntAsync(stream, ct);   // 包 id（0x00）

        var json = $"{{\"version\":{{\"name\":\"{Escape(_version)}\",\"protocol\":-1}},"
                 + $"\"players\":{{\"max\":20,\"online\":1}},"
                 + $"\"description\":{{\"text\":\"{Escape(_motd)}\"}}}}";
        using var payload = new MemoryStream();
        McWire.WriteVarInt(payload, 0x00);
        McWire.WriteString(payload, json);
        var payloadBytes = payload.ToArray();

        McWire.WriteVarInt(stream, payloadBytes.Length);
        await stream.WriteAsync(payloadBytes, ct);
        await stream.FlushAsync(ct);
    }

    private async Task RespondLegacyAsync(NetworkStream stream, CancellationToken ct)
    {
        // §1\0协议号\0版本\0MOTD\0在线\0上限
        var text = "\u00a71\0-1\0" + _version + "\0" + _motd + "\01\020";
        var payload = Encoding.BigEndianUnicode.GetBytes(text);
        var chars = text.Length;

        var header = new byte[3];
        header[0] = 0xFF;
        header[1] = (byte)(chars >> 8);
        header[2] = (byte)(chars & 0xFF);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // 幂等收尾。
        }

        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // 取消导致的异常不必上报。
        }

        _cts.Dispose();
    }
}
