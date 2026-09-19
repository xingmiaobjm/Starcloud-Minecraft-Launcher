// 日志页：启动器日志 / 每局游戏日志切换查看

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class LogsPage : Page
{
    private readonly List<string> _gameLogs = [];
    private bool _ready;
    private bool _fillingCombo;

    public LogsPage()
    {
        InitializeComponent();
        ApplySourceLabels();
        Loc.LanguageChanged += ApplySourceLabels;
        Unloaded += (_, _) => Loc.LanguageChanged -= ApplySourceLabels;
        Loaded += (_, _) =>
        {
            _ready = true;
            LogSource.SelectedIndex = 0;   // 触发 SelectionChanged → Load()
            Load();
        };
    }

    /// <summary>语言切换时重建来源选项（RadioButtons 的 x:String 子项无法挂本地化附加属性）。</summary>
    private void ApplySourceLabels()
    {
        var selected = LogSource.SelectedIndex;
        LogSource.ItemsSource = new[] { Loc.T("启动器日志"), Loc.T("游戏日志") };
        LogSource.SelectedIndex = selected;
    }

    private bool IsGameSource => LogSource.SelectedIndex == 1;

    private void LogSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        GameLogCombo.Visibility = IsGameSource ? Visibility.Visible : Visibility.Collapsed;
        if (IsGameSource) FillGameLogs();
        Load();
    }

    private void GameLogCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _fillingCombo) return;
        Load();
    }

    /// <summary>填充最近 10 个游戏日志（倒序，默认最新）</summary>
    private void FillGameLogs()
    {
        _fillingCombo = true;
        try
        {
            _gameLogs.Clear();
            GameLogCombo.Items.Clear();
            try
            {
                _gameLogs.AddRange(Directory.EnumerateFiles(LauncherPaths.GameLogsDir, "game-*.log")
                    .Select(Path.GetFullPath)
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(10));
            }
            catch { /* 目录不存在等忽略 */ }

            foreach (var f in _gameLogs) GameLogCombo.Items.Add(Path.GetFileName(f));
            GameLogCombo.SelectedIndex = _gameLogs.Count > 0 ? 0 : -1;
        }
        finally { _fillingCombo = false; }
    }

    private void Load()
    {
        string? file = null;
        if (IsGameSource)
        {
            var idx = GameLogCombo.SelectedIndex;
            if (idx >= 0 && idx < _gameLogs.Count) file = _gameLogs[idx];
        }
        else
        {
            file = LauncherLog.CurrentFile;
            if (!File.Exists(file))
            {
                try
                {
                    file = Directory.EnumerateFiles(LauncherPaths.LogsDir, "launcher*.log")
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                }
                catch { file = null; }
            }
        }

        if (file is null || !File.Exists(file))
        {
            LogText.Text = IsGameSource ? Loc.T("（暂无游戏日志：启动一次游戏后这里会出现）") : Loc.T("（暂无日志）");
            return;
        }
        try
        {
            // 游戏运行中 GameSession 全程持有日志写句柄（FileShare.ReadWrite），
            // File.ReadLines 默认只允许 FileShare.Read，与写方的写权限冲突 → 共享违规。
            // 显式以共享读写+删除打开，读取尾部 400 行
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var tail = new List<string>(400);
            while (reader.ReadLine() is { } line)
            {
                tail.Add(line);
                if (tail.Count > 400) tail.RemoveAt(0);
            }
            LogText.Text = string.Join('\n', tail);
        }
        catch (Exception ex)
        {
            LogText.Text = Loc.T("读取日志失败：{0}", ex.Message);
        }
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = IsGameSource ? LauncherPaths.GameLogsDir : LauncherPaths.LogsDir;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch { /* 打不开资源管理器忽略 */ }
    }
}
