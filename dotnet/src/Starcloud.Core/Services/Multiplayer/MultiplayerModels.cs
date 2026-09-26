// 联机（multiplayer）领域模型。
//
// 目标：把「陶瓦联机」与「第三方联机 SDK」归一成同一套状态，界面只认这里，
// 不关心底层是哪家 SDK、也不关心它的 HTTP 协议长什么样。
// 设计：docs/designs/2026-09-21-multiplayer-sdk-design.md

namespace Starcloud.Core.Services.Multiplayer;

/// <summary>
/// 归一化联机阶段。各 SDK 的原始状态字符串（`host-scanning` / `guest-ok` …）
/// 由各自的协议描述映射到这几档，界面据此显示文案与可用操作。
/// </summary>
public enum MultiplayerPhase
{
    /// <summary>空闲：核心就绪、未在联机</summary>
    Idle,
    /// <summary>房主侧：正在扫描本机局域网世界（游戏内需先「对局域网开放」）</summary>
    HostScanning,
    /// <summary>房主侧：房间启动中</summary>
    HostStarting,
    /// <summary>房主侧：房间已开，<see cref="MultiplayerState.Room"/> 是邀请码</summary>
    HostReady,
    /// <summary>加入方：连接房间中</summary>
    GuestConnecting,
    /// <summary>加入方：已进入虚拟局域网，仍在等对端就绪</summary>
    GuestEntering,
    /// <summary>加入方：联机成功，游戏内局域网列表可见房主世界</summary>
    GuestReady,
    /// <summary>异常</summary>
    Failed,
}

/// <summary>
/// 一次联机的归一化状态快照。<paramref name="Room"/> / <paramref name="Address"/> /
/// <paramref name="Difficulty"/> / <paramref name="Players"/> 只有对应阶段才有值。
/// </summary>
public sealed record MultiplayerState(
    MultiplayerPhase Phase,
    string? Room = null,
    string? Address = null,
    string? Difficulty = null,
    IReadOnlyList<string>? Players = null,
    string? ErrorKind = null,
    string? Error = null)
{
    public static readonly MultiplayerState Idle = new(MultiplayerPhase.Idle);

    /// <summary>是否处于「已经在联机」的几档（用于判断能不能点创建/加入）</summary>
    public bool IsBusy => Phase is MultiplayerPhase.HostScanning or MultiplayerPhase.HostStarting
        or MultiplayerPhase.GuestConnecting or MultiplayerPhase.GuestEntering;
}

/// <summary>联机核心的安装 / 运行情况</summary>
public sealed record MultiplayerCoreStatus(bool Installed, string? Version, bool SessionRunning);

/// <summary>推给界面的完整快照：提供方信息 + 核心状态 + 联机状态</summary>
public sealed record MultiplayerSnapshot(
    string ProviderId,
    string DisplayName,
    string Vendor,
    string? VendorUrl,
    MultiplayerCoreStatus Core,
    MultiplayerState State,
    string? LastError = null);

/// <summary>核心下载 / 解压进度（阶段文案 + 字节数，速度为 0 表示未知）</summary>
public sealed record MultiplayerProgress(string Stage, long Bytes, long? Total, double SpeedBps = 0);

/// <summary>一个可用联机提供方的自述（界面下拉用；不含运行期状态）</summary>
public sealed record MultiplayerProviderInfo(
    string Id,
    string DisplayName,
    string Vendor,
    string? VendorUrl,
    string? Description,
    bool BuiltIn);
