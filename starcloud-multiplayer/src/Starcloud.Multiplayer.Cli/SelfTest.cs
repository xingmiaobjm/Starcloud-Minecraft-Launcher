using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Starcloud.Multiplayer.Client;
using Starcloud.Multiplayer.Core.Models;
using Starcloud.Multiplayer.Core.Net;
using Starcloud.Multiplayer.Core.P2P;
using Starcloud.Multiplayer.MockServer;

namespace Starcloud.Multiplayer.Cli;

/// <summary>
/// 单机端到端自检：**一台机器、不开 Minecraft、不需要外网**，把整条链路跑一遍。
/// <para>
/// 顺序：起 mock 服务端 → 两个测试账号登录 → 好友可见 → 房主开房（自检模式，假 MC 服务端）
/// → 大厅可见 → 邀请 → 接受 → 客人起隧道 → 等对端 → 向本地映射端口发 Minecraft 探测。
/// </para>
/// <para>
/// 「不需要外网」也包含会合点：实测公共节点域名（public.easytier.top 等）在 DNS 被加速器
/// 劫持的机器上解析不出来，所以自检会用已释放的内核再拉一个 <see cref="LocalRendezvous"/>
/// 当本地会合节点，host/guest 的公共节点列表在构造时就指向它。
/// </para>
/// </summary>
internal static class SelfTest
{
    private static readonly TimeSpan PeerWaitTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "starcloud-mp-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"工作目录：{workDir}");
        Console.WriteLine($"seed 文件：{options.SeedFile}");

        await using var server = await MockServerHost.StartAsync(
            options.SeedFile, Path.Combine(workDir, "state.json"), port: 0, ct);
        Console.WriteLine($"mock 联机服务端：{server.BaseAddress}");

        var hostPlatform = new DesktopPlatformHost(Path.Combine(workDir, "host"));
        var guestPlatform = new DesktopPlatformHost(Path.Combine(workDir, "guest"));
        var kernelDirectory = Path.Combine(workDir, "kernel");

        // 会合端口与「怎么连它」要在构造两个内核**之前**定好：它们的公共节点列表得指向它。
        // 通告地址必须用**非回环**本机地址：EasyTier 连对端时按「到目标的路由」选源地址，
        // 目标是 127.0.0.1 时会绑到无效源地址（本机的加速器 TUN 占默认路由时必现），
        // Windows 直接报 10049「请求的地址无效」，实测回环目标永远连不上；非回环地址则连得通。
        var rendezvousPort = LocalPorts.FindFreeTcpPort();
        var rendezvousAddresses = LocalIpv4Addresses();
        if (rendezvousAddresses.Count == 0)
        {
            Console.WriteLine("失败：本机没有任何非回环 IPv4 地址，本地会合节点无从被连入（离线且无网卡？）。");
            return 2;
        }
        var rendezvousServers = rendezvousAddresses
            .Select(ip => $"tcp://{ip}:{rendezvousPort}").ToArray();

        await using var host = new MultiplayerKernel(new MpKernelOptions
        {
            ServiceBaseAddress = server.BaseAddress,
            Platform = hostPlatform,
            KernelZipPath = options.KernelZip,
            KernelDirectory = kernelDirectory,
            PublicServers = rendezvousServers,
        });
        await using var guest = new MultiplayerKernel(new MpKernelOptions
        {
            ServiceBaseAddress = server.BaseAddress,
            Platform = guestPlatform,
            KernelZipPath = options.KernelZip,
            KernelDirectory = kernelDirectory,
            PublicServers = rendezvousServers,
        });

        Section("1/7 P2P 内核就位");
        if (!await host.PrepareEngineAsync(ct) || !await guest.PrepareEngineAsync(ct))
        {
            Console.WriteLine($"内核不可用：{host.EngineUnavailableReason ?? guest.EngineUnavailableReason}");
            Console.WriteLine("（控制面不受影响，但隧道自检需要内核；可加 --kernel-zip <本地压缩包> 走离线包）");
            return 2;
        }
        Console.WriteLine($"内核目录：{host.EngineInstallDirectory}");

        await using var rendezvous = await LocalRendezvous.StartAsync(
            host.EngineCoreExecutablePath!, rendezvousPort, rendezvousAddresses, ct);
        Console.WriteLine($"本地会合节点：tcp://0.0.0.0:{rendezvousPort}"
                          + $"（host/guest 经 {string.Join(" / ", rendezvousServers)} 连入；只做信令，不中继）");

