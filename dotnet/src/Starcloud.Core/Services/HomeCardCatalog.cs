// 卡片规格：每类卡片允许占多少格。拖动与缩放在这里收敛，
// 避免把一张卡拉成放不下内容的细条；同时给小/大尺寸的内容适配提供判断依据
// （卡片内容实现 ICardSizeAware 后，宿主会在格位变化时回调它的占格数）。

namespace Starcloud.Core.Services;

/// <summary>一类卡片的尺寸约束与出厂占格。</summary>
public sealed record HomeCardSpec(
    string Type,
    string DisplayName,
    int MinColumnSpan,
    int MaxColumnSpan,
    int MinRowSpan,
    int MaxRowSpan,
    int DefaultColumnSpan,
    int DefaultRowSpan)
{
    /// <summary>把占格钳到本类卡片允许的区间。</summary>
    public (int ColumnSpan, int RowSpan) Clamp(int columnSpan, int rowSpan) => (
        Math.Clamp(columnSpan, MinColumnSpan, MaxColumnSpan),
        Math.Clamp(rowSpan, MinRowSpan, MaxRowSpan));
}

/// <summary>内置卡片的规格表。</summary>
public static class HomeCardCatalog
{
    public const string Versions = "versions";
    public const string Account = "account";
    public const string Launch = "launch";
    public const string Favorites = "favorites";
    public const string Recent = "recent";

    public static readonly IReadOnlyList<HomeCardSpec> All =
    [
        // 已安装版本：内容最重，给足宽度；行数少到 3 行时只显示前几条
        new(Versions, "已安装版本", 4, 12, 3, 12, 8, 6),
        // 账户选择：头像 + 名称 + 模式，最小 2×2 只够头像和名字
        new(Account, "账户选择", 2, 4, 2, 6, 4, 3),
        // 启动按钮：最小 1×2 是竖条按钮，2×6 是整块大按钮
        new(Launch, "启动按钮", 1, 2, 2, 6, 2, 3),
        // 收藏 / 最近：都是版本小列表，最小 2×2 显示两三行
        new(Favorites, "收藏的版本", 2, 6, 2, 6, 4, 4),
        new(Recent, "最近的版本", 2, 6, 2, 6, 4, 4),
    ];

    private static readonly Dictionary<string, HomeCardSpec> ByType =
        All.ToDictionary(spec => spec.Type, StringComparer.Ordinal);

    /// <summary>按类型取规格；未知类型按「已安装版本」处理，保证有约束可用。</summary>
    public static HomeCardSpec Resolve(string? type) =>
        type is not null && ByType.TryGetValue(type, out var spec) ? spec : ByType[Versions];
}

/// <summary>卡片内容按自身占格重排的约定（实现放在内容控件上，宿主负责调用）。</summary>
public interface ICardSizeAware
{
    /// <summary>宿主格的占格发生变化时调用（列跨度 / 行跨度）。</summary>
    void ApplyCardSize(int columnSpan, int rowSpan);
}
