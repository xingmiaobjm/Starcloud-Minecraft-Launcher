// 启动器日志：按日滚动落盘 + 内存尾部缓冲 + 变更事件（对应旧 Rust logging.rs）
// 设计：docs/designs/2026-09-11-launch-reliability-design.md §4.1

using System.Collections.Concurrent;
using System.Text;

namespace Starcloud.Core.Services;

/// <summary>启动器自身日志（logs/launcher.log）：落盘失败一律静默，绝不影响启动</summary>
public static class LauncherLog
{
    private const int BufferLimit = 500;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>超过上限时保留的尾部长度（按字符近似，无需精确）</summary>
    private const int TrimKeepChars = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<string> Buffer = new();

    /// <summary>进程启动当天（跨天自动滚动文件名，沿用旧版「按日滚动」语义）</summary>
    private static readonly DateTime StartDate = DateTime.Today;

    /// <summary>
    /// 待落盘队列。日志写入原来在调用线程同步做（打开文件 + 追加 + 关闭），
    /// 启动期日志密集时会一次次卡住 UI 线程；现在改成后台消费者批量追加。
    /// </summary>
    private static readonly ConcurrentQueue<string> Pending = new();

    private static int _writesSinceSizeCheck;

    /// <summary>追加一行时触发（可能在后台线程；订阅方自行切 UI 线程）</summary>
    public static event Action<string>? Appended;

    /// <summary>当前日志文件：当日用 launcher.log，跨天用 launcher-日期.log</summary>
    public static string CurrentFile =>
        DateTime.Today == StartDate
            ? Path.Combine(LauncherPaths.LogsDir, "launcher.log")
            : Path.Combine(LauncherPaths.LogsDir, $"launcher-{DateTime.Today:yyyy-MM-dd}.log");

    /// <summary>写一行（自动加时间戳）</summary>
    public static void Write(string category, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}";
        lock (Gate)
        {
            Buffer.Enqueue(line);
            while (Buffer.Count > BufferLimit) Buffer.Dequeue();
        }
        try
        {
            Appended?.Invoke(line);
        }
        catch
        {
            // 订阅方异常不影响日志系统
        }
        Pending.Enqueue(line);
        EnsureWriter();
    }

    /// <summary>异常便捷写法</summary>
    public static void Error(string category, string message, Exception ex) =>
        Write(category, $"{message}：{ex.GetType().Name}: {ex.Message}");

    /// <summary>最近 N 行（内存缓冲快照）</summary>
    public static IReadOnlyList<string> Tail(int count)
    {
        if (count <= 0) return [];
        lock (Gate)
        {
            return Buffer.Count <= count ? [.. Buffer] : Buffer.Skip(Buffer.Count - count).ToList();
        }
    }

    private static int _writerStarted;

    /// <summary>确保后台写线程已启动（幂等）。</summary>
    private static void EnsureWriter()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) == 1) return;
        var thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "starcloud-log",
            Priority = ThreadPriority.Lowest,
        };
        thread.Start();
    }

    /// <summary>后台写线程：攒一小批再一次性追加，减少打开/关闭文件的次数。</summary>
    private static void WriterLoop()
    {
        var batch = new List<string>(64);
        while (true)
        {
            if (!Pending.TryDequeue(out var line))
            {
                Thread.Sleep(120);
                continue;
            }
            batch.Add(line);
            // 队列里还有就顺手多收一点，凑成一批写
            while (batch.Count < 128 && Pending.TryDequeue(out var more)) batch.Add(more);
            AppendBatch(batch);
            batch.Clear();
        }
    }

    private static void AppendBatch(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(LauncherPaths.LogsDir);
            var file = CurrentFile;
            var text = string.Concat(lines.Select(l => l + Environment.NewLine));
            File.AppendAllText(file, text, Encoding.UTF8);
            if (++_writesSinceSizeCheck < 50) return;
            _writesSinceSizeCheck = 0;
            TrimIfTooLarge(file);
        }
        catch
        {
            // 日志写入失败忽略
        }
    }

    /// <summary>单文件超 5 MB 时截断保留尾部，避免无限增长</summary>
    private static void TrimIfTooLarge(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length <= MaxFileBytes) return;
            var text = File.ReadAllText(file, Encoding.UTF8);
            var keep = text.Length > TrimKeepChars ? text[^TrimKeepChars..] : text;
            var cut = keep.IndexOf('\n');
            if (cut >= 0) keep = keep[(cut + 1)..];
            File.WriteAllText(file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [log] 超过 5 MB，已截断保留尾部{Environment.NewLine}{keep}",
                Encoding.UTF8);
        }
        catch
        {
            // 截断失败下次再试
        }
    }
}
