// 主页卡片网格：布局模型 + 落盘 + 纯几何换算。
//
// 为什么放在 Core：拖拽与缩放最终都归结为「像素 → 格」的换算、越界钳制和空位查找，
// 这些都是纯函数，可以在没有 UI 的单元测试里覆盖；页面只负责把结果画出来。
// 配置损坏、手改 JSON、旧版本文件都在 Normalized() 里收敛到合法范围，页面不需要再做防御。

using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>一张卡在网格里的位置与占格。</summary>
public sealed class HomeCardLayout
{
    /// <summary>稳定标识（同一张卡跨启动保持不变，配置按它对齐）。</summary>
    public string Id { get; set; } = "";

    /// <summary>卡片类型（versions / account / plugins / …），决定宿主用哪个控件渲染。</summary>
    public string Type { get; set; } = "";

    /// <summary>左上角所在列（0 起）。</summary>
    public int Column { get; set; }

    /// <summary>左上角所在行（0 起）。</summary>
    public int Row { get; set; }

    /// <summary>横向占几格。</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>纵向占几格。</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>是否显示（隐藏的卡仍保留位置，便于随时恢复）。</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>主页网格的全部布局：列数、行高与每张卡的格位。</summary>
public sealed class HomeLayoutConfig
{
    public const int MinColumns = 4;
    public const int MaxColumns = 16;
    public const int DefaultColumns = 12;
    public const double DefaultRowHeight = 88;

    /// <summary>配置结构版本；将来改字段时用来做迁移。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>网格列数（窗口变窄时页面可临时降级显示，但配置里保留用户选定的列数）。</summary>
    public int Columns { get; set; } = DefaultColumns;

    /// <summary>单行高度（DIP）。</summary>
    public double RowHeight { get; set; } = DefaultRowHeight;

    public List<HomeCardLayout> Cards { get; set; } = [];

    /// <summary>出厂布局：左 8 格版本卡 + 右 4 格账户卡，与网格化之前的双列比例一致。</summary>
    public static HomeLayoutConfig Default() => new()
    {
        Cards =
        [
            new HomeCardLayout
            {
                Id = "versions", Type = "versions",
                Column = 0, Row = 0, ColumnSpan = 8, RowSpan = 6,
            },
            new HomeCardLayout
            {
                Id = "account", Type = "account",
                Column = 8, Row = 0, ColumnSpan = 4, RowSpan = 6,
            },
        ],
    };

    /// <summary>
    /// 把任意外来配置钳到合法范围：列数 4–16、跨度不小于 1、越界的卡贴回网格内、
    /// Id 重复或为空的条目直接丢弃。结果保证每张卡都能完整落在网格里。
    /// </summary>
    public HomeLayoutConfig Normalized()
    {
        var columns = Math.Clamp(Columns, MinColumns, MaxColumns);
        var rowHeight = RowHeight is >= 48 and <= 320 ? RowHeight : DefaultRowHeight;
        var result = new HomeLayoutConfig
        {
            SchemaVersion = SchemaVersion <= 0 ? 1 : SchemaVersion,
            Columns = columns,
            RowHeight = rowHeight,
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in Cards)
        {
            var id = card?.Id?.Trim() ?? "";
            if (id.Length == 0 || !seen.Add(id)) continue;
            var span = Math.Clamp(card!.ColumnSpan, 1, columns);
            result.Cards.Add(new HomeCardLayout
            {
                Id = id,
                Type = card.Type ?? "",
                Column = Math.Clamp(card.Column, 0, columns - span),
                Row = Math.Max(0, card.Row),
                ColumnSpan = span,
                RowSpan = Math.Max(1, card.RowSpan),
                Visible = card.Visible,
            });
        }

        // 全部条目都非法时给回出厂布局，至少页面不会是空的
        return result.Cards.Count == 0 ? Default() : result;
    }
}

/// <summary>网格几何：像素与格之间的换算、越界钳制、空位查找。</summary>
public static class HomeGridMath
{
    /// <summary>
    /// 把卡片左上角的像素位置吸附到最近的格，并保证整张卡不越界。
    /// </summary>
    /// <param name="left">卡片左上角相对网格原点的横向像素</param>
    /// <param name="top">卡片左上角相对网格原点的纵向像素</param>
    /// <param name="cellWidth">单格宽度（不含间距）</param>
    /// <param name="cellHeight">单格高度（不含间距）</param>
    /// <param name="columnGap">列间距</param>
    /// <param name="rowGap">行间距</param>
    /// <param name="columnSpan">卡片横跨格数</param>
    /// <param name="rowSpan">卡片纵跨格数（只影响调用方，不参与列钳制）</param>
    /// <param name="columns">网格总列数</param>
    public static (int Column, int Row) SnapCell(double left, double top,
        double cellWidth, double cellHeight, double columnGap, double rowGap,
        int columnSpan, int rowSpan, int columns)
    {
        _ = rowSpan; // 行数由页面按内容撑开，钳制只处理左/上边界
        var stepX = Math.Max(1, cellWidth + columnGap);
        var stepY = Math.Max(1, cellHeight + rowGap);
        var span = Math.Clamp(columnSpan, 1, Math.Max(1, columns));
        var column = (int)Math.Round(left / stepX, MidpointRounding.AwayFromZero);
        var row = (int)Math.Round(top / stepY, MidpointRounding.AwayFromZero);
        column = Math.Clamp(column, 0, Math.Max(0, columns - span));
        row = Math.Max(0, row);
        return (column, row);
    }

