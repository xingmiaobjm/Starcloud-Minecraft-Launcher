// 主页卡片网格宿主：子卡片按「列 / 行 / 跨几格」排布，编辑模式下整卡拖动换格、
// 右下角把手按格缩放。
//
// 为什么不用 Grid / VariableSizedWrapGrid：Grid 的行列是静态定义的，卡片数量与跨度
// 都可变时每改一次都要重建 RowDefinition；VariableSizedWrapGrid 只做「流式铺满」，
// 不支持真正的「一个格一个格地移动」。这里用一个自绘 Panel：格位写在 CardHost 上，
// Arrange 时换算成像素矩形；拖动/缩放只改格位，然后重排。
//
// 拖拽过程不移动卡片本身，而是画一个落点预览框（卡片降到 55% 不透明度）：
// 松手才提交，途中有明确的目标格反馈，也不会因为实时重排让整页抖动。

using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Localization;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Windows.Foundation;
using Windows.UI;

namespace Starcloud.App.Views.Controls;

/// <summary>网格里的一张卡：内容 + 编辑层（整卡拖动面 / 右下角缩放把手 / 虚线框）。</summary>
[ContentProperty(Name = nameof(CardContent))]
public sealed class CardHost : Grid
{
    private readonly ContentPresenter _presenter = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };

    // 拖动面：透明底但必须能命中。
    // Border 的命中测试要求背景 Alpha > 0 —— 纯透明（Alpha=0）的 Border 会被穿透，
    // 点击落到下面的内容上，拖动永远起不来。所以用 Alpha=1 的近全黑：视觉上完全看不见，
    // 但命中测试通过。
    private readonly Border _dragLayer = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
    };

    private readonly Button _sizeButton = new()
    {
        Width = 22,
        Height = 22,
        Padding = new Thickness(0),
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xD3, 0x22)),
        BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Content = new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Glyph = "\uE740",
            FontSize = 10,
        },
    };

    private readonly Border _editFrame = new()
    {
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD3, 0x22)),
        CornerRadius = new CornerRadius(16),
        IsHitTestVisible = false,
    };

    /// <summary>拖动时的合成变换：卡片本体跟着指针走，原位由网格画占位框。</summary>
    private readonly TranslateTransform _dragTransform = new();

    public CardHost()
    {
        _dragLayer.Tag = "card-drag";
        _sizeButton.Tag = "card-size";
        _sizeButton.Click += (_, _) => SizeRequested?.Invoke(this, EventArgs.Empty);
        _dragLayer.Child = new Grid { Children = { _sizeButton } };

        Children.Add(_presenter);
        Children.Add(_editFrame);
        Children.Add(_dragLayer);

        // 拖动变换挂在卡片自身上：位置变的是卡片本体，不是网格里的占位
        RenderTransform = _dragTransform;
        SetEditing(false);
    }

    /// <summary>拖动中：把卡片沿指针方向平移，并抬起一点（Z 轴位移 + 轻微放大感）。</summary>
    public void BeginDragVisual()
    {
        _dragTransform.X = 0;
        _dragTransform.Y = 0;
        Opacity = 0.92;
        // 抬起来的感觉：Z 轴推高一点（Grid 没有 Elevation，用 Translation 的 Z）
        Translation = new System.Numerics.Vector3(0, 0, 16);
        // 注意：这里不能设 IsHitTestVisible=false —— 卡片自己一旦退出命中链，
        // 指针移动事件就不再路由过来、也不再冒泡到网格，拖动会「按下去就没反应」。
        // 指针已被捕获，保持可命中不影响任何交互。
    }

    /// <summary>拖动中：按指针位移平移卡片本体。</summary>
    public void UpdateDragVisual(double dx, double dy)
    {
        _dragTransform.X = dx;
        _dragTransform.Y = dy;
    }

    /// <summary>结束拖动：清掉变换与抬起效果，卡片回到网格位置。</summary>
    public void EndDragVisual()
    {
        _dragTransform.X = 0;
        _dragTransform.Y = 0;
        Opacity = 1;
        Translation = new System.Numerics.Vector3(0, 0, 0);
    }

    /// <summary>点了右下角的尺寸按钮：页面据此弹出固定尺寸档位菜单。</summary>
    public event EventHandler? SizeRequested;

    /// <summary>编辑模式下右键菜单选了尺寸档位（宿主负责落位）。</summary>
    public event Action<CardHost, int, int>? SizeChosen;

    /// <summary>编辑模式下右键菜单选了「移除」。</summary>
    public event Action<CardHost>? RemoveChosen;

    /// <summary>稳定标识（与 HomeCardLayout.Id 对应）。</summary>
    public string CardId { get; set; } = "";

    /// <summary>卡片类型（与 HomeCardCatalog 的 Type 对应，决定最小/最大占格）。</summary>
    public string CardType { get; set; } = "";

    /// <summary>卡片内容（页面把自己的控件塞进来）。</summary>
    public object? CardContent
    {
        get => _presenter.Content;
        set => _presenter.Content = value;
    }

    public int Column { get; set; }
    public int Row { get; set; }
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;

    /// <summary>是否处于布局编辑模式：显示拖动面、缩放把手与虚线框，内容不再吃点击。</summary>
    public void SetEditing(bool editing)
    {
        _dragLayer.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _editFrame.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _presenter.IsHitTestVisible = !editing;
        // 编辑模式才挂右键菜单：平时卡片内容（版本列表等）自己用右键，
        // 编辑时内容不吃指针，右键落到拖动面上 —— 正好给卡片级菜单。
        _dragLayer.ContextFlyout = editing ? BuildEditMenu() : null;
    }

    /// <summary>
    /// 编辑模式的右键菜单：尺寸档位（当前项打勾）+ 移除。
    /// <para>为什么要有它：原来改尺寸只能「先点选卡片、再跑到右侧工具箱点档位」，
    /// 两步跨半个屏幕；右键是 Windows 桌面的既有惯例，就地改完更顺。</para>
    /// </summary>
    private MenuFlyout BuildEditMenu()
    {
        var menu = new MenuFlyout();
        var spec = HomeCardCatalog.Resolve(CardType);
        var sizes = new MenuFlyoutSubItem { Text = Loc.T("尺寸") };
        foreach (var size in spec.Presets)
        {
            var item = new MenuFlyoutItem
            {
                Text = size.Label,
                // 当前档位打勾（FlyoutItem 的勾选态用 Icon 表达最稳）
                Icon = size.ColumnSpan == ColumnSpan && size.RowSpan == RowSpan
                    ? new FontIcon { Glyph = "", FontSize = 12 }
                    : null,
            };
            var w = size.ColumnSpan;
            var h = size.RowSpan;
            item.Click += (_, _) => SizeChosen?.Invoke(this, w, h);
            sizes.Items.Add(item);
        }
        menu.Items.Add(sizes);
        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = Loc.T("移除这张卡片") };
        remove.Click += (_, _) => RemoveChosen?.Invoke(this);
        menu.Items.Add(remove);
        return menu;
    }
}

