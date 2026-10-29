// 主页布局编辑工具箱（独立顶层窗口）。
//
// 为什么是独立窗口：主页内容区本身占满窗口宽（MaxWidth=1280 DIP 居中，右侧只剩约
// 100 DIP 留白），任何浮层都会盖住账户卡 —— 之前做成页内浮层时实测账户卡的
// 「离线」分段被切掉一截。独立窗口可以拖到屏幕别处，主页一个像素都不动。
//
// 跨窗口拖拽怎么做的：不用 OLE 拖放，沿用主页内已有的「指针捕获 + Win32 GetCursorPos
// 取屏幕物理坐标 + 自己算格位」方案。指针在工具箱窗口里被捕获，主窗口收不到指针事件，
// 所以由本窗口主动跨窗口调用主页的网格：GetCursorPos 的结果是全局的，主窗口被本窗口
// 遮住也照样有效（这正是选它而非 GetCurrentPoint 的理由 —— 后者在指针捕获期间
// 会锁死在按下那一刻的位置，见 docs/designs/2026-10-28-home-card-grid-design.md §4.1）。
//
// 跨窗口调用必须经 DispatcherQueue：两个窗口各有各的 UI 线程，直接调会跨线程。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Controls;
using Starcloud.Core.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Starcloud.App.Views.Dialogs;

public sealed partial class ToolboxWindow : Window
{
    private const int DefaultW = 400;
    private const int DefaultH = 620;
    private const int MinW = 320;
    private const int MinH = 320;
    private const int EdgeMargin = 24;

    private readonly AppServices _s = AppServices.Instance;
    private readonly ToolboxPanel _panel = new();
    private readonly DispatcherQueue _dq;

    /// <summary>主窗口（落位操作的真正执行方）。</summary>
    private MainWindow? _main;

    /// <summary>正在拖拽的卡片类型。</summary>
    private string? _dragType;

    private bool _closed;

    public ToolboxWindow()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();