        Section("2/7 两个测试账号登录");
        var alice = await host.LoginAsync("alice", "test", StarcloudIdentity.TestChannel, ct);
        var bob = await guest.LoginAsync("bob", "test", StarcloudIdentity.TestChannel, ct);
        Console.WriteLine($"房主：{alice.DisplayName}（{alice.UserId}）");
        Console.WriteLine($"客人：{bob.DisplayName}（{bob.UserId}）");

        Section("3/7 好友关系与在线状态");
        var friends = await host.FriendsAsync(ct);
        foreach (var friend in friends)
            Console.WriteLine($"  {friend.DisplayName}：{(friend.Online ? "在线" : "离线")}（{friend.UserId}）");
        if (friends.All(f => f.UserId != bob.UserId))
        {
            Console.WriteLine("失败：bob 不在 alice 的好友列表里（检查 seed.json）");
            return 1;
        }

        Section("4/7 房主开房（自检模式）");
        var minecraftPort = LocalPorts.FindFreeTcpPort();
        var roomId = await host.HostAsync(new HostOptions(
            Public: true, MinecraftPort: minecraftPort, Name: "自检房间", SelfTest: true), ct);
        Console.WriteLine($"房间号：{roomId}（房间号就是邀请码，可念给别人）");
        Console.WriteLine($"假 Minecraft 服务端监听：{host.Snapshot.MinecraftPort}");

        Section("5/7 公开房间大厅");
        var rooms = await guest.RoomsAsync(ct);
        foreach (var room in rooms)
            Console.WriteLine($"  {room.Name}（{room.RoomId}）房主 {room.Host.DisplayName}，{room.Players}/{room.Capacity} 人");
        if (rooms.All(r => r.RoomId != roomId))
        {
            Console.WriteLine("失败：大厅里看不到刚开的房间");
            return 1;
        }

        Section("6/7 邀请 → 接受 → 客人起隧道");
        await host.RefreshAsync(includePeers: false, ct);   // 先把房主心跳刷新一次，避免房间过期
        var inviteId = await host.InviteAsync("bob", ct);
        Console.WriteLine($"邀请已发出：{inviteId}");

        var invites = await guest.InvitesAsync(ct);
        if (invites.Count == 0)
        {
            Console.WriteLine("失败：客人没收到邀请");
            return 1;
        }
        Console.WriteLine($"客人收到邀请：来自 {invites[0].From.DisplayName} 的「{invites[0].RoomName}」");

        var credentials = await guest.AcceptInviteAsync(invites[0].InviteId, ct);
        Console.WriteLine($"进网凭据：网络名 {credentials.NetworkName}，密钥 {Mask(credentials.NetworkSecret)}");
        Console.WriteLine($"客人本地映射端口：{guest.Snapshot.LocalPort} → 房主 {credentials.MinecraftPort}");

        Section("7/7 隧道自检");
        var hostSeen = await WaitForHostAsync(guest, credentials, ct);
        // 房主侧此前只上报心跳、没拉过对端列表，打印前刷一次才有内容。
        await host.RefreshAsync(includePeers: true, ct);
        PrintPeers("房主侧看到", host.Snapshot.Peers);
        PrintPeers("客人侧看到", guest.Snapshot.Peers);

        var ping = await guest.ProbeTunnelAsync(ct);
        Console.WriteLine(ping.Ok
            ? $"隧道已打通：本机 127.0.0.1:{guest.Snapshot.LocalPort} → 房主 {credentials.MinecraftPort}"
            : $"隧道探测失败：{ping.Error}");

        if (ping.Ok)
        {
            Console.WriteLine($"  版本：{ping.Version}");
            Console.WriteLine($"  MOTD：{ping.Motd}");
            Console.WriteLine($"  耗时：{ping.LatencyMs} ms");
            Console.WriteLine($"  路径：{CliText.PeerPath(guest.Snapshot.ConnectionPath)}"
                              + (hostSeen ? "" : "（没等到房主出现在对端列表里，但端口通了）"));
        }

        await guest.StopSessionAsync(ct);
        await host.StopSessionAsync(ct);

