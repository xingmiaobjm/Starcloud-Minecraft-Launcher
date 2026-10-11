using Starcloud.Multiplayer.Client;
using Starcloud.Multiplayer.Core.Models;
using Starcloud.Multiplayer.Core.Protocol;
using Starcloud.Multiplayer.MockServer;

namespace Starcloud.Multiplayer.Cli;

/// <summary>命令分发与输出。只做「把命令翻译成一次内核调用 + 打印」，不含业务逻辑。</summary>
internal static class CliApp
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        if (options.Command.Length == 0 || options.Command is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        if (options.Command.Equals("selftest", StringComparison.OrdinalIgnoreCase))
            return await SelfTest.RunAsync(options, ct);
        if (options.Command.Equals("mock", StringComparison.OrdinalIgnoreCase))
            return await RunMockAsync(options, ct);

        var platform = new DesktopPlatformHost(options.DataDirectory);
        await using var kernel = new MultiplayerKernel(options.ToKernelOptions(platform));

        switch (options.Command.ToLowerInvariant())
        {
            case "kernel":
            {
                Console.WriteLine($"内核目录：{kernel.EngineInstallDirectory}");
                var ok = await kernel.PrepareEngineAsync(ct);
                Console.WriteLine(ok ? "P2P 内核已就位。" : $"P2P 内核不可用：{kernel.EngineUnavailableReason}");
                return ok ? 0 : 1;
            }

            case "login":
            {
                var id = Require(options, 0, "用法：login <starcloud_id>");
                var identity = await kernel.LoginAsync(id, options.Password, options.Channel, ct);
                Console.WriteLine($"已登录：{identity.DisplayName}（{identity.UserId}，通道 {identity.Channel}）");
                return 0;
            }

            case "logout":
                kernel.Logout();
                Console.WriteLine("已登出。");
                return 0;

            case "whoami":
                PrintIdentity(kernel);
                return kernel.Identity is null ? 1 : 0;

            case "friends":
            {
                var friends = await kernel.FriendsAsync(ct);
                if (friends.Count == 0)
                {
                    Console.WriteLine("还没有好友（原型阶段好友关系来自服务端预置数据）。");
                    return 0;
                }

                foreach (var friend in friends)
                {
                    var where = friend.Online
                        ? (friend.RoomId is null ? "在线" : $"在线 · 在房间 {friend.RoomId}")
                        : "离线";
                    Console.WriteLine($"  {friend.DisplayName,-12} {where}");
                }
                return 0;
            }

            case "rooms":
            {
                var rooms = await kernel.RoomsAsync(ct);
                if (rooms.Count == 0)
                {
                    Console.WriteLine("公开大厅里暂时没有房间。");
                    return 0;
                }

                foreach (var room in rooms)
                {
                    Console.WriteLine($"  {room.RoomId}  {room.Name}  房主 {room.Host.DisplayName}  "
                                      + $"{room.Players}/{room.Capacity} 人  MC 端口 {room.MinecraftPort}");
                }
                return 0;
            }

            case "invite":
            {
                var who = Require(options, 0, "用法：invite <好友名>");
                var inviteId = await kernel.InviteAsync(who, ct);
                Console.WriteLine($"已邀请「{who}」（邀请号 {inviteId}）。对方上线后执行 invites --accept 即可进来。");
                return 0;
            }

            case "invites":
            {
                if (options.Flag("accept") is { } acceptId)
                {
                    var credentials = await kernel.AcceptInviteAsync(acceptId, ct);
                    Console.WriteLine($"已接受邀请，进入房间 {credentials.RoomId}。");
                    Console.WriteLine($"在 Minecraft 里连：127.0.0.1:{kernel.Snapshot.LocalPort}");
                    await kernel.RunUntilCancelledAsync(ct);
                    return 0;
                }

                if (options.Flag("reject") is { } rejectId)
                {
                    await kernel.RejectInviteAsync(rejectId, ct);
                    Console.WriteLine("已拒绝该邀请。");
                    return 0;
                }

                var invites = await kernel.InvitesAsync(ct);
                if (invites.Count == 0)
                {
                    Console.WriteLine("没有待处理的邀请。");
                    return 0;
                }

                foreach (var invite in invites)
                    Console.WriteLine($"  {invite.InviteId}  来自 {invite.From.DisplayName} 的「{invite.RoomName}」"
                                      + $"（{invite.CreatedAt:HH:mm:ss}）");
                Console.WriteLine("用 invites --accept <邀请号> 进房。");
                return 0;
            }

            case "host":
            {
                var port = ParsePort(options.Flag("port"));
                var roomId = await kernel.HostAsync(new HostOptions(
                    Public: options.Has("public"),
                    MinecraftPort: port,
                    Name: options.Flag("name"),
                    SelfTest: options.Has("selftest")), ct);

                Console.WriteLine($"房间已开：{roomId}");
                Console.WriteLine(options.Has("public")
                    ? "已登记到公开大厅，任何人都能加入。"
                    : "非公开房间：只有被你邀请的好友能进。");
                Console.WriteLine($"房主的 Minecraft 端口：{kernel.Snapshot.MinecraftPort}");
                Console.WriteLine("按 Ctrl+C 关房。");
                await kernel.RunUntilCancelledAsync(ct);
                Console.WriteLine("已关房。");
                return 0;
            }

            case "join":
            {
                var target = Require(options, 0, "用法：join <房间号|好友名>");
                var roomId = await kernel.JoinAsync(target, ct);
                Console.WriteLine($"已进入房间 {roomId}。");
                Console.WriteLine($"在 Minecraft 里连：127.0.0.1:{kernel.Snapshot.LocalPort}");
                Console.WriteLine("按 Ctrl+C 退出房间。");
                await kernel.RunUntilCancelledAsync(ct);
                Console.WriteLine("已退出房间。");
                return 0;
            }

            case "status":
                await kernel.RefreshAsync(includePeers: true, ct);
                PrintStatus(kernel);
                return 0;

            case "leave":
                await kernel.StopSessionAsync(ct);
                Console.WriteLine("已离开房间。");
                return 0;

            default:
                Console.WriteLine($"不认识的命令：{options.Command}");
                PrintHelp();
                return 1;
        }
    }

    private static async Task<int> RunMockAsync(CliOptions options, CancellationToken ct)
    {
        var dataDir = options.DataDirectory ?? Path.Combine(Path.GetTempPath(), "starcloud-mp-mock");
        Directory.CreateDirectory(dataDir);

        var port = ParsePort(options.Flag("port"), 5510);
        await using var server = await MockServerHost.StartAsync(
            options.SeedFile, Path.Combine(dataDir, "state.json"), port, ct);

        Console.WriteLine($"mock 联机服务端：{server.BaseAddress}{MpProtocol.BasePath}");
        Console.WriteLine("按 Ctrl+C 退出。");
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }

    private static void PrintIdentity(MultiplayerKernel kernel)
    {
        if (kernel.Identity is null)
        {
            Console.WriteLine("未登录（starcloud-mp login <starcloud_id>）。");
            return;
        }

        var identity = kernel.Identity;
        Console.WriteLine($"{identity.DisplayName}（{identity.UserId}，通道 {identity.Channel}）");
    }

    private static void PrintStatus(MultiplayerKernel kernel)
    {
        PrintIdentity(kernel);
        Console.WriteLine($"联机服务端：{kernel.ServiceBaseAddress}");
        Console.WriteLine($"P2P 内核：{(kernel.EngineAvailable ? "已就位" : $"不可用（{kernel.EngineUnavailableReason}）")}");

        var snapshot = kernel.Snapshot;
        Console.WriteLine($"会话状态：{CliText.Phase(snapshot.Phase)}"
                          + (snapshot.FailureReason is null ? "" : $"（{snapshot.FailureReason}）"));

        if (snapshot.RoomId is not null)
        {
            Console.WriteLine($"房间：{snapshot.RoomId}{(snapshot.RoomName is null ? "" : $"「{snapshot.RoomName}」")}"
                              + $"（{(snapshot.IsHost ? "房主" : "客人")}）");
            Console.WriteLine($"房主 MC 端口：{snapshot.MinecraftPort}");
            if (!snapshot.IsHost)
                Console.WriteLine($"本地映射端口：{snapshot.LocalPort}（Minecraft 连 127.0.0.1:{snapshot.LocalPort}）");
            Console.WriteLine($"连接路径：{CliText.PeerPath(snapshot.ConnectionPath)}");
        }

        if (snapshot.Peers.Count > 0)
        {
            Console.WriteLine("对端：");
            foreach (var peer in snapshot.Peers)
                Console.WriteLine($"  {peer.Hostname} {peer.VirtualIpv4} NAT={peer.NatType} cost={peer.Cost}");
        }
    }

    private static string Require(CliOptions options, int index, string usage) =>
        options.Positional.Count > index
            ? options.Positional[index]
            : throw new ArgumentException(usage);

    private static int ParsePort(string? raw, int fallback = 25565) =>
        raw is not null && int.TryParse(raw, out var port) && port is > 0 and <= 65535 ? port : fallback;

    private static void PrintHelp()
    {
        Console.WriteLine("""
            starcloud-mp —— Starcloud 联机内核（验收用命令行驱动，不是产品界面）

            用法：starcloud-mp <命令> [参数] [--service <url>] [--data <目录>] [--kernel-zip <压缩包>]

            身份
              login <starcloud_id> [--channel test|cloud] [--password <密码>]
              logout | whoami

            好友与房间
              friends                        好友列表 + 在线状态
              invite <好友名>                 邀请好友进我当前的房间
              invites [--accept <邀请号> | --reject <邀请号>]
              rooms                          公开房间大厅
              host [--name 名称] [--port 25565] [--public] [--selftest]
              join <房间号|好友名>
              status | leave

            P2P 内核
              kernel                         检查并准备 EasyTier 内核（首次会联网下载）

            端到端自检（一台机器、不开游戏、不需要外网）
              selftest

            其它
              mock [--port 5510]             只起本地 mock 联机服务端

            说明：房间号就是邀请码；客人侧在 Minecraft 里连 127.0.0.1:<本地映射端口>。
            """);
    }
}
