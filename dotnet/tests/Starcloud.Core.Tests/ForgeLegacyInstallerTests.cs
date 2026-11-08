// 旧版 Forge 安装器回归（issue #11）：
// GTNH 这类 1.7.10 整合包声明 forge=10.13.4.1614，官方两段坐标 404，备选三段坐标下到的
// 是 2016 年的旧版安装器 —— 它的 install_profile.json 只有 install+versionInfo（versionInfo
// 是内嵌对象、没有 processors），不认识 --installClient，跑起来直接抛
// joptsimple.UnresolvedOptionException。修复：读 profile 判格式，旧版解包手工安装
// （universal jar 落 libraries + versionInfo 写版本 JSON），不再跑安装程序。

using System.Net;
using System.Net.Sockets;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

[Collection("进程内单例")]
public class ForgeLegacyInstallerTests
{
    private const string LegacyProfileJson = """
{
  "install": {
    "profileName": "Forge",
    "target": "1.7.10-Forge10.13.4.1614-1.7.10",
    "path": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10",
    "version": "Forge 10.13.4.1614-1.7.10",
    "filePath": "forge-1.7.10-10.13.4.1614-1.7.10-universal.jar",
    "minecraft": "1.7.10"
  },
  "versionInfo": {
    "id": "1.7.10-Forge10.13.4.1614-1.7.10",
    "time": "2016-02-06T02:29:34-0500",
    "releaseTime": "2016-02-06T02:29:34-0500",
    "type": "release",
    "minecraftArguments": "--username ${auth_player_name} --version ${version_name}",
    "mainClass": "net.minecraft.launchwrapper.Launch",
    "assets": "1.7.10",
    "inheritsFrom": "1.7.10",
    "libraries": [
      { "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10" }
    ]
  }
}
""";

    [Fact]
    public void 旧版profile_能识别()
    {
        var path = BuildFakeInstaller(LegacyProfileJson);
        try
        {
            var profile = ForgeInstallerService.TryReadLegacyForgeProfile(path);
            Assert.NotNull(profile);
            Assert.Equal("1.7.10-Forge10.13.4.1614-1.7.10", profile!.VersionId);
            Assert.Equal("forge-1.7.10-10.13.4.1614-1.7.10-universal.jar", profile.FilePath);
            Assert.Equal("net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", profile.MavenCoord);
            Assert.Equal("1.7.10", profile.Minecraft);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 新版profile_有processors_不判为旧版()
    {
        const string modern = """
{
  "install": { "path": "net.minecraftforge:forge:1.20.1-47.4.0", "filePath": "-client.jar" },
  "versionInfo": "version.json",
  "processors": [ { "sides": ["client"], "jar": "..." } ]
}
""";
        var path = BuildFakeInstaller(modern);
        try
        {
            Assert.Null(ForgeInstallerService.TryReadLegacyForgeProfile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task 旧版安装器_解包安装_不跑installClient()
    {
        var saved = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();
        try
        {
            // 假安装器：旧版 profile + 1KB 的假 universal jar
            var universalPayload = Encoding.ASCII.GetBytes(new string('U', 1024));
            var installerPath = BuildFakeInstaller(LegacyProfileJson, ("forge-1.7.10-10.13.4.1614-1.7.10-universal.jar", universalPayload));
            var payload = File.ReadAllBytes(installerPath);
            File.Delete(installerPath);

            using var server = new StaticServer(payload);
            var dotMc = Path.Combine(Path.GetTempPath(), "sc-legacy-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(dotMc, "versions", "1.7.10"));   // 原版已在位（前置条件）
            try
            {
                var settings = new LauncherSettings { DotMinecraft = dotMc, DownloadSource = "auto" };
                var downloader = new DownloadService(() => settings);
                var forge = new ForgeInstallerService(downloader, new JavaService(downloader));

                var installed = await forge.InstallViaInstallerAsync(
                    server.Url + "forge-legacy-installer.jar", "1.7.10", 8, settings, null, CancellationToken.None);

                // 装出的目录名 = versionInfo.id（不是拼出来的 1.7.10-forge-10.13.4.1614）
                Assert.Equal("1.7.10-Forge10.13.4.1614-1.7.10", installed);
                var versionJson = Path.Combine(dotMc, "versions", installed!, $"{installed}.json");
                Assert.True(File.Exists(versionJson), "版本 JSON 必须写入，否则实例在列表里隐身");
                var json = File.ReadAllText(versionJson);
                Assert.Contains("\"inheritsFrom\"", json);
                Assert.Contains("1.7.10", json);
                Assert.Contains("launchwrapper", json);

                // universal jar 落在 libraries 的 maven 坐标位
                var lib = ForgeInstallerService.MavenArtifactPath(dotMc,
                    "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10",
                    "forge-1.7.10-10.13.4.1614-1.7.10-universal.jar");
                Assert.True(File.Exists(lib));
                Assert.Equal(universalPayload.Length, new FileInfo(lib).Length);
            }
            finally
            {
                try { Directory.Delete(dotMc, recursive: true); } catch { }
            }
        }
        finally
        {
            HttpClient.DefaultProxy = saved;
        }
    }

    [Fact]
    public void Maven坐标转路径_点分层级()
    {
        var p = ForgeInstallerService.MavenArtifactPath(
            "C:\\mc", "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", "x.jar");
        Assert.Equal(Path.Combine("C:\\mc", "libraries", "net", "minecraftforge", "forge",
            "1.7.10-10.13.4.1614-1.7.10", "x.jar"), p);
    }

    /// <summary>在临时目录里拼一个假安装器 zip（可带额外条目），返回路径。</summary>
    private static string BuildFakeInstaller(string profileJson, (string Name, byte[] Data)? extra = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-forge-{Guid.NewGuid():N}.jar");
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = zip.CreateEntry("install_profile.json");
        using (var w = new StreamWriter(entry.Open()))
            w.Write(profileJson);
        if (extra is { } e)
        {
            var e2 = zip.CreateEntry(e.Name);
            using var s = e2.Open();
            s.Write(e.Data);
        }
        return path;
    }

    /// <summary>最小 HTTP 静态服务器：把一段字节当作任何 GET 的响应。</summary>
    private sealed class StaticServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _cts = new();

        public StaticServer(byte[] payload)
        {
            _payload = payload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/";
            _ = AcceptLoopAsync();
        }

        public string Url { get; }

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
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync();
                    if (requestLine is null) return;
                    for (var line = await reader.ReadLineAsync();
                         !string.IsNullOrEmpty(line);
                         line = await reader.ReadLineAsync()) { }

                    var head = $"HTTP/1.1 200 OK\r\nContent-Length: {_payload.Length}\r\n" +
                               "Content-Type: application/java-archive\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(_payload);
                    await stream.FlushAsync();
                }
                catch
                {
                    // 客户端提前断开是预期行为
                }
            }
        }
    }
}
