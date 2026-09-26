// 联机 SDK 层测试：清单解析（离线，随普通测试跑）+ 真机联调（需显式开启）
//
// 真机联调用 STARCLOUD_IT_MULTIPLAYER=1 开启：它会**真的**下载陶瓦联机核心（约 8 MB）
// 到数据目录、拉起核心会话、走一遍「空闲 → 创建房间 → 空闲 → 结束会话」。
// 默认关闭，避免普通测试跑出网络与进程。

using Starcloud.Core.Services;
using Starcloud.Core.Services.Multiplayer;
using Xunit;

namespace Starcloud.Core.Tests;

public class MultiplayerSdkTests
{
    private const string MinimalManifest = """
    {
      "id": "demo-link",
      "displayName": "示例联机",
      "vendor": "Demo SDK",
      "version": "1.2.3",
      "executableName": "demo-core.exe",
      "downloadUrls": ["https://example.com/demo-{version}-{platform}.tar.gz"],
      "launchArguments": ["--hmcl", "{port_file}"]
    }
    """;

    [Fact]
    public void 清单缺省字段回落到陶瓦同形协议()
    {
        var p = MultiplayerSdkManifests.Parse(MinimalManifest);
        Assert.Equal("demo-link", p.Id);
        Assert.Equal("示例联机", p.DisplayName);
        Assert.Equal("Demo SDK", p.Vendor);
        Assert.Equal("tar.gz", p.ArchiveKind);
        Assert.Equal("http.json", p.PortFile);
        Assert.Equal("/state", p.StatePath);
        Assert.Equal("/state/scanning", p.HostPath);
        Assert.Equal("/state/guesting", p.JoinPath);
        // 第三方清单不写 shutdownPath 时，停止联机只回空闲、不动它的进程（不猜别家的接口）
        Assert.Equal("", p.ShutdownPath);
        Assert.Equal("public_nodes", p.NodesQueryKey);
        // 状态表缺省时沿用官方命名
        Assert.Equal(MultiplayerPhase.HostReady, p.PhaseMap["host-ok"]);
        Assert.Equal(MultiplayerPhase.Failed, p.PhaseMap["exception"]);
    }

    [Fact]
    public void 清单支持自定义状态表与占位符展开()
    {
        var p = MultiplayerSdkManifests.Parse("""
        {
          "id": "custom_link",
          "displayName": "自定义联机",
          "version": "2.0.0",
          "executableName": "link-core",
          "downloadUrls": ["https://example.com/{version}/{platform}-{arch}.zip"],
          "archiveKind": "zip",
          "launchArguments": ["--port-file", "{port_file}"],
          "apiBase": "http://127.0.0.1:{port}",
          "phaseMap": { "ready": "host-ready", "joining": "guest-connecting", "boom": "failed" }
        }
        """);
        Assert.Equal("zip", p.ArchiveKind);
        Assert.Equal(MultiplayerPhase.HostReady, p.PhaseMap["ready"]);
        Assert.Equal(MultiplayerPhase.GuestConnecting, p.PhaseMap["joining"]);
        Assert.Equal(MultiplayerPhase.Failed, p.PhaseMap["boom"]);
        Assert.DoesNotContain("waiting", p.PhaseMap.Keys);
        Assert.Equal("http://127.0.0.1:1234/state", p.Expand(p.ApiBase, 1234) + p.StatePath);
        Assert.Contains(Platforms.PlatformTag, p.Expand(p.DownloadUrls[0]));
    }

    [Theory]
    [InlineData("""{"displayName":"x","version":"1","executableName":"a","downloadUrls":["u"],"launchArguments":["a"]}""")] // 缺 id
    [InlineData("""{"id":"../evil","displayName":"x","version":"1","executableName":"a","downloadUrls":["u"],"launchArguments":["a"]}""")] // 路径穿越
    [InlineData("""{"id":"terracotta","displayName":"x","version":"1","executableName":"a","downloadUrls":["u"],"launchArguments":["a"]}""")] // 与内置冲突
    [InlineData("""{"id":"ok","displayName":"x","version":"1","executableName":"a","launchArguments":["a"]}""")] // 缺下载地址
    [InlineData("""{"id":"ok","displayName":"x","version":"1","executableName":"a","downloadUrls":["u"]}""")] // 缺启动参数
    [InlineData("""{"id":"ok","displayName":"x","version":"1","executableName":"a","downloadUrls":["u"],"launchArguments":["a"],"phaseMap":{"x":"nonsense"}}""")]
    public void 非法清单被拒绝(string json) =>
        Assert.Throws<MultiplayerSdkManifestException>(() => MultiplayerSdkManifests.Parse(json));