    /// <summary>两张卡的占格区域是否重叠（相邻不算）。</summary>
    public static bool Overlaps(HomeCardLayout a, HomeCardLayout b) =>
        a.Column < b.Column + b.ColumnSpan && b.Column < a.Column + a.ColumnSpan &&
        a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;

    /// <summary>
    /// 按行优先扫描第一个能放下 <paramref name="columnSpan"/>×<paramref name="rowSpan"/> 的空位；
    /// 找不到（理论上不会，除非网格被撑满到没有空行）就放在现有内容下方。
    /// <paramref name="ignoreId"/> 用于「移动已有卡片」时把它自己排除掉。
    /// </summary>
    public static (int Column, int Row) FindSlot(IReadOnlyList<HomeCardLayout> cards, int columns,
        int columnSpan, int rowSpan, string? ignoreId = null)
    {
        var cols = Math.Max(1, columns);
        var span = Math.Clamp(columnSpan, 1, cols);
        var rows = Math.Max(1, rowSpan);

        // 只扫到「现有卡片的最低行」：再往下一定是空的，没必要无限扫
        var maxRow = 0;
        foreach (var card in cards)
        {
            if (card is null || !card.Visible) continue;
            if (ignoreId is not null && string.Equals(card.Id, ignoreId, StringComparison.Ordinal)) continue;
            maxRow = Math.Max(maxRow, card.Row + card.RowSpan);
        }

        var probe = new HomeCardLayout { ColumnSpan = span, RowSpan = rows };
        for (var r = 0; r <= maxRow; r++)
        {
            for (var c = 0; c <= cols - span; c++)
            {
                probe.Column = c;
                probe.Row = r;
                var blocked = false;
                foreach (var card in cards)
                {
                    if (card is null || !card.Visible) continue;
                    if (ignoreId is not null && string.Equals(card.Id, ignoreId, StringComparison.Ordinal)) continue;
                    if (Overlaps(probe, card))
                    {
                        blocked = true;
                        break;
                    }
                }
                if (!blocked) return (c, r);
            }
        }

        return (0, maxRow);
    }
}

/// <summary>主页布局的读写（%APPDATA%/starcloud-launcher/home-layout.json）。</summary>
public sealed class HomeLayoutStore(string? dataDir = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>配置文件完整路径（测试与诊断用）。</summary>
    public string FilePath { get; } = Path.Combine(dataDir ?? LauncherPaths.DataDir, "home-layout.json");

    /// <summary>读取布局；文件缺失或损坏时返回出厂布局，绝不抛异常。</summary>
    public HomeLayoutConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return HomeLayoutConfig.Default();
            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<HomeLayoutConfig>(json, JsonOpts);
            return config is null ? HomeLayoutConfig.Default() : config.Normalized();
        }
        catch
        {
            return HomeLayoutConfig.Default();
        }
    }

    /// <summary>写回布局；写入前统一过一遍 Normalized()，落盘失败静默（不影响使用）。</summary>
    public void Save(HomeLayoutConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config.Normalized(), JsonOpts));
        }
        catch
        {
            // 布局保存失败只影响下次启动的排版，不该打断用户当前操作
        }
    }
}
