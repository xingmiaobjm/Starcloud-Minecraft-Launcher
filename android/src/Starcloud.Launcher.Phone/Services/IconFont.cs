// 图标字体入口：HarmonyOS 图标库（见 App.xaml 的 ScIconFont 与 Assets/Fonts/）
//
// XAML 里用 FontFamily="{StaticResource ScIconFont}"；代码里 new FontIcon 时挂这里给的字体，
// 两边都指向同一个资源，避免字体 URI 写两遍、改一处漏一处。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Starcloud.Launcher.Phone.Services;

internal static class IconFont
{
    /// <summary>HarmonyOS 图标库字体（App.xaml 的 ScIconFont 资源）。</summary>
    public static FontFamily Family =>
        (FontFamily)Application.Current.Resources["ScIconFont"];
}
