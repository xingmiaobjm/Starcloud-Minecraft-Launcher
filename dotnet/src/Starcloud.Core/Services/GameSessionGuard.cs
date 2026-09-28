// 跨进程游戏实例注册表：同一 .minecraft（实例目录）下允许同时跑多局
// 设计：docs/designs/2026-09-11-launch-reliability-design.md §8.3（已改为「允许同版本多开」）
//
// 一实例一文件：<dotMc>/starcloud/game-session-<pid>.json。
// 注册表只做「谁在跑」的事实记录，不再承担互斥（多开是产品能力）。

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>单个游戏实例的注册内容（<c>&lt;dotMc&gt;/starcloud/game-session-&lt;pid&gt;.json</c>）</summary>
public sealed class GameSessionLock
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("version_id")] public string VersionId { get; set; } = "";
    [JsonPropertyName("started_at")] public DateTime StartedAt { get; set; }

    /// <summary>本局日志文件（供重启后仍能读日志尾；老文件缺失时为 null）</summary>
    [JsonPropertyName("log_file")] public string? LogFile { get; set; }
}

/// <summary>
/// 游戏实例注册表（比 App 内存里的运行态更可靠：启动器重启后仍能看见本机在跑的游戏）。
/// 所有查询都是「按 pid 判活过滤」后的结果，且不产生副作用（清理死文件在 <see cref="PruneDead"/>）。
///
/// 写侧只有 <see cref="Publish"/> / <see cref="Unpublish"/> 一对，且由 <c>GameRuntimeMonitor</c> 独占调用：
/// 注册文件的生命周期 = 实例在监控器里的生命周期，页面/启动路径不再持有句柄（多开时不能互相删文件）。
/// </summary>
public sealed class GameSessionGuard
{
    /// <summary>登记一个游戏实例（进程拉起后调用）；内部顺带清理已死实例的残留文件</summary>
    public static void Publish(string dotMc, int pid, string versionId, DateTime startedAt, string? logFile)
    {
        if (string.IsNullOrEmpty(dotMc) || pid <= 0) return;
        PruneDead(dotMc);
        WriteLock(LauncherPaths.GameSessionFile(dotMc, pid), new GameSessionLock
        {
            Pid = pid,
            VersionId = versionId,
            StartedAt = startedAt,
            LogFile = logFile,
        });
    }

    /// <summary>注销一个游戏实例（该实例收口时调用）；失败静默</summary>
    public static void Unpublish(string dotMc, int pid)
    {
        if (string.IsNullOrEmpty(dotMc) || pid <= 0) return;
        try { File.Delete(LauncherPaths.GameSessionFile(dotMc, pid)); } catch { /* 已删除忽略 */ }
    }

    /// <summary>本机在该 .minecraft 下存活的全部实例（按启动时刻升序；纯读、无副作用）</summary>
    public static IReadOnlyList<GameSessionLock> All(string dotMc, Func<int, bool>? isAlive = null)
    {
        var probe = isAlive ?? IsAliveJavaProcess;
        var result = new List<GameSessionLock>();
        try
        {
            var dir = LauncherPaths.GameStateDir(dotMc);
            if (!Directory.Exists(dir)) return [];
            foreach (var file in Directory.EnumerateFiles(dir, "game-session-*.json"))
            {
                var lk = ReadLock(file);
                if (lk is null || lk.Pid <= 0) continue;
                if (!probe(lk.Pid)) continue;
                result.Add(lk);
            }
        }
        catch { return []; }
        return [.. result.OrderBy(l => l.StartedAt)];
    }

    /// <summary>该 .minecraft 下是否有存活的游戏进程（危险操作避让 / 自更新避让）</summary>
    public static bool IsAnyGameAlive(string dotMc) => All(dotMc).Count > 0;

    /// <summary>指定版本是否有存活实例（版本列表的「运行中」标记）</summary>
    public static bool IsVersionAlive(string dotMc, string versionId) =>
        All(dotMc).Any(l => l.VersionId == versionId);

    /// <summary>
    /// 存活实例中最近启动的一个（没有则 null）。
    /// 供插件 ctx.game.status() 等「只认一局」的旧调用点兜底；新代码优先用 GameRuntimeMonitor 的选中实例。
    /// </summary>
    public static GameSessionLock? Current(string dotMc)
    {
        var all = All(dotMc);
        return all.Count == 0 ? null : all[^1];
    }

    /// <summary>删掉 pid 已死的注册文件（只在 <see cref="Publish"/> 时调用，避免状态目录无限增长）</summary>
    public static void PruneDead(string dotMc)
    {
        try
        {
            var dir = LauncherPaths.GameStateDir(dotMc);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "game-session-*.json"))
            {
                var lk = ReadLock(file);
                if (lk is not null && lk.Pid > 0 && IsAliveJavaProcess(lk.Pid)) continue;
                try { File.Delete(file); } catch { /* 被占用忽略 */ }
            }
        }
        catch { /* 清理失败不影响注册 */ }
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
        catch { /* 注册持久化失败不阻塞启动 */ }
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

    /// <summary>pid 存活且进程名形如 java* 才算真在跑（防 pid 复用误判）</summary>
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