        Title = Loc.T("布局编辑工具箱");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Header);

        // 工具箱面板整体挂进来（条目区 + 参数区）。
        // 不拆子元素：XAML 元素只能有一个父级，拆走会抛 ArgumentException。
        PanelHost.Content = _panel;

        BuildHeader();
        BuildPalette();

        // 窗口装饰、位置、图标：都在首次激活之后再做。
        // <para>原因：窗口还没显示时 AppWindow 的部分属性不可用（SetIcon 会抛
        // ArgumentException: Value does not fall within the expected range），
        // 主窗口的 OnboardingWindow 也是延到激活后才摆位置。</para>
        // 语言切换：独立窗口不跟随主窗口重建，文案要自己刷
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Cleanup();
    }

    /// <summary>关掉窗口（由宿主调用，例如用户从主页切走了）。</summary>
    public void CloseToolbox()
    {
        try { Close(); } catch { /* 已经关了 */ }
    }

    // ── 抬头与条目 ──

    private void BuildHeader()
    {
        TitleText.Text = Loc.T("布局编辑工具箱");
    }

    private void BuildPalette()
    {
        _panel.BuildPalette();
        _panel.DragStarted += OnDragStarted;
        _panel.DragMoved += OnDragMoved;
        _panel.DragEnded += OnDragEnded;
        _panel.SizeRequested += OnSizeRequested;
        _panel.RemoveRequested += OnRemoveRequested;
    }

    // ── 跨窗口拖拽 ──

    private void OnDragStarted(string cardType)
    {
        _dragType = cardType;
    }

    private void OnDragMoved(string cardType, Windows.Graphics.PointInt32 screen)
    {
        if (_dragType is null) return;
        var grid = HomeGrid();
        if (grid is null) return;
        _main!.DispatcherQueue.TryEnqueue(() => grid.ShowDropPreviewAtScreenPoint(cardType, screen));
    }

    private void OnDragEnded(string cardType, Windows.Graphics.PointInt32 screen, bool released)
    {
        if (_dragType is null) return;
        _dragType = null;
        var grid = HomeGrid();
        if (grid is null) return;

        _main!.DispatcherQueue.TryEnqueue(() =>
        {
            if (!released)
            {
                grid.CancelDrop();
                return;
            }

            // 指针是否在主页网格内：跨窗口拿不到相对坐标，用屏幕坐标比网格的屏幕矩形
            if (!IsInsideGrid(screen))
            {
                grid.CancelDrop();
                return;
            }

            if (!grid.CommitDrop(cardType))
            {
                grid.CancelDrop();
                ShowToast(Loc.T("主页空间不足：先把其它卡片换成更小的尺寸，或移除不用的卡片"));
                return;
            }

            // 腾位规则可能把卡片挪到别处、或换成更小的档位 —— 说清楚，别让用户以为拖错了
            var spec = HomeCardCatalog.Resolve(cardType);
            var host = grid.FindHost(cardType);
            if (host is not null)
            {
                var (w, h) = spec.Clamp(host.ColumnSpan, host.RowSpan);
                if (w != spec.DefaultColumnSpan || h != spec.DefaultRowSpan)
                    ShowToast(Loc.T("主页空间有限，这张卡片已换成较小的尺寸"));
                else
                    ShowToast(Loc.T("已添加卡片"));
            }
            RefreshParams();
        });
    }

    /// <summary>屏幕坐标是否落在主页网格内（用网格的屏幕矩形判断）。</summary>
    private bool IsInsideGrid(Windows.Graphics.PointInt32 screen)
    {
        var grid = HomeGrid();
        if (grid is null) return false;
        try
        {
            var root = grid.XamlRoot;
            if (root is null) return false;
            // 网格左上角的屏幕坐标（物理像素）
            var originLocal = grid.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            var originScreen = root.CoordinateConverter.ConvertLocalToScreen(originLocal);
            var scale = root.RasterizationScale is > 0 ? root.RasterizationScale : 1.0;
            var width = grid.ActualWidth * scale;
            var height = grid.ActualHeight * scale;
            return screen.X >= originScreen.X && screen.Y >= originScreen.Y
                && screen.X <= originScreen.X + width && screen.Y <= originScreen.Y + height;
        }
        catch
        {
            return false;
        }
    }

    // ── 参数区 ──

    private void OnSizeRequested(string cardId, int spanW, int spanH)
    {
        var grid = HomeGrid();
        if (grid is null) return;
        _main!.DispatcherQueue.TryEnqueue(() =>
        {
            var host = grid.FindHost(cardId);
            if (host is null) return;
            var spec = HomeCardCatalog.Resolve(host.CardType);
            var placement = HomeCardPlacer.Place(
                grid.Hosts().Select(h => new HomeCardLayout
                {
                    Id = h.CardId,
                    Type = h.CardType,
                    Column = h.Column,
                    Row = h.Row,
                    ColumnSpan = h.ColumnSpan,
                    RowSpan = h.RowSpan,
                    Visible = h.Visibility == Visibility.Visible,
                }).ToList(),
                grid.Columns, grid.Rows, spec, host.Column, host.Row, host.CardId);

            if (!placement.Success)
            {
                ShowToast(Loc.T("主页空间不足：先把其它卡片换成更小的尺寸，或移除不用的卡片"));
                return;
            }

            if (placement.Displaced is { Count: > 0 })
            {
                foreach (var shift in placement.Displaced)
                {
                    var victim = grid.FindHost(shift.Id);
                    if (victim is null) continue;
                    victim.Column = shift.Column;
                    victim.Row = shift.Row;
                    victim.ColumnSpan = shift.ColumnSpan;
                    victim.RowSpan = shift.RowSpan;
                }
            }

            host.Column = placement.Column;
            host.Row = placement.Row;
            host.ColumnSpan = placement.ColumnSpan;
            host.RowSpan = placement.RowSpan;
            (host.CardContent as ICardSizeAware)?.ApplyCardSize(placement.ColumnSpan, placement.RowSpan);
            grid.InvalidateMeasure();
            grid.NotifyLayoutChanged();
            RefreshParams();
        });
    }

    private void OnRemoveRequested(string cardId)
    {
        var grid = HomeGrid();
        if (grid is null) return;
        _main!.DispatcherQueue.TryEnqueue(() =>
        {
            var host = grid.FindHost(cardId);
            if (host is null) return;
            host.Visibility = Visibility.Collapsed;
            grid.ClearSelection();
            grid.InvalidateMeasure();
            grid.NotifyLayoutChanged();
            RefreshParams();
        });
    }

    /// <summary>刷新参数区：显示主页当前选中卡片的信息。</summary>
    public void RefreshParams()
    {
        var grid = HomeGrid();
        if (grid is null) return;
        _main?.DispatcherQueue.TryEnqueue(() =>
        {
            var host = grid.SelectedHost;
            if (host is null)
            {
                _panel.ShowHint();
                return;
            }
            _panel.ShowCardParams(host.CardId, host.CardType, host.ColumnSpan, host.RowSpan);
        });
    }

    // ── 与主页的连接 ──

    /// <summary>绑定主窗口并进入主页编辑模式。</summary>
    public void Attach(MainWindow main)
    {
        _main = main;
        _main.DispatcherQueue.TryEnqueue(() =>
        {
            if (_main?.CurrentPage is Views.HomePage page) page.EnterLayoutEditing();
        });
    }

    private CardGridPanel? HomeGrid() =>
        _main?.CurrentPage is Views.HomePage page ? page.Grid : null;

    // ── 窗口位置与主题 ──

    /// <summary>
    /// 首次显示后的收尾：配窗口装饰、摆位置、设图标、套主题。
    /// <para>由调用方在 <c>Activate()</c> 之后调 —— 窗口构造期间 AppWindow 的部分属性
    /// 还不可用（SetIcon 会抛 ArgumentException），而 Activated 事件在这条链路上
    /// 不保证触发（实测没触发），所以不靠事件。</para>
    /// </summary>
    public void FinishSetup()
    {
        // 必须回到本窗口的 UI 线程：独立窗口各有各的 DispatcherQueue，
        // 主页线程直接动 AppWindow 会被系统拒绝（实测位置设了没反应，窗口停在系统默认的居中位置）。
        _dq.TryEnqueue(() =>
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.SetBorderAndTitleBar(true, false);
                    // 置顶：工具箱是编辑主页时一直要用的浮窗，被主窗口盖住就得反复找
                    presenter.IsAlwaysOnTop = true;
                    presenter.IsResizable = true;
                    presenter.PreferredMinimumWidth = MinW;
                    presenter.PreferredMinimumHeight = MinH;
                }
            }
            catch { /* 窗口装饰配不了也要能用 */ }

            // 摆位置要等窗口真正映射到屏幕上：刚 Activate 时设位置会被系统忽略。
            // 延到下一帧再设。
            _dq.TryEnqueue(() => RestoreOrPlace());

            try
            {
                var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(icon)) AppWindow.SetIcon(icon);
            }
            catch { /* 图标设不了不影响使用 */ }

            ApplyTheme();
            ApplyAcrylic();
            ApplyWallpaper();
        });
    }

    /// <summary>壁纸三层（与主窗口同一套 WindowBackgroundHost）。
    /// <para>没有这层时 GlassCardBrush（应用内亚克力）取样不到内容，
    /// 卡片会透出桌面原色；有这层后工具箱与主窗口看到的是同一张壁纸、
    /// 同一道毛玻璃 —— 这才是「同一种设计语言」。</para></summary>
    private void ApplyWallpaper()
    {
        try
        {
            _background = new Views.Shared.WindowBackgroundHost(
                BackgroundLayer, BackgroundImage, BackgroundBlurHost, BackgroundMask);
            _background.Apply(_s.Settings, Root.ActualTheme);
            Root.ActualThemeChanged += (_, _) => _background?.Apply(_s.Settings, Root.ActualTheme);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("ui", "工具箱壁纸背景初始化失败", ex);
        }
    }

    private Views.Shared.WindowBackgroundHost? _background;

    /// <summary>亚克力毛玻璃背景（与主窗口同一套 AcrylicBackdropHost）。
    /// <para>必须在窗口激活后挂：构造期挂会触发框架回调抛 stowed exception 直接杀进程
    /// （主窗口 ApplyBackdropOnce 的注释里有完整事故记录）。挂上后内容根透明，
    /// 桌面透过毛玻璃模糊进来 —— 与启动器同一种设计语言。</para></summary>
    private void ApplyAcrylic()
    {
        try
        {
            _acrylic = new Services.AcrylicBackdropHost(this, Root)
            {
                // 浮窗没有壁纸层兜底：着色不透明度封底 0.5（透明度不超过 50%），
                // 否则用户把毛玻璃强度调到最低时窗口几乎全透、内容看不清。
                MinimumTintOpacity = 0.5,
            };
            var settings = _s.Settings;
            _acrylic.SetGlassStrength(settings.BackgroundFollowTheme || settings.BackgroundMaskOpacity is null,
                                      settings.BackgroundMaskOpacity);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("ui", "工具箱毛玻璃背景初始化失败，保持无背景", ex);
        }
    }

    private Services.AcrylicBackdropHost? _acrylic;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    /// <summary>恢复上次的位置；没有就贴在主窗口右侧外部。</summary>
    private void RestoreOrPlace()
    {
        try
        {
            // 固定尺寸：DefaultW × DefaultH（屏幕不够时按工作区收缩）
            var area0 = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var w = Math.Min(DefaultW, Math.Max(MinW, area0.Width - EdgeMargin * 2));
            var h = Math.Min(DefaultH, Math.Max(MinH, area0.Height - EdgeMargin * 2));

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int x, y;
            var saved = _s.Settings;
            if (saved.ToolboxWindowX is { } sx && saved.ToolboxWindowY is { } sy)
            {
                x = sx;
                y = sy;
            }
            else if (_main?.AppWindow is { } mainApp)
            {
                // 默认贴在主窗口右侧外面（用户一眼能看见它在哪儿，也能直接拖走）。
                // 右边放不下（主窗口最大化 / 占了整个屏幕）就贴主窗口左上角内侧 ——
                // 反正工具箱是浮层，用户自己拖到不碍事的地方。
                var right = mainApp.Position.X + mainApp.Size.Width + 8;
                if (right + w <= area.X + area.Width)
                {
                    x = right;
                    y = mainApp.Position.Y + 48;
                }
                else
                {
                    x = mainApp.Position.X + 16;
                    y = mainApp.Position.Y + 16;
                }
            }
            else
            {
                x = area.X + area.Width - w - EdgeMargin;
                y = area.Y + EdgeMargin;
            }

            // 钳进工作区：分辨率变了或显示器拔了，位置可能跑到屏幕外
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - w));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - h));
            // 用 Win32 SetWindowPos 而不是 AppWindow.Move：
            // 窗口刚 Activate 后 AppWindow.Move 常常不生效（实测窗口仍停在系统默认的居中位置），
            // SetWindowPos 是直接对 HWND 设位置，立刻生效。
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("home", "工具箱定位失败，用系统默认位置", ex);
        }
    }

    /// <summary>记住窗口位置，下次打开还在这儿。</summary>
    private void SavePosition()
    {
        try
        {
            _s.Settings.ToolboxWindowX = AppWindow.Position.X;
            _s.Settings.ToolboxWindowY = AppWindow.Position.Y;
            _s.Settings.Save();
        }
        catch { /* 落盘失败不影响使用 */ }
    }

    /// <summary>主题：独立窗口不继承主窗口的 RequestedTheme，要单独套一次。</summary>
    private void ApplyTheme()
    {
        try
        {
            _s.Appearance.ApplyTheme(_s.Settings.Theme, Root);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("ui", "工具箱套主题失败", ex);
        }
    }

    private void OnLanguageChanged()
    {
        try
        {
            _dq.TryEnqueue(() =>
            {
                Title = Loc.T("布局编辑工具箱");
                BuildHeader();
                _panel.BuildPalette();
                RefreshParams();
            });
        }
        catch { /* 语言切换失败不影响使用 */ }
    }

    private void ShowToast(string message)
    {
        // 提示走主页（那里有现成的 InfoBar），工具箱自己不弹窗
        _main?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_main?.CurrentPage is Views.HomePage page) page.ShowLayoutToast(message);
        });
    }

    private void Cleanup()
    {
        if (_closed) return;
        _closed = true;
        Loc.LanguageChanged -= OnLanguageChanged;
        SavePosition();
        // 释放静态强引用：窗口关了还留着，下次点「布局」会拿到一个已死的实例
        if (ReferenceEquals(App.Toolbox, this)) App.Toolbox = null;
        // 退出主页编辑模式（工具箱关了 = 编辑态结束）
        _main?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_main?.CurrentPage is Views.HomePage page) page.ExitLayoutEditing();
        });
    }
}
