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
using Microsoft.UI.Xaml.Controls;
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

    private readonly Border _dragLayer = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };

    private readonly Border _grip = new()
    {
        Width = 20,
        Height = 20,
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xD3, 0x22)),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Child = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x5F, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private readonly Border _editFrame = new()
    {
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD3, 0x22)),
        CornerRadius = new CornerRadius(16),
        IsHitTestVisible = false,
    };

    public CardHost()
    {
        _dragLayer.Tag = "card-drag";
        _grip.Tag = "card-grip";
        _dragLayer.Child = new Grid { Children = { _grip } };

        Children.Add(_presenter);
        Children.Add(_editFrame);
        Children.Add(_dragLayer);
        SetEditing(false);
    }

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

    private readonly PointerEventHandler _pressedHandler;
    private readonly PointerEventHandler _movedHandler;
    private readonly PointerEventHandler _releasedHandler;
    private readonly PointerEventHandler _canceledHandler;

    private CardHost? _dragHost;
    private bool _resizing;
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

    /// <summary>编辑模式：卡片可拖动换格、右下角把手可缩放。</summary>
    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    /// <summary>某张卡被拖动或缩放后提交了新格位（页面据此落盘）。</summary>
    public event EventHandler? LayoutChanged;

    public IReadOnlyList<CardHost> Hosts() => Children.OfType<CardHost>().ToList();

    public CardHost? FindHost(string id) =>
        Children.OfType<CardHost>().FirstOrDefault(h => string.Equals(h.CardId, id, StringComparison.Ordinal));

    /// <summary>把配置套到已有的 CardHost 上（页面先按类型放好子元素，再调这里定位）。</summary>
    public void ApplyLayout(HomeLayoutConfig config)
    {
        Columns = config.Columns;
        RowHeight = config.RowHeight;
        var byId = config.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var host in Hosts())
        {
            if (!byId.TryGetValue(host.CardId, out var card)) continue;
            var spec = HomeCardCatalog.Resolve(host.CardType);
            var (spanW, spanH) = spec.Clamp(card.ColumnSpan, card.RowSpan);
            host.Column = Math.Clamp(card.Column, 0, Math.Max(0, Columns - spanW));
            host.Row = Math.Max(0, card.Row);
            host.ColumnSpan = spanW;
            host.RowSpan = spanH;
            host.Visibility = card.Visible ? Visibility.Visible : Visibility.Collapsed;
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
        if (!StretchRows || double.IsInfinity(availableHeight)) return RowHeight;
        var bottom = 0;
        foreach (var host in Children.OfType<CardHost>())
        {
            if (host.Visibility == Visibility.Collapsed) continue;
            bottom = Math.Max(bottom, host.Row + host.RowSpan);
        }
        if (bottom <= 0) return RowHeight;
        var height = (availableHeight - RowGap * (bottom - 1)) / bottom;
        return Math.Max(RowHeight, height);
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
        foreach (var host in panel.Hosts()) host.SetEditing(editing);
        if (editing) panel.AttachEditHandlers();
        else
        {
            panel.DetachEditHandlers();
            panel.CancelDrag();
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
        var host = HostFrom(e.OriginalSource);
        if (host is null) return;

        _dragHost = host;
        _resizing = IsInsideTagged(e.OriginalSource, "card-grip");
        _dragStart = e.GetCurrentPoint(this).Position;
        _originColumn = host.Column;
        _originRow = host.Row;
        _originColumnSpan = host.ColumnSpan;
        _originRowSpan = host.RowSpan;
        _previewColumn = host.Column;
        _previewRow = host.Row;
        _previewColumnSpan = host.ColumnSpan;
        _previewRowSpan = host.RowSpan;

        host.Opacity = 0.55;
        ShowPreview();
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
        var dx = point.Position.X - _dragStart.X;
        var dy = point.Position.Y - _dragStart.Y;

        var columns = Math.Max(1, Columns);
        if (_resizing)
        {
            var spec = HomeCardCatalog.Resolve(_dragHost.CardType);
            // 右边界最多到网格末尾；极端情况下先保证不小于最小跨度，避免 Math.Clamp 的 min > max
            var maxW = Math.Max(spec.MinColumnSpan, Math.Min(spec.MaxColumnSpan, columns - _originColumn));
            _previewColumnSpan = Math.Clamp(_originColumnSpan + (int)Math.Round(dx / stepX),
                spec.MinColumnSpan, maxW);
            _previewRowSpan = Math.Clamp(_originRowSpan + (int)Math.Round(dy / stepY),
                spec.MinRowSpan, spec.MaxRowSpan);
        }
        else
        {
            _previewColumn = Math.Clamp(_originColumn + (int)Math.Round(dx / stepX), 0,
                columns - Math.Max(1, _originColumnSpan));
            _previewRow = Math.Max(0, _originRow + (int)Math.Round(dy / stepY));
        }

        ArrangePreview(cellWidth, cellHeight);
        e.Handled = true;
    }

    private void OnGridPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragHost is null) return;
        var host = _dragHost;
        ReleasePointerCapture(e.Pointer);
        CancelDrag();

        // 与其它卡重叠就退回原位：网格里两张卡叠着没有意义，也不做自动推挤
        var candidate = new HomeCardLayout
        {
            Id = host.CardId,
            Column = _previewColumn,
            Row = _previewRow,
            ColumnSpan = _previewColumnSpan,
            RowSpan = _previewRowSpan,
        };
        var blocked = Hosts()
            .Where(h => !ReferenceEquals(h, host) && h.Visibility == Visibility.Visible)
            .Select(ToLayout)
            .Any(other => HomeGridMath.Overlaps(candidate, other));

        host.Opacity = 1;
        if (blocked)
        {
            e.Handled = true;
            return;
        }

        host.Column = candidate.Column;
        host.Row = candidate.Row;
        host.ColumnSpan = candidate.ColumnSpan;
        host.RowSpan = candidate.RowSpan;
        NotifyCardSize(host);
        InvalidateMeasure();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
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
        if (_dragHost is not null) _dragHost.Opacity = 1;
        _dragHost = null;
        _resizing = false;
        _preview.Visibility = Visibility.Collapsed;
    }

    private void ShowPreview()
    {
        // 预览框必须画在所有卡片之上：把它挪到子元素末尾再显示
        Children.Remove(_preview);
        Children.Add(_preview);
        _preview.Visibility = Visibility.Visible;
    }

    private void ArrangePreview(double cellWidth, double cellHeight)
    {
        if (_preview.Visibility != Visibility.Visible) return;
        var stepX = cellWidth + ColumnGap;
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
