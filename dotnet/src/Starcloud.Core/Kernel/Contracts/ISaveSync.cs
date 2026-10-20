// 跨设备存档同步契约：把本地世界推到对端 / 从对端拉回 / 列出对端世界。
//
// 与链路（DeviceLinkService）分开：链路负责"找到对端 + 加密连接"，
// 同步负责"挑哪个世界、冲突怎么裁决、落到哪个版本"。换同步策略（比如整实例同步）
// 只换这条契约的实现，链路不动。

using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Contracts;

public interface ISaveSync
{
    /// <summary>实现名（设置 / 插件页展示）</summary>
    string DisplayName { get; }

    /// <summary>把本地世界推送到对端（含所属版本配置）</summary>
    Task<SaveSyncResult> PushAsync(PeerInfo peer, SaveInfo save, CancellationToken ct = default);

    /// <summary>列出对端可拉取的世界；对端不可达返回 null</summary>
    Task<List<RemoteSaveInfo>?> ListRemoteAsync(PeerInfo peer, CancellationToken ct = default);

    /// <summary>从对端拉回一个世界（冲突裁决与推送对称）</summary>
    Task<SaveSyncResult> PullAsync(PeerInfo peer, RemoteSaveInfo remote,
        string localVersionId, bool localIsolated, CancellationToken ct = default);
}
