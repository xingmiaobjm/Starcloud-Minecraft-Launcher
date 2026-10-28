// 主页布局编辑工具箱的面板部分：卡片条目（缩略图 + 名称）与参数区（尺寸档位 + 移除）。
//
// 为什么要抽出来：工具箱有两种形态 —— 独立顶层窗口（常态）与页内浮层（独立窗口起不来时的降级路径）。
// 两者要显示同样的卡片条目、同样的尺寸档位，条目还要能拖到主页网格上落位；
// 逻辑写两份必然走样。这里把「长什么样」和「条目怎么拖」抽到控件里，
// 只把「拖到哪儿、落位怎么算」用委托交给宿主（页内版与独立窗口版的落位路径完全不同：
// 前者在同窗口内直接调 HomeGrid，后者要跨窗口经 DispatcherQueue 转发）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Controls;

/// <summary>工具箱面板：卡片条目列表 + 选中卡片的参数区。</summary>
public sealed class ToolboxPanel : UserControl
{
    /// <summary>条目被按下（开始拖拽）。</summary>
    public event Action<string>? DragStarted;

    /// <summary>拖拽中：参数是屏幕物理坐标（Win32 GetCursorPos 的原样输出）。</summary>
    public event Action<string, Windows.Graphics.PointInt32>? DragMoved;

    /// <summary>拖拽结束：参数是屏幕物理坐标与「是否松手」。</summary>
    public event Action<string, Windows.Graphics.PointInt32, bool>? DragEnded;

    /// <summary>尺寸档位被点击：卡片 id 与目标占格。</summary>
    public event Action<string, int, int>? SizeRequested;

    /// <summary>「移除」被点击：卡片 id。</summary>
    public event Action<string>? RemoveRequested;

    private readonly Grid _palette;
    private readonly StackPanel _params;
    private string? _dragType;

    public ToolboxPanel()
    {
        // 条目容器：每行两列的 Grid。
        // 为什么不用 ItemsControl + ItemsWrapGrid：ItemsWrapGrid 的面板类型只能在
        // XAML 里指定，代码里构造不出来（ItemsPanelTemplate.Factory 是内部实现），
        // 而这个控件整体是代码搭的。手画 Grid 更可控：每行两个，超出的换下一行。
        _palette = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _palette.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _palette.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _params = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };

        var grid = new Grid
        {
            RowSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 行序：0 条目区（占满余下高度）/ 1 分隔线 / 2 参数区
        // 注意：Grid 的子元素不设 Grid.Row 就全落在 (0,0)，这里必须逐个指定。
        // 这一层不放标题文字：独立窗口的标题栏已经写清楚了，代码里取主题画刷
        // （Application.Current.Resources[...]）在独立窗口里会解析到错误的主题，
        // 文字颜色会不对 —— 标题交给 XAML 声明更稳。
        var divider = new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
        Grid.SetRow(divider, 1);
        Grid.SetRow(_params, 2);

        // 条目区套滚动：条目多的时候（窄窗口 / 小缩略图）超出高度要能滚，
        // 否则最后一条会被下面的参数区盖住。
        // 条目区高度按内容走（Auto 行）：条目多时整个面板变高，
        // 由宿主（窗口）套滚动条来滚。
        // <para>为什么不在这里套 ScrollViewer：Grid 的 * 行测量时给的是「无限高度」，
        // ScrollViewer 拿到无限视口就永远不会出滚动条（实测条目被参数区盖住）。
        // 把滚动交给外层，滚动条才会真的出现。</para>
        Grid.SetRow(_palette, 0);
        Grid.SetRow(divider, 1);
        Grid.SetRow(_params, 2);

        grid.Children.Add(_palette);
        grid.Children.Add(divider);
        grid.Children.Add(_params);

        Content = grid;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        BuildPalette();
        ShowHint();
    }

    /// <summary>条目所在容器（宿主可替换布局或加滚动条）。</summary>
    public Grid PaletteHost => _palette;

    /// <summary>参数区所在容器（宿主可替换成横排布局）。</summary>
    public Panel ParamsHost => _params;

