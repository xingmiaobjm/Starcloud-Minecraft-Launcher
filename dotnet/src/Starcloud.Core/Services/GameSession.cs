// 单次游戏会话：进程包装 + stdout/stderr 重定向落盘 + 实时行事件
// 设计：docs/superpowers/specs/2026-09-11-launch-reliability-design.md §4.2

using System.Diagnostics;
using System.Text;

namespace Starcloud.Core.Services;

/// <summary>
/// 一次游戏进程会话。重定向后必须持续读流（写满管道会阻塞游戏），
/// 输出按行落盘到 <see cref="LogFile"/>（单文件上限 20 MB），并向 UI 抛 <see cref="OutputLine"/>。
/// 退出码只在 <c>WaitForExitAsync()</c> 之后读取。
/// </summary>
public sealed class GameSession : IDisposable
{
    private const int TailLimit = 1000;
    private const long MaxLogBytes = 20 * 1024 * 1024;

    private readonly Process _process;
    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly StreamWriter? _writer;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly Task _lifecycle;

    private int _exitedRaised;
    private bool _logFull;
    private bool _hasExited;
    private bool _disposed;
    private int _exitCode;
    private DateTime _exitedAt;

    internal GameSession(Process process, string versionId, string logFile)
    {
        _process = process;
        VersionId = versionId;
        LogFile = logFile;
        StartedAt = DateTime.Now;
        try
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { _writer = null; }

        _stdoutPump = PumpAsync(process.StandardOutput, "OUT");
        _stderrPump = PumpAsync(process.StandardError, "ERR");
        _lifecycle = CompleteAsync();
    }

    /// <summary>实时输出行（含 [OUT]/[ERR] 前缀），在后台读流线程上触发</summary>
    public event Action<string>? OutputLine;

    /// <summary>进程退出（参数为退出码），保证只触发一次</summary>
    public event Action<int>? Exited;

    public string VersionId { get; }

    /// <summary>本局日志文件（logs/game/game-*.log）</summary>
    public string LogFile { get; }

    public DateTime StartedAt { get; }

    public int Pid => _process.Id;

    /// <summary>兼容 System.Diagnostics.Process.Id 的别名</summary>
    public int Id => Pid;

    public bool HasExited
    {
        get
        {
            if (_hasExited) return true;
            try { return _process.HasExited; } catch { return true; }
        }
    }

    /// <summary>退出码（进程未退出时读会回退到 0；调用方应先判断 <see cref="HasExited"/>）</summary>
    public int ExitCode
    {
        get
        {
            if (_hasExited) return _exitCode;
            try { return _process.ExitCode; } catch { return 0; }
        }
    }

    public TimeSpan Uptime => (_hasExited ? _exitedAt : DateTime.Now) - StartedAt;

    /// <summary>兼容 Process.MainWindowHandle（已退出返回 IntPtr.Zero）</summary>
    public IntPtr MainWindowHandle
    {
        get
        {
            try { return _process.HasExited ? IntPtr.Zero : _process.MainWindowHandle; }
            catch { return IntPtr.Zero; }
        }
    }

    /// <summary>兼容 Process.Refresh()</summary>
    public void Refresh()
    {
        try { _process.Refresh(); } catch { /* 已退出忽略 */ }
    }

    /// <summary>结束游戏进程（连子进程一起结束）；失败静默</summary>
    public void Kill(bool entireTree = true)
    {
        try { if (!_process.HasExited) _process.Kill(entireTree); } catch { /* 已退出忽略 */ }
    }

    /// <summary>最近 <paramref name="count"/> 行输出快照（上限 1000 行）</summary>
    public IReadOnlyList<string> Tail(int count)
    {
        if (count <= 0) return [];
        lock (_gate)
        {
            return _recent.Count <= count ? [.. _recent] : _recent.Skip(_recent.Count - count).ToList();
        }
    }

    /// <summary>等待本局结束（供需要顺序收口的调用方使用）</summary>
    public Task WaitForExitAsync() => _lifecycle;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OutputLine = null;
        Exited = null;
        try { _writer?.Dispose(); } catch { }
        try { _process.Dispose(); } catch { }
    }

    private async Task PumpAsync(StreamReader reader, string tag)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                HandleLine(tag, line);
            }
        }
        catch { /* 进程被杀时读流会抛，忽略 */ }
        finally
        {
            try { reader.Dispose(); } catch { }
        }
    }

    private void HandleLine(string tag, string line)
    {
        var text = $"[{tag}] {line}";
        lock (_gate)
        {
            _recent.Enqueue(text);
            while (_recent.Count > TailLimit) _recent.Dequeue();
        }

        var writer = _writer;
        if (writer is not null && !_logFull)
        {
            try
            {
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}");
                if (writer.BaseStream.Length > MaxLogBytes)
                {
                    _logFull = true;
                    writer.WriteLine("[log] 本局日志超过 20 MB，已停止写入");
                }
            }
            catch { /* 落盘失败不影响游戏 */ }
        }

        try { OutputLine?.Invoke(text); } catch { }
    }

    private async Task CompleteAsync()
    {
        try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch { }
        try { _exitCode = _process.ExitCode; } catch { }

        // 等两条读流自然收尾，确保崩溃诊断能拿到完整尾部输出
        try { await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false); } catch { }

        _hasExited = true;
        _exitedAt = DateTime.Now;
        try { _writer?.Dispose(); } catch { }

        if (Interlocked.Exchange(ref _exitedRaised, 1) == 0)
        {
            try { Exited?.Invoke(_exitCode); } catch { }
        }
    }
}
