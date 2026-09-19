// 插件独立日志：写 %APPDATA%/starcloud-launcher/plugins/logs/<id>.log，并镜像进启动器统一日志

using Starcloud.Core.Services;

namespace Starcloud.Core.Plugins;

public sealed class PluginLogger
{
    private readonly string _pluginId;
    private readonly string _logDir;
    private readonly object _lock = new();

    public PluginLogger(string pluginId, string logDir)
    {
        _pluginId = pluginId;
        _logDir = logDir;
    }

    public string LogFile => Path.Combine(_logDir, $"{_pluginId}.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_logDir);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志写失败不阻塞插件运行
        }
        LauncherLog.Write("plugin", $"[{_pluginId}] {message}");
    }
}
