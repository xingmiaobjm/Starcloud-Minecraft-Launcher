// 联机 SDK 契约：一个「联机提供方」要能装核心、起会话、建/进/退房间、报状态。
//
// 两种接入方式（设计：docs/designs/2026-09-21-multiplayer-sdk-design.md）：
//   1) 清单式（推荐，零代码）：核心是「端口文件 + 本地 HTTP 控制面」形态的第三方联机 SDK，
//      写一份 JSON 清单放进 `<数据目录>/multiplayer/providers/` 即可，
//      字段见 <see cref="MultiplayerSdkProtocol"/>；
//   2) 代码式（深度集成）：第三方 SDK 若能提供 .NET 程序集，实现本接口并在
//      <see cref="MultiplayerService.Register"/> 处登记，界面与设置无需改动。

namespace Starcloud.Core.Services.Multiplayer;

public interface IMultiplayerProvider : IDisposable
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>署名字符串（界面底部「联机由 X 提供技术支持」）</summary>
    string Vendor { get; }

    string? VendorUrl { get; }

    string? Description { get; }

    /// <summary>核心是否就绪（版本匹配且可执行文件在位）</summary>
    bool Installed { get; }

    string? InstalledVersion { get; }

    /// <summary>本地联机会话进程是否在跑（含"上一次会话还活着"的情况）</summary>
    bool SessionRunning { get; }

    /// <summary>下载并安装核心（进度经 <paramref name="progress"/> 上报；可取消）</summary>
    Task InstallCoreAsync(IProgress<MultiplayerProgress>? progress, CancellationToken ct);

    /// <summary>确保会话就绪（幂等）；返回是否可用</summary>
    Task<bool> EnsureSessionAsync(CancellationToken ct);

    /// <summary>
    /// 探活：本地是否已有一个活着的联机会话。**不启动新进程**——
    /// 界面刷新状态时用这个，免得「看一眼页面」就把核心拉起来。
    /// </summary>
    Task<bool> ProbeSessionAsync(CancellationToken ct);

    /// <summary>查询当前联机状态</summary>
    Task<MultiplayerState> FetchStateAsync(CancellationToken ct);

    /// <summary>创建房间（游戏内需先「对局域网开放」）</summary>
    Task HostAsync(string? player, IReadOnlyList<string> nodes, CancellationToken ct);

    /// <summary>加入房间</summary>
    Task JoinAsync(string room, string? player, IReadOnlyList<string> nodes, CancellationToken ct);

    /// <summary>退回空闲（不结束核心进程）</summary>
    Task IdleAsync(CancellationToken ct);

    /// <summary>结束本启动器拉起的那次会话</summary>
    Task StopSessionAsync(CancellationToken ct);

    /// <summary>拉取核心日志（无日志接口返回空串）</summary>
    Task<string> FetchLogsAsync(CancellationToken ct);

    /// <summary>
    /// 拉取可用公共节点。<paramref name="region"/> 取 <c>auto</c> / <c>cn</c> / <c>global</c>：
    /// 同一房间的玩家建议接入同一地区节点，跨地域节点之间常常互不联通。
    /// </summary>
    Task<IReadOnlyList<string>> FetchNodesAsync(string region, CancellationToken ct);
}
