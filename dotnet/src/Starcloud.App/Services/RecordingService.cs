// 录屏对外服务：许可闸门 + 目标选择 + 与游戏生命周期联动 + 状态播报。
//
// 目标选择：
//   · 游戏启动自动录 → 等游戏窗口出现后录「游戏窗口」（WGC 抓窗口不受遮挡影响，桌面上的别的东西不会被录进去）
//   · 手动「立即录屏」→ 录主显示器
    // 两个入口都只在「录屏许可」开启时可用；每次开录/停录都写一条可审计记录。

using System.Diagnostics;
using System.IO;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

public sealed class RecordingService
{
    /// <summary>录像落盘目录（%APPDATA%/starcloud-launcher/recordings）</summary>
    public static string OutputDirectory => Path.Combine(LauncherPaths.DataDir, "recordings");

    /// <summary>等游戏窗口出现的上限：Java 进程起来到窗口映射通常几秒，给足余量但不无限等</summary>
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(30);

    private readonly ScreenRecorder _recorder = new();
    private readonly NovaPermissionStore _permissions;
    private readonly NovaActionLog _log;
    private readonly object _gate = new();

    private bool _windowWatcherRunning;

    public RecordingService(NovaPermissionStore permissions, NovaActionLog log)
    {
        _permissions = permissions;
        _log = log;
        Directory.CreateDirectory(OutputDirectory);
        // 这台机器到底有没有硬件编码器，直接写进启动器日志：出问题时一眼能看出是软编还是硬编
        try
        {
            var names = ProbeEncoders();
            LauncherLog.Write("record", names.Count > 0
                ? $"硬件编码器：{string.Join(" / ", names)}"
                : "未检测到硬件编码器，将退回软件编码");
        }
        catch (Exception ex)
        {
            LauncherLog.Write("record", $"硬件编码器探测失败：{ex.Message}");
        }
    }

    /// <summary>状态变化（开录 / 停录 / 出错）——UI 侧据此刷新</summary>
    public event Action? StatusChanged;

    public bool IsRecording { get; private set; }

    /// <summary>本机硬件编码器（子进程探测一次后缓存）</summary>
    public IReadOnlyList<string> HardwareEncoders => ScreenRecorder.HardwareEncoders;

    public RecordingStatus Snapshot() => _recorder.Snapshot();

    public static bool IsSupported => ScreenRecorder.IsSupported();

    /// <summary>探测硬件编码器；返回空列表 = 这台机器只能软编。失败不抛。</summary>
    public IReadOnlyList<string> ProbeEncoders()
    {
        try { return ScreenRecorder.ProbeHardwareEncoders(); }
        catch (Exception ex)
        {
            LauncherLog.Write("record", $"硬件编码器探测失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>手动录屏：录主显示器</summary>
    public bool StartManual(out string message)
    {
        if (!_permissions.IsOn(NovaPermissionStore.Recording))
        {
            message = Loc.T("录屏许可还没开：先在上面的开关里授权，Nova 才会看屏幕。");
            return false;
        }
        return Start(IntPtr.Zero, Loc.T("主显示器"), "manual", out message);
    }

    /// <summary>游戏启动后自动录：等窗口出现再录那个窗口</summary>
    public void BeginAutoRecording(string versionId, int gamePid)
    {
        if (gamePid <= 0 || !_permissions.IsOn(NovaPermissionStore.Recording)) return;
        lock (_gate)
        {
            if (_windowWatcherRunning) return;
            _windowWatcherRunning = true;
        }
        _ = Task.Run(() => WaitForGameWindowAsync(versionId, gamePid));
    }

    private async Task WaitForGameWindowAsync(string versionId, int gamePid)
    {
        try
        {
            var deadline = DateTime.UtcNow + WindowWait;
            while (DateTime.UtcNow < deadline)
            {
                // 已在录 / 这一局（按 pid 认，多开时不看别的实例）已经结束
                if (IsRecording || !GameRuntimeMonitor.Instance.Instances.Any(i => i.Pid == gamePid)) return;
                var hwnd = MainWindowOf(gamePid);
                if (hwnd != IntPtr.Zero)
                {
                    Start(hwnd, Loc.T("{0} 游戏窗口", versionId), versionId, out var message);
                    if (!string.IsNullOrEmpty(message)) LauncherLog.Write("record", message);
                    return;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            LauncherLog.Write("record", $"等 {versionId} 的游戏窗口超时（{WindowWait.TotalSeconds:F0}s），这一局不录");
        }
        catch (Exception ex)
        {
            LauncherLog.Write("record", $"自动录屏失败：{ex.Message}");
        }
        finally
        {
            lock (_gate) _windowWatcherRunning = false;
        }
    }

    private static IntPtr MainWindowOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private bool Start(IntPtr hwnd, string target, string tag, out string message)
    {
        lock (_gate)
        {
            if (IsRecording)
            {
                message = Loc.T("已经在录屏了。");
                return false;
            }
            try
            {
                Directory.CreateDirectory(OutputDirectory);
                var path = Path.Combine(OutputDirectory,
                    $"Starcloud-{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
                // 分辨率由 ScreenRecorder 按捕获目标的实际 Size 决定，这里只给帧率与码率
                _recorder.Start(new RecordingRequest(path), hwnd, target);
                IsRecording = true;
                _log.Append(new NovaActionEntry(Guid.NewGuid().ToString("N")[..12], DateTime.Now,
                    "record", Loc.T("开始录屏"), "已开始", target));
                message = Loc.T("开始录屏：{0}", target);
                LauncherLog.Write("record", $"开始录屏 target={target} hwnd=0x{hwnd:X} out={path}");
                StatusChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                LauncherLog.Write("record", $"开始录屏失败：{ex}");
                message = Loc.T("开始录屏失败：{0}", ex.Message);
                StatusChanged?.Invoke();
                return false;
            }
        }
    }

    /// <summary>停止并封装。返回落盘路径。</summary>
    public async Task<string> StopAsync()
    {
        string path;
        lock (_gate)
        {
            if (!IsRecording) return "";
        }
        try
        {
            path = await _recorder.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("record", $"停止录屏失败：{ex}");
            path = "";
        }
        lock (_gate)
        {
            IsRecording = false;
            _log.Append(new NovaActionEntry(Guid.NewGuid().ToString("N")[..12], DateTime.Now,
                "record", Loc.T("停止录屏"), "已停止", path));
        }
        LauncherLog.Write("record",
            $"停止录屏，文件 {path}（{_recorder.Snapshot().Frames} 帧，错误：{_recorder.Snapshot().Error}）");
        StatusChanged?.Invoke();
        return path;
    }

    /// <summary>游戏退出时收尾：这一局录完了</summary>
    public async Task OnGameExitedAsync()
    {
        if (!IsRecording) return;
        await StopAsync().ConfigureAwait(false);
    }
}
