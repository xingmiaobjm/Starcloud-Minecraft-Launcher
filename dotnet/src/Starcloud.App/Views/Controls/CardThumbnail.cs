// 卡片缩略图：工具箱里每类卡片画一张示意图，让人一眼看出这张卡片长什么样。
//
// 为什么不用截图或真实控件渲染：真实卡片内容依赖运行时数据（版本列表、账户），
// 在工具箱里渲染会把整份主页数据都拉起来；而这里只需要示意「这张卡有哪几块内容」，
// 用简笔几何图形画出来更快、更轻，也更好认。
//
// 画法：4×4 的示意网格，按卡片类型摆出内部结构 ——
// 标题条 / 列表行 / 圆形头像 / 主按钮药丸，与真实卡片里的元素一一对应。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Starcloud.Core.Services;
using Windows.UI;

namespace Starcloud.App.Views.Controls;

/// <summary>一张卡片的缩略示意图：按卡片类型画出内部的大致结构。</summary>
public sealed class CardThumbnail : Grid
{
    // Color 是结构体，不能 const（CS0283）——用 static readonly
    private static readonly Color Line = Color.FromArgb(0xFF, 0xDD, 0xE0, 0xE5);
    private static readonly Color GlassEdge = Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
    private static readonly Color Fill = Color.FromArgb(0xFF, 0xEE, 0xF0, 0xF3);
    private static readonly Color Accent = Color.FromArgb(0xFF, 0xFF, 0xD3, 0x22);
    private static readonly Color Avatar = Color.FromArgb(0xFF, 0xC5, 0xD3, 0xEF);

    private string _cardType = "";

    public CardThumbnail()
    {
        // 尺寸按「七类卡片一屏放得下」倒推：工具箱可用高度约 700 DIP，
        // 每条要控制在 80 DIP 以内（缩略图 40 + 名称 18 + 间距与内边距 22）。
        Width = 58;
        Height = 40;
        // 主题翻转时重画：构造时机早于宿主套主题（独立窗口在激活后才 ApplyTheme），
        // 第一次画走的是系统主题，宿主套完浅色/深色后要跟着翻一次。
        ActualThemeChanged += (_, _) => Redraw();
        Children.Add(CanvasFor(""));
    }

    private void Redraw()
    {
        Children.Clear();
        Children.Add(CanvasFor(_cardType));
    }

    /// <summary>卡片类型（决定画哪种结构）；改了会重画。</summary>
    public string CardType
    {
        get => _cardType;
        set
        {
            if (_cardType == value) return;
            _cardType = value;
            Redraw();
        }
    }

