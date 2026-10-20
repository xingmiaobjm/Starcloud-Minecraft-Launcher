// ContentDialog 统一形态：白底按钮（取消 / 关闭 / 次要）挂 ScShadowButtonStyle（跟手阴影），
// 强调色主按钮保持原样。移植自桌面 Starcloud.App.Views.Dialogs.ThemedDialog。
// 桌面那份还修了「弹层不继承 RequestedTheme」的 WinUI 3 已知问题；Uno 侧同样在 Loading
// 时把主题钉到根元素的解析主题（无害，跨端形态一致）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Starcloud.Launcher.Uno.Views.Dialogs;

public sealed class ThemedDialog : ContentDialog
{
    public ThemedDialog()
    {
        ApplyShadowButtons(this);
        Loading += (_, _) =>
        {
            try
            {
                if (XamlRoot?.Content is FrameworkElement root)
                    RequestedTheme = root.ActualTheme;
            }
            catch
            {
                // 主题同步失败不影响弹窗本身
            }
        };
    }

    /// <summary>
    /// 让对话框底部的白底按钮（取消 / 关闭 / 次要）也带跟手阴影。
    /// <para>这些按钮来自 ContentDialog 的模板，XAML 侧够不到，无法逐个挂附加属性；这里改挂
    /// <c>ScShadowButtonStyle</c>——同样基于 Fluent 的 DefaultButtonStyle，外观不变，只多一条阴影开关。</para>
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
