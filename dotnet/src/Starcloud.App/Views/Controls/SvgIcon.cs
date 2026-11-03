// ﻿// 单色 SVG 图标加载器：把 Assets/Icons/<name>.svg 染色后画到 Image 上。
//
// 用法（XAML）：<Image ctrl:SvgIcon.Source="host" Width="28" Height="28" />
//
// 为什么不用 FontIcon：字体渲染的图标随字体回退/缩放发虚，且码位只有字体作者知道；
// SVG 文件看得见摸得着，换图标 = 替换文件。图标源是项目自带的 HarmonyOS Symbol
// 字体（Assets/Fonts/HarmonyOSIcons.ttf），用 fontTools 把字形轮廓烘焙成 24×24 的
// path —— 图形就是鸿蒙图标库的正版形状。
//
// 染色：SVG 里的 #FE01FE 是占位色（洋红 = 设计界「未染色」惯例），加载时替换成
// Tint 指定的画刷颜色（默认品牌黄 accent）。WinUI 的 SvgImageSource 不认
// currentColor，也改不了已渲染的颜色，所以只能这样在字节层面替换。

using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Starcloud.App.Views.Controls;

public static class SvgIcon
{
    /// <summary>图标名 = Assets/Icons 下的文件名（不带 .svg）。</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(SvgIcon), new PropertyMetadata(null, OnSourceChanged));

    /// <summary>染色目标：accent（品牌黄，默认）或 text（主题前景色）。</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.RegisterAttached(
        "Tint", typeof(string), typeof(SvgIcon), new PropertyMetadata("accent", OnTintChanged));

    /// <summary>加载世代：Source/Tint 快速变化时丢弃旧请求，避免慢加载覆盖新值。</summary>
    private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
        "Generation", typeof(int), typeof(SvgIcon), new PropertyMetadata(0));

    public static void SetSource(Image image, string value) => image.SetValue(SourceProperty, value);
    public static string? GetSource(Image image) => (string?)image.GetValue(SourceProperty);
    public static void SetTint(Image image, string value) => image.SetValue(TintProperty, value);
    public static string GetTint(Image image) => (string)image.GetValue(TintProperty);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        if (GetSource(image) is null) return;
        // WinUI 对未加载到可视树的元素改 Source 可能不触发渲染 —— 统一等 Loaded 再画；
        // ActualThemeChanged 负责 text 染色在切主题时重画（accent 色浅深同值，重画无害）。
        image.Loaded -= OnImageLoaded;
        image.Loaded += OnImageLoaded;
        image.ActualThemeChanged -= OnThemeChanged;
        image.ActualThemeChanged += OnThemeChanged;
        if (image.IsLoaded) _ = ApplyAsync(image);
    }

    private static void OnTintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Image { IsLoaded: true } image) _ = ApplyAsync(image);
    }

    private static void OnImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) _ = ApplyAsync(image);
    }

    private static void OnThemeChanged(object sender, object args)
    {
        if (sender is Image image) _ = ApplyAsync(image);
    }

    private static async Task ApplyAsync(Image image)
    {
        var name = GetSource(image);
        if (string.IsNullOrEmpty(name)) return;
        var generation = (int)image.GetValue(GenerationProperty) + 1;
        image.SetValue(GenerationProperty, generation);

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", name + ".svg");
            if (!File.Exists(path))
            {
                image.Source = null;
                return;
            }

            var svg = (await File.ReadAllTextAsync(path)).Replace("#FE01FE", ToHex(TintColor(image)));
            var source = new SvgImageSource
            {
                // 按 4 倍栅格化：28 逻辑像素的图标在 200% 缩放屏上依然锐利
                RasterizePixelWidth = 96,
                RasterizePixelHeight = 96,
            };
            var bytes = Encoding.UTF8.GetBytes(svg);
            using var stream = new MemoryStream(bytes);
            await source.SetSourceAsync(stream.AsRandomAccessStream());
            if (generation != (int)image.GetValue(GenerationProperty)) return;   // 已有更新的请求
            image.Source = source;
        }
        catch
        {
            // 图标加载失败不值得打断页面：留空比崩溃好
        }
    }

    private static Windows.UI.Color TintColor(Image image)
    {
        if (!string.Equals(GetTint(image), "text", StringComparison.OrdinalIgnoreCase))
        {
            if (FindBrush(image, "AccentFillColorDefaultBrush") is SolidColorBrush accent)
                return accent.Color;
        }
        // 主题前景色兜底（ThemeResource 查不到时给一个可读的近似值）
        return FindBrush(image, "TextFillColorPrimaryBrush") is SolidColorBrush fg
            ? fg.Color
            : image.ActualTheme == ElementTheme.Dark
                ? Microsoft.UI.Colors.White
                : Microsoft.UI.Colors.Black;
    }

    /// <summary>沿元素 → 应用资源 → 当前主题字典查画刷（与 MainWindow.ThemedBrush 同口径）。</summary>
    private static Brush? FindBrush(Image image, string key)
    {
        var node = (DependencyObject?)image;
        while (node is not null)
        {
            if (node is FrameworkElement { Resources: { } res } &&
                res.TryGetValue(key, out var local) && local is Brush lb)
                return lb;
            node = VisualTreeHelper.GetParent(node);
        }
        if (Application.Current.Resources.TryGetValue(key, out var appLevel) && appLevel is Brush ab)
            return ab;
        var dictKey = image.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        return Application.Current.Resources.ThemeDictionaries.TryGetValue(dictKey, out var themeDict) &&
               themeDict is ResourceDictionary td &&
               td.TryGetValue(key, out var themed) && themed is Brush tb
            ? tb
            : null;
    }

    // SVG 的 fill 只认 #RRGGBB（带 alpha 段的 #AARRGGBB 会被 Win2D 判非法、渲染成黑）
    private static string ToHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
