// 启动上下文（本局生效的启动参数快照）存取
// 设计：docs/designs/2026-09-26-game-running-status-panel-design.md §3
//
// 只写「本局实际生效」的启动参数，供「运行状态」页在启动器重启后仍能展示 JVM / 窗口 / 内容计数。
// 刻意没有完整命令行的字段：BuildCommandLineAsync 的产物含 --accessToken，绝不落盘。

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>启动上下文（写 <c>&lt;dotMc&gt;/starcloud/game-launch-&lt;pid&gt;.json</c>，本局退出即删）</summary>
public sealed record GameLaunchContext(
    [property: JsonPropertyName("version_id")] string VersionId,
    [property: JsonPropertyName("java_path")] string JavaPath,
    [property: JsonPropertyName("max_memory_mb")] int MaxMemoryMb,
    [property: JsonPropertyName("min_memory_mb")] int MinMemoryMb,
    [property: JsonPropertyName("window_width")] int WindowWidth,
    [property: JsonPropertyName("window_height")] int WindowHeight,
    [property: JsonPropertyName("fullscreen")] bool Fullscreen,
    [property: JsonPropertyName("extra_jvm_args")] string[] ExtraJvmArgs,
    [property: JsonPropertyName("isolation")] string Isolation,
    [property: JsonPropertyName("mod_count")] int ModCount,
    [property: JsonPropertyName("shader_pack_count")] int ShaderPackCount,
    [property: JsonPropertyName("resource_pack_count")] int ResourcePackCount,
    [property: JsonPropertyName("captured_at")] DateTime CapturedAt);

/// <summary>
/// 启动上下文文件存取（仅「运行状态」页消费）。
/// 写读全程静默：任何失败都不阻塞启动，读失败按「上下文缺失」降级。
/// </summary>
public static class GameLaunchContextStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>上下文文件路径（与实例注册文件同目录，一实例一份）</summary>
    public static string FileFor(string dotMc, int pid) =>
        Path.Combine(LauncherPaths.GameStateDir(dotMc), $"game-launch-{pid}.json");

    /// <summary>写入上下文（原子写：tmp + File.Move overwrite；失败静默）</summary>
    public static void Write(string dotMc, int pid, GameLaunchContext ctx)
    {
        if (pid <= 0) return;
        try
        {
            var file = FileFor(dotMc, pid);
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ctx, Options));
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* 上下文只是展示增强，写失败不影响启动 */ }
    }

    /// <summary>删除上下文（本局退出时调用；失败静默）</summary>
    public static void Delete(string dotMc, int pid)
    {
        if (pid <= 0) return;
        try { File.Delete(FileFor(dotMc, pid)); } catch { /* 已删除/被占用忽略 */ }
    }

    /// <summary>清理不再存活的实例留下的上下文文件（启动器被强杀时才会有残留；失败静默）</summary>
    public static void PruneDead(string dotMc, IReadOnlyCollection<int> alivePids)
    {
        try
        {
            var dir = LauncherPaths.GameStateDir(dotMc);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "game-launch-*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var tail = name["game-launch-".Length..];
                if (int.TryParse(tail, out var pid) && alivePids.Contains(pid)) continue;
                try { File.Delete(file); } catch { /* 被占用忽略 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    /// <summary>
    /// 读取上下文；不存在 / 损坏 / 与 <paramref name="expectVersionId"/> 不符 → null。
    /// 版本不符返回 null 用于丢弃上一局的残留文件。
    /// </summary>
    public static GameLaunchContext? Read(string dotMc, int pid, string? expectVersionId = null)
    {
        if (pid <= 0) return null;
        try
        {
            var file = FileFor(dotMc, pid);
            if (!File.Exists(file)) return null;
            var ctx = JsonSerializer.Deserialize<GameLaunchContext>(File.ReadAllText(file), Options);
            if (ctx is null) return null;
            if (expectVersionId is not null && ctx.VersionId != expectVersionId) return null;
            return ctx;
        }
        catch { return null; }
    }

    /// <summary>由层叠启动参数组装上下文（计数由调用方传入，见 <see cref="CountFiles"/>）</summary>
    public static GameLaunchContext Capture(string versionId, EffectiveLaunch eff,
        int modCount, int shaderPackCount, int resourcePackCount) =>
        new(versionId,
            eff.Java ?? "",
            eff.MaxMem,
            eff.MinMem,
            eff.WindowW ?? 0,
            eff.WindowH ?? 0,
            eff.Fullscreen ?? false,
            [.. eff.ExtraJvmArgs],
            eff.Isolated ? "isolated" : "shared",
            modCount,
            shaderPackCount,
            resourcePackCount,
            DateTime.Now);

    /// <summary>统计目录下的文件数（目录不存在 / 无权限 → 0）</summary>
    public static int CountFiles(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Count() : 0;
        }
        catch { return 0; }
    }
}
