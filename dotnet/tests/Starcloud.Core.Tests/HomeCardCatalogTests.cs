// 卡片规格回归：每类卡片的最小/最大占格，以及钳制行为。
// 拖动与缩放在 UI 里最终都落到 HomeCardSpec.Clamp，这里把边界钉死。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class HomeCardCatalogTests
{
    [Theory]
    [InlineData("account", 2, 4, 2, 6)]
    [InlineData("launch", 1, 2, 2, 6)]
    [InlineData("favorites", 2, 6, 2, 6)]
    [InlineData("recent", 2, 6, 2, 6)]
    public void 规格表与约定一致(string type, int minW, int maxW, int minH, int maxH)
    {
        var spec = HomeCardCatalog.Resolve(type);
        Assert.Equal(minW, spec.MinColumnSpan);
        Assert.Equal(maxW, spec.MaxColumnSpan);
        Assert.Equal(minH, spec.MinRowSpan);
        Assert.Equal(maxH, spec.MaxRowSpan);
    }

    [Fact]
    public void 占格钳制不越界且保持最小可用尺寸()
    {
        var launch = HomeCardCatalog.Resolve(HomeCardCatalog.Launch);

        // 拉得比最小还小：回到 1×2
        Assert.Equal((1, 2), launch.Clamp(0, 1));
        // 拉得比最大还大：回到 2×6
        Assert.Equal((2, 6), launch.Clamp(99, 99));
        // 正常范围内原样保留
        Assert.Equal((2, 4), launch.Clamp(2, 4));

        var favorites = HomeCardCatalog.Resolve(HomeCardCatalog.Favorites);
        Assert.Equal((2, 2), favorites.Clamp(1, 1));
        Assert.Equal((6, 6), favorites.Clamp(6, 7));
    }

    [Fact]
    public void 未知类型回落到已安装版本规格()
    {
        var spec = HomeCardCatalog.Resolve("nope");
        Assert.Equal(HomeCardCatalog.Versions, spec.Type);
        Assert.Equal(4, spec.MinColumnSpan);
    }

    [Fact]
    public void 出厂布局的两张卡都在各自规格范围内()
    {
        var config = HomeLayoutConfig.Default();
        foreach (var card in config.Cards)
        {
            var spec = HomeCardCatalog.Resolve(card.Type);
            var (width, height) = spec.Clamp(card.ColumnSpan, card.RowSpan);
            Assert.Equal(card.ColumnSpan, width);
            Assert.Equal(card.RowSpan, height);
        }
    }
}
