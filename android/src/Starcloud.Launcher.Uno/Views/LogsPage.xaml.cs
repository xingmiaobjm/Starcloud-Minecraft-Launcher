// 日志页：启动器日志 / 每局游戏日志切换查看
// 移植自桌面 Starcloud.App.Views.LogsPage，仅做安卓侧适配：
// - 「打开日志目录」：桌面 Process.Start 拉起资源管理器；安卓无对应语义，
//   改为 UiDialog 展示目录路径（LauncherPaths 日志文件在安卓 DataDir 下同样可用）
// - 跨页来源请求：桌面走 OnNavigatedTo；安卓壳导航走 IShellPage.OnShown，
//   两个入口都记录意图（谁先到谁记，Loaded 消费一次）

using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class LogsPage : Page, IShellPage
{
    /// <summary>日志行：时间戳 [..] + 可选类别 [..] + 正文（游戏日志是 [时间] [线程/级别]: 正文）</summary>
    private static readonly Regex LinePattern = new(
        @"^(?<ts>\[[^\]]+\])\s*(?<cat>\[[^\]]+\])?\s*(?<msg>.*)$", RegexOptions.Compiled);

    private readonly List<string> _gameLogs = [];
    private bool _ready;
    private bool _fillingCombo;

    /// <summary>跨页来源请求（「运行状态」页点「打开日志」时置 "game"）；显示时机消费一次</summary>
    private string? _pendingSource;

    /// <summary>首次 Loaded 已跑完：页面启用导航缓存，之后每次进入由 OnShown 负责落地来源并重载。</summary>
    private bool _firstLoadDone;

    /// <summary>日志窗口显示的行数：只读尾部若干行，几十万行的日志也不会卡住页面</summary>
    private const int TailLines = 400;

    public LogsPage()
    {
        InitializeComponent();
        ApplySourceLabels();
        // 页面启用导航缓存后实例与壳同寿命：语言事件订阅随构造建立一次，不再随 Unloaded 解绑
        // （缓存页每次导航离开都会触发 Unloaded，若解绑，再次进入后就收不到语言切换了）。
        Loc.LanguageChanged += ApplySourceLabels;
        Loaded += (_, _) =>
        {
            if (_firstLoadDone) return; // 缓存实例再次挂树：落地来源与重载由 OnShown 负责，避免双份
            _firstLoadDone = true;
            ApplySourceIntent();
        };
    }

    /// <summary>壳导航显示时机：补记跨页来源意图并重载日志（桌面在 OnNavigatedTo + Loaded 里做同一件事）。</summary>
    public void OnShown()
    {
        if (_pendingSource is null && App.PendingLogSource is { } src)
        {
            _pendingSource = src;
            App.PendingLogSource = null;
        }
        if (!_firstLoadDone) return; // 首次进入交给 Loaded（此刻控件尚未就绪）
        ApplySourceIntent();
    }

    /// <summary>落地来源意图并重载日志（首次 Loaded 与缓存后每次进入共用；对齐桌面新实例的初始态）。</summary>
    private void ApplySourceIntent()
    {
        _ready = true;
        // 来源切换在这里落地，null 时维持「启动器日志」现状
        var idx = _pendingSource == "game" ? 1 : 0;
        _pendingSource = null;
        LogSource.SelectedIndex = idx;   // 触发 SelectionChanged → Load()
        Load();
    }

    /// <summary>只记意图，真正切来源在显示时机（OnShown / 首次 Loaded）里做</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_pendingSource is null && App.PendingLogSource is { } src)
        {
            _pendingSource = src;
            App.PendingLogSource = null;
        }
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
            LogText.Inlines.Clear();
            LogText.Text = IsGameSource ? Loc.T("（暂无游戏日志：启动一次游戏后这里会出现）") : Loc.T("（暂无日志）");
            return;
        }
        try
        {
            // 游戏运行中日志写方全程持有写句柄（FileShare.ReadWrite），
            // File.ReadLines 默认只允许 FileShare.Read，与写方的写权限冲突 → 共享违规。
            // 显式以共享读写+删除打开，读取尾部 400 行
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            // 队列当环形缓冲用：List 头部删除是 O(400) 的搬移，日志动辄几十万行时会白烧掉几百毫秒
            var tail = new Queue<string>(TailLines);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == TailLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            RenderLines(tail);
        }
        catch (Exception ex)
        {
            LogText.Inlines.Clear();
            LogText.Text = Loc.T("读取日志失败：{0}", ex.Message);
        }
    }

    /// <summary>
    /// 三级着色：时间戳最轻、类别次之（错误/警告按语义给色）、正文为主色。
    /// 一屏 400 行等宽同色文字是读不下去的，分色之后"哪一行有事"能一眼扫出来。
    /// </summary>
    private void RenderLines(IEnumerable<string> lines)
    {
        var time = Lookup("TextFillColorTertiaryBrush", Windows.UI.Color.FromArgb(255, 140, 140, 140));
        var category = Lookup("TextFillColorSecondaryBrush", Windows.UI.Color.FromArgb(255, 110, 110, 110));
        var body = Lookup("TextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 32, 32, 32));
        var danger = Lookup("DangerBrush", Windows.UI.Color.FromArgb(255, 244, 67, 54));
        var caution = Lookup("CautionChipTextBrush", Windows.UI.Color.FromArgb(255, 180, 83, 9));

        LogText.Inlines.Clear();
        foreach (var line in lines)
        {
            var m = LinePattern.Match(line);
            if (!m.Success)
            {
                // 续行（例如堆栈）：正文色更轻，避免抢视线
                LogText.Inlines.Add(new Run { Text = line + "\n", Foreground = category });
                continue;
            }

            var cat = m.Groups["cat"].Value;
            LogText.Inlines.Add(new Run { Text = m.Groups["ts"].Value + " ", Foreground = time });
            if (cat.Length > 0)
                LogText.Inlines.Add(new Run { Text = cat + " ", Foreground = SeverityOf(cat, category, danger, caution) });
            LogText.Inlines.Add(new Run { Text = m.Groups["msg"].Value + "\n", Foreground = body });
        }
    }

    private static Brush SeverityOf(string category, Brush normal, Brush danger, Brush caution)
    {
        var c = category.ToUpperInvariant();
        if (c.Contains("ERROR") || c.Contains("FATAL") || c.Contains("SEVERE") || category.Contains("错误"))
            return danger;
        if (c.Contains("WARN") || category.Contains("警告"))
            return caution;
        return normal;
    }

    /// <summary>
    /// 取主题画刷。注意：直接 <c>Application.Current.Resources[key]</c> 不会走 ThemeDictionaries
    /// 的当前主题解析（实测拿到的是深色主题那一套，浅色界面上文字几乎看不见），
    /// 所以先按元素的 ActualTheme 去对应字典里查，再退回直接查找。
    /// </summary>
    private Brush Lookup(string key, Windows.UI.Color fallback)
    {
        var res = Application.Current.Resources;
        var themeKey = LogText.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (res.ThemeDictionaries.TryGetValue(themeKey, out var themeObj) &&
            themeObj is ResourceDictionary theme &&
            theme.TryGetValue(key, out var themed) && themed is Brush themedBrush)
            return themedBrush;
        return res[key] as Brush ?? new SolidColorBrush(fallback);
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        // 桌面 Process.Start 拉起资源管理器；安卓无对应语义，展示路径供文件管理器前往
        var dir = IsGameSource ? LauncherPaths.GameLogsDir : LauncherPaths.LogsDir;
        try { Directory.CreateDirectory(dir); }
        catch { /* 创建失败也照常展示路径 */ }
        _ = UiDialog.AlertAsync(Loc.T("日志目录"), dir);
    }
}
