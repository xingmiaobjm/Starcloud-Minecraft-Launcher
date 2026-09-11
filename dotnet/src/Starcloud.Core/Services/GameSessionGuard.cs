// 跨进程游戏会话锁：同一 .minecraft（实例目录）下只允许一局游戏在跑
// 设计：docs/superpowers/specs/2026-09-11-launch-reliability-design.md §8.3

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>锁文件内容（<c>&lt;dotMc&gt;/starcloud/game-session.json</c>）</summary>
public sealed class GameSessionLock
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("version_id")] public string VersionId { get; set; } = "";
    [JsonPropertyName("started_at")] public DateTime StartedAt { get; set; }
}

/// <summary>游戏会话锁持有句柄；Dispose 时移除锁文件（进程退出/取消/启动失败都要走到）</summary>
public sealed class GameSessionHandle : IDisposable
{
    private readonly string _file;
    private bool _disposed;

    internal GameSessionHandle(string file, string versionId)
    {
        _file = file;
        VersionId = versionId;
    }

    public string VersionId { get; }

    /// <summary>游戏进程 pid（拉起前为 0）</summary>
    public int Pid { get; private set; }

    /// <summary>进程拉起后回填真实 pid 并重写锁文件</summary>
    public void Bind(int pid)
    {
        Pid = pid;
        GameSessionGuard.WriteLock(_file, new GameSessionLock
        {
            Pid = pid,
            VersionId = VersionId,
            StartedAt = DateTime.Now,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { File.Delete(_file); } catch { /* 已被清理忽略 */ }
    }
}

/// <summary>游戏会话互斥锁（比 App.RunningVersionId 更可靠：启动器重启后仍生效）</summary>
public sealed class GameSessionGuard
{
    /// <summary>尝试占锁；成功返回句柄（Dispose 释放）。已被占用返回 null，blockedBy = 占用者版本</summary>
    public static GameSessionHandle? TryAcquire(string dotMc, string versionId, out string? blockedBy)
    {
        blockedBy = null;
        var file = LauncherPaths.GameSessionFile(dotMc);
        var existing = ReadLock(file);
        if (existing is not null && IsAliveJavaProcess(existing.Pid))
        {
            blockedBy = string.IsNullOrEmpty(existing.VersionId) ? "未知版本" : existing.VersionId;
            return null;
        }

        var handle = new GameSessionHandle(file, versionId);
        WriteLock(file, new GameSessionLock { Pid = 0, VersionId = versionId, StartedAt = DateTime.Now });
        return handle;
    }

    internal static void WriteLock(string file, GameSessionLock content)
    {
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(content,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* 占锁持久化失败不阻塞启动 */ }
    }

    private static GameSessionLock? ReadLock(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<GameSessionLock>(File.ReadAllText(file));
        }
        catch { return null; }
    }

    /// <summary>pid 存活且进程名形如 java* 才算真占用（防 pid 复用误判）</summary>
    private static bool IsAliveJavaProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
