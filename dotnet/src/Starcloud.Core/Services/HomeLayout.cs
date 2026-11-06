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

    /// <summary>编辑工具箱相对默认位置（内容区右沿）的横向偏移（DIP）；用户拖动后记住。</summary>
    public double ToolboxOffsetX { get; set; }

    /// <summary>编辑工具箱相对默认位置的纵向偏移（DIP）。</summary>
    public double ToolboxOffsetY { get; set; }

    public List<HomeCardLayout> Cards { get; set; } = [];

    /// <summary>出厂布局：左 8 格版本卡 + 右 4 格账户卡，与网格化之前的双列比例一致。</summary>
    public static HomeLayoutConfig Default() => new()
    {
        SchemaVersion = 3,
        Cards =
        [
            // 初始只有两张卡，按网格化之前的比例铺满整个主页：左 8 格、右 4 格、都是整页高
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
            ToolboxOffsetX = double.IsFinite(ToolboxOffsetX) ? ToolboxOffsetX : 0,
            ToolboxOffsetY = double.IsFinite(ToolboxOffsetY) ? ToolboxOffsetY : 0,
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
    /// 按行优先扫描第一个能放下 <paramref name="columnSpan"/>×<paramref name="rowSpan"/> 的空位。
    /// <paramref name="ignoreId"/> 用于「移动已有卡片」时把它自己排除掉；
    /// <paramref name="maxRows"/> 大于 0 时只在这几行里找，找不到返回 (-1, -1)。
    /// </summary>
    public static (int Column, int Row) FindSlot(IReadOnlyList<HomeCardLayout> cards, int columns,
        int columnSpan, int rowSpan, string? ignoreId = null, int maxRows = 0)
    {        var cols = Math.Max(1, columns);
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

        var limit = maxRows > 0 ? Math.Max(0, maxRows - rows) : maxRow;
        var probe = new HomeCardLayout { ColumnSpan = span, RowSpan = rows };
        for (var r = 0; r <= limit; r++)
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

        // 固定行数（主页不滚动）下找不到空位就是真的放不下
        return maxRows > 0 ? (-1, -1) : (0, maxRow);
    }
}

/// <summary>一张卡片落位后的结果。</summary>
/// <param name="Success">是否落得下。</param>
/// <param name="Column">落位后的列（失败时为 -1）。</param>
/// <param name="Row">落位后的行（失败时为 -1）。</param>
/// <param name="ColumnSpan">落位后的横向占格（可能被自动调小）。</param>
/// <param name="RowSpan">落位后的纵向占格（可能被自动调小）。</param>
/// <param name="MovedAway">落位点与用户拖的目标不一致——卡片被挪到了别处（腾位所致）。</param>
/// <param name="Shrunk">占格被自动调小了（腾位所致）。</param>
/// <param name="Displaced">为腾出空间而被缩小/挪开的已有卡片（id → 新的占格与位置）。</param>
public readonly record struct HomeCardPlacement(
    bool Success,
    int Column,
    int Row,
    int ColumnSpan,
    int RowSpan,
    bool MovedAway = false,
    bool Shrunk = false,
    IReadOnlyList<HomeCardShift>? Displaced = null);

/// <summary>一张被腾位挤开的卡片的新状态。</summary>
/// <param name="Id">卡片 id。</param>
/// <param name="Column">新位置列。</param>
/// <param name="Row">新位置行。</param>
/// <param name="ColumnSpan">新横向占格。</param>
/// <param name="RowSpan">新纵向占格。</param>
public readonly record struct HomeCardShift(string Id, int Column, int Row, int ColumnSpan, int RowSpan);

/// <summary>
/// 卡片落位决策：给定用户想要的位置，算出真正能落下的位置与占格。
/// <para>
/// 为什么需要这一层：主页默认布局把 12×6 的网格占满（版本卡 8×6 + 账户卡 4×6），
/// 此时从工具箱拖任何一张新卡都会与已有卡片重叠 —— 旧实现直接判定失败且不给任何提示，
/// 用户看到的是「拖了没反应」。这里把腾位策略收在一处：
/// 先试原档位落在目标格；放不下就逐档换小档位；再放不下就在整张网格里找空位
/// （从大档位往小档位试）。三条路都走不通才算失败。</para>
/// </summary>
public static class HomeCardPlacer
{
    /// <summary>
    /// 把一张卡片落到 <paramref name="desiredColumn"/>/<paramref name="desiredRow"/>。
    /// </summary>
    /// <param name="occupied">网格里已有的卡片（只考虑可见的）。</param>
    /// <param name="columns">网格列数。</param>
    /// <param name="rows">网格行数（主页固定，不滚动）。</param>
    /// <param name="spec">卡片类型的规格（决定可用档位）。</param>
    /// <param name="desiredColumn">用户拖到的列（-1 表示不指定，由系统找位）。</param>
    /// <param name="desiredRow">用户拖到的行。</param>
    /// <param name="ignoreId">要移动的卡片自身 id（把它从占位表里排除）。</param>
    /// <param name="desiredSize">
    /// 用户想要的档位（改尺寸时传；不传 = 用该类型的出厂占格）。
    /// <para>不传时本方法自己从出厂档位起挑——加卡场景够用，但「用户点了 6×4」
    /// 这种明确意图必须尊重，否则会被出厂档位（8×6）顶掉。</para>
    /// </param>
    public static HomeCardPlacement Place(
        IReadOnlyList<HomeCardLayout> occupied,
        int columns,
        int rows,
        HomeCardSpec spec,
        int desiredColumn,
        int desiredRow,
        string? ignoreId = null,
        CardSize? desiredSize = null)
    {
        var cols = Math.Max(1, columns);
        var rowLimit = Math.Max(1, rows);

        // 候选档位：先试用户指定的档位（改尺寸场景），没指定就试出厂占格（加卡场景），
        // 再从大到小试其余档位。不能直接拿「最大的档位」当首选 —— 收藏卡出厂 3×2，
        // 档位表里却有 3×3，拿 3×3 当首选会让新卡白占一格空间。
        var preferred = desiredSize is { } wanted
            ? new CardSize(spec.Clamp(wanted.ColumnSpan, wanted.RowSpan).ColumnSpan,
                           spec.Clamp(wanted.ColumnSpan, wanted.RowSpan).RowSpan)
            : new CardSize(spec.DefaultColumnSpan, spec.DefaultRowSpan);
        var sizes = new List<CardSize> { preferred };
        foreach (var size in spec.Presets)
        {
            if (size.ColumnSpan == preferred.ColumnSpan && size.RowSpan == preferred.RowSpan) continue;
            sizes.Add(size);
        }
        // 其余档位按面积从大到小：放不下时优先牺牲最小的空间
        var rest = sizes.Skip(1).OrderByDescending(s => s.ColumnSpan * s.RowSpan).ToList();
        sizes = [preferred, .. rest];

        // 只看可见且不是自己的卡片
        var blockers = occupied
            .Where(c => c is not null && c.Visible)
            .Where(c => ignoreId is null || !string.Equals(c.Id, ignoreId, StringComparison.Ordinal))
            .ToList();

        var hasRoom = (int column, int row, int spanW, int spanH) =>
        {
            if (column < 0 || row < 0) return false;
            if (column + spanW > cols || row + spanH > rowLimit) return false;
            var probe = new HomeCardLayout { Column = column, Row = row, ColumnSpan = spanW, RowSpan = spanH };
            return !blockers.Any(b => HomeGridMath.Overlaps(probe, b));
        };

        // ① 目标格位 + 首选档位：用户想要的位置，放得下就用它
        if (desiredColumn >= 0 && desiredRow >= 0)
        {
            var (w, h) = spec.Clamp(preferred.ColumnSpan, preferred.RowSpan);
            if (hasRoom(desiredColumn, desiredRow, w, h))
                return new HomeCardPlacement(true, desiredColumn, desiredRow, w, h);
        }

        // ② 目标格位 + 换小档位：位置不动，把卡片缩到放得下的最大档
        foreach (var size in sizes)
        {
            var (w, h) = spec.Clamp(size.ColumnSpan, size.RowSpan);
            if (w == preferred.ColumnSpan && h == preferred.RowSpan) continue;
            if (desiredColumn >= 0 && desiredRow >= 0 && hasRoom(desiredColumn, desiredRow, w, h))
                return new HomeCardPlacement(true, desiredColumn, desiredRow, w, h, Shrunk: true);
        }

        // ③ 整张网格找空位：位置让给别的卡，从大档位往小档位试
        foreach (var size in sizes)
        {
            var (w, h) = spec.Clamp(size.ColumnSpan, size.RowSpan);
            var (column, row) = HomeGridMath.FindSlot(blockers, cols, w, h, maxRows: rowLimit);
            if (column >= 0 && hasRoom(column, row, w, h))
                return new HomeCardPlacement(true, column, row, w, h, MovedAway: true);
        }

        // ④ 缩小已有卡片腾位：出厂布局把 12×6 占满，前三步都无处可放。
        // 只有这一条路能让用户加卡 —— 挑一张能缩小的已有卡片，缩到下一档，
        // 腾出的空间给新卡。缩到最小还放不下，才是真的放不下。
        var displaced = ShrinkExistingForRoom(blockers, cols, rowLimit, spec, sizes, desiredColumn, desiredRow);
        if (displaced is not null)
        {
            var (newCard, shrunk) = displaced.Value;
            return new HomeCardPlacement(true, newCard.Column, newCard.Row,
                newCard.ColumnSpan, newCard.RowSpan, MovedAway: true, Shrunk: true, Displaced: shrunk);
        }

        return new HomeCardPlacement(false, -1, -1, 0, 0);
    }

    /// <summary>
    /// 腾位重排：网格被占满时，缩小若干张已有卡片，给新卡腾出空间。
    /// <para>
    /// 做法是「整体重排」而不是一张张挤：先把新卡放在用户指定的位置（或第一个能放的位置），
    /// 再把其余卡片按各自的尺寸重排一遍（保持相对顺序，行优先找位），
    /// 放不下的卡片就缩到下一档继续试。全部放得下才返回结果。
    /// 这比「只挤一张卡」可靠得多 —— 出厂布局里版本卡 8×6 与账户卡 4×6 铺满 12×6，
    /// 只缩一张必然还有另一张压着新卡。</para>
    /// </summary>
    private static (HomeCardLayout NewCard, List<HomeCardShift> Shifted)? ShrinkExistingForRoom(
        List<HomeCardLayout> blockers, int cols, int rowLimit, HomeCardSpec spec,
        List<CardSize> sizes, int desiredColumn, int desiredRow)
    {
        // 新卡的尺寸：首选出厂占格，放不下再逐档换小
        foreach (var size in sizes)
        {
            var (w, h) = spec.Clamp(size.ColumnSpan, size.RowSpan);

            // 新卡的位置：优先用户指定的目标格
            var newCard = new HomeCardLayout
            {
                Column = desiredColumn >= 0 ? desiredColumn : 0,
                Row = desiredRow >= 0 ? desiredRow : 0,
                ColumnSpan = w,
                RowSpan = h,
            };
            if (newCard.Column + w > cols || newCard.Row + h > rowLimit) continue;

            // 格数不够就别搜了：每张卡缩到它能缩的最小档，再加上新卡，
            // 总占格仍超过网格总面积 —— 那就必然放不下。
            // 这一剪枝把「12 张卡铺满 12×6」这类无解局面从「搜索到预算耗尽」
            // 变成「一眼看出」，是避免搜索爆炸最有效的一条。
            var minUsed = w * h;
            foreach (var b in blockers)
            {
                var bSpec = HomeCardCatalog.Resolve(b.Type);
                var bMin = bSpec.Presets
                    .Select(p => bSpec.Clamp(p.ColumnSpan, p.RowSpan))
                    .Where(c => c.ColumnSpan * c.RowSpan <= b.ColumnSpan * b.RowSpan)
                    .Select(c => c.ColumnSpan * c.RowSpan)
                    .DefaultIfEmpty(b.ColumnSpan * b.RowSpan)
                    .Min();
                minUsed += bMin;
                if (minUsed > cols * rowLimit) break;
            }
            if (minUsed > cols * rowLimit) continue;

            var layout = LayoutAround(blockers, newCard, cols, rowLimit);
            if (layout is not null)
                return (newCard, layout.Value.Shifted);

            // 新卡自己的位置也可能是问题：目标格放不下时整网格找位
            var (column, row) = HomeGridMath.FindSlot(blockers, cols, w, h, maxRows: rowLimit);
            if (column < 0) continue;

            newCard.Column = column;
            newCard.Row = row;
            layout = LayoutAround(blockers, newCard, cols, rowLimit);
            if (layout is not null)
                return (newCard, layout.Value.Shifted);
        }
        return null;
    }

    /// <summary>
    /// 把新卡之外的卡片重排到剩余空间里；放不下的卡片缩到下一档再试。
    /// <para>
    /// 用回溯而不是贪心：贪心地把每张卡放到第一个空位，会出现「前面的卡占了位置，
    /// 后面的卡就再也放不下」的死局（出厂布局 8×6 + 4×6 就是如此）。
    /// 回溯按卡片顺序逐个决定尺寸与位置，放不下就换一种选择再试。
    /// 网格只有 12×6、卡片最多十来张，搜索空间很小。</para>
    /// <para>
    /// 但「很小」只是直觉：12 张卡 × 3 档尺寸 × 70 个位置，搜索树仍有 10^11 量级 ——
    /// 实测确实把测试进程卡死了。所以这里加两道闸：
    /// <list type="bullet">
    /// <item>节点预算：搜索节点超过上限就放弃这次腾位（宁可报「放不下」，也不能卡住界面）。</item>
    /// <item>改动上限：最多调整 N 张卡。腾位的语义是「给新卡让出必要的空间」，
    /// 把整页卡片都挪一遍既不必要也让人难以预期。</item>
    /// </list></para>
    /// </summary>
    private static (List<HomeCardShift> Shifted, bool AllPlaced)? LayoutAround(
        List<HomeCardLayout> cards, HomeCardLayout anchor, int cols, int rowLimit)
    {
        // 搜索预算：节点数上限与最多可挪动的卡片数
        const int nodeBudget = 20000;
        const int maxShifted = 3;
        var nodes = 0;

        var placed = new List<HomeCardLayout> { anchor };
        var shifts = new List<HomeCardShift>();

        bool Search(int index)
        {
            if (index >= cards.Count) return true;
            if (++nodes > nodeBudget) return false;
            var card = cards[index];
            var cardSpec = HomeCardCatalog.Resolve(card.Type);

            // 候选尺寸：先试更小的档位，且保持原位。
            // 为什么优先「缩小」而不是「挪位」：把一张 8×6 的版本卡挪三列，
            // 用户看到的是整块版面大改；缩到 6×4 留在原位，视觉上只动了一处。
            var candidates = new List<(int W, int H)>();
            foreach (var preset in cardSpec.Presets)
            {
                var (cw, ch) = cardSpec.Clamp(preset.ColumnSpan, preset.RowSpan);
                if (cw * ch < card.ColumnSpan * card.RowSpan) candidates.Add((cw, ch));
            }
            candidates.Add((card.ColumnSpan, card.RowSpan));
            candidates = candidates.Distinct().ToList();

            foreach (var (cw, ch) in candidates)
            {
                // 候选位置：原位优先（保持用户摆好的样子），再按行优先扫全网格
                var spots = new List<(int C, int R)> { (card.Column, card.Row) };
                for (var r = 0; r + ch <= rowLimit; r++)
                    for (var c = 0; c + cw <= cols; c++)
                    {
                        if (c == card.Column && r == card.Row) continue;
                        spots.Add((c, r));
                    }

                foreach (var (c, r) in spots)
                {
                    var changed = cw != card.ColumnSpan || ch != card.RowSpan
                        || c != card.Column || r != card.Row;
                    // 改动上限：卡片已经挪得够多就别再挪了
                    if (changed && shifts.Count >= maxShifted) continue;

                    var probe = new HomeCardLayout
                    {
                        Id = card.Id,
                        Type = card.Type,
                        Column = c,
                        Row = r,
                        ColumnSpan = cw,
                        RowSpan = ch,
                        Visible = true,
                    };
                    if (placed.Any(o => HomeGridMath.Overlaps(probe, o))) continue;

                    placed.Add(probe);
                    if (changed)
                        shifts.Add(new HomeCardShift(card.Id, c, r, cw, ch));

                    if (Search(index + 1)) return true;

                    // 这条路走不通：撤销本层与更深层留下的痕迹，再换下一个候选。
                    // 深层递归自己会撤销自己的部分，这里只需撤掉本层加的两条。
                    placed.RemoveAt(placed.Count - 1);
                    if (changed) shifts.RemoveAt(shifts.Count - 1);
                }
            }
            return false;
        }

        return Search(0) ? (shifts, true) : null;
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
            // 旧版本（网格化之前 / 默认布局改版前）直接回退出厂布局
            if (config is null || config.SchemaVersion < 3) return HomeLayoutConfig.Default();
            return config.Normalized();
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
