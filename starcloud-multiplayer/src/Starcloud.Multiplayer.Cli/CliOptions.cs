using Starcloud.Multiplayer.Client;
using Starcloud.Multiplayer.Core.Models;

namespace Starcloud.Multiplayer.Cli;

/// <summary>命令行全局参数。</summary>
internal sealed class CliOptions
{
    public string Service { get; private set; } = "http://127.0.0.1:5510";

    public string? DataDirectory { get; private set; }

    public string? KernelZip { get; private set; }

    public string SeedFile { get; private set; } = Path.Combine(AppContext.BaseDirectory, "seed.json");

    public string Channel { get; private set; } = StarcloudIdentity.TestChannel;

    public string Password { get; private set; } = "test";

    public string Command { get; private set; } = "";

    public List<string> Positional { get; } = new();

    public Dictionary<string, string?> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (options.Command.Length == 0) options.Command = arg;
                else options.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            Apply(options, name, value);
        }

        return options;
    }

    private static void Apply(CliOptions options, string name, string? value)
    {
        switch (name.ToLowerInvariant())
        {
            case "service": options.Service = value ?? options.Service; break;
            case "data": options.DataDirectory = value; break;
            case "kernel-zip": options.KernelZip = value; break;
            case "seed": options.SeedFile = value ?? options.SeedFile; break;
            case "channel": options.Channel = value ?? options.Channel; break;
            case "password": options.Password = value ?? options.Password; break;
            default: options.Flags[name] = value; break;
        }
    }

    public string? Flag(string name) => Flags.TryGetValue(name, out var value) ? value : null;

    public bool Has(string name) => Flags.ContainsKey(name);

    public MpKernelOptions ToKernelOptions(IPlatformHost platform) => new()
    {
        ServiceBaseAddress = Service,
        Platform = platform,
        KernelZipPath = KernelZip,
        // mock 的测试通道靠派生规则认人，不需要登记；云端通道的 key 反推不出来才需要。
        RegisterMockKey = Channel == StarcloudIdentity.CloudChannel,
    };
}

/// <summary>命令行里的通用小工具。</summary>
internal static class CliText
{
    public static string PeerPath(string? path) => path switch
    {
        "local" => "本机",
        "direct" => "直连（打洞成功）",
        "relay" => "经公共节点中继",
        "unknown" => "未知",
        _ => "尚未建立",
    };

    public static string Phase(string phase) => phase switch
    {
        MultiplayerSnapshot.PhaseIdle => "空闲",
        MultiplayerSnapshot.PhasePreparing => "准备中",
        MultiplayerSnapshot.PhaseHosting => "作为房主联机中",
        MultiplayerSnapshot.PhaseJoining => "正在加入",
        MultiplayerSnapshot.PhaseConnected => "已加入房间",
        MultiplayerSnapshot.PhaseFailed => "已失败",
        _ => phase,
    };
}