    [Fact]
    public void 内置陶瓦描述指向官方发布包()
    {
        var p = MultiplayerSdkProtocol.Terracotta;
        Assert.Equal("terracotta", p.Id);
        Assert.Contains("Terracotta", p.Vendor);
        Assert.Equal("terracotta", Path.GetFileNameWithoutExtension(p.ExecutableName));
        Assert.Contains(p.DownloadUrls, u => u.Contains("github.com/burningtnt/Terracotta"));
        Assert.Contains(p.DownloadUrls, u => u.Contains("gitee.com/burningtnt/Terracotta"));
        Assert.Contains("windows-x86_64", p.Expand(p.DownloadUrls[0]));
        // 老版本（Tauri 架构）装过的核心目录：版本一致时会被复用，省一次 8 MB 下载
        Assert.Equal(Path.Combine(LauncherPaths.DataDir, "terracotta", "core"),
            Path.GetFullPath(p.Expand(p.LegacyCoreDir!)));
    }

    [Fact]
    public async Task 安装可复用旧版核心目录而不下载()
    {
        // 造一个"旧版装好的核心"：版本文件 + 可执行文件，下载地址故意指向不存在的域名
        var root = Path.Combine(Path.GetTempPath(), "starcloud-mp-legacy-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy-core");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "version.txt"), "9.9.9");
        await File.WriteAllTextAsync(Path.Combine(legacy, "fake-core.exe"), "stub");

        var protocol = MultiplayerSdkManifests.Parse($$"""
        {
          "id": "legacy-demo",
          "displayName": "旧版复用",
          "version": "9.9.9",
          "executableName": "fake-core.exe",
          "downloadUrls": ["https://invalid.invalid/{version}.tar.gz"],
          "launchArguments": ["--hmcl", "{port_file}"],
          "legacyCoreDir": "{{legacy.Replace("\\", "/")}}"
        }
        """);

        using var provider = new ProtocolMultiplayerProvider(protocol, new DownloadService(() => LauncherSettings.Load()));
        Assert.False(provider.Installed);
        await provider.InstallCoreAsync(null, CancellationToken.None);

        Assert.True(provider.Installed, "应复用旧版核心目录而不去下载");
        Assert.Equal("9.9.9", provider.InstalledVersion);
        Directory.Delete(root, recursive: true);
    }

    // ── 真机联调（默认跳过） ────────────────────────────────────────────

    [Fact]
    public async Task 真机联调_安装核心并走一遍建房与收尾()
    {
        if (Environment.GetEnvironmentVariable("STARCLOUD_IT_MULTIPLAYER") != "1") return;

        var service = new AppServicesForTest().Multiplayer;
        var provider = service.Active;
        Assert.Equal("terracotta", provider.Id);

        if (!provider.Installed)
        {
            var progress = new Progress<MultiplayerProgress>();
            await service.InstallCoreAsync(progress, CancellationToken.None);
        }
        Assert.True(provider.Installed, "核心安装后应处于已安装状态");

        Assert.True(await provider.EnsureSessionAsync(CancellationToken.None), "会话应能拉起");
        var idle = await service.RefreshAsync();
        Assert.True(idle.Core.SessionRunning);
        Assert.Equal(MultiplayerPhase.Idle, idle.State.Phase);

        // 游戏没开、没有局域网世界：房主侧会停在「扫描中」，正好验证状态映射没串档
        await service.HostAsync(CancellationToken.None);
        await Task.Delay(1500);
        var scanning = await service.RefreshAsync();
        Assert.Equal(MultiplayerPhase.HostScanning, scanning.State.Phase);

        await service.IdleAsync(CancellationToken.None);
        await Task.Delay(500);
        Assert.Equal(MultiplayerPhase.Idle, (await service.RefreshAsync()).State.Phase);

        await service.StopAsync(CancellationToken.None);
        Assert.False(await provider.ProbeSessionAsync(CancellationToken.None), "停止后会话应不再响应");
        service.Dispose();
    }

    /// <summary>只测联机服务所需的最小装配（不建 Nova / 插件等重组件）</summary>
    private sealed class AppServicesForTest
    {
        public MultiplayerService Multiplayer { get; }

        public AppServicesForTest()
        {
            LauncherPaths.EnsureDataDir();
            var settings = LauncherSettings.Load();
            Multiplayer = new MultiplayerService(
                new DownloadService(() => settings), () => settings, () => "TestPlayer");
        }
    }
}
