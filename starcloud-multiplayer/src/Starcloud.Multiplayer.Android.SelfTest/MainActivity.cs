using Android.App;
using Android.OS;
using Android.Widget;
using Starcloud.Multiplayer.Client.Android;
using Starcloud.Multiplayer.Core.Net;
using Starcloud.Multiplayer.Core.P2P;
using Starcloud.Multiplayer.Core.Rooms;

// 命名空间不能以 .Android 结尾——否则会遮蔽全局的 Android.* 命名空间（Android.Util 等全炸）。
namespace Starcloud.Multiplayer.SelfTest;

/// <summary>
/// 真机自检：jniLibs 内置内核以子进程拉起 → RPC 存活 → peer 列表 → 干净停止。
/// 逐步结果落 logcat（tag StarcloudMp），屏幕上同步显示结论；无 UI 交互。
/// </summary>
[Activity(Label = "MP内核自检", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    private const string LogTag = "StarcloudMp";

    private TextView _status = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _status = new TextView(this)
        {
            Text = "自检运行中……（逐步结果见 logcat -s StarcloudMp）",
            TextSize = 18,
            Gravity = Android.Views.GravityFlags.Center,
        };
        SetContentView(_status);

        // StartAsync 等内核起 RPC 可能要几秒，放后台线程跑，不阻塞 UI 线程。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok;
            try
            {
                ok = RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error(LogTag, ex.ToString());
                ok = false;
            }

            var conclusion = ok ? "自检通过：内核子进程拉起、RPC 存活、干净停止" : "自检失败：见上方日志";
            Android.Util.Log.Info(LogTag, $"===== {conclusion} =====");
            RunOnUiThread(() => _status.Text = conclusion);
        });
    }

    private async Task<bool> RunAsync()
    {
        // 1. W^X 前提：nativeLibraryDir 下必须能看到内置内核实体文件。
        var libDir = ApplicationInfo!.NativeLibraryDir!;
        Info($"nativeLibraryDir = {libDir}");
        foreach (var name in new[] { EasytierNativeEngine.CoreLibraryName, EasytierNativeEngine.CliLibraryName })
        {
            var full = Path.Combine(libDir, name);
            Info($"  {name}: {(File.Exists(full) ? $"在位（{new FileInfo(full).Length} 字节）" : "缺失！")}");
        }

        await using var engine = new EasytierNativeEngine(this);
        if (!engine.IsAvailable)
        {
            Info($"内核不可用：{engine.UnavailableReason}");
            return false;
        }

        // 2. 拉内核：独立小网络（PublicServers 置空 = 不连任何公共节点，单机自洽，
        //    断言 0 个 peer 才稳定；联网真机连上公共节点会把节点算进 peer 列表）。
        var options = new P2POptions
        {
            NetworkName = "sc-mp-selftest",
            NetworkSecret = RoomCode.NewNetworkSecret(),
            IsHost = true,
            MinecraftPort = 25565,
            RpcPort = LocalPorts.FindFreeTcpPort(15800),
            PublicServers = Array.Empty<string>(),
        };
        Info($"开始拉起内核（RPC 端口 {options.RpcPort}）……");
        await engine.StartAsync(options);

        // 3. RPC 存活 + peer 列表可读。EasyTier 的 peer 列表含本地节点自己，
        //    所以单机自洽的正确断言是「没有非本地 peer」，而不是总数为 0。
        var alive = await engine.IsAliveAsync();
        Info($"IsAlive = {alive}");
        var peers = await engine.GetPeersAsync();
        foreach (var peer in peers)
        {
            Info($"  peer: hostname={peer.Hostname} ip={peer.VirtualIpv4 ?? "-"} local={peer.IsLocal}");
        }
        var foreign = peers.Count(p => !p.IsLocal);
        Info($"peer 列表 = {peers.Count} 个（非本地 {foreign} 个）");

        // 4. 干净停止：进程树被杀掉，RPC 不再应答。
        await engine.StopAsync();
        var stopped = !await engine.IsAliveAsync();
        Info($"停止后进程已退出 = {stopped}");

        return alive && foreign == 0 && stopped;
    }

    private static void Info(string message) => Android.Util.Log.Info(LogTag, message);
}
