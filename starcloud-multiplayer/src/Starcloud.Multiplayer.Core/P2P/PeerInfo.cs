namespace Starcloud.Multiplayer.Core.P2P;

/// <summary>对端某条连接的路径性质（界面据此如实展示，不假装直连）。</summary>
public enum ConnectionPath
{
    Unknown,
    /// <summary>本机自己。</summary>
    Local,
    /// <summary>P2P 直连（打洞成功）。</summary>
    Direct,
    /// <summary>经公共共享节点中继（打洞失败时的回退）。</summary>
    Relay,
}

/// <summary>EasyTier 报出的 NAT 类型（字符串已归一化，容忍大小写与下划线差异）。</summary>
public enum NatType
{
    Unknown,
    OpenInternet,
    NoPat,
    FullCone,
    Restricted,
    PortRestricted,
    Symmetric,
    SymmetricUdpFirewall,
    SymmetricEasyIncrease,
    SymmetricEasyDecrease,
}

/// <summary>对端节点。<c>Cost</c> 保留原始字符串，便于排障时原样打印。</summary>
public sealed record PeerInfo(
    string Hostname,
    string? VirtualIpv4,
    NatType NatType,
    string Cost,
    bool IsLocal,
    ConnectionPath Path);
