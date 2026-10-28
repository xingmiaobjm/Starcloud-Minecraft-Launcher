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
        Children.Add(CanvasFor(""));
    }

    /// <summary>卡片类型（决定画哪种结构）；改了会重画。</summary>
    public string CardType
    {
        get => _cardType;
        set
        {
            if (_cardType == value) return;
            _cardType = value;
            Children.Clear();
            Children.Add(CanvasFor(value));
        }
    }

    private Grid CanvasFor(string type)
    {
        var grid = new Grid
        {
            Padding = new Thickness(5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = new SolidColorBrush(Line),
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
