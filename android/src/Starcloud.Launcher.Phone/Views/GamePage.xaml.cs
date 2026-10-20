// 游戏页：Surface 注册给游戏窗口、触摸/键盘经输入枢纽投递、JVM 拉起与退出联动前台服务。
// 虚拟键鼠层（设计稿 §6.5）：PointerPressed/Released/Exited 经 JavaBridge 投递，
// 与 GameInputHub.HandleKey 同一模式（先更新 GLFW 修饰位，再投 Android 键码）。
// autoLaunch 模式（首页 / 版本页「启动」进入）在页面显示时自动拉起一次启动链。
// 由 MAUI 壳迁移：MainThread → UiThread、DisplayAlert → UiDialog、
// Navigation.Push/Pop → AppNavigation 覆盖层、OnAppearing/OnDisappearing → Loaded/Unloaded。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.Android.Core.Input;
using Starcloud.Android.Host;
using Starcloud.Android.Host.Bridging;
using Starcloud.Android.Host.Runtime;
using Starcloud.Launcher.Phone.Platforms.Android;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views;

/// <summary>
/// 游戏页：覆盖层全窗显示；退出（Unloaded）时撤销输入投递与事件订阅。
/// </summary>
public sealed partial class GamePage : Page
{
    /// <summary>虚拟按键常态底色（#66000000 的 Color 形态）</summary>
    private static readonly Brush KeyIdleBrush = new SolidColorBrush(
        Windows.UI.ColorHelper.FromArgb(0x66, 0, 0, 0));

    private readonly bool _autoLaunch;
    private bool _autoLaunchDone;
    private int _touchCount;
    private object? _surface;                       // 最新 Surface（可能早于 Java 桥初始化，先缓存）
    private (int Width, int Height)? _lastSurfaceSize;
    private readonly Action<string> _logHandler;
    private readonly Action<int> _jvmExitedHandler;

