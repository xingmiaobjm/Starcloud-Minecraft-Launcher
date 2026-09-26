// ContentDialog 修复：WinUI 3 弹层（PopupRoot）不继承 Window.Content 上的 RequestedTheme
// （microsoft-ui-xaml 已知问题），导致浅色主题下弹窗跟随系统深色。显示前把主题
// 显式钉到 XamlRoot 根元素的解析主题（ActualTheme），全部弹窗统一改用本类型。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Starcloud.App.Views.Dialogs;

public sealed class ThemedDialog : ContentDialog
{
    public ThemedDialog()
    {
        ApplyShadowButtons(this);
        Loading += (_, _) =>
        {
            if (XamlRoot?.Content is FrameworkElement root)
                RequestedTheme = root.ActualTheme;
        };
    }

    /// <summary>
    /// 让对话框底部的白底按钮（取消 / 关闭 / 次要）也带跟手阴影。
    /// <para>这些按钮来自 ContentDialog 的模板，XAML 侧够不到，无法逐个挂附加属性；这里改挂
    /// <c>ScShadowButtonStyle</c>——同样基于 Fluent 的 DefaultButtonStyle，外观不变，只多一条阴影开关。
    /// 强调色主按钮保持原样（它是 accent 底，本来就不走白底按钮那套）。</para>
    /// </summary>
    public static void ApplyShadowButtons(ContentDialog dialog)
    {
        if (Application.Current.Resources["ScShadowButtonStyle"] is Style style)
        {
            dialog.CloseButtonStyle = style;
            dialog.SecondaryButtonStyle = style;
        }
    }
}
