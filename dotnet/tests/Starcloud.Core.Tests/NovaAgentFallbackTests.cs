// Nova Agent 通道守卫：
//   · 自接模型用 400 拒掉带 tools 的请求体时（不少中转 / 本地服务都会），
//     自动去掉 tools 重发一次，用户仍然能拿到回答，而不是只看到「400」；
//   · 失败原因写进启动器日志，可追溯。

using System.Net;
using System.Net.Sockets;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class NovaAgentFallbackTests
{
    [Fact]
    public async Task 上游用400拒掉工具时去掉工具重试并给出回答()
    {
        using var server = new ToolsHatingServer();
        var settings = Fixture(server.BaseUrl, out var nova, out var loop);

        var text = new StringBuilder();
        var events = 0;
        await foreach (var evt in loop.RunAsync("你好", settings, null))
        {
            events++;
            if (evt is AgentTextDelta d) text.Append(d.Delta);
        }

        Assert.True(events > 0, "一条事件都没有：通道被 400 打断后没有走重试");
        Assert.Contains("pong", text.ToString());
        Assert.Equal(2, server.Requests);                       // ① 带 tools 被拒 ② 去掉 tools
        Assert.DoesNotContain("\"tools\"", server.LastBody);    // 第二次的请求体里确实没有 tools
    }

    [Fact]
    public async Task 正常通道不会多发一次请求()
    {
        using var server = new ToolsHatingServer(acceptTools: true);
        var settings = Fixture(server.BaseUrl, out _, out var loop);

        await foreach (var _ in loop.RunAsync("你好", settings, null)) { }

        Assert.Equal(1, server.Requests);
        Assert.Contains("\"tools\"", server.LastBody);
    }

    private static LauncherSettings Fixture(
        string baseUrl, out NovaService nova, out NovaAgentLoop loop)
    {
        var settings = new LauncherSettings { DotMinecraft = Path.Combine(Path.GetTempPath(), "sc-fallback") };
        settings.NovaLlmProfiles.Add(new NovaLlmProfile
        {
            BaseUrl = baseUrl,
            ApiKey = "sk-test",
            Model = "local-model",
        });

        var download = new DownloadService(() => settings);
        var cache = new ResourceCacheService(() => settings);
        var manifest = new ManifestService(download, cache);
        var mods = new ModrinthService(download, cache);
        var installer = new InstallerService(download);
        var java = new JavaService(download);
        var launcher = new GameLauncher(installer, java);
        var tools = new NovaToolRegistry(new NovaToolContext
        {
            Manifest = manifest,
            Installer = installer,
            Mods = mods,
            Library = new ContentLibraryService(download, mods, manifest, () => settings),
            Settings = () => settings,
        });
        nova = new NovaService(
            mods, new GameDiagnosticsService(installer, manifest), new ModConflictService(),
            new NovaPermissionStore(), new NovaActionLog(), new NovaMomentsService(),
            new NovaInviteService(installer, manifest, java, launcher, download),
            new SaveService(), tools, starcloudId: null);
        loop = new NovaAgentLoop(nova, tools);
        return settings;
    }

    /// <summary>
    /// 假的自接模型服务端：默认见到 tools 就回 400（像不少中转 / 本地服务那样），
    /// 不带 tools 时正常流式返回 pong。裸 TcpListener：非管理员也能起。
    /// </summary>
    private sealed class ToolsHatingServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _acceptTools;
        private int _requests;
        private string _lastBody = "";

        public ToolsHatingServer(bool acceptTools = false)
        {
            _acceptTools = acceptTools;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
        public int Requests => Volatile.Read(ref _requests);
        public string LastBody => _lastBody;

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var headerBytes = new List<byte>(1024);
                    var one = new byte[1];
                    while (headerBytes.Count < 64 * 1024)
                    {
                        if (await stream.ReadAsync(one) <= 0) return;
                        headerBytes.Add(one[0]);
                        if (headerBytes.Count >= 4 &&
                            headerBytes[^4] == 13 && headerBytes[^3] == 10 &&
                            headerBytes[^2] == 13 && headerBytes[^1] == 10)
                            break;
                    }
                    var header = Encoding.UTF8.GetString(headerBytes.ToArray());
                    var contentLength = 0;
                    foreach (var line in header.Split("\r\n"))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line[15..].Trim(), out contentLength);

                    var buffer = new byte[contentLength];
                    var read = 0;
                    while (read < contentLength)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(read, contentLength - read));
                        if (n <= 0) break;
                        read += n;
                    }
                    var body = Encoding.UTF8.GetString(buffer, 0, read);
                    if (read > 0) _lastBody = body;
                    Interlocked.Increment(ref _requests);

                    if (!_acceptTools && body.Contains("\"tools\"", StringComparison.Ordinal))
                    {
                        await ReplyAsync(stream, 400, "application/json",
                            "{\"error\":{\"message\":\"请求体错误：不支持 tools\",\"type\":\"invalid_request_error\"}}");
                        return;
                    }

                    await ReplyAsync(stream, 200, "text/event-stream",
                        "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"m\"," +
                        "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"pong\"}," +
                        "\"finish_reason\":null}]}\n\ndata: [DONE]\n\n");
                }
                catch
                {
                    // 客户端提前断开：忽略
                }
            }
        }

        private async Task ReplyAsync(NetworkStream stream, int status, string contentType, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\n" +
                       $"Content-Type: {contentType}\r\n" +
                       $"Content-Length: {payload.Length}\r\n" +
                       "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
    }
}
