using Starcloud.Multiplayer.Core.P2P;

namespace Starcloud.Multiplayer.Tests;

/// <summary>
/// 测试用的内存 P2P 引擎：不起任何进程、不碰网络，只把调用记下来。
/// 让「控制面」与「数据面」可以分开验收（控制面端到端不需要 EasyTier）。
/// </summary>
internal sealed class FakeP2PEngine : IP2PEngine
{
    public P2POptions? Started { get; private set; }

    public List<P2POptions> Starts { get; } = new();

    public int StopCount { get; private set; }

    public IReadOnlyList<PeerInfo> Peers { get; set; } = Array.Empty<PeerInfo>();

    public bool Alive { get; set; } = true;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public Task StartAsync(P2POptions options, CancellationToken ct = default)
    {
        Started = options;
        Starts.Add(options);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PeerInfo>> GetPeersAsync(CancellationToken ct = default) =>
        Task.FromResult(Peers);

    public Task<bool> IsAliveAsync(CancellationToken ct = default) => Task.FromResult(Alive);

    public Task StopAsync(CancellationToken ct = default)
    {
        StopCount++;
        Started = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
