// 外观应用中心：主题应用 + 内置壁纸清单 + 毛玻璃遮罩解析（设计：custom-background-theme-design.md §5）

using Microsoft.UI.Xaml;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Phone.Services;

/// <summary>内置壁纸条目（PreferDark 仅用于设置页分组展示，不强制切换主题）。</summary>
public sealed record BuiltinWallpaper(string Id, string Name, string FileName, bool PreferDark);

/// <summary>
/// 外观应用中心：把 LauncherSettings 里的外观偏好翻译成窗口行为。
/// 只做无状态计算与主题落地；窗口级背景渲染见 Views/WindowBackgroundHost。
/// </summary>
public sealed class AppearanceService
{
    /// <summary>内置壁纸清单（文件随输出目录发布，位于 Assets/Wallpapers 下）。</summary>
    public static IReadOnlyList<BuiltinWallpaper> Wallpapers { get; } =
    [
        new("aurora", Loc.T("极光"), "aurora.jpg", true),
        new("nebula", Loc.T("星云"), "nebula.jpg", true),
        new("midnight", Loc.T("子夜"), "midnight.jpg", true),
        new("dawn", Loc.T("破晓"), "dawn.jpg", false),
        new("mist", Loc.T("晨雾"), "mist.jpg", false),
        new("sand", Loc.T("暖沙"), "sand.jpg", false),
    ];

    /// <summary>内置壁纸目录（安卓端壁纸经 resizetizer 走 ms-appx 分发，此文件系统路径仅保留状态用途）。</summary>
    public static string WallpaperDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpapers");

    /// <summary>按 id 取内置壁纸；不存在 → null。</summary>
    public static BuiltinWallpaper? FindWallpaper(string? id) =>
        Wallpapers.FirstOrDefault(w => w.Id == id);

    /// <summary>主题默认遮罩不透明度（与 TransparentAcrylicBackdrop 的亚克力配方保持一致）。</summary>
    public static double DefaultMaskOpacity(ElementTheme theme) =>
        theme == ElementTheme.Dark ? 0.45 : 0.25;

    /// <summary>按设置与当前主题推算生效的毛玻璃遮罩不透明度。</summary>
    public static double ResolveMaskOpacity(LauncherSettings s, ElementTheme theme)
    {
        if (s.BackgroundFollowTheme || s.BackgroundMaskOpacity is not { } m)
            return DefaultMaskOpacity(theme);
        return Math.Clamp(m, 0, 1);
    }

    /// <summary>把设置里的背景解析成绝对文件路径；未配置 / 模式非法 / 文件不存在 → null。</summary>
    public static string? ResolveBackgroundPath(LauncherSettings s) => s.BackgroundMode switch
    {
        "file" => s.ResolvedBackgroundPath(),
        "builtin" => BuiltinPath(s.BackgroundBuiltinId),
        _ => null,
    };

    private static string? BuiltinPath(string? id)
    {
        if (FindWallpaper(id) is not { } w) return null;
        var p = Path.Combine(WallpaperDirectory, w.FileName);
        return File.Exists(p) ? p : null;
    }

    /// <summary>把 "system" | "light" | "dark" 应用到窗口根元素（Uno 单窗口下同样作用于根元素）。</summary>
    public void ApplyTheme(string theme, FrameworkElement? root)
    {
        if (root is null) return;
        root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
