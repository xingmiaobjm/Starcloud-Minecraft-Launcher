// 元数据请求的有界性回归：镜像假死时不能无限转圈。
//
// 真实故障（2026-09-22 用户报告「点下载一直转进度、无带宽、调度中心没有任务」）：
// BMCLAPI 把请求 302 交给 P2P 节点，实测约六成节点「TCP 连上了但永不应答」。
// 元数据请求当时用单个首选源 + 无限超时，撞上死节点就永远等下去：
// 无字节、无进度、无报错 —— 表现为启动器界面一直转圈。
// 现在：单个源 MetadataTimeout（默认 12 秒）内无响应即换下一个源，官方兜底；全都不行才报错。

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>下载链路会往进程内单例 TaskCenterService 写聚合任务，
/// 与 TaskCenterServiceTests 抢同一个单例 —— 必须同集合串行。</summary>
[Collection("进程内单例")]
public class MetadataFetchTests
{
    private const string MojangMetaUrl =
        "https://piston-meta.mojang.com/v1/packages/" +
        "11cfd0bd10c61a782a2e75f06011310becb5c5b2/1.19.4.json";

    [Fact]
    public void 元数据超时是有界的默认值() =>
        Assert.True(DownloadService.MetadataTimeout > TimeSpan.Zero &&
                    DownloadService.MetadataTimeout <= TimeSpan.FromSeconds(15),
            "元数据请求必须有界：撞上假死镜像必须换源，不能无限等");

    [Fact]
    public void 站点页面型镜像不再进入自动池()
    {
        // RainYun 镜像根下没有 Minecraft 文件，请求返回的是站点 HTML（实测）。
        // 它留在自动池里只会制造「HTTP 200 但不是数据」这种失败模式，故退出自动竞速；
        // 用户在设置里显式选它时仍然生效。
        var auto = new DownloadSourceRegistry(() => new LauncherSettings { DownloadSource = "auto" });
        Assert.DoesNotContain(SourceKind.Rainyun, auto.RacingPool());

        var pinned = new DownloadSourceRegistry(
            () => new LauncherSettings { DownloadSource = "rainyun" });
        Assert.Equal([SourceKind.Rainyun], pinned.RacingPool());
    }

    [Fact]
    public Task 镜像假死时元数据请求有界失败而不是永远转圈() => WithDirectHttpAsync(async () =>
    {
        var saved = DownloadService.MetadataTimeout;
        DownloadService.MetadataTimeout = TimeSpan.FromSeconds(2);
        try
        {
            using var stall = new StallServer();
            var settings = new LauncherSettings
            {
                DownloadSource = "custom",                                  // 池里只有它，专测「不换源也不许挂死」
                DownloadCustomMirrorUrl = $"127.0.0.1:{stall.Port}",        // 改写到本地假死服务器
            };
            var service = new DownloadService(() => settings);

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<IOException>(() => service.GetStringAsync(MojangMetaUrl));
            sw.Stop();

            Assert.True(stall.Connections >= 1, "假死服务器应该收到过连接");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"耗时 {sw.Elapsed.TotalSeconds:F1}s：超时没生效，仍在无限等待");
            Assert.Contains("1.19.4.json", ex.Message);
        }
        finally
        {
            DownloadService.MetadataTimeout = saved;
        }
    });

    /// <summary>本机系统代理会把连回环的请求也截走，端到端用例先强制直连（跑完恢复）</summary>
    private static async Task WithDirectHttpAsync(Func<Task> body)
    {
        var saved = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();
        try
        {
            await body();
        }
        finally
        {
            HttpClient.DefaultProxy = saved;
        }
    }

    /// <summary>只接受连接、永不应答的服务器（模拟 BMCLAPI 的假死 P2P 节点）</summary>
    private sealed class StallServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _held = [];
        private int _connections;

        public StallServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref _connections);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_held)
            {
                foreach (var client in _held) client.Dispose();
                _held.Clear();
            }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }
                Interlocked.Increment(ref _connections);
                lock (_held) _held.Add(client);   // 捏着不放：不读不写，让对端一直等
            }
        }
    }
}
