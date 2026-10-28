// 主页卡片网格的纯逻辑回归：越界钳制、像素吸附、空位查找与配置读写。
// 这些是拖拽/缩放在 UI 之外的确定部分，页面只要把结果画出来。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class HomeLayoutTests
{
    private static string MakeDataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-home-layout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 出厂布局在固定行列内且互不重叠()
    {
        var config = HomeLayoutConfig.Default();

        Assert.Equal(12, config.Columns);
        Assert.Equal(2, config.Cards.Count);

        foreach (var card in config.Cards)
        {
            var spec = HomeCardCatalog.Resolve(card.Type);
            var (width, height) = spec.Clamp(card.ColumnSpan, card.RowSpan);
            Assert.Equal(card.ColumnSpan, width);
            Assert.Equal(card.RowSpan, height);
            Assert.True(card.Column + card.ColumnSpan <= config.Columns, card.Id);
            Assert.True(card.Row + card.RowSpan <= 6, card.Id);
            Assert.True(card.Visible, card.Id);
        }

        for (var i = 0; i < config.Cards.Count; i++)
            for (var j = i + 1; j < config.Cards.Count; j++)
                Assert.False(HomeGridMath.Overlaps(config.Cards[i], config.Cards[j]),
                    $"{config.Cards[i].Id} 与 {config.Cards[j].Id} 重叠");
    }

    [Fact]
    public void 非法配置被钳到网格内并丢弃重复与空Id()
    {
        var config = new HomeLayoutConfig
        {
            Columns = 99,
            RowHeight = 5,
            Cards =
            [
                new HomeCardLayout { Id = "", Type = "versions", Column = 0, Row = 0 },
                new HomeCardLayout { Id = "dup", Type = "a", Column = -5, Row = -2, ColumnSpan = 999 },
                new HomeCardLayout { Id = "dup", Type = "b", Column = 0, Row = 0 },
                new HomeCardLayout { Id = "ok", Type = "c", Column = 40, Row = 3, ColumnSpan = 6, RowSpan = 0 },
            ],
        };

        var normalized = config.Normalized();

        Assert.Equal(HomeLayoutConfig.MaxColumns, normalized.Columns);
        Assert.Equal(HomeLayoutConfig.DefaultRowHeight, normalized.RowHeight);
        Assert.Equal(2, normalized.Cards.Count);
        Assert.DoesNotContain(normalized.Cards, c => c.Id.Length == 0);
        Assert.Single(normalized.Cards, c => c.Id == "dup");

        var dup = normalized.Cards.Single(c => c.Id == "dup");
        Assert.Equal(0, dup.Column);
        Assert.Equal(0, dup.Row);
        Assert.Equal(normalized.Columns, dup.ColumnSpan);

        var ok = normalized.Cards.Single(c => c.Id == "ok");
        Assert.Equal(1, ok.RowSpan);
        Assert.True(ok.Column + ok.ColumnSpan <= normalized.Columns);
    }

    [Fact]
    public void 像素吸附按最近格取整并在右边界钳制()
    {
        // 单格 100 + 间距 20 → 步长 120
        var (col, row) = HomeGridMath.SnapCell(121, 241, 100, 100, 20, 20,
            columnSpan: 2, rowSpan: 1, columns: 12);
        Assert.Equal(1, col);
        Assert.Equal(2, row);

        // 拖到最右侧：跨度 2 的卡最多从第 10 列开始
        var (edgeCol, _) = HomeGridMath.SnapCell(5000, 0, 100, 100, 20, 20,
            columnSpan: 2, rowSpan: 1, columns: 12);
        Assert.Equal(10, edgeCol);

        // 拖到左上角外侧：钳到 0
        var (negCol, negRow) = HomeGridMath.SnapCell(-500, -500, 100, 100, 20, 20,
            columnSpan: 1, rowSpan: 1, columns: 12);
        Assert.Equal(0, negCol);
        Assert.Equal(0, negRow);
    }

    [Fact]
    public void 空位查找按行优先并跳过被占用区域()
    {
        var cards = new List<HomeCardLayout>
        {
            new() { Id = "a", Column = 0, Row = 0, ColumnSpan = 6, RowSpan = 2 },
            new() { Id = "b", Column = 6, Row = 0, ColumnSpan = 6, RowSpan = 2 },
        };

        // 前两行整行被占：4 格宽的卡应落到第 2 行第 0 列
        var (col, row) = HomeGridMath.FindSlot(cards, 12, 4, 1);
        Assert.Equal(0, col);
        Assert.Equal(2, row);

        // 忽略自己时，原位应当可用
        (col, row) = HomeGridMath.FindSlot(cards, 12, 6, 2, ignoreId: "a");
        Assert.Equal(0, col);
        Assert.Equal(0, row);

        // 隐藏的卡不占位
        cards[0].Visible = false;
        (col, row) = HomeGridMath.FindSlot(cards, 12, 6, 2);
        Assert.Equal(0, col);
        Assert.Equal(0, row);
    }

    [Fact]
    public void 配置读写往返一致且坏文件回退出厂布局()
    {
        var dir = MakeDataDir();
        var store = new HomeLayoutStore(dir);
        var config = HomeLayoutConfig.Default();
        config.Cards[0].ColumnSpan = 6;
        config.Cards[1].Column = 6;
        config.Cards[1].ColumnSpan = 6;

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(12, loaded.Columns);
        Assert.Equal(2, loaded.Cards.Count);
        Assert.Equal(6, loaded.Cards.Single(c => c.Id == "versions").ColumnSpan);
        Assert.Equal(6, loaded.Cards.Single(c => c.Id == "account").Column);

        File.WriteAllText(store.FilePath, "{ 这不是 JSON");
        var fallback = store.Load();
        Assert.Equal(2, fallback.Cards.Count);
        Assert.Contains(fallback.Cards, c => c.Id == "versions");

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void 空文件缺失时返回出厂布局()
    {
        var dir = MakeDataDir();
        var store = new HomeLayoutStore(dir);

        var loaded = store.Load();

        Assert.Equal(HomeLayoutConfig.DefaultColumns, loaded.Columns);
        Assert.Equal(2, loaded.Cards.Count);
        Directory.Delete(dir, recursive: true);
    }

    // ── 落位决策（腾位规则） ──

    /// <summary>出厂布局铺满 12×6（版本卡 8×6 + 账户卡 4×6）。</summary>
    private static List<HomeCardLayout> FullGrid() =>
    [
        new HomeCardLayout { Id = "versions", Type = "versions", Column = 0, Row = 0, ColumnSpan = 8, RowSpan = 6, Visible = true },
        new HomeCardLayout { Id = "account", Type = "account", Column = 8, Row = 0, ColumnSpan = 4, RowSpan = 6, Visible = true },
    ];

    [Fact]
    public void 网格占满时优先原地缩放而不是挪走()
    {
        // 收藏卡出厂 3×2。目标格 (0,0) 被版本卡占着、且整网格无空位：
        // 应当缩小版本卡腾位，而不是把新卡挪到别处。
        var cards = FullGrid();
        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Favorites);

        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 0, desiredRow: 0);

        Assert.True(placement.Success);
        Assert.Equal(0, placement.Column);
        Assert.Equal(0, placement.Row);
        Assert.NotEmpty(placement.Displaced!);
    }

    [Fact]
    public void 网格有空位时按原档位落在空位而不是缩小()
    {
        var cards = FullGrid();
        // 把账户卡缩到 4×3，右下空出 4×3
        cards[1].RowSpan = 3;

        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Favorites);
        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 0, desiredRow: 0);

        Assert.True(placement.Success);
        // 收藏卡出厂 3×2：目标格 (0,0) 被版本卡占着，但右下有空位 → 挪过去，尺寸不变
        Assert.Equal(3, placement.ColumnSpan);
        Assert.Equal(2, placement.RowSpan);
        Assert.True(placement.MovedAway);
        Assert.False(placement.Shrunk);
        // 落点必须真的空着
        Assert.DoesNotContain(cards.Where(b => b.Visible), b => HomeGridMath.Overlaps(
            new HomeCardLayout { Column = placement.Column, Row = placement.Row,
                ColumnSpan = placement.ColumnSpan, RowSpan = placement.RowSpan }, b));
    }

    [Fact]
    public void 目标格放不下且整网格无空位时换小档位()
    {
        // 只留右下角 2×1 的空位
        var cards = new List<HomeCardLayout>
        {
            new() { Id = "big", Type = "versions", Column = 0, Row = 0, ColumnSpan = 10, RowSpan = 6, Visible = true },
            new() { Id = "edge", Type = "account", Column = 10, Row = 0, ColumnSpan = 2, RowSpan = 5, Visible = true },
        };

        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Launch);
        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 0, desiredRow: 0);

        // 启动按钮最小 2×1：右下角那格正好放得下
        Assert.True(placement.Success);
        Assert.Equal(2, placement.ColumnSpan);
        Assert.Equal(1, placement.RowSpan);
    }

    [Fact]
    public void 已占满且没有卡片能再缩小时才真的放不下()
    {
        // 12 张账户卡（规格最小 2×3，都已是最小档、缩不动）正好铺满 12 列 × 6 行，
        // 再加一张最小 2×1 的启动卡就无处可放 —— 这时才该报「空间不足」。
        var cards = new List<HomeCardLayout>();
        for (var i = 0; i < 12; i++)
        {
            cards.Add(new HomeCardLayout
            {
                Id = $"acc{i}", Type = "account",
                Column = (i % 6) * 2, Row = (i / 6) * 3,
                ColumnSpan = 2, RowSpan = 3, Visible = true,
            });
        }

        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Launch);
        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 0, desiredRow: 3);

        Assert.False(placement.Success);
        Assert.Equal(-1, placement.Column);
        Assert.Equal(-1, placement.Row);
    }

    [Fact]
    public void 网格占满时缩小已有卡片给新卡腾位()
    {
        // 出厂布局：版本卡 8×6 + 账户卡 4×6，把 12×6 铺满
        var cards = FullGrid();
        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Favorites);

        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 0, desiredRow: 0);

        Assert.True(placement.Success);
        // 收藏卡出厂 3×2
        Assert.Equal(3, placement.ColumnSpan);
        Assert.Equal(2, placement.RowSpan);
        // 必须有卡片被挤开腾位
        Assert.NotNull(placement.Displaced);
        Assert.NotEmpty(placement.Displaced!);
        // 被挤开的卡片要么缩小、要么挪位（两者皆变也可以）
        foreach (var shift in placement.Displaced!)
        {
            var victim = cards.Single(c => c.Id == shift.Id);
            var shrunk = shift.ColumnSpan * shift.RowSpan < victim.ColumnSpan * victim.RowSpan;
            var moved = shift.Column != victim.Column || shift.Row != victim.Row;
            Assert.True(shrunk || moved, shift.Id);
        }
    }

    [Fact]
    public void 缩小腾位后新卡与所有卡片都不重叠()
    {
        var cards = FullGrid();
        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Favorites);

        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 0, desiredRow: 0);

        Assert.True(placement.Success);
        // 把被挤开的卡片按新占格与新位置算一遍
        var final = cards.Select(c =>
        {
            var d = placement.Displaced?.FirstOrDefault(x => x.Id == c.Id);
            return new HomeCardLayout
            {
                Id = c.Id,
                Column = d?.Column ?? c.Column,
                Row = d?.Row ?? c.Row,
                ColumnSpan = d?.ColumnSpan ?? c.ColumnSpan,
                RowSpan = d?.RowSpan ?? c.RowSpan,
                Visible = true,
            };
        }).ToList();

        var newCard = new HomeCardLayout
        {
            Id = "favorites",
            Column = placement.Column,
            Row = placement.Row,
            ColumnSpan = placement.ColumnSpan,
            RowSpan = placement.RowSpan,
        };
        Assert.DoesNotContain(final, c => HomeGridMath.Overlaps(c, newCard));
        // 新卡本身也要在网格内
        Assert.True(newCard.Column + newCard.ColumnSpan <= 12);
        Assert.True(newCard.Row + newCard.RowSpan <= 6);
    }

    [Fact]
    public void 移动自己的卡片时不把自己算作占位()
    {
        var cards = new List<HomeCardLayout>
        {
            new() { Id = "versions", Type = "versions", Column = 0, Row = 0, ColumnSpan = 6, RowSpan = 3, Visible = true },
            new() { Id = "account", Type = "account", Column = 6, Row = 0, ColumnSpan = 6, RowSpan = 3, Visible = true },
        };

        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Versions);
        // 把 versions 拖回自己的原位：应当落回原处，而不是被当成与自己重叠
        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 0, desiredRow: 0,
            ignoreId: "versions");

        Assert.True(placement.Success);
        Assert.Equal(0, placement.Column);
        Assert.Equal(0, placement.Row);
    }

    [Fact]
    public void 隐藏的卡片不占位()
    {
        var cards = FullGrid();
        cards[1].Visible = false;

        var spec = HomeCardCatalog.Resolve(HomeCardCatalog.Account);
        var placement = HomeCardPlacer.Place(cards, 12, 6, spec, desiredColumn: 8, desiredRow: 0);

        Assert.True(placement.Success);
        Assert.Equal(8, placement.Column);
        Assert.Equal(0, placement.Row);
    }
}
