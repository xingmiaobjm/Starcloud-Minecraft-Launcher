// 卡片内容的尺寸适配适配器：内容本身在 XAML 里写，占格变化通过回调转给页面。
//
// 为什么不给每类卡片单独做一个 UserControl：卡片的视觉和主页现有资源（GlassCardBrush、
// 行模板、按钮样式）高度复用，写在主页 XAML 里最省事；但 XAML 里的 Grid 没法实现
// ICardSizeAware 接口，所以套一层这个薄壳，只负责把「现在占几格」转成回调。

using Microsoft.UI.Xaml.Controls;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Controls;

/// <summary>承载卡片内容并转发占格变化。</summary>
public sealed class SizeAwareContent : ContentControl, ICardSizeAware
{
    /// <summary>占格变化回调：(列跨度, 行跨度)。</summary>
    public Action<int, int>? CardSizeChanged { get; set; }

    public SizeAwareContent()
    {
        // ContentControl 默认把内容居中在期望尺寸上，卡片边框会缩成一小块；
        // 卡片必须始终撑满所在格
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
    }

    public void ApplyCardSize(int columnSpan, int rowSpan) => CardSizeChanged?.Invoke(columnSpan, rowSpan);
}
