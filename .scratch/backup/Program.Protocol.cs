using System.Text.Json;
using Starcloud.Core;

namespace Starcloud.Bridge;

/// <summary>stdio 协议输出：请求响应与事件写入（单锁串行，保证行不交错）。</summary>
public static partial class Program
{
    // ── 输出 ─────────────────────────────────────────────────────────────────

    private static readonly object StdoutLock = new();

    private static void Reply(TextWriter writer, int id, bool ok, object? result = null, string? error = null)
    {
        WriteLine(writer, JsonSerializer.Serialize(new
        {
            id,
            ok,
            result,
            error,
        }, JsonOpts));
    }

    private static void Emit(TextWriter writer, string eventName, object parameters)
    {
        WriteLine(writer, JsonSerializer.Serialize(new
        {
            @event = eventName,
            @params = parameters,
        }, JsonOpts));
    }

    private static void WriteLine(TextWriter writer, string line)
    {
        lock (StdoutLock)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }
}
