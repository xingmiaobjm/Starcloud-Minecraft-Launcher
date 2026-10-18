// 临时帧探针：排查动效卡顿用（拿到证据后删除）。
//
// 只有环境变量 STARCLOUD_FRAME_PROBE=1 时才接线，平时零开销。
// 用法：在动作起点调用 Note("xxx")，之后 1.5 秒内统计每个合成帧的间隔，
// 窗口结束时把「帧数 / 均帧 / 最大帧 / 超 16.7ms 帧数 / 超 25ms 帧数」写进
// logs/launcher.log（[frame] 分类），用来判断"卡一下"到底是丢帧还是观感问题。

using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

internal static class FrameProbe
{
    /// <summary>Note 之后持续观察的窗口（毫秒）</summary>
    private const double WindowMs = 1600;

    private static bool _enabled;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastMs = -1;
    private static double _activeUntil = -1;
    private static string _windowName = "";
    private static FrameworkElement? _traced;
    private static double _tracedH = -1;
    private static readonly List<double> Samples = [];
    private static string _file = "";

    public static void Start()
    {
        if (Environment.GetEnvironmentVariable("STARCLOUD_FRAME_PROBE") != "1") return;
        _enabled = true;
        _file = Path.Combine(Path.GetTempPath(), "sc_frame_probe.txt");
        try { File.WriteAllText(_file, $"probe {DateTime.Now:HH:mm:ss}\n"); } catch { }
        CompositionTarget.Rendering += OnRendering;
        LauncherLog.Write("frame", "探针已接线");
    }

    /// <summary>标记一个动作起点，并开启观察窗口</summary>
    public static void Note(string name)
    {
        if (!_enabled) return;
        Flush();
        var now = Clock.Elapsed.TotalMilliseconds;
        _windowName = name;
        _activeUntil = now + WindowMs;
        _lastMs = now;
        LauncherLog.Write("frame", $"▶ {name} @{now:F0}ms");
    }

    /// <summary>逐帧记录某元素的高度变化轨迹（判断展开/收起到底有没有在动）</summary>
    public static void TraceHeightOf(FrameworkElement element)
    {
        if (_enabled) _traced = element;
    }

    /// <summary>把一次操作计时并落盘（用于定位同步耗时的那一段）</summary>
    public static void Measure(string name, Action action)
    {
        if (!_enabled)
        {
            action();
            return;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            Emit($"⏱ {name} 同步耗时 {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
    }

    private static void OnRendering(object? sender, object e)
    {
        var now = Clock.Elapsed.TotalMilliseconds;
        var last = _lastMs;
        _lastMs = now;

        if (_traced is not null)
        {
            var h = _traced.ActualHeight;
            if (Math.Abs(h - _tracedH) > 0.6)
            {
                _tracedH = h;
                Emit($"   h={h:F0} @{now:F0}ms");
            }
        }

        if (now > _activeUntil)
        {
            Flush();
            return;
        }
        if (last > 0) Samples.Add(now - last);
    }

    /// <summary>窗口结束：输出这一段的帧率分布</summary>
    private static void Flush()
    {
        if (Samples.Count == 0) return;
        var list = Samples.ToArray();
        Samples.Clear();
        var avg = list.Average();
        var max = list.Max();
        var over167 = list.Count(v => v > 16.7);
        var over25 = list.Count(v => v > 25);
        Emit($"窗口[{_windowName}] 帧数 {list.Length}｜均帧 {avg:F1} ms｜最大 {max:F1} ms｜超16.7ms {over167} 帧｜超25ms {over25} 帧");
    }

    private static void Emit(string message)
    {
        LauncherLog.Write("frame", "   " + message);
        try { File.AppendAllText(_file, message + "\n", Encoding.UTF8); } catch { }
    }
}