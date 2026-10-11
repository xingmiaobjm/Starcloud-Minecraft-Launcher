namespace Starcloud.Multiplayer.Core.P2P;

/// <summary>
/// P2P 引擎契约：内核的其余部分只认这个接口，不认识 EasyTier。
/// <para>
/// 平台实现（换的是实现，不是接口）：
/// <list type="bullet">
///   <item>Windows / Linux / macOS：<c>EasytierProcessEngine</c>（释放并 spawn 官方 core，用 cli 做 RPC）。</item>
///   <item>Android：jniLibs 内置 musl 静态二进制，仍以子进程拉起（<c>Starcloud.Multiplayer.Client.Android</c> 的 <c>EasytierNativeEngine</c> 包装同一引擎）。</item>
///   <item>鸿蒙 / 鸿蒙 PC：由 ArkTS 侧实现同一语义的引擎。</item>
/// </list>
/// </para>
/// </summary>
public interface IP2PEngine : IAsyncDisposable
{
    /// <summary>当前平台/当前机器上引擎是否可用（例如内核二进制缺失就是不可用）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用时的原因（中文，可直接展示给用户）。</summary>
    string? UnavailableReason { get; }

    /// <summary>起网络。成功后网络名/密钥对应的虚拟网络即可用。</summary>
    Task StartAsync(P2POptions options, CancellationToken ct = default);

    /// <summary>读对端列表（用于展示直连/中继与 NAT 类型）。</summary>
    Task<IReadOnlyList<PeerInfo>> GetPeersAsync(CancellationToken ct = default);

    /// <summary>网络是否还活着。</summary>
    Task<bool> IsAliveAsync(CancellationToken ct = default);

    /// <summary>停网络并做收尾清理。</summary>
    Task StopAsync(CancellationToken ct = default);
}