    private Grid CanvasFor(string type)
    {
        // 卡面：主题半透明实色 + 玻璃缘（半透明白描边）—— 液态玻璃的标准做法。
        // <para>为什么不用主页卡片同款的 GlassCardBrush（AcrylicBrush，应用内亚克力）：
        // 应用内亚克力取样的是窗口背后的内容 —— 主窗口背后是壁纸层，取出来是浅色；
        // 工具箱窗口背后是 DesktopAcrylic + 桌面（用户毛玻璃强度为 0 时壁纸原样透出），
        // 取出来是深色。同一个画刷在两个窗口渲染不一致，实测卡片发黑。
        // 半透明实色不依赖取样，任何窗口、任何桌面下都是同一张玻璃面。</para>
        var face = ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(0xC8, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);
        var grid = new Grid
        {
            Padding = new Thickness(5),
            Background = new SolidColorBrush(face),
            BorderBrush = new SolidColorBrush(GlassEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
        };
        for (var c = 0; c < 4; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 4; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        foreach (var piece in Describe(type))
        {
            FrameworkElement element = piece.Kind switch
            {
                PieceKind.Bar => new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(piece.Accent ? Accent : Fill),
                },
                PieceKind.Pill => new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Accent),
                },
                PieceKind.Avatar => new Ellipse
                {
                    Fill = new SolidColorBrush(Avatar),
                },
                _ => new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Fill),
                },
            };

            Grid.SetColumn(element, piece.Col);
            Grid.SetRow(element, piece.Row);
            Grid.SetColumnSpan(element, piece.ColSpan);
            Grid.SetRowSpan(element, piece.RowSpan);
            element.Margin = new Thickness(2, 1.5, 2, 1.5);
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Stretch;
            grid.Children.Add(element);
        }
        return grid;
    }

    /// <summary>
    /// 沿 ActualTheme 在合并字典的 ThemeDictionaries 里查主题画刷。
    /// <para>Application.Current.Resources 的索引器不认当前窗口的主题
    /// （独立窗口尤甚），直接取会拿到另一个主题的画刷 —— 与 MainWindow.ThemedBrush
    /// 同一套做法。</para>
    /// </summary>
    private Brush? ThemedBrush(string key)
    {
        var want = ActualTheme.ToString();
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (d.ThemeDictionaries.TryGetValue(want, out var v) &&
                v is ResourceDictionary rd && rd.TryGetValue(key, out var b) &&
                b is Brush brush)
                return brush;
        return Application.Current.Resources.TryGetValue(key, out var fallback) &&
            fallback is Brush fb ? fb : null;
    }

    private enum PieceKind { Bar, Pill, Avatar }

    private readonly record struct Piece(PieceKind Kind, int Col, int Row, int ColSpan, int RowSpan, bool Accent = false);

    /// <summary>
    /// 每类卡片的内部结构（4×4 示意网格）：画的是「这张卡大致由什么组成」，不是精确排版。
    /// </summary>
    private static List<Piece> Describe(string type) =>
        type switch
        {
            // 已安装版本：标题条 + 若干列表行（行首带方块图标位）
            HomeCardCatalog.Versions =>
            [
                new(PieceKind.Bar, 0, 0, 4, 1),
                new(PieceKind.Bar, 0, 1, 1, 1),
                new(PieceKind.Bar, 1, 1, 3, 1),
                new(PieceKind.Bar, 0, 2, 1, 1),
                new(PieceKind.Bar, 1, 2, 3, 1),
                new(PieceKind.Bar, 0, 3, 1, 1),
                new(PieceKind.Bar, 1, 3, 3, 1),
            ],
            // 账户与启动：顶部分段 + 中间头像 + 底部主按钮
            HomeCardCatalog.Account =>
            [
                new(PieceKind.Bar, 0, 0, 4, 1),
                new(PieceKind.Avatar, 1, 1, 2, 2),
                new(PieceKind.Pill, 0, 3, 4, 1, Accent: true),
            ],
            // 账户选择：顶部分段 + 头像与名字行
            HomeCardCatalog.AccountPicker =>
            [
                new(PieceKind.Bar, 0, 0, 4, 1),
                new(PieceKind.Avatar, 0, 1, 1, 2),
                new(PieceKind.Bar, 1, 1, 3, 1),
                new(PieceKind.Bar, 1, 2, 2, 1),
            ],
            // 启动按钮：中间一颗主按钮
            HomeCardCatalog.Launch =>
            [
                new(PieceKind.Pill, 1, 1, 2, 2, Accent: true),
            ],
            // 收藏 / 最近 / 游玩时长：标题 + 几条列表行
            HomeCardCatalog.Favorites or HomeCardCatalog.Recent or HomeCardCatalog.MostPlayed =>
            [
                new(PieceKind.Bar, 0, 0, 4, 1),
                new(PieceKind.Bar, 0, 1, 1, 1),
                new(PieceKind.Bar, 1, 1, 3, 1),
                new(PieceKind.Bar, 0, 2, 1, 1),
                new(PieceKind.Bar, 1, 2, 3, 1),
                new(PieceKind.Bar, 0, 3, 1, 1),
                new(PieceKind.Bar, 1, 3, 3, 1),
            ],
            _ =>
            [
                new(PieceKind.Bar, 0, 0, 4, 4),
            ],
        };
}