        Console.WriteLine();
        Console.WriteLine(ping.Ok ? "自检通过。" : "自检未通过。");
        return ping.Ok ? 0 : 1;
    }

    private static async Task<bool> WaitForHostAsync(
        MultiplayerKernel guest, RoomCredentials credentials, CancellationToken ct)
    {
        var hostHostname = P2PDefaults.HostHostnameFor(credentials.MinecraftPort);
        var deadline = DateTimeOffset.Now + PeerWaitTimeout;
        Console.Write($"等待出现房主节点（{hostHostname}）");

        while (DateTimeOffset.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await guest.RefreshAsync(includePeers: true, ct);

            if (guest.Snapshot.Peers.Any(p =>
                    string.Equals(p.Hostname, hostHostname, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("：出现了");
                return true;
            }

            Console.Write(".");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        Console.WriteLine("：超时（继续探测端口，可能仍能通）");
        return false;
    }

    private static void PrintPeers(string title, IReadOnlyList<PeerInfo> peers)
    {
        Console.WriteLine($"{title}的对端：");
        if (peers.Count == 0)
        {
            Console.WriteLine("  （空的）");
            return;
        }

        foreach (var peer in peers)
            Console.WriteLine($"  {peer.Hostname} {peer.VirtualIpv4} NAT={peer.NatType} cost={peer.Cost} 路径={CliText.PeerPath(PathText(peer.Path))}");
    }

    private static string? PathText(ConnectionPath path) => path switch
    {
        ConnectionPath.Local => "local",
        ConnectionPath.Direct => "direct",
        ConnectionPath.Relay => "relay",
        _ => null,
    };

    private static string Mask(string secret) => secret.Length <= 6 ? "***" : secret[..3] + "***";

    /// <summary>本机所有非回环、已启用的 IPv4 地址（本地会合节点的候选通告地址）。</summary>
    private static List<string> LocalIpv4Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.ToString())
        .Distinct()
        .ToList();

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");
    }

    /// <summary>
    /// 自检专用的本地会合节点：再拉一个 easytier-core，不带网络身份（即共享节点形态），
    /// 只做成员发现的信令，不中继流量。host/guest 的 <c>-p</c> 都指向它，这样两台
    /// 「同机 peer」也能像真跨网一样走「会合 → 交换端点 → 直连」的完整路径。
    /// <para>
    /// 监听绑 0.0.0.0，但 host/guest 拿到的通告地址是**非回环**本机地址：EasyTier 连
    /// 回环目标会按路由绑到无效源地址（Windows 报 10049），实测只有非回环地址连得上。
    /// </para>
    /// </summary>
    private sealed class LocalRendezvous : IAsyncDisposable
    {
        private const int TailLines = 100;

        private readonly Process _process;
        private readonly Queue<string> _tail = new();
        private readonly object _gate = new();

        private LocalRendezvous(Process process) => _process = process;

        /// <summary>
        /// 拉起会合节点并等它经任意一个通告地址可连（≤20s），失败时带出最后几行日志。
        /// </summary>
        public static async Task<LocalRendezvous> StartAsync(
            string coreExecutablePath, int port, IReadOnlyList<string> advertiseAddresses,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo(coreExecutablePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // 显式 -l 会替换默认监听（默认是 0.0.0.0:11010-11013 一串），这里只留 0.0.0.0 上各一个。
            psi.ArgumentList.Add("--no-tun");
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add($"tcp://0.0.0.0:{port}");
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add($"udp://0.0.0.0:{port}");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var node = new LocalRendezvous(process);
            process.OutputDataReceived += (_, e) => node.Append(e.Data);
            process.ErrorDataReceived += (_, e) => node.Append(e.Data);

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("本地会合节点进程启动失败");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var deadline = DateTimeOffset.Now.AddSeconds(20);
                while (DateTimeOffset.Now < deadline)
                {
                    ct.ThrowIfCancellationRequested();

                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"本地会合节点启动后立刻退出（退出码 {process.ExitCode}）。最后几行日志：{node.Tail()}");
                    }

                    foreach (var address in advertiseAddresses)
                    {
                        if (await LocalPorts.IsTcpPortOpenAsync(address, port, TimeSpan.FromSeconds(1), ct))
                            return node;
                    }

                    await Task.Delay(300, ct);
                }

                throw new TimeoutException(
                    $"等本地会合节点监听超时（{string.Join("/", advertiseAddresses)}:{port}）。最后几行日志：{node.Tail()}");
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // 收尾失败就交给操作系统回收，不影响自检结论。
            }
            finally
            {
                _process.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        private void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_gate)
            {
                _tail.Enqueue(line);
                while (_tail.Count > TailLines) _tail.Dequeue();
            }
        }

        private string Tail()
        {
            lock (_gate)
            {
                return string.Join(" | ", _tail.ToArray());
            }
        }
    }
}
