// Nova 游戏内浮窗（设计：nova-overlay-design.md §3，方案 A：独立置顶透明窗口，不碰游戏进程）。
// 收起态 48×48 Orb：可拖动、点击展开、Toast 左侧浮现 10s 自收；
// 展开态 320×420：内嵌 NovaChatView，对话 / 确认清单 / 时刻候选全量复用。
// 细节约定：
//   - TOPMOST + WS_EX_TOOLWINDOW：置顶但不进任务栏 / Alt-Tab；
//   - Ctrl+N 全局热键：RegisterHotKey + comctl32 SetWindowSubclass 拦 WM_HOTKEY；
//   - 独占全屏自动隐藏：DXGI GetFullscreenState，2s 轮询，失败 = 不隐藏（宁可漏判不误判）；
//   - 位置记忆：拖动结束写回 Settings.NovaOverlayX/Y。

using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Graphics;
using Windows.UI;

namespace Starcloud.App.Views.Overlays;

public sealed partial class NovaOverlayWindow : Window
{
    private const int CollapsedSize = 48;          // 收起态（Orb 直径）
    private const int ToastW = 272;                // Toast 态窗口宽（216 toast + 8 + 48）
    private const int ToastH = 72;                 // Toast 态窗口高（容纳 3 行）
    private const int PanelW = 320;                // 展开态面板
    private const int PanelH = 420;
    private const int EdgeMargin = 16;             // 默认贴角边距
    private const double DragThreshold = 4.0;      // 位移阈值：区分点击与拖动

    private const int HotKeyId = 0x4E4F;           // 'NO'
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_N = 0x4E;
    private const uint WM_HOTKEY = 0x0312;

    private readonly AppServices _s;
    private readonly DispatcherQueue _dq;

    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _fsTimer;
    private readonly WndProcDelegate _wndProc;     // 防 GC：子类化回调必须保活

    private bool _expanded;
    private bool _hiddenByFullscreen;
    private bool _dragMoved;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private PointInt32 _pressWindowPos;
    private PointInt32 _orbPos;                    // 收起态 Orb 位置（展开前记忆）
    private PointInt32? _preToastPos;              // Toast 加宽前的窗口位置
    private bool _closed;

