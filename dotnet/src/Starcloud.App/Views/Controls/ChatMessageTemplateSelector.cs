// 聊天气泡模板选择器：自己的消息右对齐品牌色，对方消息左对齐灰底。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Views;

namespace Starcloud.App.Views.Controls;

public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MineTemplate { get; set; }
    public DataTemplate? TheirsTemplate { get; set; }
    public DataTemplate? SeparatorTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        ChatMessageRowModel { IsSeparator: true } => SeparatorTemplate,
        ChatMessageRowModel { IsMine: true } => MineTemplate,
        _ => TheirsTemplate,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
