// 大厅房间卡的封面图：本地宣传图按房间码轮换。
//
// 服务端 RoomSummary 没有封面字段（只有 pack.name），所以封面先用项目自带的
// 6 张壁纸顶替；按联机码取模选图 —— 同一房间每次进大厅都是同一张，不会闪变。
// 输出是 ImageBrush（不是 ImageSource）：卡片顶部用 Border.Background 铺图，
// Border 的 CornerRadius 会自动把图裁成圆角，比在 Border 里放 Image 干净。
//
// 将来服务端下发 cover_url 时，只改这里：有远程图就用远程图，否则回落本地。

using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Starcloud.App.Views.Shared;

public sealed class RoomCoverConverter : IValueConverter
{
    private static readonly string[] Covers =
    [
        "ms-appx:///Assets/Rooms/room-cover-java.jpg",
        "ms-appx:///Assets/Rooms/room-cover-bundle.jpg",
        "ms-appx:///Assets/Rooms/room-cover-bedrock.jpg",
    ];

    // 每张图只解码一次：卡片随滚动反复生成时不重复解大图（这些 jpg 有几百 KB）
    private static readonly Dictionary<string, ImageBrush> Cache = new(StringComparer.Ordinal);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var code = value as string ?? "";
        var idx = 0;
        foreach (var c in code)
            if (char.IsAsciiDigit(c)) idx = (idx * 10 + (c - '0')) % Covers.Length;

        var uri = Covers[idx];
        if (Cache.TryGetValue(uri, out var hit)) return hit;

        var brush = new ImageBrush
        {
            ImageSource = new BitmapImage(new Uri(uri)) { DecodePixelWidth = 560 },
            Stretch = Stretch.UniformToFill,
        };
        Cache[uri] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
