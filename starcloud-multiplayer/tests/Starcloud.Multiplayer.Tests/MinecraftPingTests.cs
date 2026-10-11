using System.Net;
using System.Net.Sockets;
using System.Text;
using Starcloud.Multiplayer.Core.Net;
using Xunit;

namespace Starcloud.Multiplayer.Tests;

public class MinecraftPingTests
{
    [Fact]
    public async Task 现代协议探测假服务端成功()
    {
        await using var server = FakeMinecraftServer.Start(0, "自检测试", "starcloud/selftest");

        var result = await MinecraftPing.ProbeAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(5));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("starcloud/selftest", result.Version);
        Assert.Equal("自检测试", result.Motd);
    }

    [Fact]
    public async Task 假服务端也认老式协议()
    {
        await using var server = FakeMinecraftServer.Start(0, "老式", "old-version");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        stream.WriteByte(0xFE);
        await stream.FlushAsync();

        var head = new byte[1];
        await stream.ReadExactlyAsync(head);
        Assert.Equal(0xFF, head[0]);

        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes);
        var chars = (lengthBytes[0] << 8) | lengthBytes[1];
        var payload = new byte[chars * 2];
        await stream.ReadExactlyAsync(payload);

        var text = Encoding.BigEndianUnicode.GetString(payload);
        Assert.Contains("old-version", text);
        Assert.Contains("老式", text);
    }

    [Fact]
    public async Task 现代协议被拒时回退老式协议()
    {
        // 一个只答老式协议的裸服务端：第一条连接（现代握手）直接断开，第二条（0xFE）才应答。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var first = new byte[1];
                await stream.ReadExactlyAsync(first);
                if (first[0] != 0xFE) continue;   // 现代握手：不应答，让探针回退

                var text = "\u00a71\0-1\0legacy-version\0legacy-motd\0" + "1" + "\0" + "20";
                var payload = Encoding.BigEndianUnicode.GetBytes(text);
                await stream.WriteAsync(new byte[] { 0xFF, (byte)(text.Length >> 8), (byte)(text.Length & 0xFF) });
                await stream.WriteAsync(payload);
            }
        });

        try
        {
            var result = await MinecraftPing.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));
            Assert.True(result.Ok, result.Error);
            Assert.Equal("legacy-version", result.Version);
            Assert.Equal("legacy-motd", result.Motd);
        }
        finally
        {
            listener.Stop();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task 端口没人监听时探测失败()
    {
        var port = LocalPorts.FindFreeTcpPort();

        var result = await MinecraftPing.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(2));

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }
}