    public GamePage(bool autoLaunch = false)
    {
        _autoLaunch = autoLaunch;
        InitializeComponent();
        GameSurface.SurfaceCreated += OnSurfaceCreated;
        GameSurface.SurfaceChanged += OnSurfaceChanged;
        GameSurface.SurfaceDestroyed += OnSurfaceDestroyed;
        GameSurface.TouchReceived += OnSurfaceTouch;
        _logHandler = line => Log(line);
        GameLauncherService.Instance.Log += _logHandler;
        _jvmExitedHandler = exitCode => UiThread.Run(() => OnJvmExited(exitCode));
        JvmHost.Exited += _jvmExitedHandler;
        AttachVirtualKeys();
        AttachJoystick();
        VirtualControlsToggle.IsOn = true;   // 虚拟键鼠默认开启（Toggled 处理器回写可见性）
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // ── 页面可见期 ──

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        GameInputHub.Enabled = true;
        // 全应用锁定 SensorLandscape（对齐平板版）：游戏覆盖页无需动态转屏

        // 首页 / 版本页「启动」进入：自动拉起一次启动链（失败信息走 OnLaunchClicked 的日志与状态）
        if (_autoLaunch && !_autoLaunchDone && !JvmHost.IsRunning)
        {
            _autoLaunchDone = true;
            OnLaunchClicked(this, new RoutedEventArgs());
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        GameInputHub.Enabled = false;
        GameLauncherService.Instance.Log -= _logHandler;
        JvmHost.Exited -= _jvmExitedHandler;
    }

    private async void OnLaunchClicked(object sender, RoutedEventArgs e)
    {
        Log("[启动] 开始");
        if (JvmHost.IsRunning)
        {
            Log("[启动] 游戏已在运行");
            return;
        }
        LaunchButton.IsEnabled = false;
        StatusLabel.Text = "启动中";
        var context = global::Android.App.Application.Context;
        try
        {
            // 前台服务必须先于启动链请求：StartForegroundService 后系统只给 5 秒窗口执行
            // StartForeground，若在组件解压 + JVM 拉起（CPU 打满）之后才请求，主线程排队
            // 超时即遭 ForegroundServiceDidNotStartInTimeException 杀进程（真机已复现）。
            // 此刻主线程空闲，握手确认前台化后再进入重活
            GameForegroundService.Start(context);
            await GameForegroundService.WaitForForegroundAsync(TimeSpan.FromSeconds(4));
            // 启动链整体放线程池：组件解压为 CPU 密集长任务，留在主线程会阻塞 Looper，
            // 排队的 RunnableImplementor 恰逢大 GC 会触发 Mono 已知 JNI 引用表错乱（SIGABRT）；
            // 线程池上无 SynchronizationContext，内部 await 全部不再回主线程
            await Task.Run(() => GameLauncherService.Instance.LaunchAsync());
            // Surface 可能早于 Java 桥就绪：启动完成后补注册并补报窗口尺寸
            // （SDL 后端跳过：窗口面归 SdlGameActivity 的 SDLSurface）
            if (!GameLauncherService.IsSdlMode)
            {
                if (_surface is not null && JavaBridge.IsInitialized)
                    JavaBridge.SetupBridgeWindow(_surface);
                if (_lastSurfaceSize is { } size)
                    JavaBridge.SendUpdateWindowSize(size.Width, size.Height);
            }
            Log("[启动] JVM 已拉起");
            StatusLabel.Text = "游戏运行中";
        }
        catch (Exception ex)
        {
            // 启动链失败：撤回已请求的前台服务，避免通知栏残留常驻通知
            GameForegroundService.Stop(context);
            // 完整异常（类型 + 栈）进 logcat：真机过滤 StarcloudS4 标签即可取证
            global::Android.Util.Log.Error("StarcloudS4", ex.ToString());
            Log($"[启动] 失败：{ex.Message}");
            Log($"[dirs] {GameLauncherService.Instance.PathDiagnostics}");
            StatusLabel.Text = "启动失败";
        }
        finally
        {
            LaunchButton.IsEnabled = true;
        }
    }

    private async void OnExitClicked(object sender, RoutedEventArgs e)
    {
        // 游戏运行中退出页面 = 结束游戏：确认防误触（对标 FCL 退出链）
        if (JvmHost.IsRunning &&
            !await UiDialog.ConfirmAsync(
                "退出游戏", "游戏仍在运行，返回将结束游戏进程。确定退出吗？", "退出"))
            return;
        AppNavigation.PopOverlay();
    }

    // ---- 虚拟键鼠层 ----

    // 虚拟键鼠开关：显示 / 隐藏四角按键层
    private void OnVirtualControlsToggled(object sender, RoutedEventArgs e) =>
        VirtualControls.Visibility = Ui.Vis(VirtualControlsToggle.IsOn);

    /// <summary>为功能虚拟按键挂按住 / 抬起手势（方向交给摇杆，见 AttachJoystick）</summary>
    private void AttachVirtualKeys()
    {
        AttachVirtualKey(KeyShift, AndroidKey.ShiftLeft, Glfw.KeyLeftShift);
        AttachVirtualKey(KeyJump, AndroidKey.Space, Glfw.KeySpace);
        AttachVirtualKey(KeyE, AndroidKey.KeycodeA + 'E' - 'A', Glfw.KeyA + 'E' - 'A');
        AttachVirtualKey(KeyT, AndroidKey.KeycodeA + 'T' - 'A', Glfw.KeyA + 'T' - 'A');
        AttachVirtualKey(KeyF5, AndroidKey.F1 + 4, Glfw.KeyF5);
        AttachVirtualKey(KeyEsc, AndroidKey.Escape, Glfw.KeyEscape);
    }

    private void AttachVirtualKey(FrameworkElement key, int androidKeycode, int glfwKeycode)
    {
        // PointerPressed 按下；Released / Canceled / Exited（手指滑出）都抬键，防止移动键卡住
        key.PointerPressed += (_, _) => SendVirtualKey(key, androidKeycode, glfwKeycode, isDown: true);
        key.PointerReleased += (_, _) => SendVirtualKey(key, androidKeycode, glfwKeycode, isDown: false);
        key.PointerCanceled += (_, _) => SendVirtualKey(key, androidKeycode, glfwKeycode, isDown: false);
        key.PointerExited += (_, _) => SendVirtualKey(key, androidKeycode, glfwKeycode, isDown: false);
    }

    private void SendVirtualKey(FrameworkElement key, int androidKeycode, int glfwKeycode, bool isDown)
    {
        if (!JavaBridge.IsInitialized)
            return;
        // 与 GameInputHub.HandleKey 同一投递模式：先更新 GLFW 修饰位，再投 Android 键码
        // （字符参数传 '\0'：动作键由 keycode 回调驱动，避免产生文本输入副作用）
        JavaBridge.SetModifiers(glfwKeycode, isDown);
        JavaBridge.SendKeycode(androidKeycode, '\0', 0, 0, isDown);
        key.Background = isDown ? Ui.Brush("Primary") : KeyIdleBrush;
    }

    // ---- 方向摇杆：推杆八方向映射 W / A / S / D ----

    // 摇杆几何：底座 150dp，推杆行程半径、死区、斜向分量阈值（按行程比例）
    private const double JoystickRadius = 46;
    private const double JoystickDeadZone = 10;
    private const double JoystickDiagonalRatio = 0.45;

    /// <summary>W / A / S / D 键位（与原十字键同一码表：字母 A=29 起，GLFW A=65 起）</summary>
    private static readonly (string Name, int Android, int Glfw)[] JoystickKeyMap =
    {
        ("W", AndroidKey.KeycodeA + 'W' - 'A', Glfw.KeyA + 'W' - 'A'),
        ("A", AndroidKey.KeycodeA + 'A' - 'A', Glfw.KeyA + 'A' - 'A'),
        ("S", AndroidKey.KeycodeA + 'S' - 'A', Glfw.KeyA + 'S' - 'A'),
        ("D", AndroidKey.KeycodeA + 'D' - 'A', Glfw.KeyA + 'D' - 'A'),
    };

    private readonly HashSet<string> _heldJoystickKeys = [];   // 当前按住的方向键（差量投递依据）
    private bool _joystickDragging;

    private void AttachJoystick()
    {
        JoystickBase.PointerPressed += OnJoystickPressed;
        JoystickBase.PointerMoved += OnJoystickMoved;
        JoystickBase.PointerReleased += OnJoystickReleased;
        JoystickBase.PointerCanceled += OnJoystickReleased;
    }

    private void OnJoystickPressed(object sender, PointerRoutedEventArgs e)
    {
        _joystickDragging = true;
        JoystickBase.CapturePointer(e.Pointer);   // 手指滑出底座仍继续跟踪
        UpdateJoystick(e);
    }

    private void OnJoystickMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_joystickDragging)
            UpdateJoystick(e);
    }

    private void OnJoystickReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_joystickDragging)
            return;
        _joystickDragging = false;
        JoystickBase.ReleasePointerCapture(e.Pointer);
        // 回中：推杆归位 + 全部方向键抬起
        JoystickKnobShift.X = 0;
        JoystickKnobShift.Y = 0;
        ApplyJoystickKeys([]);
    }

    private void UpdateJoystick(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(JoystickBase).Position;
        // 相对底座中心的偏移，限幅到行程半径（推杆永远贴边不飞出）
        var dx = p.X - JoystickBase.ActualWidth / 2;
        var dy = p.Y - JoystickBase.ActualHeight / 2;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len > JoystickRadius)
        {
            dx = dx / len * JoystickRadius;
            dy = dy / len * JoystickRadius;
        }
        JoystickKnobShift.X = dx;
        JoystickKnobShift.Y = dy;

        // 八方向判定：死区内松开；超出死区后按横/纵分量与行程比例判定，可同时按住相邻两键
        var held = new List<string>();
        if (len >= JoystickDeadZone)
        {
            var vertical = Math.Abs(dy);
            var horizontal = Math.Abs(dx);
            if (vertical >= JoystickDeadZone && vertical / JoystickRadius >= JoystickDiagonalRatio * 0.5)
                held.Add(dy < 0 ? "W" : "S");
            if (horizontal >= JoystickDeadZone && horizontal / JoystickRadius >= JoystickDiagonalRatio * 0.5)
                held.Add(dx < 0 ? "A" : "D");
        }
        ApplyJoystickKeys(held);
    }

    /// <summary>把目标方向键集合与当前按住集合做差量：只投递新增（按下）与移除（抬起）</summary>
    private void ApplyJoystickKeys(IReadOnlyCollection<string> target)
    {
        if (!JavaBridge.IsInitialized)
            return;
        foreach (var key in JoystickKeyMap)
        {
            var want = target.Contains(key.Name);
            var have = _heldJoystickKeys.Contains(key.Name);
            if (want == have)
                continue;
            JavaBridge.SetModifiers(key.Glfw, want);
            JavaBridge.SendKeycode(key.Android, '\0', 0, 0, want);
            _ = want ? _heldJoystickKeys.Add(key.Name) : _heldJoystickKeys.Remove(key.Name);
        }
    }

    // ---- Surface 与输入事件 ----

    private void OnSurfaceCreated(object? sender, object? surface)
    {
        _surface = surface;
        Log("[surface] 已创建");
        // SDL 后端（26.x）：窗口面由 SdlGameActivity 的 SDLSurface 供给，
        // 不经 GLFW 桥注册本页面 Surface
        if (GameLauncherService.IsSdlMode)
            return;
        if (surface is not null && JavaBridge.IsInitialized)
            JavaBridge.SetupBridgeWindow(surface);
    }

    private void OnSurfaceChanged(object? sender, (int Width, int Height) size)
    {
        _lastSurfaceSize = size;
        Log($"[surface] 尺寸 {size.Width}×{size.Height}");
        if (GameLauncherService.IsSdlMode)
            return;
        if (JavaBridge.IsInitialized)
            JavaBridge.SendUpdateWindowSize(size.Width, size.Height);
    }

    private void OnSurfaceDestroyed(object? sender, EventArgs e)
    {
        _surface = null;
        Log("[surface] 已销毁");
    }

    private void OnSurfaceTouch(object? sender, SurfaceTouchEventArgs e)
    {
        // 输入投递不依赖 UI 线程（JavaBridge 经 JNIEnv 自动附加）
        GameInputHub.HandleTouch(e);

        // 触摸计数日志保留（链路验证 + logcat 自动化）；移动事件每 100 条记 1 条
        var count = Interlocked.Increment(ref _touchCount);
        var shouldLog = e.Action switch
        {
            SurfaceTouchAction.Pressed => true,
            SurfaceTouchAction.Released => true,
            SurfaceTouchAction.Cancelled => true,
            SurfaceTouchAction.Moved => count % 100 == 0,
            _ => false,
        };
        if (shouldLog)
            Log($"[touch #{count}] {e.Action} @ ({e.X:F0}, {e.Y:F0})");
    }

    private void OnJvmExited(int exitCode)
    {
        // JVM 线程回调：切主线程更新 UI 并撤前台服务（设计稿 §6.6 退出链）
        Log($"[JVM] 已退出，退出码 {exitCode}");
        StatusLabel.Text = "游戏已退出";
        if (GameLauncherService.IsSdlMode)
        {
            // SDL 后端：放行 SdlGameActivity 的 SDLThread（main 阻塞在退出闩），
            // 由其 finish 游戏活动返回启动器；GLFW 链路无此活动
            SdlGameLauncher.NotifyExit();
        }
        GameForegroundService.Stop(global::Android.App.Application.Context);
    }

    private static void Log(string line)
    {
        // 日志只进 logcat（StarcloudS4 标签，adb 可过滤）；页面不再显示日志区
        global::Android.Util.Log.Info("StarcloudS4", line);
    }
}
