// 动效档位 → 界面材质：极少档连「毛玻璃卡片」也收起（亚克力退化为近实色），其余档恢复。
//
// 「毛玻璃」是较少档的标志性观感之一（设置文案同口径），但它的代价是每张卡片一层
// 实时模糊采样。极少档的定位是"只保留最基本的页面切换动画"，所以这里把 GlassCardBrush
// 的 TintOpacity 拉满——亚克力完全被 Tint 覆盖，读起来就是实色卡片，模糊采样不再可见。
// 实现是改主题字典里那两个 AcrylicBrush 实例的属性，页面上已应用的 ThemeResource 引用
// 同一实例，改完即时生效，不需要重排可视树。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

/// <summary>按动效档位切换毛玻璃材质（极少档收起，其余档恢复原配方）。</summary>
public static class MotionMaterials
{
    /// <summary>原配方（App.xaml 主题字典里的初值）：浅 0.72 / 深 0.75。首次见到时记下，切换档位后照此恢复。</summary>
    private static readonly Dictionary<string, double> Recipe = new(StringComparer.Ordinal)
    {
        ["Light"] = 0.72,
        ["Dark"] = 0.75,
    };

    private const string BrushKey = "GlassCardBrush";

    /// <summary>把两套主题字典里的 GlassCardBrush 都调到当前档位的形态（幂等，可反复调用）。</summary>
    public static void Apply(LauncherSettings settings)
    {
        var minimal = settings.UiMotionLevel == "minimal";
        foreach (var (theme, dict) in CollectThemeDictionaries())
        {
            if (!Recipe.TryGetValue(theme, out var recipe)) continue;
            if (dict.TryGetValue(BrushKey, out var value) && value is AcrylicBrush acrylic)
                acrylic.TintOpacity = minimal ? 1.0 : recipe;
        }
    }

    /// <summary>
    /// 收集应用资源里的主题字典：ThemeDictionaries 直接挂在外层资源字典上（App.xaml 的写法），
    /// 框架合并字典（XamlControlsResources）里也可能有，一并收——与 MainWindow.ThemedBrush 的
    /// 查找顺序同源，两处都翻到才不漏。
    /// </summary>
    private static List<(string Theme, ResourceDictionary Dict)> CollectThemeDictionaries()
    {
        List<(string Theme, ResourceDictionary Dict)> result = [];
        var app = Application.Current;
        if (app is null) return result;

        foreach (var kv in app.Resources.ThemeDictionaries)
            if (kv.Value is ResourceDictionary rd && kv.Key is string key)
                result.Add((key, rd));
        foreach (var merged in app.Resources.MergedDictionaries)
            foreach (var kv in merged.ThemeDictionaries)
                if (kv.Value is ResourceDictionary rd && kv.Key is string key)
                    result.Add((key, rd));
        return result;
    }
}
