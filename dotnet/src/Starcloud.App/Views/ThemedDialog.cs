// ContentDialog 修复：WinUI 3 弹层（PopupRoot）不继承 Window.Content 上的 RequestedTheme
// （microsoft-ui-xaml 已知问题），导致浅色主题下弹窗跟随系统深色。显示前把主题
// 显式钉到 XamlRoot 根元素的解析主题（ActualTheme），全部弹窗统一改用本类型。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Starcloud.App.Views;

public sealed class ThemedDialog : ContentDialog
{
    public ThemedDialog()
    {
        Loading += (_, _) =>
        {
            if (XamlRoot?.Content is FrameworkElement root)
                RequestedTheme = root.ActualTheme;
        };
    }
}
