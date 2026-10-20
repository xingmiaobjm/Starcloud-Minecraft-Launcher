using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Text.Method;
using Android.Views;
using Android.Widget;

namespace Starcloud.Spike.S1Jvm;

/// <summary>
/// S1 spike 壳 Activity：纯程序化 UI（无 AXAML），一个按钮触发验证流程，
/// 结果逐行追加到等宽日志区。验证逻辑全部在 Spike 类中。
/// </summary>
[Activity(Label = "S1 JVM Spike", MainLauncher = true)]
public class MainActivity : Activity
{
    private TextView _log = null!;
    private Button _runButton = null!;
    private string _cacheDir = string.Empty;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _cacheDir = CacheDir!.AbsolutePath;
        float density = Resources!.DisplayMetrics!.Density;
        int pad = (int)(16 * density);

        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        root.SetPadding(pad, pad, pad, pad);

        var title = new TextView(this)
        {
            Text = "M0-S1 JVM 拉起验证（libjvm.so / JNI Invocation）",
            TextSize = 18f,
        };
        title.SetPadding(0, 0, 0, (int)(12 * density));

        _runButton = new Button(this) { Text = "运行 Spike" };
        _runButton.Click += OnRunClicked;

        _log = new TextView(this)
        {
            TextSize = 13f,
            Typeface = Typeface.Monospace,
            MovementMethod = ScrollingMovementMethod.Instance,
        };

        var scroll = new ScrollView(this);
        scroll.AddView(_log);

        root.AddView(title);
        root.AddView(_runButton, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
    }

    private void OnRunClicked(object? sender, EventArgs e)
    {
        // JVM 进程内只能创建一次，跑完禁用按钮；重跑需重启应用
        _runButton.Enabled = false;
        _runButton.Text = "已运行（重跑请重启应用）";
        _log.Text = string.Empty;

        var log = new Action<string>(msg => RunOnUiThread(() => AppendLine(msg)));
        Task.Run(() =>
        {
            try
            {
                Spike.Run(log, _cacheDir);
            }
            catch (Exception ex)
            {
                log("未捕获异常：" + ex);
                log("=== S1 结论：未通过 ===");
            }
        });
    }

    private void AppendLine(string message)
    {
        _log.Append(message + "\n");
    }
}
