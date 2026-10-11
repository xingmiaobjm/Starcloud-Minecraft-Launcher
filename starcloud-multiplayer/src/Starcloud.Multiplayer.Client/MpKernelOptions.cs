using Starcloud.Multiplayer.Core.P2P;

namespace Starcloud.Multiplayer.Client;

/// <summary>内核装配参数。</summary>
public sealed record MpKernelOptions
{
    /// <summary>联机服务端地址（本次是本地 mock；将来换成云端域名）。</summary>
    public required string ServiceBaseAddress { get; init; }

    public required IPlatformHost Platform { get; init; }

    /// <summary>P2P 引擎工厂；不传就用桌面实现（EasyTier 进程）。测试注入内存实现。</summary>
    public Func<IP2PEngine>? EngineFactory { get; init; }

    /// <summary>公共共享节点；不传用默认列表。</summary>
    public IReadOnlyList<string>? PublicServers { get; init; }

    /// <summary>本地已有的 EasyTier 压缩包（离线/加速用）；不传就走标准下载地址。</summary>
    public string? KernelZipPath { get; init; }

    /// <summary>内核二进制放哪；不传就放 <c>&lt;数据目录&gt;/easytier</c>。多实例可共用同一份。</summary>
    public string? KernelDirectory { get; init; }

    /// <summary>
    /// 登录后是否向「仅 mock 存在」的端点登记 api_key → user_id。
    /// mock 的测试通道靠派生规则认人，云端通道的 key 反推不出来，只能靠登记。接真云端时置 false。
    /// </summary>
    public bool RegisterMockKey { get; init; }
}

/// <summary>开房参数。</summary>
public sealed record HostOptions(bool Public, int MinecraftPort, string? Name = null, bool SelfTest = false);