/// <summary>卡片网格：按格排布 CardHost，编辑模式下支持拖动与缩放。</summary>
public sealed class CardGridPanel : Panel
{
    private static readonly Color Accent = Color.FromArgb(0xFF, 0xFF, 0xD3, 0x22);

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CardGridPanel),
        new PropertyMetadata(HomeLayoutConfig.DefaultColumns, OnMetricsChanged));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(CardGridPanel),
        new PropertyMetadata(HomeLayoutConfig.DefaultRowHeight, OnMetricsChanged));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(CardGridPanel), new PropertyMetadata(16d, OnMetricsChanged));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(CardGridPanel), new PropertyMetadata(16d, OnMetricsChanged));

    /// <summary>固定行数：主页不滚动，所有卡片都必须落在这几行里。</summary>
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(int), typeof(CardGridPanel), new PropertyMetadata(6, OnMetricsChanged));

    /// <summary>true = 行高按可用高度均分（卡片撑满内容区）；false = 使用固定 RowHeight。</summary>
    public static readonly DependencyProperty StretchRowsProperty = DependencyProperty.Register(
        nameof(StretchRows), typeof(bool), typeof(CardGridPanel), new PropertyMetadata(false, OnMetricsChanged));

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(CardGridPanel), new PropertyMetadata(false, OnEditingChanged));

    private readonly Border _preview = new()
    {
        BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(16),
        BorderBrush = new SolidColorBrush(Accent),
        Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xD3, 0x22)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>原位占位框：卡片本体被拖走后，原格留一个虚线框表示「这里空出来了」。</summary>
    private readonly Border _vacancy = new()
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xD3, 0x22)),
        Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xD3, 0x22)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private readonly PointerEventHandler _pressedHandler;
    private readonly PointerEventHandler _movedHandler;
    private readonly PointerEventHandler _releasedHandler;
    private readonly PointerEventHandler _canceledHandler;

    private CardHost? _dragHost;
    private bool _handlersAttached;
    private Point _dragStart;
    private int _originColumn;
    private int _originRow;
    private int _originColumnSpan;
    private int _originRowSpan;
    private int _previewColumn;
    private int _previewRow;
    private int _previewColumnSpan;
    private int _previewRowSpan;

    public CardGridPanel()
    {
        _pressedHandler = OnGridPointerPressed;
        _movedHandler = OnGridPointerMoved;
        _releasedHandler = OnGridPointerReleased;
        _canceledHandler = OnGridPointerCanceled;
        Children.Add(_vacancy);
        Children.Add(_preview);
    }

    /// <summary>网格列数。</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>单行高度（DIP）。</summary>
    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    /// <summary>true = 行高按可用高度均分。</summary>
    public bool StretchRows
    {
        get => (bool)GetValue(StretchRowsProperty);
        set => SetValue(StretchRowsProperty, value);
    }

    /// <summary>固定行数（主页不滚动）。</summary>
    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>编辑模式：卡片可拖动换格、右下角把手可缩放。</summary>
    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    /// <summary>某张卡被拖动或缩放后提交了新格位（页面据此落盘）。</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>
    /// 通知外部：格位变了，需要落盘。
    /// <para>独立工具箱窗口改了主页里的卡片后要落盘，但事件不能从外部 invoke
    /// （C# 只允许在声明类型内触发），所以给外部一个显式入口。</para>
    /// </summary>
    public void NotifyLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<CardHost> Hosts() => Children.OfType<CardHost>().ToList();

    public CardHost? FindHost(string id) =>
        Children.OfType<CardHost>().FirstOrDefault(h => string.Equals(h.CardId, id, StringComparison.Ordinal));

    /// <summary>
    /// 把配置套到已有的 CardHost 上。逐张落位；与已落位的卡重叠时找第一个空位，
    /// 固定行数里放不下就先隐藏（用户缩小其它卡片后可以从「卡片」菜单加回来）。
    /// </summary>
    public void ApplyLayout(HomeLayoutConfig config)
    {
        Columns = config.Columns;
        RowHeight = config.RowHeight;
        var byId = config.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var placed = new List<HomeCardLayout>();

        foreach (var host in Hosts())
        {
            if (!byId.TryGetValue(host.CardId, out var card)) continue;
            var spec = HomeCardCatalog.Resolve(host.CardType);
            var (spanW, spanH) = spec.Clamp(card.ColumnSpan, card.RowSpan);
            spanH = Math.Clamp(spanH, 1, Math.Max(1, Rows));

            var candidate = new HomeCardLayout
            {
                Id = host.CardId,
                Column = Math.Clamp(card.Column, 0, Math.Max(0, Columns - spanW)),
                Row = Math.Clamp(card.Row, 0, Math.Max(0, Math.Max(1, Rows) - spanH)),
                ColumnSpan = spanW,
                RowSpan = spanH,
                Visible = card.Visible,
            };

            if (candidate.Visible && placed.Any(other => HomeGridMath.Overlaps(candidate, other)))
            {
                var (column, row) = HomeGridMath.FindSlot(placed, Columns, spanW, spanH, maxRows: Rows);
                if (column < 0)
                {
                    host.Visibility = Visibility.Collapsed;
                    continue;
                }
                candidate.Column = column;
                candidate.Row = row;
            }

            host.Column = candidate.Column;
            host.Row = candidate.Row;
            host.ColumnSpan = spanW;
            host.RowSpan = spanH;
            host.Visibility = candidate.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (candidate.Visible) placed.Add(candidate);
            NotifyCardSize(host);
        }

        InvalidateMeasure();
    }

    /// <summary>把当前格位导出成配置（保留传入模板里其它卡的类型等信息）。</summary>
    public HomeLayoutConfig CaptureLayout(HomeLayoutConfig? template = null)
    {
        var config = new HomeLayoutConfig
        {
            SchemaVersion = template?.SchemaVersion ?? 1,
            Columns = Columns,
            RowHeight = RowHeight,
            ToolboxOffsetX = template?.ToolboxOffsetX ?? 0,
            ToolboxOffsetY = template?.ToolboxOffsetY ?? 0,
        };
        var types = template?.Cards.ToDictionary(c => c.Id, c => c.Type, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var host in Hosts())
        {
            config.Cards.Add(new HomeCardLayout
            {
                Id = host.CardId,
                Type = types.TryGetValue(host.CardId, out var type) ? type : host.CardId,
                Column = host.Column,
                Row = host.Row,
                ColumnSpan = host.ColumnSpan,
                RowSpan = host.RowSpan,
                Visible = host.Visibility == Visibility.Visible,
            });
        }

        return config.Normalized();
    }

    // ── 排布 ──

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var (cellWidth, _) = GetCellMetrics(width);
        var cellHeight = GetCellHeight(availableSize.Height);

        double bottom = 0;
        foreach (var host in Children.OfType<CardHost>())
        {
            if (host.Visibility == Visibility.Collapsed) continue;
            var size = new Size(SpanWidth(cellWidth, host.ColumnSpan), SpanHeight(cellHeight, host.RowSpan));
            host.Measure(size);
            bottom = Math.Max(bottom, host.Row * (cellHeight + RowGap) + size.Height);
        }

        var height = double.IsInfinity(availableSize.Height) ? bottom : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (cellWidth, stepX) = GetCellMetrics(finalSize.Width);
        var cellHeight = GetCellHeight(finalSize.Height);

        foreach (var host in Children.OfType<CardHost>())
        {
            if (host.Visibility == Visibility.Collapsed) continue;
            host.Arrange(new Rect(
                host.Column * stepX,
                host.Row * (cellHeight + RowGap),
                SpanWidth(cellWidth, host.ColumnSpan),
                SpanHeight(cellHeight, host.RowSpan)));
        }

        ArrangePreview(cellWidth, cellHeight);
        return finalSize;
    }

    private (double CellWidth, double Step) GetCellMetrics(double width)
    {
        var columns = Math.Max(1, Columns);
        var step = Math.Max(1, (width - ColumnGap * (columns - 1)) / columns);
        return (step, step + ColumnGap);
    }

    private double GetCellHeight(double availableHeight)
    {
        var rows = Math.Max(1, Rows);
        if (!StretchRows || double.IsInfinity(availableHeight)) return RowHeight;
        var height = (availableHeight - RowGap * (rows - 1)) / rows;
        return Math.Max(48, height);
    }

    private double SpanWidth(double cellWidth, int span) =>
        cellWidth * Math.Max(1, span) + ColumnGap * (Math.Max(1, span) - 1);

    private double SpanHeight(double cellHeight, int span) =>
        cellHeight * Math.Max(1, span) + RowGap * (Math.Max(1, span) - 1);

    // ── 编辑模式 ──

    private static void OnEditingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CardGridPanel panel) return;
        var editing = (bool)e.NewValue;
        foreach (var host in panel.Hosts())
        {
            host.SetEditing(editing);
            // 先退订再订：SetEditing 每次都重建菜单，事件不能越挂越多
            host.SizeChosen -= panel.OnHostSizeChosen;
            host.RemoveChosen -= panel.OnHostRemoveChosen;
            if (editing)
            {
                host.SizeChosen += panel.OnHostSizeChosen;
                host.RemoveChosen += panel.OnHostRemoveChosen;
            }
        }
        if (editing) panel.AttachEditHandlers();
        else
        {
            panel.DetachEditHandlers();
            panel.CancelDrag();
            panel.ClearSelection();
        }
    }

    private static void OnMetricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardGridPanel panel) panel.InvalidateMeasure();
    }

    private void AttachEditHandlers()
    {
        if (_handlersAttached) return;
        _handlersAttached = true;
        AddHandler(PointerPressedEvent, _pressedHandler, handledEventsToo: true);
        AddHandler(PointerMovedEvent, _movedHandler, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, _releasedHandler, handledEventsToo: true);
        AddHandler(PointerCanceledEvent, _canceledHandler, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, _canceledHandler, handledEventsToo: true);
    }

    private void DetachEditHandlers()
    {
        if (!_handlersAttached) return;
        _handlersAttached = false;
        RemoveHandler(PointerPressedEvent, _pressedHandler);
        RemoveHandler(PointerMovedEvent, _movedHandler);
        RemoveHandler(PointerReleasedEvent, _releasedHandler);
        RemoveHandler(PointerCanceledEvent, _canceledHandler);
        RemoveHandler(PointerCaptureLostEvent, _canceledHandler);
    }

    /// <summary>
    /// 取当前光标的屏幕物理坐标。
    /// <para>
    /// 为什么不用 GetCurrentPoint：指针被捕获后它返回的是「按下那一刻」的位置，
    /// 不再随光标走 —— 拿它算位移永远是 0，卡片会一动不动。
    /// WinUI 3 的 PointerPoint 也没有 RawPosition，只能问 Win32 要。</para>
    /// </summary>
    private static Point GetCursorScreenPosition()
    {
        if (GetCursorPos(out var p)) return new Point(p.X, p.Y);
        return new Point(0, 0);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static CardHost? HostFrom(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null && node is not CardHost)
            node = VisualTreeHelper.GetParent(node);
        return node as CardHost;
    }

    private static bool IsInsideTagged(object? source, string tag)
    {
        var node = source as DependencyObject;
        while (node is not null && node is not CardHost)
        {
            if (node is FrameworkElement fe && Equals(fe.Tag, tag)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnGridPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEditing) return;
        // 尺寸按钮自己处理点击，不能当成拖动起点
        if (IsInsideTagged(e.OriginalSource, "card-size")) return;
        var host = HostFrom(e.OriginalSource);
        if (host is null) return;

        _dragHost = host;
        // 起点用屏幕物理坐标：指针捕获后 GetCurrentPoint 会锁死在按下那一刻的位置，
        // 只有 RawPosition 才随光标走。全程统一用物理坐标，格位换算时再折回 DIP。
        _dragStart = GetCursorScreenPosition();
        _originColumn = host.Column;
        _originRow = host.Row;
        _originColumnSpan = host.ColumnSpan;
        _originRowSpan = host.RowSpan;
        _previewColumn = host.Column;
        _previewRow = host.Row;
        _previewColumnSpan = host.ColumnSpan;
        _previewRowSpan = host.RowSpan;

        host.BeginDragVisual();
        ShowPreview();
        ShowVacancy(_originColumn, _originRow, _originColumnSpan, _originRowSpan);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnGridPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragHost is null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        var (cellWidth, stepX) = GetCellMetrics(ActualWidth);
        var cellHeight = GetCellHeight(ActualHeight);
        var stepY = cellHeight + RowGap;

        // 指针位置：必须用 RawPosition（屏幕物理坐标）自己换算。
        // 指针被捕获后，GetCurrentPoint 返回的是「按下那一刻」的位置，不再随光标走 ——
        // 拿它算位移永远是 0，卡片就一动不动。
        var raw = GetCursorScreenPosition();
        var dxPhysical = raw.X - _dragStart.X;
        var dyPhysical = raw.Y - _dragStart.Y;

        // 折回 DIP：格位换算与合成变换都用 DIP 单位
        var scale = XamlRoot?.RasterizationScale is > 0 ? XamlRoot.RasterizationScale : 1.0;
        var dx = dxPhysical / scale;
        var dy = dyPhysical / scale;

        // 卡片本体跟着指针走：合成变换只改绘制位置，不动网格里的格位。
        // 格位要等松手才定 —— 途中重排会让整页抖动。
        _dragHost.UpdateDragVisual(dx, dy);

        // 卡片尺寸固定，拖动只换位置（尺寸在右下角按钮里选档位）
        var columns = Math.Max(1, Columns);
        _previewColumn = Math.Clamp(_originColumn + (int)Math.Round(dx / stepX), 0,
            columns - Math.Max(1, _originColumnSpan));
        _previewRow = Math.Clamp(_originRow + (int)Math.Round(dy / stepY), 0,
            Math.Max(0, Math.Max(1, Rows) - Math.Max(1, _originRowSpan)));

        // 预览框按「真正会落下的占格」画：目标格放不下时腾位会换小档位，
        // 预览若还画原尺寸，用户松手后会看到卡片突然变小。
        if (_dragHost is not null)
        {
            var placement = HomeCardPlacer.Place(
                Hosts().Select(ToLayout).ToList(),
                Columns,
                Rows,
                HomeCardCatalog.Resolve(_dragHost.CardType),
                _previewColumn,
                _previewRow,
                _dragHost.CardId);
            if (placement.Success)
            {
                _previewColumn = placement.Column;
                _previewRow = placement.Row;
                _previewColumnSpan = placement.ColumnSpan;
                _previewRowSpan = placement.RowSpan;
            }
        }

        ArrangePreview(cellWidth, cellHeight);
        e.Handled = true;
    }

    private void OnGridPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragHost is null) return;
        var host = _dragHost;
        // 松手位置同样取 RawPosition：捕获期间 GetCurrentPoint 拿不到真实光标位置
        var position = GetCursorScreenPosition();
        var moved = (position.X - _dragStart.X) * (position.X - _dragStart.X)
            + (position.Y - _dragStart.Y) * (position.Y - _dragStart.Y) > 64;
        ReleasePointerCapture(e.Pointer);

        // 收尾拖动状态，但不急着清视觉 —— 卡片还要保持「跟着手」的样子落到新格位，
        // 先落位再清变换，中间才不会闪一下原位。
        _dragHost = null;
        _preview.Visibility = Visibility.Collapsed;
        _vacancy.Visibility = Visibility.Collapsed;

        // 没怎么动 = 一次点选：进入选中态，参数区（编辑浮条）随之显示这张卡的档位
        if (!moved)
        {
            host.EndDragVisual();
            SelectedHost = host;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // 与其它卡重叠就腾位：换小档位、再整网格找空位（与「加卡」同一套规则）。
        // 旧实现是重叠就退回原位 —— 默认布局占满网格时，用户会发现卡片「拖不动」。
        var spec = HomeCardCatalog.Resolve(host.CardType);
        var placement = HomeCardPlacer.Place(
            Hosts().Select(ToLayout).ToList(),
            Columns,
            Rows,
            spec,
            _previewColumn,
            _previewRow,
            host.CardId);

        if (placement.Success)
        {
            // 腾位时可能有别的卡片被缩小 —— 一并落实
            ApplyDisplaced(placement.Displaced);
            host.Column = placement.Column;
            host.Row = placement.Row;
            host.ColumnSpan = placement.ColumnSpan;
            host.RowSpan = placement.RowSpan;
            NotifyCardSize(host);
            InvalidateMeasure();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        host.EndDragVisual();
        e.Handled = true;
    }

    private void OnHostSizeChosen(CardHost host, int spanW, int spanH)
    {
        if (!IsEditing) return;
        TryApplyCardSize(host, spanW, spanH);
    }

    private void OnHostRemoveChosen(CardHost host)
    {
        if (!IsEditing) return;
        host.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(SelectedHost, host)) ClearSelection();
        InvalidateMeasure();
        NotifyLayoutChanged();
    }

    /// <summary>
    /// 把卡片改成指定占格并落位（重叠时自动腾位）。
    /// <para>工具箱参数区与卡片右键菜单都走这里 —— 落位规则只有一份。</para>
    /// </summary>
    /// <returns>落位结果；false = 主页空间不足，调用方负责提示。</returns>
    public bool TryApplyCardSize(CardHost host, int columnSpan, int rowSpan)
    {
        var spec = HomeCardCatalog.Resolve(host.CardType);
        var placement = HomeCardPlacer.Place(
            Hosts().Where(h => !ReferenceEquals(h, host)).Select(ToLayout).ToList(),
            Columns,
            Rows,
            spec,
            host.Column,
            host.Row,
            host.CardId,
            new CardSize(columnSpan, rowSpan));
        if (!placement.Success) return false;

        ApplyDisplaced(placement.Displaced);
        host.Column = placement.Column;
        host.Row = placement.Row;
        host.ColumnSpan = placement.ColumnSpan;
        host.RowSpan = placement.RowSpan;
        NotifyCardSize(host);
        InvalidateMeasure();
        NotifyLayoutChanged();
        return true;
    }

    /// <summary>编辑模式下当前选中的卡片（参数区随之显示它的档位）；null = 未选中。</summary>
    public CardHost? SelectedHost { get; private set; }

    /// <summary>选中卡片变化（含取消选中）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>清除选中（例如卡片被移除时）。</summary>
    public void ClearSelection()
    {
        if (SelectedHost is null) return;
        SelectedHost = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>程序化选中一张卡片（例如点卡片右下角的尺寸按钮）。</summary>
    public void RequestSelect(CardHost host)
    {
        SelectedHost = host;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从工具箱拖出一张卡片时的落点预览：<paramref name="position"/> 是相对网格的指针位置。
    /// <para>预览按「真正会落下的位置与占格」画：默认布局铺满网格时，
    /// 腾位规则会把新卡换小档位并挪到空位，预览必须提前反映出来，
    /// 否则用户松手后会看到卡片凭空换了位置与大小。</para>
    /// </summary>
    public void ShowDropPreview(string cardType, Point position)
    {
        var spec = HomeCardCatalog.Resolve(cardType);
        ShowPreview();

        var (cellWidth, stepX) = GetCellMetrics(ActualWidth);
        var cellHeight = GetCellHeight(ActualHeight);
        var columns = Math.Max(1, Columns);
        var rowLimit = Math.Max(1, Rows);
        var spanW = Math.Min(spec.DefaultColumnSpan, columns);
        var spanH = Math.Min(spec.DefaultRowSpan, rowLimit);

        var column = Math.Clamp((int)Math.Round(position.X / stepX), 0, columns - Math.Max(1, spanW));
        var row = Math.Clamp((int)Math.Round(position.Y / (cellHeight + RowGap)), 0,
            Math.Max(0, rowLimit - Math.Max(1, spanH)));

        var host = FindHost(cardType);
        var placement = HomeCardPlacer.Place(
            Hosts().Select(ToLayout).ToList(),
            Columns,
            Rows,
            spec,
            column,
            row,
            host?.CardId);

        if (placement.Success)
        {
            _previewColumn = placement.Column;
            _previewRow = placement.Row;
            _previewColumnSpan = placement.ColumnSpan;
            _previewRowSpan = placement.RowSpan;
        }
        else
        {
            _previewColumn = column;
            _previewRow = row;
            _previewColumnSpan = spanW;
            _previewRowSpan = spanH;
        }
        ArrangePreview(cellWidth, cellHeight);
    }

    /// <summary>
    /// 跨窗口拖拽入口：<paramref name="screenPoint"/> 是屏幕物理坐标。
    /// <para>
    /// 工具箱做成独立顶层窗口后，拖拽时的指针在另一个窗口里，
    /// 拿不到本窗口的相对坐标。这里用框架的坐标转换器把屏幕坐标换到
    /// 本窗口的本地坐标，再减去网格在窗口内的偏移，得到网格内相对 DIP。
    /// 走框架而不是自己除 RasterizationScale：跨窗口跨 DPI 时由框架兜底。</para>
    /// </summary>
    public void ShowDropPreviewAtScreenPoint(string cardType, Windows.Graphics.PointInt32 screenPoint)
    {
        var local = ToLocalDip(screenPoint);
        if (local is null) return;
        ShowDropPreview(cardType, local.Value);
    }

    /// <summary>屏幕物理坐标 → 网格内相对 DIP；换算不了时返回 null。</summary>
    private Point? ToLocalDip(Windows.Graphics.PointInt32 screenPoint)
    {
        try
        {
            var root = XamlRoot;
            if (root is null) return null;

            // 屏幕物理坐标 → 本窗口本地 DIP
            var windowLocal = root.CoordinateConverter.ConvertScreenToLocal(screenPoint);

            // 本窗口本地 DIP → 网格内相对 DIP（减去网格原点偏移）。
            // TransformToVisual(null) 变换到当前 XamlRoot 的根，即窗口内容根。
            var origin = TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            return new Point(windowLocal.X - origin.X, windowLocal.Y - origin.Y);
        }
        catch
        {
            // 转换器不可用（窗口正在关闭等）：本次不更新预览
            return null;
        }
    }

    /// <summary>
    /// 提交工具箱拖出的卡片：落在预览格。
    /// <para>放不下时不是直接失败：交给 <see cref="HomeCardPlacer"/> 腾位
    /// （先换小档位，再整网格找空位），这样默认布局占满网格时也能把新卡加进来。
    /// 完全放不下才返回 false，由页面弹提示。</para>
    /// </summary>
    public bool CommitDrop(string cardType)
    {
        if (_preview.Visibility != Visibility.Visible) return false;
        var host = FindHost(cardType);
        if (host is null) return false;

        var spec = HomeCardCatalog.Resolve(cardType);
        var placement = HomeCardPlacer.Place(
            Hosts().Select(ToLayout).ToList(),
            Columns,
            Rows,
            spec,
            _previewColumn,
            _previewRow,
            host.CardId);
        LauncherLog.Write("home",
            $"落卡 {cardType}：目标 ({_previewColumn},{_previewRow}) → " +
            (placement.Success
                ? $"({placement.Column},{placement.Row}) {placement.ColumnSpan}×{placement.RowSpan}"
                : "失败（无处可放）"));
        CancelDrag();
        if (!placement.Success) return false;

        // 腾位时可能有别的卡片被缩小 —— 一并落实，否则布局与显示会不一致
        ApplyDisplaced(placement.Displaced);

        host.Column = placement.Column;
        host.Row = placement.Row;
        host.ColumnSpan = placement.ColumnSpan;
        host.RowSpan = placement.RowSpan;
        host.Visibility = Visibility.Visible;
        NotifyCardSize(host);
        SelectedHost = host;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateMeasure();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>取消工具箱拖拽（指针移出网格等）。</summary>
    public void CancelDrop() => CancelDrag();

    /// <summary>
    /// 落实腾位时被挤开的卡片：改占格与位置、通知内容重排。
    /// <para>只动一张卡 —— 一次动太多张会让主页面目全非。</para>
    /// </summary>
    private void ApplyDisplaced(IReadOnlyList<HomeCardShift>? displaced)
    {
        if (displaced is null || displaced.Count == 0) return;
        foreach (var shift in displaced)
        {
            var victim = FindHost(shift.Id);
            if (victim is null) continue;
            victim.Column = shift.Column;
            victim.Row = shift.Row;
            victim.ColumnSpan = shift.ColumnSpan;
            victim.RowSpan = shift.RowSpan;
            NotifyCardSize(victim);
        }
    }

    /// <summary>把新的占格告诉卡片内容（实现 ICardSizeAware 的内容据此切换排版档位）。</summary>
    private static void NotifyCardSize(CardHost host)
    {
        if (host.CardContent is ICardSizeAware aware)
            aware.ApplyCardSize(host.ColumnSpan, host.RowSpan);
    }

    private void OnGridPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_dragHost is null) return;
        CancelDrag();
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (_dragHost is not null) _dragHost.EndDragVisual();
        _dragHost = null;
        _preview.Visibility = Visibility.Collapsed;
        _vacancy.Visibility = Visibility.Collapsed;
    }

    private void ShowPreview()
    {
        // 预览框必须画在所有卡片之上：把它挪到子元素末尾再显示
        Children.Remove(_preview);
        Children.Add(_preview);
        _preview.Visibility = Visibility.Visible;
    }

    /// <summary>在原格画一个占位框（卡片被拖走后原位留白）。</summary>
    private void ShowVacancy(int column, int row, int spanW, int spanH)
    {
        _vacancy.Visibility = Visibility.Visible;
        // 占位框在卡片之下：把它挪到子元素开头
        Children.Remove(_vacancy);
        Children.Insert(0, _vacancy);
    }

    private void ArrangePreview(double cellWidth, double cellHeight)
    {
        var stepX = cellWidth + ColumnGap;

        // 原位占位框：拖动中的卡片离开后，原格显示一个淡边框
        if (_vacancy.Visibility == Visibility.Visible)
        {
            var vw = SpanWidth(cellWidth, _originColumnSpan);
            var vh = SpanHeight(cellHeight, _originRowSpan);
            _vacancy.Width = vw;
            _vacancy.Height = vh;
            _vacancy.HorizontalAlignment = HorizontalAlignment.Left;
            _vacancy.VerticalAlignment = VerticalAlignment.Top;
            _vacancy.Measure(new Size(vw, vh));
            _vacancy.Arrange(new Rect(
                _originColumn * stepX,
                _originRow * (cellHeight + RowGap),
                vw,
                vh));
        }

        if (_preview.Visibility != Visibility.Visible) return;
        var width = SpanWidth(cellWidth, _previewColumnSpan);
        var height = SpanHeight(cellHeight, _previewRowSpan);
        _preview.Width = width;
        _preview.Height = height;
        _preview.HorizontalAlignment = HorizontalAlignment.Left;
        _preview.VerticalAlignment = VerticalAlignment.Top;
        _preview.Measure(new Size(width, height));
        _preview.Arrange(new Rect(
            _previewColumn * stepX,
            _previewRow * (cellHeight + RowGap),
            width,
            height));
    }

    private static HomeCardLayout ToLayout(CardHost host) => new()
    {
        Id = host.CardId,
        Column = host.Column,
        Row = host.Row,
        ColumnSpan = host.ColumnSpan,
        RowSpan = host.RowSpan,
        Visible = host.Visibility == Visibility.Visible,
    };
}