    public NovaOverlayWindow()
    {
        InitializeComponent();

        _s = AppServices.Instance;
        _dq = DispatcherQueue;

        // 窗口壳：无标题栏 / 无边框 / 置顶 / 不可调整，内容铺满
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false); // 去掉 48×48 内碍事的标题栏按钮
        }
        SystemBackdrop = new TransparentAcrylicBackdrop();

        // 初始尺寸 + 记忆位置（无记忆则右下贴角）
        AppWindow.Resize(new SizeInt32(CollapsedSize, CollapsedSize));
        _orbPos = RestoreOrbPosition();
        AppWindow.Move(_orbPos);

        // 不进任务栏 / Alt-Tab
        ApplyToolWindowStyle();

        // Ctrl+N 全局热键
        _wndProc = WndProcHandler;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        RegisterHotKey(hwnd, HotKeyId, MOD_CONTROL | MOD_NOREPEAT, VK_N);
        SetWindowSubclass(hwnd, _wndProc, 1u, IntPtr.Zero);

        // Toast 10s 自收
        _toastTimer = _dq.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(10);
        _toastTimer.Tick += (_, _) => HideToast();

        // 独占全屏轮询：真独占 → 隐藏；退出 → 恢复
        _fsTimer = _dq.CreateTimer();
        _fsTimer.Interval = TimeSpan.FromSeconds(2);
        _fsTimer.IsRepeating = true;
        _fsTimer.Tick += (_, _) => PollFullscreen();
        _fsTimer.Start();

        // 交互
        OrbBtn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Orb_Pressed), true);
        OrbBtn.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Orb_Moved), true);
        OrbBtn.Click += Orb_Click;
        CollapseBtn.Click += CollapseBtn_Click;

        // 五态着色跟随全局状态机
        _s.NovaRuntime.StateChanged += OnRuntimeStateChanged;
        ApplyNovaState(_s.NovaRuntime.State);

        Closed += (_, _) => Cleanup();
    }

    // ── 公共 API（NovaRuntime 单例驱动：T4 挂钩） ──

    /// <summary>显示浮窗（首次 Activate，之后免抢焦点）。</summary>
    public void ShowOverlay()
    {
        if (_closed) return;
        if (!AppWindow.IsVisible) Activate();
        AppWindow.Show();
    }

    /// <summary>隐藏浮窗（游戏退出等场景）。</summary>
    public void HideOverlay()
    {
        if (_closed) return;
        HideToast();
        if (_expanded) CollapseCore();
        AppWindow.Hide();
    }

    /// <summary>Toast 一句话提醒（10s 自收，新 Toast 顶掉旧的）。</summary>
    public void ShowToast(string text)
    {
        if (_closed) return;
        if (_expanded) return; // 展开态消息流里已可见，不需要额外提醒（克制）

        ToastText.Text = text;
        ToastHost.Visibility = Visibility.Visible;

        // 窗口加宽且右缘不动（Orb 恒贴原位，Toast 向左展开）
        if (_preToastPos is null)
        {
            _preToastPos = AppWindow.Position;
            AppWindow.Resize(new SizeInt32(ToastW, ToastH));
            AppWindow.Move(new PointInt32(
                _preToastPos.Value.X - (ToastW - CollapsedSize),
                _preToastPos.Value.Y - (ToastH - CollapsedSize) / 2));
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ── 展开 / 收起 ──

    private void Toggle()
    {
        if (_expanded) CollapseCore();
        else ExpandCore();
    }

    private void ExpandCore()
    {
        if (_expanded) return;
        HideToast();
        _orbPos = AppWindow.Position;
        CollapsedHost.Visibility = Visibility.Collapsed;
        PanelHost.Visibility = Visibility.Visible;
        _expanded = true;
        AppWindow.Resize(new SizeInt32(PanelW, PanelH));
        ClampIntoWorkArea(PanelW, PanelH);
        Activate(); // 展开即接受键盘焦点（对话输入）
    }

    private void CollapseCore()
    {
        if (!_expanded) return;
        PanelHost.Visibility = Visibility.Collapsed;
        CollapsedHost.Visibility = Visibility.Visible;
        _expanded = false;
        AppWindow.Resize(new SizeInt32(CollapsedSize, CollapsedSize));
        AppWindow.Move(_orbPos);
    }

    private void CollapseBtn_Click(object sender, RoutedEventArgs e) => CollapseCore();

    // ── Orb：点击展开，按住拖动（4px 阈值） ──

    private void Orb_Pressed(object sender, PointerRoutedEventArgs e)
    {
        _pressPoint = e.GetCurrentPoint(OrbBtn).Position;
        _pressWindowPos = AppWindow.Position;
        _dragMoved = false;
        _dragging = true;
        OrbBtn.CapturePointer(e.Pointer);
    }

    private void Orb_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetCurrentPoint(OrbBtn).Position;
        var dx = p.X - _pressPoint.X;
        var dy = p.Y - _pressPoint.Y;
        if (!_dragMoved && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;

        _dragMoved = true;
        var wa = WorkArea();
        var x = Math.Clamp(_pressWindowPos.X + (int)dx, wa.X, wa.X + wa.Width - CollapsedSize);
        var y = Math.Clamp(_pressWindowPos.Y + (int)dy, wa.Y, wa.Y + wa.Height - CollapsedSize);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void Orb_Click(object sender, RoutedEventArgs e)
    {
        if (_dragMoved) return; // 拖动结束落在 Orb 内也会触发 Click，不算点击
        Toggle();
    }

    // ── Toast 收起（10s 定时） ──

    private void HideToast()
    {
        ToastHost.Visibility = Visibility.Collapsed;
        _toastTimer.Stop();
        if (_preToastPos is not { } pos) return;
        _preToastPos = null;
        if (_expanded || _closed) return;
        AppWindow.Resize(new SizeInt32(CollapsedSize, CollapsedSize));
        AppWindow.Move(pos);
    }

    // ── 独占全屏轮询（宁可漏判不误判） ──

    private void PollFullscreen()
    {
        if (_closed) return;
        var fs = DxgiNative.IsAnyExclusiveFullscreen();
        if (fs && !_hiddenByFullscreen && AppWindow.IsVisible)
        {
            _hiddenByFullscreen = true;
            AppWindow.Hide(); // 不动 _expanded：恢复时保持原样
        }
        else if (!fs && _hiddenByFullscreen)
        {
            _hiddenByFullscreen = false;
            AppWindow.Show();
        }
    }

    // ── 五态着色（与 MainWindow 一致） ──

    private void OnRuntimeStateChanged(NovaState state) =>
        _dq.TryEnqueue(() => ApplyNovaState(state));

    private void ApplyNovaState(NovaState state)
    {
        var brush = StateBrush(state);
        // Nova 图标换成了品牌标记（多色图片），五态着色改画在外圈描边上
        OrbBtn.BorderBrush = brush;
        OrbDot.Visibility = state == NovaState.HasPlan ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(OrbBtn, $"Nova：{_s.NovaRuntime.StateText}");
    }

    private static Brush StateBrush(NovaState state) => state switch
    {
        NovaState.Thinking => Solid(0x7C, 0x3A, 0xED),   // 星云紫
        NovaState.HasPlan => Solid(0xF5, 0x9E, 0x0B),    // 琥珀黄（提案待确认）
        NovaState.Observing => Solid(0x3D, 0xD3, 0x22),  // 品牌深绿（观察中）
        NovaState.Offline => (Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
        _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b) =>
        new(Color.FromArgb(0xFF, r, g, b));

    // ── 位置记忆 ──

    private PointInt32 RestoreOrbPosition()
    {
        var st = _s.Settings;
        if (st.NovaOverlayX is int x && st.NovaOverlayY is int y)
        {
            // 记忆位置可能因换显示器失效，夹回工作区
            var wa = WorkArea();
            return new PointInt32(
                Math.Clamp(x, wa.X, Math.Max(wa.X, wa.X + wa.Width - CollapsedSize)),
                Math.Clamp(y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - CollapsedSize)));
        }
        var area = WorkArea();
        return new PointInt32(
            area.X + area.Width - CollapsedSize - EdgeMargin,
            area.Y + area.Height - CollapsedSize - EdgeMargin);
    }

    private void SavePosition()
    {
        _s.Settings.NovaOverlayX = AppWindow.Position.X;
        _s.Settings.NovaOverlayY = AppWindow.Position.Y;
        try { _s.Settings.Save(); } catch { /* 落盘失败不影响使用 */ }
    }

    private RectInt32 WorkArea() =>
        DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

    private void ClampIntoWorkArea(int w, int h)
    {
        var wa = WorkArea();
        var x = Math.Clamp(AppWindow.Position.X, wa.X, Math.Max(wa.X, wa.X + wa.Width - w));
        var y = Math.Clamp(AppWindow.Position.Y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - h));
        AppWindow.Move(new PointInt32(x, y));
    }

    // ── Win32：工具窗样式 + Ctrl+N 热键子类化 ──

    private void ApplyToolWindowStyle()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_HOTKEY && wParam.ToInt64() == HotKeyId)
        {
            _dq.TryEnqueue(Toggle); // 回调在 UI 线程消息循环，入队双保险
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void Cleanup()
    {
        if (_closed) return;
        _closed = true;
        _s.NovaRuntime.StateChanged -= OnRuntimeStateChanged;
        _toastTimer.Stop();
        _fsTimer.Stop();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            UnregisterHotKey(hwnd, HotKeyId);
            RemoveWindowSubclass(hwnd, _wndProc, 1u);
        }
        catch { /* 窗口句柄已失效则无需清理 */ }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, WndProcDelegate pfnSubclass,
        uint uIdSubclass, IntPtr dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, WndProcDelegate pfnSubclass,
        uint uIdSubclass);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