    /// <summary>
    /// 构建卡片条目：每类一张，带缩略示意图与说明。
    /// <para>条目做成「缩略图 + 名称 + 一句说明」：只看名字分不清卡片长什么样，
    /// 缩略图标出这张卡的内部结构（标题条 / 列表行 / 头像 / 主按钮）。</para>
    /// </summary>
    public void BuildPalette()
    {
        _palette.Children.Clear();
        _palette.RowDefinitions.Clear();
        var index = 0;
        foreach (var spec in HomeCardCatalog.All)
        {
            var thumbnail = new CardThumbnail { CardType = spec.Type };
            var content = new StackPanel
            {
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            content.Children.Add(thumbnail);
            content.Children.Add(new TextBlock
            {
                Text = spec.DisplayName,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160,
            });

            var chip = new Button
            {
                Content = content,
                Tag = spec.Type,
                Padding = new Thickness(4, 4, 4, 4),
                MinWidth = 0,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // 内容是「缩略图 + 名称」的多层元素，读屏会读成一长串；
            // 给一个干净的名字，说明挂在帮助文本里。
            AutomationProperties.SetName(chip, spec.DisplayName);
            AutomationProperties.SetHelpText(chip, spec.Summary);
            ToolTipService.SetToolTip(chip, spec.Summary);
            // Button 基类会把按下 / 抬起标记为已处理，必须用 handledEventsToo 才能收得到
            chip.AddHandler(PointerPressedEvent, new PointerEventHandler(Chip_PointerPressed), handledEventsToo: true);
            chip.AddHandler(PointerMovedEvent, new PointerEventHandler(Chip_PointerMoved), handledEventsToo: true);
            chip.AddHandler(PointerReleasedEvent, new PointerEventHandler(Chip_PointerReleased), handledEventsToo: true);
            chip.AddHandler(PointerCanceledEvent, new PointerEventHandler(Chip_PointerCanceled), handledEventsToo: true);
            // 每行两个：列 = 序号 % 2，行 = 序号 / 2
            var row = index / 2;
            while (_palette.RowDefinitions.Count <= row)
                _palette.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(chip, index % 2);
            Grid.SetRow(chip, row);
            _palette.Children.Add(chip);
            index++;
        }
    }

    private void Chip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string type } chip) return;
        _dragType = type;
        chip.CapturePointer(e.Pointer);
        DragStarted?.Invoke(type);
        e.Handled = true;
    }

    private void Chip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragType is null) return;
        DragMoved?.Invoke(_dragType, CursorPosition.Get());
        e.Handled = true;
    }

    private void Chip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragType is null) return;
        var type = _dragType;
        _dragType = null;
        if (sender is FrameworkElement chip) chip.ReleasePointerCapture(e.Pointer);
        DragEnded?.Invoke(type, CursorPosition.Get(), true);
        e.Handled = true;
    }

    private void Chip_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_dragType is null) return;
        var type = _dragType;
        _dragType = null;
        DragEnded?.Invoke(type, CursorPosition.Get(), false);
    }

    /// <summary>
    /// 把参数区的内容搬出去（宿主要把参数区放到窗口底栏的固定位置时用）。
    /// <para>只能搬子元素、不能搬容器本身：一个 XAML 元素只能有一个父级，
    /// 把容器塞进第二个父级会抛 ArgumentException。</para>
    /// </summary>
    public void DetachParamsTo(Panel target)
    {
        target.Children.Clear();
        foreach (var child in _params.Children)
        {
            _params.Children.Remove(child);
            target.Children.Add(child);
        }
    }

    /// <summary>参数区未选中卡片时的提示。</summary>
    public void ShowHint()
    {
        _params.Children.Clear();
        _params.Children.Add(new TextBlock
        {
            Text = Loc.T("点选卡片后在这里改尺寸"),
            FontSize = 12,
            Opacity = 0.65,
        });
    }

    /// <summary>
    /// 参数区：显示选中卡片的固定尺寸档位 + 移除按钮。
    /// <para>档位从卡片类型规格来；点击后由宿主决定怎么落位（页内版直接改，独立窗口版跨窗口转发）。</para>
    /// </summary>
    public void ShowCardParams(string cardId, string cardType, int currentSpanW, int currentSpanH)
    {
        _params.Children.Clear();
        var spec = HomeCardCatalog.Resolve(cardType);

        _params.Children.Add(new TextBlock
        {
            Text = $"{spec.DisplayName} · {Loc.T("尺寸")}",
            FontSize = 12,
            Opacity = 0.65,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var size in spec.Presets)
        {
            var isCurrent = currentSpanW == size.ColumnSpan && currentSpanH == size.RowSpan;
            var button = new Button
            {
                Content = size.Label,
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                MinWidth = 0,
            };
            if (isCurrent && Application.Current.Resources.TryGetValue("ScAccentButtonStyle", out var style))
                button.Style = (Style)style;
            var w = size.ColumnSpan;
            var h = size.RowSpan;
            button.Click += (_, _) => SizeRequested?.Invoke(cardId, w, h);
            row.Children.Add(button);
        }
        _params.Children.Add(row);

        var remove = new Button
        {
            Content = Loc.T("移除"),
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            MinWidth = 0,
        };
        remove.Click += (_, _) => RemoveRequested?.Invoke(cardId);
        _params.Children.Add(remove);
    }
}

/// <summary>当前光标的屏幕物理坐标。</summary>
public static class CursorPosition
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>取当前光标位置（物理像素）。取不到时返回 (0,0)。</summary>
    public static Windows.Graphics.PointInt32 Get()
    {
        if (GetCursorPos(out var p)) return new Windows.Graphics.PointInt32(p.X, p.Y);
        return new Windows.Graphics.PointInt32(0, 0);
    }
}
