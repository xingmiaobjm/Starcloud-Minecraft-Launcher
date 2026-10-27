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
}
