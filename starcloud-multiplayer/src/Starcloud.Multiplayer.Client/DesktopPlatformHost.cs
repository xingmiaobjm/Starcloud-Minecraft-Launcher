using System.Runtime.InteropServices;

namespace Starcloud.Multiplayer.Client;

/// <summary>
/// 桌面（Windows / Linux / macOS）实现：数据目录按各平台惯例，
/// 日志同时进控制台与 <c>&lt;数据目录&gt;/logs/kernel.log</c>。
/// </summary>
public sealed class DesktopPlatformHost : IPlatformHost
{
    private readonly object _gate = new();
    private readonly string _logPath;

    public DesktopPlatformHost(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? DefaultDataDirectory();
        Directory.CreateDirectory(DataDirectory);
        var logDir = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "kernel.log");
    }

    public string DataDirectory { get; }

    public void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Console.WriteLine(line);
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // 日志写不进去不该影响联机本身。
        }
    }

    private static string DefaultDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Starcloud-Multiplayer");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Path.Combine(home, "Library", "Application Support", "Starcloud-Multiplayer")
            : Path.Combine(home, ".local", "share", "Starcloud-Multiplayer");
    }
}
