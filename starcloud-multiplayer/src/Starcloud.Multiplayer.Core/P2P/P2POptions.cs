namespace Starcloud.Multiplayer.Core.P2P;

/// <summary>一条端口映射：把本地某端口的流量转到虚拟网络里某个地址。</summary>
public sealed record PortForward(string Proto, int LocalPort, string RemoteHost, int RemotePort)
{
    public static PortForward Tcp(int localPort, string remoteHost, int remotePort) =>
        new("tcp", localPort, remoteHost, remotePort);

    public static PortForward Udp(int localPort, string remoteHost, int remotePort) =>
        new("udp", localPort, remoteHost, remotePort);

    public override string ToString() =>
        $"{Proto}://0.0.0.0:{LocalPort}/{RemoteHost}:{RemotePort}";
}

/// <summary>EasyTier 相关的固定约定。房主虚拟地址与公共节点列表是**跨端冻结**的。</summary>
public static class P2PDefaults
{
    /// <summary>房主固定虚拟 IPv4：客人不需要先发现房主就能直接给出映射目标。</summary>
    public const string HostVirtualIp = "10.144.144.1";

    /// <summary>房主机名前缀：后面拼 MC 端口，客人据此识别「哪个对端是房主」。</summary>
    public const string HostHostnamePrefix = "sc-mp-host-";

    /// <summary>公共共享节点（社区公益资源，可在设置里替换）。</summary>
    public static readonly IReadOnlyList<string> PublicServers = new[]
    {
        "tcp://public.easytier.top:11010",
        "tcp://public2.easytier.cn:54321",
    };

    public static string HostHostnameFor(int minecraftPort) => HostHostnamePrefix + minecraftPort;
}

/// <summary>起一个 EasyTier 网络所需的全部输入。</summary>
public sealed record P2POptions
{
    public required string NetworkName { get; init; }

    public required string NetworkSecret { get; init; }

    /// <summary>房主固定虚拟地址并开端口白名单；客人走 DHCP 并自己加端口映射。</summary>
    public bool IsHost { get; init; }

    public int MinecraftPort { get; init; }

    /// <summary>本地管理端口（RPC）：只监听 127.0.0.1，由内核自己分配。</summary>
    public int RpcPort { get; init; }

    public IReadOnlyList<string> PublicServers { get; init; } = P2PDefaults.PublicServers;

    public IReadOnlyList<PortForward> PortForwards { get; init; } = Array.Empty<PortForward>();
}
