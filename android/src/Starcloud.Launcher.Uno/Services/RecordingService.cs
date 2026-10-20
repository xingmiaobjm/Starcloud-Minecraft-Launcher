// 录屏对外服务（安卓适配版）：许可闸门 + 状态播报 + 与游戏生命周期联动。
//
// 桌面端的 WGC（Windows.Graphics.Capture）+ Media Foundation 采集链路在安卓不可用：
// 这里保留与桌面一致的对外 API（许可闸门 / 快照 / 生命周期挂钩），采集能力按「不支持」处理
// （IsSupported = false，Nova 工具层据此不注册录屏动作），页面与 Nova 的 UI 结构保持齐全。

using Starcloud.Launcher.Uno.Localization;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Uno.Services;

/// <summary>录屏运行状态（给 UI 用的快照）</summary>
public sealed record RecordingStatus(
    bool Active,
    string Target,
    string Encoder,
    string OutputPath,
    int Width,
    int Height,
    int Fps,
    long Frames,
    string Error)
{
    public string Describe() => Active
        ? $"正在录屏：{Target} · {Width}×{Height} @{Fps}fps · {Encoder} · 已写 {Frames} 帧\n{OutputPath}"
          + (Error.Length > 0 ? $"\n注意：{Error}" : "")
        : Encoder.Length > 0 ? $"已停止：{OutputPath}\n编码器：{Encoder}" : "";
}

public sealed class RecordingService
{
    /// <summary>录像落盘目录（数据根/recordings）</summary>
    public static string OutputDirectory => Path.Combine(LauncherPaths.DataDir, "recordings");

    private readonly NovaPermissionStore _permissions;
    private readonly NovaActionLog _log;
    private readonly object _gate = new();

    public RecordingService(NovaPermissionStore permissions, NovaActionLog log)
    {
        _permissions = permissions;
        _log = log;
        Directory.CreateDirectory(OutputDirectory);
    }

    /// <summary>状态变化（开录 / 停录 / 出错）——UI 侧据此刷新；
    /// 安卓端无采集链路（IsSupported=false），状态恒不变，事件保留以对齐桌面服务契约</summary>
#pragma warning disable CS0067 // 事件仅在采集链路可用时触发
    public event Action? StatusChanged;
#pragma warning restore CS0067

    public bool IsRecording { get; private set; }

    /// <summary>本机硬件编码器（安卓端无采集链路，恒为空）</summary>
    public IReadOnlyList<string> HardwareEncoders => [];

    /// <summary>当前状态快照（安卓端恒为「未在录屏」）</summary>
    public RecordingStatus Snapshot() => new(false, "", "", "", 0, 0, 0, 0, "");

    /// <summary>安卓端暂无屏幕采集能力：按不支持处理，录屏入口与 Nova 工具据此降级</summary>
    public static bool IsSupported => false;

    /// <summary>探测硬件编码器；安卓端恒为空（失败不抛，与桌面同约定）</summary>
    public IReadOnlyList<string> ProbeEncoders() => [];

    /// <summary>手动录屏：先过许可闸门（与桌面同文案），采集本身不支持</summary>
    public bool StartManual(out string message)
    {
        if (!_permissions.IsOn(NovaPermissionStore.Recording))
        {
            message = Loc.T("录屏许可还没开：先在上面的开关里授权，Nova 才会看屏幕。");
            return false;
        }
        message = Loc.T("安卓端暂不支持录屏。");
        return false;
    }

    /// <summary>游戏启动后自动录：安卓端无采集能力，生命周期挂钩保留为 no-op</summary>
    public void BeginAutoRecording(string versionId, int gamePid)
    {
        // 安卓端不做任何采集；保留挂钩点便于未来接入 MediaProjection 方案
    }

    /// <summary>停止并封装。返回落盘路径（安卓端恒为空串）。</summary>
    public Task<string> StopAsync()
    {
        lock (_gate)
        {
            if (!IsRecording) return Task.FromResult("");
            IsRecording = false;
        }
        return Task.FromResult("");
    }

    /// <summary>游戏退出时收尾：这一局录完了（安卓端无录像，直接返回）</summary>
    public async Task OnGameExitedAsync()
    {
        if (!IsRecording) return;
        await StopAsync().ConfigureAwait(false);
    }
}
